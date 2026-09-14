import json
import re
from urllib.request import Request, urlopen
from urllib.error import URLError

def read_key_proof(url):
    match = re.fullmatch(r'https://gist\.github\.com/([A-Za-z0-9-]+)/([a-fA-F0-9]{20,40})/?', str(url))
    if not match: raise ValueError('Use a public gist.github.com account/gist URL')
    request = Request('https://api.github.com/gists/' + match[2], headers={
        'Accept': 'application/vnd.github+json', 'User-Agent': 'UVM-Registry/1.0', 'X-GitHub-Api-Version': '2022-11-28'})
    try:
        with urlopen(request, timeout=10) as response:
            if response.geturl() != request.full_url: raise ValueError('Unexpected GitHub redirect')
            raw = response.read(1048577)
            if len(raw) > 1048576: raise ValueError('GitHub response too large')
            gist = json.loads(raw)
    except (URLError, TimeoutError) as ex: raise ValueError('GitHub could not confirm this gist; try again later') from ex
    owner = gist.get('owner') or {}
    if not gist.get('public') or owner.get('type') != 'User' or owner.get('login','').lower() != match[1].lower():
        raise ValueError('Proof must be in a public gist owned by a GitHub user')
    proof = gist.get('files',{}).get('uvm-key.json',{})
    if proof.get('truncated') or len(proof.get('content','')) > 20000: raise ValueError('Invalid proof file')
    try: envelope = json.loads(proof['content'])
    except (KeyError, ValueError) as ex: raise ValueError('Gist must contain uvm-key.json') from ex
    return owner, envelope
