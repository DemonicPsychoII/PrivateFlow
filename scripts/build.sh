#!/usr/bin/env bash
# Cross-compile Windows on the isolated Linux CI runner. Never execute Windows UI here.
# --no-package stops after assembling artifacts/app, so a release can sign the files before package.py zips them.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet build src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -p:EnableWindowsTargeting=true
dotnet run --project tests/PrivateFlow.Tests -c Release
# The verification host is not part of the package; here it is compiled so it keeps up with the app. payload.py publishes it for the VM.
dotnet build src/PrivateFlow.Verification -c Release -p:EnableWindowsTargeting=true --nologo -v quiet
# Every other test project the checkout holds is compiled too, so it keeps up with the app; none of them runs here.
for project in tests/*/*.csproj; do
  case "$project" in tests/PrivateFlow.Tests/*) ;; *) dotnet build "$project" -c Release -p:EnableWindowsTargeting=true --nologo -v quiet ;; esac
done
python3 -I scripts/release_test.py
# The version against the base: a pull request's build is the merge with its base (HEAD^1); on main a change to the shipped app must
# be numbered (release.py check-version). Other runs, such as a manual build of a branch, have no base to compare with.
case "${GITHUB_EVENT_NAME:-}:${GITHUB_REF:-}" in
  pull_request:*) python3 -I scripts/release.py check-version ;;
  push:refs/heads/main) python3 -I scripts/release.py check-version --main ;;
esac
dotnet publish src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -r win-x64 \
  --self-contained true -p:EnableWindowsTargeting=true -o artifacts/app
python3 -I scripts/release.py check-symbols artifacts/app
cp README.md LICENSE THIRD_PARTY_NOTICES.md artifacts/app/
mkdir -p artifacts/app/scripts
for script in 'setup-engine.ps1' 'setup-rewrite.ps1' 'settings-file.ps1' 'models.json' 'install.ps1' 'uninstall.ps1'; do
  cp "scripts/$script" artifacts/app/scripts/
done
if [ "${1:-}" != --no-package ]; then
  python3 -I scripts/package.py
  # The CI package carries nothing of the verification host; build.ps1 -Portable also runs the host's --check-package on Windows.
  python3 -I - <<'CHECK'
import zipfile
with zipfile.ZipFile('artifacts/PrivateFlow-win-x64.zip') as package:
    host_files = [name for name in package.namelist() if 'privateflow.verification' in name.lower()]
    assert not host_files, 'The package holds files of the verification host: ' + ', '.join(host_files)
    assembly = package.read('PrivateFlow.dll')
host_types = [name for name in ('WorkspaceVerification', 'InsertionVerification', 'OverlayVerification', 'MeetingEvaluation', 'DictationEvaluation',
                                'AudioLibraryBuilder', 'ImportVerification', 'PackageCheck') if name.encode() in assembly]
assert not host_types, 'PrivateFlow.dll in the package names types of the verification host: ' + ', '.join(host_types)
print('The package holds no file and PrivateFlow.dll no type of the verification host.')
CHECK
fi
