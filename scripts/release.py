"""Release helpers for the tag-triggered release workflow of the public repository, kept free of side effects except in main().

  release.py check-tag <tag>                    the tag is vX.Y.Z or vX.Y.Z-<label>, and X.Y.Z is the project version
  release.py notes <version> <output>           writes the public docs/RELEASE_NOTES.md section of that version
  release.py assign-version                     numbers the "# Private Flow next" section and the project version, at merge time
  release.py check-version [--main] [base]      the version and notes of HEAD fit its base (default HEAD^1, the base of a CI merge)
  release.py inventory <app folder> [version]   writes scripts/package-inventory.ps1, listing every other file of the folder
  release.py check-symbols <app folder>         Private Flow's own binaries name no checkout path, home folder, development repository
                                                or, in a release build, its commit
  release.py checksums <output> <file>...       writes SHA256SUMS in the sha256sum format
  release.py verify-checksums <sums> <file>...  checks the files against SHA256SUMS
  release.py create-release <tag> <notes> <file>...  creates or completes the GitHub release of the tag in GITHUB_REPOSITORY
"""
from pathlib import Path
import hashlib
import json
import os
import re
import subprocess
import sys
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ElementTree

TAG = re.compile(r'v(\d+\.\d+\.\d+)(?:-([0-9A-Za-z][0-9A-Za-z.]*))?')
# The same rules hold in install.ps1 (Get-ListedChecksum), ReleaseUpdate.ListedChecksum and publish-public.ps1.
CHECKSUM_LINE = re.compile(r'([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)')
MAXIMUM_CHECKSUM_BYTES = 65536
INVENTORY_NAME = 'scripts/package-inventory.ps1'
INVENTORY_PATH = re.compile(r'[A-Za-z0-9_][A-Za-z0-9._+-]*(?:/[A-Za-z0-9_][A-Za-z0-9._+-]*)*')
# The public repository, which the app's release update reads (ReleaseUpdate.cs); every other repository of the checkout is private.
PUBLIC_REPOSITORY = 'DemonicPsychoII/PrivateFlow'
GITHUB_REPOSITORY_URL = re.compile(r'github\.com[:/]+([A-Za-z0-9-]+/[A-Za-z0-9._-]+?)(?:\.git)?/*$', re.IGNORECASE)
# A pull request or issue number of the development repository, such as "(#48)"; public notes name none. Internal notes have a
# file of their own, which public snapshots leave out.
PULL_REQUEST_REFERENCE = re.compile(r'(?<![\w&#/])#\d+(?!\w)')
# What the package is built from or carries (build.sh and build.ps1 copy the files listed here; release_test.py checks that):
# a change to any of them is a change to the shipped app, which takes the next version number.
SHIPPED_PATHS = ('src/PrivateFlow.Windows/', 'src/PrivateFlow.Core/', 'assets/', 'Directory.Build.props', 'global.json', 'README.md', 'LICENSE',
                 'THIRD_PARTY_NOTICES.md', 'scripts/setup-engine.ps1', 'scripts/setup-rewrite.ps1', 'scripts/settings-file.ps1', 'scripts/models.json',
                 'scripts/install.ps1', 'scripts/uninstall.ps1')
# The section of changes merged since the last version, numbered when a change to the shipped app is merged (assign-version).
NEXT_HEADING = re.compile(r'# Private Flow next(?![\w.])(.*)')
NUMBERED_HEADING = re.compile(r'# Private Flow v?(\d+)\.(\d+)\.(\d+)(?![\d.])')


def tag_version(tag):
    """Returns (version, is_prerelease); a label after the version makes a pre-release."""
    match = TAG.fullmatch(tag)
    if not match:
        raise ValueError(f'Tag {tag!r} is not vX.Y.Z or vX.Y.Z-<label>.')
    return match.group(1), match.group(2) is not None


def release_notes(text, version):
    """Returns the body under the first '# Private Flow <version>' heading, up to the next top-level heading. A pull request
    reference in it is refused."""
    heading = re.compile(r'# Private Flow v?' + re.escape(version) + r'(?![\d.])')
    lines = text.splitlines()
    start = next((index for index, line in enumerate(lines) if heading.match(line)), None)
    if start is None:
        raise ValueError(f'docs/RELEASE_NOTES.md has no "# Private Flow {version}" heading.')
    end = next((index for index in range(start + 1, len(lines)) if lines[index].startswith('# ')), len(lines))
    body = '\n'.join(lines[start + 1:end]).strip()
    if not body:
        raise ValueError(f'The release notes of {version} are empty.')
    references = PULL_REQUEST_REFERENCE.findall(body)
    if references:
        raise ValueError(f'The release notes of {version} name pull requests of the development repository ({", ".join(references)}); '
                         'remove them or move the line to the internal notes.')
    return body + '\n'


