#!/usr/bin/env bash
# Cross-compile Windows on the isolated Linux CI runner. Never execute Windows UI here.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project tests/PrivateFlow.Tests -c Release
dotnet publish src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -r win-x64 \
  --self-contained true -p:EnableWindowsTargeting=true -o artifacts/app
python3 - <<'PACKAGE'
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import os
import re
import shutil
import xml.etree.ElementTree as ElementTree
import zipfile
app = Path('artifacts/app')
Path('artifacts/manifest.json').unlink(missing_ok=True)
for name in ('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md'):
    shutil.copyfile(name, app / name)
(app / 'scripts').mkdir(exist_ok=True)
for script in ('setup-engine.ps1', 'setup-rewrite.ps1', 'settings-file.ps1', 'models.json', 'install.ps1', 'uninstall.ps1'):
    shutil.copyfile('scripts/' + script, app / 'scripts' / script)
with zipfile.ZipFile('artifacts/PrivateFlow-win-x64.zip', 'w', zipfile.ZIP_DEFLATED) as package:
    for path in sorted(app.rglob('*')):
        if path.is_file():
            package.write(path, path.relative_to(app).as_posix())
assert (app / 'PrivateFlow.exe').is_file(), 'Windows executable missing from package'
print('Packaged Windows x64 app on the Linux CI guest.')
# This manifest accompanies the ZIP on the update server. Only an Actions
# checkout has a GITHUB_SHA that matches the clean tree the package was built from.
commit = os.environ.get('GITHUB_SHA', '')
if commit:
    assert re.fullmatch(r'[0-9a-f]{40}', commit), 'GITHUB_SHA is not a full commit ID'
    version = ElementTree.parse('src/PrivateFlow.Windows/PrivateFlow.Windows.csproj').findtext('.//Version')
    assert version, 'Version missing from PrivateFlow.Windows.csproj'
    manifest = {
        'schema': 1, 'project': 'private-flow', 'board': 'win-x64', 'commit': commit, 'dirty': False,
        'built_at': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), 'merge_commit': None,
        'files': [{
            'name': 'PrivateFlow-win-x64.zip', 'profile': 'release', 'version': version,
            'size': Path('artifacts/PrivateFlow-win-x64.zip').stat().st_size,
            'sha256': hashlib.sha256(Path('artifacts/PrivateFlow-win-x64.zip').read_bytes()).hexdigest(),
        }],
    }
    Path('artifacts/manifest.json').write_text(json.dumps(manifest, separators=(',', ':')) + '\n')
    print('Wrote artifacts/manifest.json for commit ' + commit + '.')
PACKAGE
