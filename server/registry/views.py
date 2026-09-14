import base64
import json
import secrets
from django.conf import settings
from django.core import signing
from django.db import transaction, connection
from django.db.models import Q
from django.http import JsonResponse, FileResponse, Http404
from django.shortcuts import render, get_object_or_404
from django.utils import timezone
from rest_framework.decorators import api_view
from rest_framework.response import Response
from .models import Verifier, SigningKey, Mod, Release, Attestation
from .protocol import public_key, verify, recent, validate_files, validate_manifest, manifest_hash, package_matches
from .github import read_key_proof

def index(request): return render(request, 'index.html')
def download(request,filename):
    allowed={'UVM-v0.2.0.zip','UVM-source-v0.2.0.zip','UVM-v0.3.0.zip','UVM-source-v0.3.0.zip','README.md','USER_GUIDE.md','SHA256SUMS.txt'}
    path=settings.BASE_DIR/'downloads'/filename
    if filename not in allowed or not path.is_file(): raise Http404()
    return FileResponse(path.open('rb'),as_attachment=True,filename=filename)
def health(request):
    with connection.cursor() as cursor: cursor.execute('SELECT 1')
    return JsonResponse({'status':'ok','service':'uvm-registry','schema':1})

def key_data(key):
    return {'id': key.fingerprint, 'algorithm':'ECDSA-P256-SHA256-P1363','public_key':key.public_key,
      'github':{'id':key.verifier.github_id,'login':key.verifier.github_login,
                'url':'https://github.com/'+key.verifier.github_login},
      'proof_url':key.proof_url, 'proof':key.proof, 'revoked_at':key.revoked_at, 'revocation':key.revocation, 'created_at':key.created_at}

def summary(release):
    attestations = list(release.attestations.select_related('key__verifier'))
    active = [a for a in attestations if not a.key.revoked_at]
    publishers = {a.key.verifier.github_id for a in active if a.result == 'published'}
    matching = {a.key.verifier.github_id for a in active if a.result == 'reproduced'}
    mismatches = {a.key.verifier.github_id for a in active if a.result == 'mismatch'}
    variants = Release.objects.filter(mod=release.mod,version=release.version).exclude(pk=release.pk).filter(attestations__isnull=False,attestations__key__revoked_at__isnull=True).exists()
    status = 'DISPUTED' if mismatches or variants else 'REPRODUCED' if matching else 'PUBLISHED' if publishers else 'UNVERIFIED'
    return {'id':str(release.pk), 'platform':release.mod.platform,'mod_id':release.mod.platform_id,
      'name':release.manifest['name'],'version':release.version,'manifest_hash':release.manifest_hash,
      'manifest':release.manifest,'status':status,'matching_builds':len(matching),
      'other_account_builds':len(matching-publishers),'mismatch_reports':len(mismatches),
      'has_conflicting_manifests':variants,'attestations':len(attestations),'created_at':release.created_at,
      'verification_scope':'Declared files; all installed executable files must be covered. Reproducibility is not a safety audit.'}

@api_view(['GET'])
def mods(request):
    query = str(request.GET.get('q',''))[:160]
    found = Mod.objects.filter(Q(name__icontains=query)|Q(platform_id__icontains=query)).order_by('-created_at')[:50]
    results=[]
    for mod in found:
        latest = mod.releases.order_by('-created_at').first()
        results.append({'platform':mod.platform,'mod_id':mod.platform_id,'name':mod.name,
                        'latest':summary(latest) if latest else None})
    return Response({'results':results,'stats':{'mods':Mod.objects.count(),'releases':Release.objects.count(),
                     'verifiers':Verifier.objects.count(),'attestations':Attestation.objects.count()}})

@api_view(['GET'])
def mod_detail(request,platform,mod_id):
    mod=get_object_or_404(Mod,platform=platform,platform_id=mod_id)
    return Response({'platform':platform,'mod_id':mod_id,'name':mod.name,
                      'releases':[summary(r) for r in mod.releases.order_by('-created_at')[:100]]})

@api_view(['GET'])
def version_detail(request,platform,mod_id,version):
    mod=get_object_or_404(Mod,platform=platform,platform_id=mod_id)
    variants=mod.releases.filter(version=version).order_by('created_at')
    if not variants.exists(): return Response({'error':'Release not found'},status=404)
    return Response({'results':[summary(r) for r in variants[:100]]})

@api_view(['GET'])
def attestations(request,release_id):
    release=get_object_or_404(Release,pk=release_id)
    # Every stored signature is public; callers can verify the original bytes.
    values=[{'id':str(a.pk),'result':a.result,'payload':a.payload,'signature':a.signature,
             'key':key_data(a.key),'created_at':a.created_at} for a in release.attestations.select_related('key__verifier').order_by('created_at')[:500]]
    return Response({'release':summary(release),'results':values})

@api_view(['GET'])
def keys(request,fingerprint): return Response(key_data(get_object_or_404(SigningKey.objects.select_related('verifier'),pk=fingerprint)))

@api_view(['POST'])
def key_challenge(request):
    try:
        encoded=request.data.get('public_key')
        _, fingerprint=public_key(encoded)
        challenge=signing.dumps({'fingerprint':fingerprint,'nonce':secrets.token_urlsafe(24)},salt='uvm-key')
        body={'schema':1,'registry':settings.REGISTRY_ORIGIN,'purpose':'register-key',
              'public_key':encoded,'challenge':challenge}
        payload=base64.b64encode(json.dumps(body,separators=(',',':')).encode()).decode()
        return Response({'payload':payload,'expires_in':3600,'proof_filename':'uvm-key.json'})
    except ValueError as ex: return Response({'error':str(ex)},status=400)

