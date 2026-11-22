# Private Flow

Windows only, published as is. These rules hold for every change.

## Privacy

- Audio and speech recognition stay on the PC. No telemetry and no silent cloud fallback.
- Only two opt-in features send text, never audio, to the OpenAI-compatible API the user configured: the Concise output style, which is off unless selected and falls back to the on-device Clean style on failure, and model-written meeting notes, which send the transcript of one meeting only when the user presses the button for that meeting and fall back to the extractive notes, with the reason shown, on failure. Notes written by the local model send nothing anywhere.
- Do not commit audio, model weights, settings, transcripts, credentials or generated packages.

## Insertion safety

- Session IDs reject stale results. The paste goes only to the window selected at recording start; password fields and Private Flow itself never receive a paste; held modifiers block insertion; no Enter key is sent.
- Insertion pastes into any other field through the clipboard and Ctrl+V, keeps the text out of clipboard history and restores the previous clipboard. When it cannot paste, it leaves the text on the clipboard and shows a notice instead of opening the window.
- Automatic insertion is on by default: the old default is migrated once, and a later explicit opt-out is kept.
- The shortcut toggles recording by default. Hold to talk is opt-in: it records while the shortcut is held, discards a tap under about 0.3 s and waits, bounded, for the held modifiers to be released before the modifier rule applies.
- The optional meeting shortcut, not set by default, starts and stops meeting capture without showing the window; the meeting pill and the tray's recording state show the capture for as long as it runs.
- A Local polish dictation that arrives while meeting notes are written on the PC waits for them, at most for the time set by the user (two minutes by default), and then uses Clean; after such a wait the text is pasted only if its window is still in front by itself, and otherwise the notice is shown and the clipboard is left untouched.

## Tests

- On a PC someone uses, tests never use the microphone or send keyboard input to existing apps without an explicit user gesture. Use synthetic or library audio and isolated test fixtures.
- Simulated keys (`SendInput`), a virtual audio cable and pastes into real apps run only on a disposable Windows test machine that is reset for every run.
- Keeping dictations as test clips is off unless the user turns it on. The clips stay outside the repository, are never used on CI and are kept private like recorded meetings: reports and logs give counts only, never transcript text.
- Every test runs independently, with its own data folder, temporary folder, ports and fixture windows, and never reads another test's output, so tests run in parallel. Tests that use a resource the whole desktop shares (the foreground, the clipboard, simulated input or the virtual audio cable) run one per Windows session, and GPU tests only as many as fit in GPU memory.
- `scripts/build.ps1 -Portable` builds the package with its safety checks, and `scripts/smoke-test.ps1` checks the real engine on synthetic speech after setup. Report unverified behaviour plainly.
- Keep runtime dependencies and model downloads pinned and hash-verified.
