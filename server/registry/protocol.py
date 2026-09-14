import base64
import hashlib
import json
import re
from datetime import datetime, timezone
from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.asymmetric import ec, utils
from django.conf import settings

CODE_EXTENSIONS = {'.dll', '.exe', '.so', '.bundle', '.js', '.mjs', '.cjs', '.wasm'}
HEX = re.compile(r'^[0-9a-f]{64}$')

def pairs(items):
    result = {}
    for key, value in items:
        if key in result: raise ValueError('Duplicate JSON property')
        result[key] = value
    return result

def decode_json(encoded):
    try:
        raw = base64.b64decode(encoded, validate=True)
        if len(raw) > 180000: raise ValueError('Payload too large')
        value = json.loads(raw.decode('utf-8'), object_pairs_hook=pairs,
                           parse_constant=lambda _: (_ for _ in ()).throw(ValueError('Invalid number')))
        if not isinstance(value, dict): raise ValueError('Expected an object')
        return value, raw
    except (TypeError, UnicodeError, json.JSONDecodeError) as ex:
        raise ValueError('Invalid payload') from ex

def public_key(encoded):
    try:
        raw = base64.b64decode(encoded, validate=True)
        if len(raw) != 65 or raw[0] != 4: raise ValueError('Use an uncompressed P-256 public key')
        return ec.EllipticCurvePublicKey.from_encoded_point(ec.SECP256R1(), raw), hashlib.sha256(raw).hexdigest()
    except (TypeError, ValueError) as ex: raise ValueError('Invalid P-256 public key') from ex

def verify(encoded_key, payload, signature):
    key, fingerprint = public_key(encoded_key)
    body, raw = decode_json(payload)
    try:
        sig = base64.b64decode(signature, validate=True)
        if len(sig) != 64: raise ValueError('Use a 64-byte P1363 signature')
        der = utils.encode_dss_signature(int.from_bytes(sig[:32], 'big'), int.from_bytes(sig[32:], 'big'))
        key.verify(der, raw, ec.ECDSA(hashes.SHA256()))
    except (InvalidSignature, TypeError, ValueError) as ex: raise ValueError('Invalid signature') from ex
    if body.get('schema') != 1 or body.get('registry') != settings.REGISTRY_ORIGIN:
        raise ValueError('Wrong schema or registry audience')
    return body, hashlib.sha256(raw).hexdigest(), fingerprint

def recent(body):
    try:
        when = datetime.fromisoformat(body['issued_at'].replace('Z', '+00:00'))
        seconds = (datetime.now(timezone.utc) - when).total_seconds()
        if not -300 <= seconds <= 86400: raise ValueError('Timestamp outside submission window')
        if not re.fullmatch(r'[A-Za-z0-9_-]{16,100}', body.get('nonce', '')): raise ValueError('Invalid nonce')
    except (KeyError, TypeError, AttributeError) as ex: raise ValueError('issued_at and nonce are required') from ex

def validate_files(files):
    if not isinstance(files, dict) or not 1 <= len(files) <= 256: raise ValueError('Supply 1–256 file hashes')
    normalized = {}
    for path, digest in files.items():
        if not isinstance(path, str) or len(path) > 240 or not re.fullmatch(r'[A-Za-z0-9_. /+@()-]+', path):
            raise ValueError('Invalid file path')
        if path.startswith('/') or any(p in ('', '.', '..') or p.endswith((' ', '.')) for p in path.split('/')):
            raise ValueError('Unsafe file path')
        path = path.lower()
        if path in normalized: raise ValueError('Case-insensitive duplicate file path')
        if not isinstance(digest, str) or not HEX.fullmatch(digest): raise ValueError('Expected lowercase SHA-256')
        normalized[path] = digest
    if not any('.' + p.rsplit('.', 1)[-1] in CODE_EXTENSIONS for p in normalized):
        raise ValueError('At least one code file must be covered')
    return dict(sorted(normalized.items()))

def required_string(data, name, limit=160):
    value = data.get(name)
    if not isinstance(value, str) or not value.strip() or len(value) > limit or any(ord(c) < 32 for c in value):
        raise ValueError('Invalid ' + name)
    return value

def validate_manifest(data):
    if not isinstance(data, dict): raise ValueError('manifest must be an object')
    allowed = {'schema','platform','mod_id','name','version','repository','commit','game_version','build','files'}
    if set(data) != allowed or data['schema'] != 1: raise ValueError('Invalid manifest fields or schema')
    if data['platform'] != 'paradox' or not re.fullmatch(r'[1-9][0-9]{0,18}', str(data['mod_id'])):
        raise ValueError('Expected a Paradox mod ID')
    for key, limit in [('name',160),('version',80),('game_version',80)]: required_string(data,key,limit)
    if not re.fullmatch(r'https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', data.get('repository','')):
        raise ValueError('Use a public https://github.com/owner/repository URL')
    if not re.fullmatch(r'[0-9a-f]{40}', data.get('commit','')): raise ValueError('Use a full Git commit SHA')
    build = data.get('build')
    if not isinstance(build,dict) or set(build) != {'project','configuration','sdk','game_assembly_sha256'}:
        raise ValueError('Build must record project, configuration, sdk and game_assembly_sha256')
    project = required_string(build,'project',240)
    if project.startswith(('/', '\\')) or '..' in project.replace('\\','/').split('/') or ':' in project:
        raise ValueError('Project must stay in the source repository')
    if build['configuration'] not in ('Release','Debug'): raise ValueError('Invalid build configuration')
    required_string(build,'sdk',80)
    if not HEX.fullmatch(build.get('game_assembly_sha256','')): raise ValueError('Game.dll SHA-256 is required')
    return {**data, 'mod_id': str(data['mod_id']), 'files': validate_files(data['files'])}

def manifest_hash(manifest):
    return hashlib.sha256(json.dumps(manifest, sort_keys=True, ensure_ascii=True, separators=(',',':')).encode()).hexdigest()

def package_matches(installed, expected):
    return (all(installed.get(path) == sha for path,sha in expected.items()) and
            all(path in expected for path in installed if '.' + path.rsplit('.',1)[-1] in CODE_EXTENSIONS))
