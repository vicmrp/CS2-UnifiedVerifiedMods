import base64
import copy
import hashlib
import json
import secrets
from unittest.mock import patch
from django.test import TestCase, override_settings
from django.utils import timezone
from django.core.cache import cache
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec, utils
from .models import Verifier, SigningKey, Attestation
from .protocol import validate_files, decode_json, package_matches

@override_settings(SECURE_SSL_REDIRECT=False,ALLOWED_HOSTS=['testserver'],REGISTRY_ORIGIN='https://vezit.net')
class RegistryTests(TestCase):
    def setUp(self):
        cache.clear()
        self.private, self.key = self.make_key(1)
        self.manifest={'schema':1,'platform':'paradox','mod_id':'155518','name':'Test mod','version':'1.0',
          'repository':'https://github.com/test/example','commit':'a'*40,'game_version':'1.6',
          'build':{'project':'Example.csproj','configuration':'Release','sdk':'10.0.302','game_assembly_sha256':'b'*64},
          'files':{'example.dll':'c'*64}}

    def make_key(self, account):
        private=ec.generate_private_key(ec.SECP256R1())
        raw=private.public_key().public_bytes(serialization.Encoding.X962,serialization.PublicFormat.UncompressedPoint)
        verifier,_=Verifier.objects.get_or_create(github_id=account,defaults={'github_login':f'verifier{account}'})
        key=SigningKey.objects.create(fingerprint=hashlib.sha256(raw).hexdigest(),verifier=verifier,
          public_key=base64.b64encode(raw).decode(),proof_url=f'https://gist.github.com/verifier{account}/'+'a'*32,proof={})
        return private,key

    def sign(self, body, private=None, key=None):
        private=private or self.private;key=key or self.key
        raw=json.dumps(body,separators=(',',':')).encode()
        r,s=utils.decode_dss_signature(private.sign(raw,ec.ECDSA(hashes.SHA256())))
        return {'key_id':key.pk,'payload':base64.b64encode(raw).decode(),
          'signature':base64.b64encode(r.to_bytes(32,'big')+s.to_bytes(32,'big')).decode()}

    def body(self, result='reproduced',manifest=None,key=None):
        manifest=manifest or self.manifest
        return {'schema':1,'registry':'https://vezit.net','purpose':'attestation','key_id':(key or self.key).pk,
          'issued_at':timezone.now().isoformat(),'nonce':secrets.token_urlsafe(20),
          'result':result,'manifest':manifest,'built_files':manifest['files']}

    def post(self, path, data): return self.client.post('/api/v1/'+path,json.dumps(data),content_type='application/json')

    def test_signature_roundtrip_and_hash_lookup(self):
        response=self.post('attestations',self.sign(self.body()))
        self.assertEqual(response.status_code,201,response.content)
        self.assertEqual(response.json()['release']['status'],'REPRODUCED')
        found=self.post('lookup',{'mod_id':'155518','files':{'Example.dll':'c'*64}})
        self.assertEqual(found.json()['status'],'REPRODUCED')
        evidence=self.client.get('/api/v1/releases/'+response.json()['release']['id']+'/attestations').json()
        self.assertEqual(evidence['results'][0]['key']['github']['id'],1)

    def test_tampering_and_wrong_audience_rejected(self):
        envelope=self.sign(self.body());envelope['signature']=base64.b64encode(bytes(64)).decode()
        self.assertEqual(self.post('attestations',envelope).status_code,400)
        body=self.body();body['registry']='https://attacker.example'
        self.assertEqual(self.post('attestations',self.sign(body)).status_code,400)
        self.assertEqual(Attestation.objects.count(),0)

    def test_replay_is_idempotent(self):
        envelope=self.sign(self.body())
        self.assertEqual(self.post('attestations',envelope).status_code,201)
        self.assertEqual(self.post('attestations',envelope).status_code,200)
        self.assertEqual(Attestation.objects.count(),1)

    def test_old_timestamp_and_missing_nonce(self):
        body=self.body();body['issued_at']='2000-01-01T00:00:00Z'
        self.assertEqual(self.post('attestations',self.sign(body)).status_code,400)
        body=self.body();body['nonce']='tiny'
        self.assertEqual(self.post('attestations',self.sign(body)).status_code,400)

    def test_distinct_accounts_not_key_count(self):
        self.post('attestations',self.sign(self.body()))
        private,key=self.make_key(1)
        response=self.post('attestations',self.sign(self.body(key=key),private,key))
        self.assertEqual(response.json()['release']['matching_builds'],1)
        private,key=self.make_key(2)
        response=self.post('attestations',self.sign(self.body(key=key),private,key))
        self.assertEqual(response.json()['release']['matching_builds'],2)

    def test_revocation_removes_trust_and_blocks_submission(self):
        self.post('attestations',self.sign(self.body()))
        body=self.body();body.update(purpose='revoke-key',target_key=self.key.pk)
        response=self.post('keys/'+self.key.pk+'/revoke',self.sign(body))
        self.assertEqual(response.status_code,200,response.content)
        self.assertIsNotNone(response.json()['revoked_at'])
        self.assertEqual(self.post('attestations',self.sign(self.body())).status_code,403)
        self.assertEqual(self.post('lookup',{'mod_id':'155518','files':self.manifest['files']}).json()['status'],'UNVERIFIED')

    def test_cannot_revoke_another_account(self):
        private,key=self.make_key(2);body=self.body();body.update(purpose='revoke-key',target_key=key.pk)
        self.assertEqual(self.post('keys/'+key.pk+'/revoke',self.sign(body)).status_code,403)

    def test_conflicting_manifest_is_disputed(self):
        self.post('attestations',self.sign(self.body()))
        other=copy.deepcopy(self.manifest);other['files']['example.dll']='d'*64
        response=self.post('attestations',self.sign(self.body(manifest=other)))
        self.assertEqual(response.json()['release']['status'],'DISPUTED')
        self.assertEqual(self.post('lookup',{'mod_id':'155518','files':self.manifest['files']}).json()['status'],'DISPUTED')

    def test_mismatch_result_must_match_evidence(self):
        body=self.body('mismatch')
        self.assertEqual(self.post('attestations',self.sign(body)).status_code,400)
        body['built_files']={'example.dll':'d'*64}
        self.assertEqual(self.post('attestations',self.sign(body)).json()['release']['status'],'DISPUTED')

    def test_unexpected_code_and_wrong_mod_do_not_match(self):
        self.post('attestations',self.sign(self.body()))
        for data in ({'mod_id':'99','files':self.manifest['files']},
                     {'mod_id':'155518','files':{**self.manifest['files'],'extra.dll':'e'*64}}):
            self.assertEqual(self.post('lookup',data).json()['status'],'UNKNOWN')

    def test_strict_paths_and_duplicate_fields(self):
        for files in ({'../bad.dll':'a'*64},{'A.dll':'a'*64,'a.dll':'a'*64},{},{'/a.dll':'a'*64}):
            with self.assertRaises(ValueError):validate_files(files)
        with self.assertRaises(ValueError):decode_json(base64.b64encode(b'{"schema":1,"schema":2}'))

    def test_bad_input_returns_400(self):
        for path in ('keys/challenge','keys','lookup','attestations'):
            for data in ([],None,42,{'files':[]}):
                response=self.post(path,data)
                self.assertIn(response.status_code,(400,404),response.content)

    @patch('registry.views.read_key_proof')
    def test_github_challenge_registration(self,read_proof):
        challenge=self.post('keys/challenge',{'public_key':self.key.public_key}).json()
        body=json.loads(base64.b64decode(challenge['payload']))
        envelope=self.sign(body)
        read_proof.return_value=({'id':1,'login':'renamed'},envelope)
        result=self.post('keys',{'gist_url':'https://gist.github.com/renamed/'+'a'*32})
        self.assertEqual(result.status_code,200,result.content)
        self.assertEqual(result.json()['github']['login'],'renamed')
        read_proof.return_value=({'id':2,'login':'thief'},envelope)
        self.assertEqual(self.post('keys',{'gist_url':'https://gist.github.com/thief/'+'a'*32}).status_code,400)

    def test_package_scope(self):
        expected={'x.dll':'a'*64}
        self.assertTrue(package_matches({**expected,'readme.md':'b'*64},expected))
        self.assertFalse(package_matches({**expected,'ui/hidden.js':'b'*64},expected))
