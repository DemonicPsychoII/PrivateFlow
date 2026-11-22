"""Zip the assembled app folder into artifacts/PrivateFlow-win-x64.zip and, in an Actions checkout, write its manifest."""
from datetime import datetime, timezone
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import xml.etree.ElementTree as ElementTree
import zipfile

app = Path('artifacts/app')
package_path = Path('artifacts/PrivateFlow-win-x64.zip')
manifest_path = Path('artifacts/manifest.json')
manifest_path.unlink(missing_ok=True)
assert (app / 'PrivateFlow.exe').is_file(), 'Windows executable missing from package'
with zipfile.ZipFile(package_path, 'w', zipfile.ZIP_DEFLATED) as package:
    for path in sorted(app.rglob('*')):
        # A link would put its target's bytes in the package under the link's name.
        assert not path.is_symlink(), f'{path} is a link; the package must hold only plain files'
        if path.is_file():
            package.write(path, path.relative_to(app).as_posix())
print('Packaged Windows x64 app on the Linux CI guest.')
# This manifest accompanies the ZIP on the update server and in releases. Only an Actions
# checkout has a GITHUB_SHA; dirty records whether its tree still matches that commit. The
# manifest of a release carries only what install.ps1 Get-ManifestProblem -Public allows.
commit = os.environ.get('GITHUB_SHA', '')
if commit:
    assert re.fullmatch(r'[0-9a-f]{40}', commit), 'GITHUB_SHA is not a full commit ID'
    version = ElementTree.parse('src/PrivateFlow.Windows/PrivateFlow.Windows.csproj').findtext('.//Version')
    assert version, 'Version missing from PrivateFlow.Windows.csproj'
    head = subprocess.run(['git', 'rev-parse', 'HEAD'], capture_output=True, text=True, check=True).stdout.strip()
    assert head == commit, f'The checkout is at {head}, not at GITHUB_SHA {commit}'
    # Ignored build output (bin, obj, artifacts) does not count; any other change or untracked file does.
    dirty = subprocess.run(['git', 'status', '--porcelain'], capture_output=True, text=True, check=True).stdout.strip() != ''
    manifest = {'schema': 1, 'project': 'private-flow', 'board': 'win-x64'}
    # A release (a version tag) is published: its manifest names no commit, build time or tree state of the development repository.
    release = os.environ.get('GITHUB_REF_TYPE') == 'tag'
    if not release:
        manifest.update({'commit': commit, 'dirty': dirty, 'built_at': datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ'), 'merge_commit': None})
    manifest['files'] = [{
        'name': package_path.name, 'profile': 'release', 'version': version,
        'size': package_path.stat().st_size,
        'sha256': hashlib.sha256(package_path.read_bytes()).hexdigest(),
    }]
    manifest_path.write_text(json.dumps(manifest, separators=(',', ':')) + '\n')
    print('Wrote artifacts/manifest.json for ' + (f'the release of {version}' if release else f'commit {commit}') + '.')