def unnumbered_changes(text):
    """Whether the release notes hold a "# Private Flow next" section, changes no version has been assigned to yet."""
    return any(NEXT_HEADING.match(line) for line in text.splitlines())


def assign_version(notes, project):
    """The release notes and project file with the "# Private Flow next" section numbered: the newest version after it, with its last
    number raised. Run when a change to the shipped app is merged, so open pull requests never claim a number."""
    lines = notes.split('\n')
    top = [index for index, line in enumerate(lines) if line.startswith('# ')]
    if not top or not NEXT_HEADING.match(lines[top[0]]) or any(NEXT_HEADING.match(lines[index]) for index in top[1:]):
        raise ValueError('docs/RELEASE_NOTES.md must begin with the one "# Private Flow next" section to number.')
    newest = next((NUMBERED_HEADING.match(lines[index]) for index in top if NUMBERED_HEADING.match(lines[index])), None)
    current = re.search(r'<Version>([^<]+)</Version>', project)
    if newest is None or current is None or current.group(1) != '.'.join(newest.groups()):
        raise ValueError('The project version is not the newest version of docs/RELEASE_NOTES.md.')
    major, minor, patch = newest.groups()
    version = f'{major}.{minor}.{int(patch) + 1}'
    lines[top[0]] = f'# Private Flow {version}' + NEXT_HEADING.match(lines[top[0]]).group(1)
    return '\n'.join(lines), project[:current.start(1)] + version + project[current.end(1):], version


def changes_shipped_app(changed_files):
    """Whether a change touches what the package is built from or carries (SHIPPED_PATHS)."""
    return any(path == shipped or (shipped.endswith('/') and path.startswith(shipped)) for path in changed_files for shipped in SHIPPED_PATHS)


def without_newest_section(notes):
    """The release notes without their topmost section when it is numbered and an older section follows, or None."""
    lines = notes.split('\n')
    top = [index for index, line in enumerate(lines) if line.startswith('# ')]
    if len(top) < 2 or not NUMBERED_HEADING.match(lines[top[0]]):
        return None
    return '\n'.join(lines[:top[0]] + lines[top[1]:])


def version_problem(base_version, base_notes, head_version, head_notes, app_changed, on_main):
    """Why the version and release notes of a change do not follow its base, or None. Only a change to the shipped app takes a number,
    exactly the base's last number raised and not yet used, so two changes can never share one and none is skipped. A pull request may
    leave its notes unnumbered under "# Private Flow next" with the base's version until the merge step numbers them (assign-version);
    on main a change to the app must be numbered, and nothing else may change the version or leave a next section. A revert of the
    newest numbered change, a change to the app whose notes are exactly the base's without its newest section, returns to the version below it."""
    headings = [line for line in head_notes.splitlines() if line.startswith('# ')]
    pending = [heading for heading in headings if NEXT_HEADING.match(heading)]
    numbered = ['.'.join(match.groups()) for heading in headings if (match := NUMBERED_HEADING.match(heading))]
    used = {'.'.join(match.groups()) for line in base_notes.splitlines() if (match := NUMBERED_HEADING.match(line))}
    if len(pending) > 1 or (pending and not NEXT_HEADING.match(headings[0])):
        return 'the "# Private Flow next" section must be the one topmost section'
    if not numbered or numbered[0] != head_version:
        return f'the topmost numbered heading ({numbered[0] if numbered else "none"}) is not the project version {head_version}'
    parse = lambda version: tuple(int(part) for part in version.split('.'))
    base, head = parse(base_version), parse(head_version)
    if not app_changed:
        if head != base:
            return f'only a change to the shipped app takes a version number; the version must stay {base_version}'
        if pending:
            return 'a "# Private Flow next" section without a change to the shipped app; internal notes go to their own file'
        return None
    if head_notes == without_newest_section(base_notes):
        return None
    if pending:
        if on_main:
            return 'a change to the shipped app reached main unnumbered: run release.py assign-version before merging'
        return None if head == base else f'an unnumbered change keeps the version {base_version}'
    expected = base[:2] + (base[2] + 1,)
    if head != expected or head_version in used:
        return f'a change to the shipped app takes {".".join(map(str, expected))}, the version after {base_version}, not {head_version}'
    return None


