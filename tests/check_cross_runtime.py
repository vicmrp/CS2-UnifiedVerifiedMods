import os,sys,json
os.environ.setdefault('UVM_SECRET_KEY','test-only')
os.environ.setdefault('DJANGO_SETTINGS_MODULE','uvm_registry.settings')
sys.path.insert(0,os.path.join(os.path.dirname(__file__),'..','server'))
import django
django.setup()
from registry.protocol import verify,validate_manifest
with open(sys.argv[1]) as f: proof=json.load(f)
body,_,fingerprint=verify(proof['key']['public_key'],proof['payload'],proof['signature'])
assert fingerprint==proof['key']['id']
validate_manifest(body['manifest'])
print('PASS: Python verifies the exact .NET-generated P1363 payload and manifest.')
