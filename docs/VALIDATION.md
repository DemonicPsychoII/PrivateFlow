# Validation

How Private Flow is verified, what is not verified and why, and the figures measured on synthetic and invented test material. [TEST_COVERAGE.md](TEST_COVERAGE.md) says which check covers which feature.

## How Private Flow is verified

| Level | Command | What it runs |
| --- | --- | --- |
| Build checks | `scripts/build.ps1 -Portable` (Windows), `scripts/build.sh` (Linux, cross-build) | Release build with warnings as errors, the pure checks of `tests/PrivateFlow.Tests`, the self-contained package; `-Portable` also builds the verification host beside the package and checks that the package holds none of it |
| One entry point | `scripts/verify.ps1 -Tier pure` | every pure check group as its own job and process, in parallel, with one result line |
| Workspace verification | `scripts/verify-workspace.ps1 -Name <name>` | the installed speech engine on synthetic audio and synthetic meeting sources, stand-ins for the setup scripts and audio devices, screenshots of every page, in an isolated data folder |
| UI smoke test | `scripts/ui-smoke-test.ps1` | the window driven through UI Automation patterns in a temporary data folder, no keys |
| Real-engine smoke test | `scripts/smoke-test.ps1` | the installed engine transcribes generated speech in an isolated data folder; the negation and the number survive |
| Full evaluation | `scripts/full-eval.ps1` | the dictation and meeting evaluations on generated speech and public speech corpora, per engine and model |

The diagnostic and evaluation modes are in the verification host `PrivateFlow.Verification.exe`, never in the package, and run only with a `PRIVATE_FLOW_DATA_DIR` outside the app's own data folder. The paste check and every check that sends simulated keys or writes the clipboard run only on a disposable Windows test machine. Tests never use a microphone and never send input to other apps.

## Not verified, and why

- Accuracy on real microphone speech in real rooms, and microphone reconnects: tests use synthetic and library audio only.
- Real meetings, real call audio and the echo rule on loudspeakers: the evaluation uses generated voices and read speech, which are cleaner than conversation; two people on the system channel are not separated.
- Paste into Office, elevated apps and remote desktops, and clipboard managers that ignore the Windows exclusion formats.
- Concise and model-written notes against a real API in the checks: the checks use a loopback stub and never call an API.
- Real downloads through the setup scripts and the installer end to end: tests do not download.
- Windows versions and machines other than the test machines, Smart App Control on unsigned files, and a screen reader announcing errors.
- Real public releases and an update from one.

## Measured figures

Measured on synthetic speech and invented meetings, which are cleaner than real use; timings are single samples, not latency distributions.

- Meetings with the recommended GPU setup (large-v3-turbo for dictation and the preview, large-v3 for the final pass): on public read speech (FLEURS) 97.5 % of words and 94 % of critical items such as times, numbers, names and negations; every time and number the recognizer wrote is found by search at its row.
- Notes by rules on invented meetings: action items and times are usually right, while most decisions and open questions are missed; the notes say so.
- Notes by the local meeting notes model (Qwen3.5-4B): on invented meetings of 15 to 60 minutes, notes in 19 to 29 s on the GPU, every decision and 95 % of action items found, about one action item in five with a wrong or missing due date; on the processor a 50-minute meeting took about 3 minutes. No local model is recommended below 8 GB of memory.
- Local polish: on 36 synthetic dictations in English, German and Spanish, 33 polished and the rest fell back to Clean; on the processor a typical polish takes 1 to 3 s.
- Dictation: a warm model turns a dictation into text in about 0.5 to 0.9 s after stop on the GPU.
- Sizes: large-v3-turbo-q8_0 874 MB, medium about 1.5 GB, base about 148 MB, large-v3 3.1 GB; the Local polish model about 2.5 GB, the meeting notes model about 2.7 GB. Every download is pinned with its SHA-256 in `scripts/models.json`.