def file_sha256(path):
    digest = hashlib.sha256()
    with open(path, 'rb') as file:
        for block in iter(lambda: file.read(1 << 20), b''):
            digest.update(block)
    return digest.hexdigest()


def checksum_text(paths):
    """sha256sum format: one '<hash>  <file name>' line per file, sorted by name."""
    names = [Path(path).name for path in paths]
    if len({name.lower() for name in names}) != len(names):
        raise ValueError('Two files share a name.')
    return ''.join(f'{file_sha256(path)}  {Path(path).name}\n' for path in sorted(paths, key=lambda path: Path(path).name))


def parse_checksums(text):
    """Strict parse of a SHA256SUMS file into {name: hash}. Lines end with LF or CRLF; blank lines are skipped;
    a malformed line, a name repeated in any letter case or a file over 64 KiB is an error."""
    if len(text.encode('utf-8')) > MAXIMUM_CHECKSUM_BYTES:
        raise ValueError('The checksum file is larger than 64 KiB.')
    listed, seen = {}, set()
    for line in text.split('\n'):
        line = line[:-1] if line.endswith('\r') else line
        if not line:
            continue
        match = CHECKSUM_LINE.fullmatch(line)
        if not match or match.group(2).lower() in seen:
            raise ValueError(f'Malformed or repeated checksum line: {line!r}')
        seen.add(match.group(2).lower())
        listed[match.group(2)] = match.group(1)
    if not listed:
        raise ValueError('The checksum file lists no files.')
    return listed


def verify_checksums(text, paths):
    listed = parse_checksums(text)
    for path in paths:
        name = Path(path).name
        if listed.get(name) != file_sha256(path):
            raise ValueError(f'{name} does not match its SHA-256 in the checksum file.')


def inventory_text(app, version):
    """A PowerShell file holding only comments: the path, size and SHA-256 of every file of the app folder except
    itself. Signed like the scripts, it lets install.ps1 authenticate the whole package, third-party files included."""
    app = Path(app)
    files = []
    for path in sorted(app.rglob('*'), key=lambda path: path.relative_to(app).as_posix()):
        relative = path.relative_to(app).as_posix()
        if path.is_symlink():
            raise ValueError(f'{relative} is a link; the package must hold only plain files.')
        if path.is_dir() or relative == INVENTORY_NAME:
            continue
        if not INVENTORY_PATH.fullmatch(relative) or '..' in relative.split('/'):
            raise ValueError(f'{relative} has a name the inventory does not allow.')
        files.append({'path': relative, 'size': path.stat().st_size, 'sha256': file_sha256(path)})
    if not any(file['path'] == 'PrivateFlow.exe' for file in files):
        raise ValueError('PrivateFlow.exe is missing from the package.')
    if len({file['path'].lower() for file in files}) != len(files):
        raise ValueError('Two package files differ only in letter case.')
    data = json.dumps({'schema': 1, 'project': 'private-flow', 'version': version, 'files': files}, separators=(',', ':'), ensure_ascii=True)
    return ('# Private Flow package inventory: the path, size and SHA-256 of every other file in this package.\r\n'
            '# Data only. install.ps1 trusts it only with a valid signature of the expected publisher.\r\n'
            '<#\r\n' + data + '\r\n#>\r\n')


def private_repositories(remote_urls, environment_repository):
    """The owner/name of every GitHub repository of the checkout's remotes and of GITHUB_REPOSITORY, except the public one."""
    found = [match.group(1) for url in remote_urls if (match := GITHUB_REPOSITORY_URL.search(url.strip()))]
    if environment_repository:
        found.append(environment_repository.strip())
    return sorted({repository for repository in found if repository and repository.lower() != PUBLIC_REPOSITORY.lower()}, key=str.lower)


def build_identifiers(checkout, home, repositories=(), commit=None):
    """What Private Flow's own binaries must not contain: the folder they were built in, the builder's home folder
    (which names the user), Source Link URLs, the development repositories and, given for a release build, its commit."""
    identifiers = ['raw.githubusercontent.com/', *repositories]
    if commit:
        identifiers.append(commit)
    for folder in (str(checkout), str(home)):
        if len(folder.strip('/\\')) >= 4:
            identifiers += [folder, folder.replace('\\', '/'), folder.replace('/', '\\')]
    return sorted({identifier.lower() for identifier in identifiers})


def git_output(*arguments):
    result = subprocess.run(['git', *arguments], capture_output=True, text=True, encoding='utf-8')
    return result.stdout if result.returncode == 0 else ''


