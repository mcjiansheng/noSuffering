#!/usr/bin/env python3
"""Reuse SSH credentials; feed PowerShell on stdin, or upload tracked source with --sync."""
import base64,os,subprocess,sys,tempfile,zipfile
from pathlib import Path
root=Path(__file__).resolve().parent.parent
settings={}
if (root/'local.env').exists():
    for line in (root/'local.env').read_text().splitlines():
        if line.strip() and not line.startswith('#') and '=' in line:
            k,v=line.split('=',1); settings[k]=v.strip().strip('"')
settings.update(os.environ)
ssh=['ssh','-o','BatchMode=yes','-o','ConnectTimeout=10']
scp=['scp','-o','BatchMode=yes','-o','ConnectTimeout=10']
if settings.get('SSH_HOSTNAME'):
    for a in (ssh,scp): a.extend(['-o','HostName='+settings['SSH_HOSTNAME'],'-o','HostKeyAlias='+settings.get('SSH_HOST_KEY_ALIAS','xht-rog')])
alias=settings.get('SSH_ALIAS','xht')
remote=settings.get('REMOTE_PROJECT_DIR','C:/Users/48811/source/NoSuffering')
def run(code):
    prefix="$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; "
    encoded=base64.b64encode((prefix+code).encode('utf-16le')).decode()
    return subprocess.call(ssh+[alias,'powershell -NoProfile -EncodedCommand '+encoded])
if '--sync' in sys.argv:
    with tempfile.TemporaryDirectory(prefix='nosuffering-') as tmp:
        z=Path(tmp)/'source.zip'
        with zipfile.ZipFile(z,'w',zipfile.ZIP_DEFLATED) as f:
            for p in root.rglob('*'):
                rel=p.relative_to(root)
                if p.is_file() and not any(x in {'.git','.tools','bin','obj','artifacts','local.env','.DS_Store','__pycache__'} for x in rel.parts): f.write(p,rel)
        subprocess.run(scp+[str(z),alias+':source-NoSuffering.zip'],check=True)
        raise SystemExit(run("New-Item -ItemType Directory -Force '"+remote+"' | Out-Null; Expand-Archive -Path \"$env:USERPROFILE/source-NoSuffering.zip\" -DestinationPath '"+remote+"' -Force"))
raise SystemExit(run(sys.stdin.read()))
