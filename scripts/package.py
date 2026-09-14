"""Package already-built clients plus public source; never include credentials or user data."""
from pathlib import Path
import sys,zipfile,hashlib,shutil
root=Path(__file__).resolve().parent.parent
clients=Path(sys.argv[1]).resolve()
downloads=root/'server'/'downloads'
downloads.mkdir(exist_ok=True)
shutil.copy2(root/'README.md',clients/'README.md')
shutil.copy2(root/'README.md',downloads/'README.md')
shutil.copy2(root/'USER_GUIDE.md',clients/'USER_GUIDE.md')
shutil.copy2(root/'USER_GUIDE.md',downloads/'USER_GUIDE.md')
shutil.copy2(root/'LICENSE',clients/'LICENSE')
shutil.copy2(root/'UVM.Mod/bin/Release/net48/UVM.dll',clients/'mod/UVM/UVM.dll')
with zipfile.ZipFile(downloads/'UVM-v0.3.0.zip','w',zipfile.ZIP_DEFLATED) as z:
    for f in sorted(clients.rglob('*')):
        if f.is_file():z.write(f,f.relative_to(clients))
allowed_dirs={'server','UVM.Cli','UVM.Mod','Shared','tests','scripts'}
excluded={'bin','obj','__pycache__','.venv','downloads','staticfiles','.git'}
with zipfile.ZipFile(downloads/'UVM-source-v0.3.0.zip','w',zipfile.ZIP_DEFLATED) as z:
    for f in sorted(root.rglob('*')):
        p=f.relative_to(root)
        if not f.is_file() or any(part in excluded for part in p.parts):continue
        if len(p.parts)>1 and p.parts[0] not in allowed_dirs:continue
        if f.suffix in {'.p8','.pem','.key','.sqlite3'} or f.name=='.env':continue
        z.write(f,p)
lines=[]
for name in ('UVM-v0.3.0.zip','UVM-source-v0.3.0.zip','README.md','USER_GUIDE.md'):
    lines.append(hashlib.sha256((downloads/name).read_bytes()).hexdigest()+'  '+name)
(downloads/'SHA256SUMS.txt').write_text('\n'.join(lines)+'\n')
print('\n'.join(lines))