def identifiers_in(data, identifiers):
    """The identifiers found in bytes, as ASCII or UTF-8 and as UTF-16LE, ignoring letter case."""
    lowered = data.lower()
    return [identifier for identifier in identifiers
            if identifier.encode('utf-8') in lowered or identifier.encode('utf-16-le') in lowered]


def symbol_problems(app, identifiers):
    app = Path(app)
    own = [path for pattern in ('PrivateFlow*.exe', 'PrivateFlow*.dll', 'PrivateFlow*.pdb') for path in sorted(app.glob(pattern))]
    if not any(path.suffix == '.pdb' for path in own) or not any(path.suffix == '.dll' for path in own):
        return ['no PrivateFlow DLL or PDB to check']
    return [f'{path.name} contains {found}' for path in own for found in identifiers_in(path.read_bytes(), identifiers)]


def release_plan(existing, local):
    """What create-release does with the release a previous run may have left: 'create', 'resume' a draft,
    or 'done' when a published release already has exactly these files. A published release with other files is refused."""
    if existing is None:
        return 'create'
    if existing['draft']:
        return 'resume'
    remote = {asset['name']: asset for asset in existing['assets']}
    if set(remote) == set(local) and all(remote[name]['size'] == size and remote[name].get('digest') == f'sha256:{sha256}'
                                         for name, (size, sha256) in local.items()):
        return 'done'
    raise ValueError(f'The published release of {existing["tag_name"]} has other files; it is never replaced. Check it by hand.')


def project_version():
    version = ElementTree.parse('src/PrivateFlow.Windows/PrivateFlow.Windows.csproj').findtext('.//Version')
    if not version:
        raise ValueError('Version missing from PrivateFlow.Windows.csproj')
    return version


def github(method, url, token, body=None, content_type='application/json'):
    request = urllib.request.Request(url, data=body, method=method, headers={
        'Authorization': f'Bearer {token}', 'Accept': 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'private-flow-release', 'Content-Type': content_type})
    with urllib.request.urlopen(request, timeout=600) as response:
        text = response.read()
    return json.loads(text) if text else None


def create_release(tag, notes_path, assets, draft=False):
    """Creates the release of an existing tag as a draft, uploads the files and publishes it, or with draft leaves it a draft for the
    maintainer's checks (publish-public.ps1 -Finish). A rerun after a partial run finishes the draft it left; a rerun after success
    changes nothing."""
    repository, token = os.environ['GITHUB_REPOSITORY'], os.environ['GITHUB_TOKEN']
    version, prerelease = tag_version(tag)
    api = f'https://api.github.com/repos/{repository}'
    local = {Path(asset).name: (Path(asset).stat().st_size, file_sha256(asset)) for asset in assets}
    existing, page = None, 1
    while existing is None:
        releases = github('GET', f'{api}/releases?per_page=100&page={page}', token)
        if not releases:
            break
        existing = next((release for release in releases if release['tag_name'] == tag), None)
        page += 1
    plan = release_plan(existing, local)
    if plan == 'done':
        print(f'The release of {tag} is already complete in {repository}: {existing["html_url"]}')
        return
    details = {'tag_name': tag, 'name': f'Private Flow {version}' + (' (pre-release)' if prerelease else ''),
               'body': Path(notes_path).read_text(encoding='utf-8'), 'prerelease': prerelease}
    if plan == 'create':
        release = github('POST', f'{api}/releases', token, json.dumps({**details, 'draft': True}).encode())
    else:
        release = existing
        print(f'Completing the draft release of {tag} that an earlier run left.')
        for asset in release['assets']:
            github('DELETE', f'{api}/releases/assets/{asset["id"]}', token)
    for asset in assets:
        name = urllib.parse.quote(Path(asset).name)
        github('POST', f'https://uploads.github.com/repos/{repository}/releases/{release["id"]}/assets?name={name}',
               token, Path(asset).read_bytes(), 'application/octet-stream')
    if draft:
        print(f'The draft release of {tag} in {repository} holds its files; publish-public.ps1 -Finish checks and publishes it.')
        return
    github('PATCH', f'{api}/releases/{release["id"]}', token, json.dumps({**details, 'draft': False}).encode())
    print(f'Released {tag} in {repository}: {release["html_url"]}')