@api_view(['POST'])
def register_key(request):
    try:
        owner,envelope=read_key_proof(request.data.get('gist_url'))
        if not isinstance(envelope,dict): raise ValueError('Invalid key proof')
        from .protocol import decode_json
        body,_=decode_json(envelope.get('payload'))
        body,_,fingerprint=verify(body.get('public_key'),envelope.get('payload'),envelope.get('signature'))
        if body.get('purpose') != 'register-key': raise ValueError('Wrong proof purpose')
        challenge=signing.loads(body.get('challenge'),salt='uvm-key',max_age=3600)
        if challenge['fingerprint'] != fingerprint: raise ValueError('Challenge does not match key')
        with transaction.atomic():
            verifier,_=Verifier.objects.get_or_create(github_id=owner['id'],defaults={'github_login':owner['login']})
            if verifier.github_login != owner['login']:
                verifier.github_login=owner['login']; verifier.save(update_fields=['github_login'])
            key,created=SigningKey.objects.get_or_create(fingerprint=fingerprint,defaults={
                'verifier':verifier,'public_key':body['public_key'],'proof_url':request.data['gist_url'],'proof':envelope})
            if key.verifier_id != verifier.pk: raise ValueError('Key already belongs to another account')
        return Response(key_data(key),status=201 if created else 200)
    except (ValueError, signing.BadSignature, KeyError, TypeError) as ex:
        return Response({'error':str(ex) if isinstance(ex,ValueError) else 'Invalid or expired proof'},status=400)

@api_view(['POST'])
def submit_attestation(request):
    try:
        key=get_object_or_404(SigningKey.objects.select_related('verifier'),pk=request.data.get('key_id'))
        if key.revoked_at: return Response({'error':'Signing key is revoked'},status=403)
        body,digest,_=verify(key.public_key,request.data.get('payload'),request.data.get('signature'))
        if body.get('purpose') != 'attestation' or body.get('key_id') != key.pk: raise ValueError('Wrong signature context')
        existing=Attestation.objects.filter(payload_hash=digest).first()
        if existing: return Response({'id':str(existing.pk),'release':summary(existing.release),'duplicate':True})
        recent(body)
        manifest=validate_manifest(body.get('manifest'))
        built=validate_files(body.get('built_files'))
        outcome=body.get('result')
        if outcome not in ('published','reproduced','mismatch'): raise ValueError('Invalid result')
        matches=built == manifest['files']
        if matches != (outcome != 'mismatch'): raise ValueError('Result contradicts submitted file hashes')
        with transaction.atomic():
            # Lock the key so revocation and submissions cannot race.
            locked=SigningKey.objects.select_for_update().get(pk=key.pk)
            if locked.revoked_at: raise ValueError('Signing key is revoked')
            mod,_=Mod.objects.get_or_create(platform=manifest['platform'],platform_id=manifest['mod_id'],defaults={'name':manifest['name']})
            release,_=Release.objects.get_or_create(mod=mod,version=manifest['version'],manifest_hash=manifest_hash(manifest),defaults={'manifest':manifest})
            att,created=Attestation.objects.get_or_create(payload_hash=digest,defaults={'release':release,'key':key,'result':outcome,
                                      'payload':request.data['payload'],'signature':request.data['signature']})
        return Response({'id':str(att.pk),'release':summary(release)},status=201 if created else 200)
    except (ValueError,TypeError,KeyError) as ex: return Response({'error':str(ex)},status=400)

@api_view(['POST'])
def revoke_key(request,fingerprint):
    try:
        signer=get_object_or_404(SigningKey,pk=request.data.get('key_id'))
        if signer.revoked_at: return Response({'error':'Signing key is revoked'},status=403)
        body,_,_=verify(signer.public_key,request.data.get('payload'),request.data.get('signature'))
        recent(body)
        if body.get('purpose') != 'revoke-key' or body.get('target_key') != fingerprint or body.get('key_id') != signer.pk:
            raise ValueError('Wrong revocation context')
        with transaction.atomic():
            signer=SigningKey.objects.select_for_update().get(pk=signer.pk)
            if signer.revoked_at: return Response({'error':'Signing key is revoked'},status=403)
            target=get_object_or_404(SigningKey.objects.select_for_update(),pk=fingerprint)
            if target.verifier_id != signer.verifier_id: return Response({'error':'Different GitHub account'},status=403)
            if not target.revoked_at:
                target.revoked_at=timezone.now(); target.revocation=dict(request.data); target.save(update_fields=['revoked_at','revocation'])
        return Response(key_data(target))
    except (ValueError,TypeError,KeyError) as ex: return Response({'error':str(ex)},status=400)

@api_view(['POST'])
def lookup(request):
    try:
        installed=validate_files(request.data.get('files'))
        mod_id=request.data.get('mod_id')
        if not mod_id: return Response({'status':'UNKNOWN','reason':'A Paradox mod ID is required; names alone are not identities.','results':[]})
        releases=Release.objects.filter(mod__platform='paradox',mod__platform_id=str(mod_id)).select_related('mod').order_by('-created_at')[:100]
        found=[summary(r) for r in releases if package_matches(installed,r.manifest['files'])]
        status='UNKNOWN'
        if found:
            status='DISPUTED' if any(r['status']=='DISPUTED' for r in found) else 'REPRODUCED' if any(r['status']=='REPRODUCED' for r in found) else 'PUBLISHED' if any(r['status']=='PUBLISHED' for r in found) else 'UNVERIFIED'
        return Response({'status':status,'results':found,'reason':'Exact declared files and executable coverage checked.' if found else 'No registered release matches these files.'})
    except (ValueError,TypeError) as ex: return Response({'error':str(ex)},status=400)
