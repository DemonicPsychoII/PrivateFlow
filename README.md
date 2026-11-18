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
- An NVIDIA GPU is optional. With one, larger and faster models are used;
  without one, speech recognition runs on the processor.
- A few GB of disk space for the models.

## Install

1. Download `PrivateFlow-win-x64.zip` from
   [Releases](https://github.com/DemonicPsychoII/PrivateFlow/releases) and extract it.
2. In the extracted folder, run:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Autostart
   ```

The installer downloads the speech models and verifies each one. Private Flow
then runs in the tray; double-click its icon to open it. New releases are
offered in the app.

To uninstall:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\PrivateFlow\scripts\uninstall.ps1"
```

This also deletes saved meetings, so export what you want to keep first.

## Smart App Control

Private Flow is not code-signed. With Smart App Control on, Windows can block
the app or its speech runtime until Microsoft's reputation service rates the
files. Blocks are listed in Event Viewer under CodeIntegrity, Operational
(event 3077). Switching Smart App Control off avoids this.

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