def main(arguments):
    command, parameters = arguments[0], arguments[1:]
    if command == 'check-tag':
        version, prerelease = tag_version(parameters[0])
        if version != project_version():
            raise ValueError(f'Tag {parameters[0]} names {version}, but the project version is {project_version()}.')
        if unnumbered_changes(Path('docs/RELEASE_NOTES.md').read_text(encoding='utf-8')):
            raise ValueError('docs/RELEASE_NOTES.md has a "# Private Flow next" section: number it (release.py assign-version) before a release.')
        print(f'Tag {parameters[0]}: version {version}, ' + ('pre-release' if prerelease else 'release') + '.')
    elif command == 'check-version':
        on_main = '--main' in parameters
        base = next((parameter for parameter in parameters if parameter != '--main'), 'HEAD^1')
        if not git_output('rev-parse', '--verify', '--quiet', f'{base}^{{commit}}').strip():
            raise ValueError(f'The base {base} is not in this checkout, so the version cannot be checked against it (fetch the whole history).')
        project_path, notes_path = 'src/PrivateFlow.Windows/PrivateFlow.Windows.csproj', 'docs/RELEASE_NOTES.md'
        def version_at(revision):
            match = re.search(r'<Version>([^<]+)</Version>', git_output('show', f'{revision}:{project_path}'))
            if not match:
                raise ValueError(f'{project_path} at {revision} names no version.')
            return match.group(1)
        changed = [path for path in git_output('diff', '--name-only', '-z', base, 'HEAD').split('\0') if path]
        app_changed = changes_shipped_app(changed)
        problem = version_problem(version_at(base), git_output('show', f'{base}:{notes_path}'), version_at('HEAD'), git_output('show', f'HEAD:{notes_path}'),
                                  app_changed, on_main)
        if problem:
            raise ValueError(f'Version check against {base}: {problem}.')
        print(f'Version {version_at("HEAD")} follows {base} ({"a change to the shipped app" if app_changed else "no change to the shipped app"}).')
    elif command == 'notes':
        Path(parameters[1]).write_text(release_notes(Path('docs/RELEASE_NOTES.md').read_text(encoding='utf-8'), parameters[0]), encoding='utf-8')
    elif command == 'assign-version':
        notes_path, project_path = Path('docs/RELEASE_NOTES.md'), Path('src/PrivateFlow.Windows/PrivateFlow.Windows.csproj')
        notes, project, version = assign_version(notes_path.read_text(encoding='utf-8'), project_path.read_text(encoding='utf-8'))
        notes_path.write_text(notes, encoding='utf-8', newline='')
        project_path.write_text(project, encoding='utf-8', newline='')
        print(f'Numbered the next section and the project version {version}.')
    elif command == 'inventory':
        target = Path(parameters[0], INVENTORY_NAME)
        target.write_bytes(inventory_text(parameters[0], parameters[1] if len(parameters) > 1 else project_version()).encode('ascii'))
        print(f'Wrote {INVENTORY_NAME}.')
    elif command == 'check-symbols':
        remotes = git_output('config', '--get-regexp', r'^remote\..*\.url$').splitlines()
        repositories = private_repositories([line.split(' ', 1)[-1] for line in remotes], os.environ.get('GITHUB_REPOSITORY'))
        # A release build is public (Directory.Build.props): it names no commit of the development repository.
        commit = git_output('rev-parse', 'HEAD').strip() if os.environ.get('GITHUB_REF_TYPE') == 'tag' else None
        problems = symbol_problems(parameters[0], build_identifiers(Path.cwd().resolve(), Path.home(), repositories, commit))
        if problems:
            raise ValueError('Private build detail in the binaries: ' + '; '.join(problems) + '. Check Directory.Build.props and the sources for a repository name.')
        print('The own binaries name no checkout path, home folder, development repository or Source Link URL' + (' and no commit.' if commit else '.'))
    elif command == 'checksums':
        Path(parameters[0]).write_text(checksum_text(parameters[1:]), encoding='ascii', newline='\n')
    elif command == 'verify-checksums':
        verify_checksums(Path(parameters[0]).read_text(encoding='ascii'), parameters[1:])
        print('Checksums match.')
    elif command == 'create-release':
        draft = '--draft' in parameters
        parameters = [parameter for parameter in parameters if parameter != '--draft']
        create_release(parameters[0], parameters[1], parameters[2:], draft)
    else:
        raise ValueError(f'Unknown command {command!r}.')


if __name__ == '__main__':
    try:
        main(sys.argv[1:])
    except (ValueError, IndexError, OSError, KeyError) as error:
        print(f'::error::{error}', file=sys.stderr)
        sys.exit(1)
