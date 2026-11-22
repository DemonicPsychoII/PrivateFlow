"""Pure checks of the release helpers: tags, release notes, checksums. Run: python3 -I scripts/release_test.py"""
from pathlib import Path
import base64
import hashlib
import importlib
import json
import re
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
import release  # noqa: E402

NOTES = """# Private Flow 0.5.10 (beta): later (2026-11-20)

- Ten.
# Private Flow 0.5.6 — signing (2026-11-19)

- First line.

  Continued.
# Private Flow 0.5.5 — earlier (2026-11-18)

- Five.
"""


class ReleaseChecks(unittest.TestCase):
    def test_tags(self):
        self.assertEqual(release.tag_version('v0.5.6'), ('0.5.6', False))
        self.assertEqual(release.tag_version('v1.0.0-rc.1'), ('1.0.0', True))
        for invalid in ('0.5.6', 'v0.5', 'v0.5.6.1', 'v0.5.6-', 'v0.5.6-rc/1', 'v0.5.6 ', 'refs/tags/v0.5.6'):
            with self.assertRaises(ValueError, msg=invalid):
                release.tag_version(invalid)

    def test_release_notes_take_one_section(self):
        self.assertEqual(release.release_notes(NOTES, '0.5.6'), '- First line.\n\n  Continued.\n')
        self.assertEqual(release.release_notes(NOTES, '0.5.5'), '- Five.\n')
        self.assertEqual(release.release_notes(NOTES, '0.5.10'), '- Ten.\n')

    def test_release_notes_need_their_own_heading(self):
        with self.assertRaises(ValueError):
            release.release_notes(NOTES, '0.5.1')
        with self.assertRaises(ValueError):
            release.release_notes(NOTES, '0.5.7')
        with self.assertRaises(ValueError):
            release.release_notes('# Private Flow 0.5.6 — empty\n\n# Private Flow 0.5.5\n- x\n', '0.5.6')

    def test_release_notes_refuse_pull_request_references(self):
        for reference in ('- Fixed (#48).', '- History #53: x', '- See #7'):
            with self.assertRaises(ValueError, msg=reference):
                release.release_notes(f'# Private Flow 0.5.5\n\n{reference}\n', '0.5.5')
        self.assertEqual(release.release_notes('# Private Flow 0.5.5\n\n- C# 12, colour #1a2b3c, [link](#section), &#38;.\n', '0.5.5'),
                         '- C# 12, colour #1a2b3c, [link](#section), &#38;.\n')

    def test_next_section_is_numbered_at_merge_time(self):
        project = '<Project><PropertyGroup><Version>0.5.19</Version></PropertyGroup></Project>\n'
        notes = '# Private Flow next (beta): safer exports (2026-11-20)\n\n- New.\n\n# Private Flow 0.5.19 (beta): older\n\n- Old.\n'
        numbered, changed, version = release.assign_version(notes, project)
        self.assertEqual((version, changed), ('0.5.20', project.replace('0.5.19', '0.5.20')))
        self.assertEqual(numbered, notes.replace('# Private Flow next', '# Private Flow 0.5.20'))
        self.assertTrue(release.unnumbered_changes(notes))
        self.assertFalse(release.unnumbered_changes(numbered))
        for invalid_notes, invalid_project in ((numbered, project), (notes, project.replace('0.5.19', '0.5.18')),
                                               ('# Private Flow 0.5.19\n\n# Private Flow next\n', project), (notes + '# Private Flow next\n', project),
                                               ('# Private Flow next\n\n- Only.\n', project), (notes, '<Project/>')):
            with self.assertRaises(ValueError):
                release.assign_version(invalid_notes, invalid_project)
        # The next section is no version: its notes are not those of the version below it.
        self.assertEqual(release.release_notes(notes, '0.5.19'), '- Old.\n')

    def test_versions_follow_their_base(self):
        base = '# Private Flow 0.5.19 (beta): b\n\n- Old.\n\n# Private Flow 0.5.18\n\n- Older.\n'
        numbered = '# Private Flow 0.5.20 (beta): n\n\n- New.\n\n' + base
        pending = '# Private Flow next (beta): n\n\n- New.\n\n' + base
        problem = release.version_problem
        # A change to the shipped app: numbered exactly one above its base, or, on a pull request only, still unnumbered.
        self.assertIsNone(problem('0.5.19', base, '0.5.20', numbered, True, False))
        self.assertIsNone(problem('0.5.19', base, '0.5.20', numbered, True, True))
        self.assertIsNone(problem('0.5.19', base, '0.5.19', pending, True, False))
        self.assertIsNotNone(problem('0.5.19', base, '0.5.19', pending, True, True), 'unnumbered on main')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.19', base, True, True), 'an app change under a number already used')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.19', base, True, False), 'an app change without notes')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.21', numbered.replace('0.5.20', '0.5.21'), True, False), 'a skipped number')
        self.assertIsNotNone(problem('0.5.20', numbered, '0.5.20', '# Private Flow 0.5.20 (beta): other\n\n- Other.\n\n' + numbered, True, True), 'a number used twice')
        self.assertIsNotNone(problem('0.5.20', base.replace('0.5.18', '0.5.21'), '0.5.21', '# Private Flow 0.5.21\n\n- x\n\n' + base, True, False), 'a number the base already holds')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.20', pending, True, False), 'a raised version under a next section')
        # A revert of the newest numbered change returns to the version below it, with exactly the notes before it.
        self.assertIsNone(problem('0.5.20', numbered, '0.5.19', base, True, False))
        self.assertIsNone(problem('0.5.20', numbered, '0.5.19', base, True, True))
        self.assertIsNotNone(problem('0.5.20', numbered, '0.5.19', base.replace('- Old.', '- Changed.'), True, True), 'a revert that also edits older notes')
        self.assertIsNotNone(problem('0.5.20', numbered, '0.5.18', '# Private Flow 0.5.18\n\n- Older.\n', True, True), 'removing more than the newest section')
        # Anything else keeps the version and adds no section.
        self.assertIsNone(problem('0.5.19', base, '0.5.19', base, False, True))
        self.assertIsNotNone(problem('0.5.19', base, '0.5.20', numbered, False, False), 'a number without an app change')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.19', pending, False, False), 'a next section without an app change')
        self.assertIsNotNone(problem('0.5.20', numbered, '0.5.19', base, False, True), 'a lower version')
        self.assertIsNotNone(problem('0.5.19', base, '0.5.19', base + '# Private Flow next\n', True, False), 'a next section below a version')
        self.assertTrue(release.changes_shipped_app(['src/PrivateFlow.Windows/UpdateCheck.cs']))
        self.assertTrue(release.changes_shipped_app(['docs/GUIDE.md', 'scripts/install.ps1']))
        self.assertFalse(release.changes_shipped_app(['docs/GUIDE.md', 'tests/PrivateFlow.Tests/Program.cs', 'src/PrivateFlow.Verification/Program.cs', 'scripts/verify.ps1']))

    def test_release_signature_round_trip(self):
        sign_checksums = importlib.import_module('sign-checksums')
        with tempfile.TemporaryDirectory() as folder:
            key = Path(folder, 'key.pem')
            subprocess.run(['openssl', 'genpkey', '-algorithm', 'EC', '-pkeyopt', 'ec_paramgen_curve:P-256', '-out', str(key)], check=True, capture_output=True)
            point = subprocess.run(['openssl', 'pkey', '-in', str(key), '-pubout', '-outform', 'DER'], check=True, capture_output=True).stdout[-64:]
            public = base64.b64encode(point).decode('ascii')
            checksums, signature = Path(folder, 'SHA256SUMS'), Path(folder, 'SHA256SUMS.sig')
            checksums.write_bytes(b'a' * 64 + b'  PrivateFlow-win-x64.zip\n')
            sign_checksums.sign(checksums, signature, key.read_text(encoding='ascii'))
            text = signature.read_text(encoding='ascii')
            self.assertTrue(text.endswith('\n') and len(base64.b64decode(text.strip())) == 64, 'r and s, 32 bytes each, in Base64')
            self.assertTrue(sign_checksums.verify(checksums, signature, public))
            checksums.write_bytes(b'b' * 64 + b'  PrivateFlow-win-x64.zip\n')
            self.assertFalse(sign_checksums.verify(checksums, signature, public), 'a changed checksum file')
        raw = bytes(range(1, 65))
        self.assertEqual(sign_checksums.der_to_raw(sign_checksums.raw_to_der(raw)), raw)
        high = b'\x80' + bytes(31) + b'\x00' * 31 + b'\x01'
        self.assertEqual(sign_checksums.der_to_raw(sign_checksums.raw_to_der(high)), high, 'a leading high bit and a short integer')
        for malformed in (b'', b'\x30\x02\x02\x00', sign_checksums.raw_to_der(raw) + b'\x00', b'\x31' + sign_checksums.raw_to_der(raw)[1:]):
            with self.assertRaises(ValueError):
                sign_checksums.der_to_raw(malformed)

    def test_shipped_paths_cover_what_the_build_copies(self):
        # Every file build.sh and build.ps1 copy into the package is a path of the shipped app.
        root = Path(__file__).resolve().parent.parent
        build_sh = (root / 'scripts' / 'build.sh').read_text(encoding='utf-8')
        build_ps1 = (root / 'scripts' / 'build.ps1').read_text(encoding='utf-8')
        copied = ['README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md']
        copied += ['scripts/' + name for line in build_sh.splitlines() if line.startswith('for script in') for name in re.findall(r"'([^']+)'", line)]
        copied += [name for line in build_ps1.splitlines() if '-Destination artifacts/app/scripts' in line for name in re.findall(r'scripts/[\w.-]+', line)]
        self.assertGreaterEqual(len(copied), 15)
        for path in copied:
            self.assertTrue(release.changes_shipped_app([path]), f'the build copies {path} into the package, which SHIPPED_PATHS leaves out')

    def test_repository_notes_have_the_project_version(self):
        root = Path(__file__).resolve().parent.parent
        version = release.ElementTree.parse(root / 'src/PrivateFlow.Windows/PrivateFlow.Windows.csproj').findtext('.//Version')
        self.assertTrue(release.release_notes((root / 'docs/RELEASE_NOTES.md').read_text(encoding='utf-8'), version).strip())

    def test_checksums_round_trip_and_reject_changes(self):
        with tempfile.TemporaryDirectory() as folder:
            package, manifest = Path(folder, 'PrivateFlow-win-x64.zip'), Path(folder, 'manifest.json')
            package.write_bytes(b'package'); manifest.write_bytes(b'{}')
            text = release.checksum_text([package, manifest])
            self.assertEqual(text, f'{hashlib.sha256(b"package").hexdigest()}  PrivateFlow-win-x64.zip\n{hashlib.sha256(b"{}").hexdigest()}  manifest.json\n')
            release.verify_checksums(text, [package, manifest])
            package.write_bytes(b'changed')
            with self.assertRaises(ValueError):
                release.verify_checksums(text, [package])
            unlisted = Path(folder, 'unlisted.zip')
            unlisted.write_bytes(b'')
            with self.assertRaises(ValueError):
                release.verify_checksums(text, [unlisted])
            with self.assertRaises(ValueError):
                release.checksum_text([package, package])

    def test_checksum_files_are_strict(self):
        valid = 'a' * 64 + '  PrivateFlow-win-x64.zip'
        self.assertEqual(release.parse_checksums(valid + '\n'), {'PrivateFlow-win-x64.zip': 'a' * 64})
        self.assertEqual(release.parse_checksums('\n' + valid + '\r\n\r\n'), {'PrivateFlow-win-x64.zip': 'a' * 64})
        # The same cases are refused by install.ps1 and ReleaseUpdate.ListedChecksum (tests/PrivateFlow.Tests/UpdateChecks.cs).
        for invalid in ('', '\n', 'A' * 64 + '  x.zip', 'a' * 63 + '  x.zip', 'a' * 64 + ' x.zip', 'a' * 64 + '  ../x.zip',
                        'a' * 64 + '  dir/x.zip', 'a' * 64 + '  dir\\x.zip', valid + '\n' + valid, valid + '\nnot a line',
                        valid + '\r\r\n', valid + ' \n', valid + '\n' + 'b' * 64 + '  privateflow-WIN-x64.zip',
                        valid + '\n' + ''.join(f'{"b" * 64}  f{index}.zip\n' for index in range(1000))):
            with self.assertRaises(ValueError, msg=invalid[:120]):
                release.parse_checksums(invalid)

    def test_inventory_lists_every_other_file(self):
        with tempfile.TemporaryDirectory() as folder:
            app = Path(folder)
            (app / 'scripts').mkdir(); (app / 'cs').mkdir()
            (app / 'PrivateFlow.exe').write_bytes(b'exe'); (app / 'scripts' / 'models.json').write_bytes(b'{}')
            (app / 'cs' / 'PresentationCore.resources.dll').write_bytes(b'')
            (app / 'scripts' / 'package-inventory.ps1').write_bytes(b'old inventory')
            text = release.inventory_text(app, '0.5.7')
            self.assertTrue(text.startswith('# Private Flow package inventory:') and '\r\n<#\r\n' in text and text.endswith('\r\n#>\r\n'))
            data = json.loads(text.split('\r\n')[3])
            self.assertEqual((data['schema'], data['project'], data['version']), (1, 'private-flow', '0.5.7'))
            self.assertEqual(data['files'], [
                {'path': 'PrivateFlow.exe', 'size': 3, 'sha256': hashlib.sha256(b'exe').hexdigest()},
                {'path': 'cs/PresentationCore.resources.dll', 'size': 0, 'sha256': hashlib.sha256(b'').hexdigest()},
                {'path': 'scripts/models.json', 'size': 2, 'sha256': hashlib.sha256(b'{}').hexdigest()}])
            self.assertTrue(text.isascii())
            (app / 'bad name.txt').write_bytes(b'')
            with self.assertRaises(ValueError):
                release.inventory_text(app, '0.5.7')
            (app / 'bad name.txt').unlink(); (app / 'PrivateFlow.exe').unlink()
            with self.assertRaises(ValueError):
                release.inventory_text(app, '0.5.7')

    def test_binaries_name_no_build_folder(self):
        repositories = release.private_repositories(['https://GitHub.COM/example-owner/internal-app.git', 'git' + '@' + 'github.com:Example-Owner/second.git',
                                                     'https://github.com/' + release.PUBLIC_REPOSITORY + '.git', 'https://example.org/other.git'], 'example-owner/internal-app')
        self.assertEqual(repositories, ['example-owner/internal-app', 'Example-Owner/second'])
        commit = 'c0ffee' * 6 + 'abcd'
        identifiers = release.build_identifiers(Path('/opt/runner/_work/internal-app/internal-app'), Path('/home/builder'), repositories, commit)
        self.assertIn('/home/builder', identifiers)
        self.assertIn('\\opt\\runner\\_work\\internal-app\\internal-app', identifiers)
        self.assertIn('raw.githubusercontent.com/', identifiers)
        self.assertEqual(release.build_identifiers(Path('/'), Path('/')), ['raw.githubusercontent.com/'])
        found = release.identifiers_in
        self.assertEqual(found(b'\0/HOME/Builder/src\0', identifiers), ['/home/builder'])
        self.assertEqual(found('C:\\'.encode() + '/home/builder/x'.encode('utf-16-le'), identifiers), ['/home/builder'])
        self.assertEqual(found(b'https://RAW.githubusercontent.com/x', identifiers), ['raw.githubusercontent.com/'])
        # The development repositories in ASCII and UTF-16LE, and the commit of a release build; the public repository passes.
        self.assertEqual(found(b'repos/EXAMPLE-OWNER/internal-app/compare', identifiers), ['example-owner/internal-app'])
        self.assertEqual(found('example-owner/second'.encode('utf-16-le'), identifiers), ['example-owner/second'])
        self.assertEqual(found(('0.5.20+' + commit.upper()).encode('utf-16-le'), identifiers), [commit])
        self.assertEqual(found(b'/_/src/PrivateFlow.Core/Notes.cs ' + release.PUBLIC_REPOSITORY.encode() + b' 0.5.20+release', identifiers), [])
        with tempfile.TemporaryDirectory() as folder:
            app = Path(folder)
            (app / 'PrivateFlow.dll').write_bytes(b'/_/src/App.cs')
            self.assertEqual(release.symbol_problems(app, identifiers), ['no PrivateFlow DLL or PDB to check'])
            (app / 'PrivateFlow.pdb').write_bytes('/home/builder/src/App.cs'.encode('utf-16-le'))
            (app / 'NAudio.dll').write_bytes(b'/home/builder')
            self.assertEqual(release.symbol_problems(app, identifiers), ['PrivateFlow.pdb contains /home/builder'])

    def test_release_reruns(self):
        local = {'PrivateFlow-win-x64.zip': (3, 'a' * 64), 'SHA256SUMS': (2, 'b' * 64)}
        def asset(name, size, sha256):
            return {'name': name, 'size': size, 'digest': f'sha256:{sha256}'}
        done = {'tag_name': 'v0.5.7', 'draft': False, 'assets': [asset('PrivateFlow-win-x64.zip', 3, 'a' * 64), asset('SHA256SUMS', 2, 'b' * 64)]}
        self.assertEqual(release.release_plan(None, local), 'create')
        self.assertEqual(release.release_plan({**done, 'draft': True, 'assets': done['assets'][:1]}, local), 'resume')
        self.assertEqual(release.release_plan(done, local), 'done')
        for other in (done['assets'][:1], [asset('PrivateFlow-win-x64.zip', 3, 'c' * 64), done['assets'][1]],
                      [{**done['assets'][0], 'digest': None}, done['assets'][1]], done['assets'] + [asset('extra', 1, 'd' * 64)]):
            with self.assertRaises(ValueError):
                release.release_plan({**done, 'assets': other}, local)


if __name__ == '__main__':
    unittest.main(verbosity=1)
