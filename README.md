# Private Flow

Voice dictation and meeting transcription for Windows that run on your own PC.
Audio never leaves it.

Published as is, without a support or maintenance promise.

## What it does

- **Dictation:** press **Ctrl + Alt + Space**, speak, press again. The text is
  pasted into the window you started in. The shortcut and hold-to-talk are
  configurable.
- **Text styles:** Exact, Clean (removes fillers and applies spoken corrections
  such as "no wait"), Local polish (a local language model), and Concise through
  an OpenAI-compatible API you configure yourself.
- **Meetings:** records microphone and system audio, transcribes when the
  meeting ends, and makes it searchable, with notes that link to the transcript.
  Exports text, subtitles or Markdown.
- **Languages:** English, German and Spanish.
- **Vocabulary, snippets and per-app profiles.**

## Requirements

- Windows 10 or 11, x64.
- The Microsoft Visual C++ Redistributable 2015-2022 (x64). Most PCs have it;
  a fresh Windows does not. Without it speech recognition cannot load, and the
  installer and the app say so. Install it from Microsoft:
  https://aka.ms/vs/17/release/vc_redist.x64.exe
- An NVIDIA GPU is optional. With one, larger and faster models are used;
  without one, speech recognition runs on the processor.
- Disk space: about 6 GB with an NVIDIA GPU, about 0.4 GB without. The optional
  local language model for meeting notes and Local polish needs 2.5 to 4.5 GB more.

## Install

1. Download `PrivateFlow-win-x64.zip` from
   [Releases](https://github.com/DemonicPsychoII/PrivateFlow/releases) and extract it.
   To check the download, compare `Get-FileHash .\PrivateFlow-win-x64.zip` with the
   release's `SHA256SUMS`, which `SHA256SUMS.sig` signs (see Code signing policy).
2. In the extracted folder, run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Autostart
   ```

The installer downloads the speech models and verifies each one. Private Flow
then runs in the tray; double-click its icon to open it. New releases are
offered in the app.

Updates check the release's signature before running any of it: the
installed copy refuses a release whose `SHA256SUMS` is not signed by the
Private Flow release key, or whose package differs from it. The installer
you start by hand cannot vouch for itself, so check the download first.

To uninstall:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\PrivateFlow\scripts\uninstall.ps1"
```

This also deletes saved meetings, so export what you want to keep first. It
also deletes the dictations you kept as test clips, in
`%USERPROFILE%\PrivateFlowEvaluation\dictation-clips`, and lists them first.

## Smart App Control

Private Flow's own files are not Authenticode-signed yet. With Smart App
Control on, Windows can block the app or its speech runtime until Microsoft's
reputation service rates the files. Blocks are listed in Event Viewer under
CodeIntegrity, Operational (event 3077). Switching Smart App Control off
avoids this.

## Code signing policy

- Releases are built from this repository's sources by its release workflow
  on GitHub's runners, never on a maintainer's PC.
- Every release signs its `SHA256SUMS` with the Private Flow release key
  (ECDSA P-256); `scripts/install.ps1` holds the public key and refuses an
  update without a valid signature. Check one by hand with
  `python3 scripts/sign-checksums.py verify SHA256SUMS SHA256SUMS.sig <key>`,
  the key being `$taskReleaseKey` in `scripts/install.ps1`.
- Authenticode signing of Private Flow's own files through the SignPath
  Foundation's open-source programme is applied for. Once granted: free code
  signing provided by [SignPath.io](https://about.signpath.io), certificate by
  [SignPath Foundation](https://signpath.org).
- Roles: changes are written by the maintainer and by AI coding agents on the
  maintainer's behalf and reviewed before they are merged. The repository
  owner, DemonicPsychoII, publishes every release and approves each signing
  request by hand.
- Privacy: Private Flow transfers no information to other networked systems
  unless you configure it to, as described under Privacy below.

## Privacy

- Speech recognition runs locally. There is no telemetry.
- Text, never audio, goes to an API only if you configure one and choose
  Concise or API meeting notes.
- Pasting skips password fields, and pasted text is kept out of the Windows
  clipboard history.

## Build from source

With the .NET 10 SDK:

```powershell
.\scripts\build.ps1 -Portable
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Autostart
```

## More

- [User guide](https://github.com/DemonicPsychoII/PrivateFlow/blob/main/docs/GUIDE.md): every feature and setting in detail.
- [Validation](https://github.com/DemonicPsychoII/PrivateFlow/blob/main/docs/VALIDATION.md): what is tested and the measured accuracy.
- [MIT licence](LICENSE) and [third-party notices](THIRD_PARTY_NOTICES.md).
