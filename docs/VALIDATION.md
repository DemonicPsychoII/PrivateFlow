# Validation

The state of verification for Private Flow 0.5.5 (2026-11-18). This file answers four questions: how the app is verified, what has been tried for real and what has not (with the reason), which steps the owner runs by hand, and which measured figures later work relies on. [TEST_COVERAGE.md](TEST_COVERAGE.md) is the table of which check covers which feature. Review rounds, superseded designs and the story of each change are in the pull requests and the git history; sections name the pull request where it helps.

## How Private Flow is verified

| Level | Command | What it runs | Last result |
| --- | --- | --- | --- |
| Build checks | `scripts/build.ps1 -Portable` (Windows); `scripts/build.sh` in the CI pool (Linux guest, cross-build) | Release build with warnings as errors, the pure checks of `tests/PrivateFlow.Tests`, self-contained publish and the ZIP package | 0.5.4: 0 warnings, 0 errors, "846 safety and preference checks passed in 48 groups" |
| Workspace verification | `scripts/verify-workspace.ps1 -Name <name>` | `--verify-workspace` of the packaged build in an isolated data folder under `artifacts\<name>`: the installed speech engine on synthetic audio and synthetic meeting sources, stand-ins for the setup scripts, the notes model and the audio devices, screenshots of every page | 0.5.4 (`simplify-dictation`): passed, 185 checks, on the second run of the final tree (the first stopped at the press-to-pill bound, see Known flaky checks) |
| UI smoke test | `scripts/ui-smoke-test.ps1 -App artifacts\app\PrivateFlow.exe` | `--diagnostic-ui` in a temporary data folder, driven through UI Automation patterns, no keys | 0.5.4: 13 PASS, 0 FAIL |
| Overlay check | `PrivateFlow.exe --verify-overlay <dir>` (isolated data folder only) | both pills with synthetic levels, the starting, recording and processing states by pixels, its own fixture window keeping the foreground | 0.5.4: PASS; not run for 0.5.1 to 0.5.3 |
| Insertion check | `PrivateFlow.exe --verify-insert <dir>` (isolated data folder only) | the real clipboard and Ctrl+V path into its own fixture process: a TextBox and a RichTextBox receive the text, a PasswordBox is refused, the previous clipboard comes back, a newer copy published during the paste is kept | last recorded pass 2026-10-19; not rerun since 0.5.2 made it require an isolated data folder |
| Real-engine smoke test | `scripts/smoke-test.ps1` | the installed engine transcribes generated speech in an isolated data root; the negation and the number survive; no temporary audio is left | passed in the full evaluation of 2026-11-16 |
| Uninstall suite | `scripts/uninstall-test.ps1` (Windows only, not in CI) | fake default installations under `%TEMP%` with a throwaway Start-menu path and HKCU key and compiled stand-in programs | 0.5.2: 29 of 29 in Windows PowerShell 5.1.26100 and in PowerShell 7.6.10 |
| Full evaluation | `scripts/full-eval.ps1 -Full -Backends cuda12` (local, about 100 minutes) | the build, the smoke test, the workspace verification, every CUDA model set up in `artifacts\engine-matrix`, `dictation-eval.ps1` and `meeting-eval.ps1` per engine, the oracle and the vocabulary run; `-DesktopChecks` adds the overlay, insertion and UI checks | 2026-11-15 23:41 to 2026-11-16 01:20: "RESULT: PASS (16/16 steps; accuracy targets are reported above, not part of pass/fail)", functional checks 96/96 |
| One entry point | `scripts/verify.ps1 -Tier pure` (PowerShell 7 on Linux, Windows PowerShell 5.1) | every pure check group as its own job and process, up to `-Parallel` at once, `report.json` and one `RESULT` line; `-CheckDeterminism` compares a sequential and a parallel run | 2026-11-18 on a Linux test host: 48/48 jobs, determinism check passed |
| Notes evaluation | `dotnet run --project tests/PrivateFlow.Tests -c Release -- --notes-report` | the rule-based notes on the 13 invented meetings of `SyntheticMeetings.cs`, scored by the rows each line cites | figures below |

Notes on the levels:

- The pure checks run in 51 named groups, one file per area (layout and counts in [TEST_COVERAGE.md](TEST_COVERAGE.md)). A failed check is recorded and the run goes on; an exception ends only its own group; the run ends with every failure as `FAILED [group] message` and exits with 1. The scripts and the workflow read only the exit code. Three groups have parts that need Windows (`settings-file-script` runs the PowerShell helpers, `settings-persistence` holds files open with Windows sharing modes, `cpu-backend-order` reads the loopback listener table); the Linux guest skips those parts and reports fewer checks.
- The version check keeps the topmost "Private Flow x.y.z" heading of [RELEASE_NOTES.md](RELEASE_NOTES.md) equal to `<Version>`; the notice-key check keeps [NOTICE_KEYS.md](NOTICE_KEYS.md) equal to the keys in the code.
- The workspace verification and the evaluations load large-v3 on the GPU. Start them on a quiet GPU (less than 2 GB in use by other work): other GPU work makes large-v3 crawl, and two other language model servers holding 7.5 GB each once made every Whisper job on the PC time out.
- Every diagnostic and evaluation mode, `--verify-overlay`, `--verify-insert` and the insertion fixture included, refuses to run without a `PRIVATE_FLOW_DATA_DIR` outside the app's own data folder (exit code 2). Tests use synthetic audio, synthetic meetings and their own fixture windows; they never use a microphone, send keys to other apps, read the owner's data or call the API.

## Publication update verification

Version 0.5.5 adds pure checks of public-release selection and failures with fake HTTP responses, and isolated installer checks with fake downloads. A real public release does not exist yet, so an end-to-end release update is unverified. Earlier measurements below describe their recorded versions.

Local verification on 2026-11-18: `build.ps1 -Portable` passed with zero warnings and 1,062 checks in 51 groups; `ui-smoke-test.ps1` passed all 13 steps. An isolated window also showed the missing-endpoint notice and the explanation for a reader token without a server URL. The ZIP's size and SHA-256 matched its manifest, and the package contained the app licence and .NET runtime notices. No real microphone, installed-app update or public release was used.

## Known flaky checks

None of these is a defect in the product as far as known; each has failed without a code change and passed on a rerun.

- **Press to pill, 250 ms bound** (`--verify-workspace`, "the meeting shortcut shows the pill … before the devices open"): failed at 343 and 347 ms on the 0.5.1 branch, at 266 ms on an unmodified main (8e0dec5) under load and at 263 ms (while another agent's check harness ran) and 264 ms (on a quiet PC) on the branch of 0.5.4, which passed it at 91 to 185 ms in four other runs. Follow-up.
- **Interface pause, 0.25 s bound** ("the interface thread stays responsive while recording"): failed at 0.26 s (parallel load, 0.5.2), 0.28 s (twice, another session's `find` busy on a core), 0.33 s and 0.39 s (a whole-disk `find` at up to 63 % CPU while another program held the GPU); passing runs measured 0.08 to 0.22 s.
- **Settings close button** ("the close button asks too; Save stores the change and closes Settings"): failed once on the branch of 0.5.4, which does not touch Settings, and passed on the rerun; the check waits a fixed 150 ms after Save for the file to be written. Follow-up.
- **Midnight date checks**: the History list check of the workspace verification and the UI smoke test expect a fixture meeting started 20 to 30 minutes earlier to be listed as "Today". Runs in the first half hour after midnight fail (seen at 00:04 to 00:17 and at 00:31; the list showed "Yesterday 23:44"). Pre-existing; follow-up.
- **Uninstall suite, the stand-in program check**: in Windows PowerShell 5.1 one run gave 28 of 29 and the rerun 29 of 29 (0.5.2). Before the console-host change of #66 one run per shell failed the process check.
- **The pure-check count** came out 716 instead of 717 in one build run (2026-11-17); two standalone runs and the next build gave 717. Which check varied was not found.
- **One failure each without a recorded cause**: the Meetings page's double-click check and the end-of-meeting check (both 2026-11-17). Both checks now print their values; neither failed again.
- **`--verify-overlay`** stops with "Fixture did not acquire focus" when started from a shell that does not have the foreground or in a disconnected desktop session; started through PowerShell's `Start-Process` it passes.

## Has anyone tried it for real?

"For real" means real keys, a real microphone or speakers, real downloads or the installed app, as opposed to synthetic audio, stand-ins and isolated test folders. The owner tested the installed build 0.4.0+5af303e by hand on 2026-11-17 with real keys and a real microphone, Notepad as the target and a video with speech as the other side of the meeting: all 29 steps worked. The steps are steps 1 to 29 of the owner's list below; "29-step test" in the table names them. That build contains everything up to #65 (one save rule, Ctrl + Alt shortcuts); the uninstall script, 0.5.0, 0.5.1, 0.5.2 and 0.5.3 came later, and **none of the 29 steps has been repeated on 0.5.0 to 0.5.3 yet**.

| Feature | Tried for real | Verified automatically | Not tried for real |
| --- | --- | --- | --- |
| Dictation with the toggle shortcut, pasted into another app's field | 29-step test, steps 2 and 3 (Notepad) | insertion policy (pure), fixture paste, workspace dictations on synthetic audio | the cross-app matrix: browsers, Word and Outlook, VS Code, Windows Settings, a terminal, an elevated app, a remote desktop; clipboard managers other than Windows clipboard history |
| Hold to talk | 29-step test, step 4 (after the #62 fix) | hold, release, tap and repeat decisions (pure) | an elevated window or the secure desktop in front; AltGr held as part of the shortcut |
| Pill at the press, starting state (0.5.0) | no | press path with a stand-in source, every event of the starting state (pure and in the window) | a real microphone's open time and the first word of a hold-to-talk dictation; the warm-up in the installed app |
| Meeting capture with microphone and system audio | 29-step test, steps 12 to 16 (meeting shortcut, window hidden, a video on the system channel, a muted microphone) | 26 synthetic and real-speech scenarios, synthetic sources in the window | real loudspeaker echo and headphones with real people, call audio, a device removed mid-meeting, the live Finalizing text and Cancel during a real meeting |
| Meeting final pass accuracy | no real meeting scored (goal 6 set aside by the owner) | meeting evaluation, FLEURS read speech | real conversation |
| Meeting shortcut | 29-step test, steps 12 and 16 | decisions (pure), the panel's shortcut path in the window | a registration conflict with another app |
| Saving a Ctrl + Alt shortcut (Ctrl + Alt + M) | 29-step test, steps 5 to 7 | validation, AltGr character through a fixed and Windows' German layout | typing with AltGr while it is registered; the quiet AltGr card at a real start (0.5.0) |
| One save rule for Settings, "Save your changes?" | 29-step test, steps 8 to 11 (also before Quit from the tray) | every section in the window, Save and Discard, the shared settings equal to the file | the question before Update, the update notice |
| Concise text style | owner, 2026-10-06 (one dictated paragraph in all styles); whether the owner used Concise with the API in the 2026-11-17 test is not recorded; agent runs through the configured API on synthetic text (2026-10-04, -06, -19) | client, prompt, guards and quote protection against a loopback stub | the 10 s timeout against a hanging endpoint |
| Local polish | no: in the 29-step test it was not set up, so the queued dictations of steps 26 and 27 used Clean (the cause of the 0.5.0 card) | pipeline per style (pure); agent runs of the real llama-server on synthetic text (2026-10-13, 2026-10-19, 2026-11-17 with the meeting notes model) | the real llama-server with a real dictation; Qwen3-4B on this PC since 2026-10 |
| Meeting notes, rules on this PC | the caveat card on a real test meeting (29-step test, step 17) | 13 invented meetings with ground truth, pure checks in three languages | the notes' content on real meetings |
| Meeting notes, local model | 29-step test, steps 18 to 20 (notes with citations, a citation opening its row, stale notes, "Save and write notes") | packaged app on invented meetings (agent, 2026-11-17) | meetings above 64 minutes, a PC without an NVIDIA GPU, notes while a final pass holds the GPU |
| Meeting notes, API | owner: Write notes through the API on a short test meeting, once on 0.4.0+39ef5e4 and again on 0.4.0+5af303e (step 21); the transcript text of that test meeting went to the configured API from the notes button, the model returned no points and the app showed that as a note card, earlier notes kept. Agent runs on invented meetings through the configured API (2026-11-16, -17) | request, validation and citations (pure, loopback) | notes with points from a real meeting; meetings above the 100,000-character limit |
| Queued Local polish behind meeting notes | 29-step test, steps 26 and 27: the wait, Escape not cancelling, the paste after the notes and Cancel keeping the Clean text were seen; the polish itself was not (not set up) | stage decisions and pipeline (pure) | the polish after the wait; switching away during the wait; Escape during the polish after the wait |
| Spoken formatting, email layout | no | pure; dictation evaluation on synthetic speech | Outlook |
| Backtrack (spoken corrections) | no | about 100 pure cases, stored Whisper transcripts of synthetic speech | real-microphone corrections, Whisper's punctuation of cues in live speech |
| Vocabulary list, corrections and snippets | the list: 29-step test, steps 23 to 25 | in the window (list), pure (rules) | Shift + click, real Ctrl + A and the Delete key; Import and Export dialogs |
| Profiles (overlay since 0.5.2) | no | in the window with a synthetic source | a profile chosen in the real tray; reading the foreground process name for matching |
| Settings → Models: Install, Remove, Recommended for this PC | no | the window against stand-in scripts and empty stand-in files; `setup-engine.ps1 -Recommend` and `-Download` offline | a real download through the buttons, Remove of real model files, a PC without an NVIDIA GPU |
| Speech recognition setup (`setup-engine.ps1`, Apply) | agent: pinned downloads in the full evaluation and the 2026-11-12 harness | stand-in script in the window | Apply pressed by hand; the recommendation after an update in an installed copy |
| One owner of settings.json (0.5.2) | the shared settings step in both shells for real; the hand-run scripts' refusal before any download | pure and workspace checks | `install.ps1` end to end; the scripts' `-ResultFile` write after a real download |
| Each setting described once (0.5.3; nothing changes for the user) | no | pure: every stored setting in the table once, the setup scripts' fields equal its installed ones; in the window: every setting of a section has exactly one control, and one value per section is changed, discarded, saved and restored | Save, Discard and "Save your changes?" with real input on 0.5.3 (steps 8 to 11 and 48) |
| Unreadable or locked settings.json (0.5.2) | no | pure; the card in the window | the "try again or quit" message box at start; a real broken file in the installed app |
| Update check, `install.ps1 -FromCloud` | agent against a loopback mock of the cloud (2026-11-07); scratch installs in PowerShell 5.1 and 7 | update decisions (pure) | the real update-server wake, package publishing, the Update button in an installed copy, the interactive `-SetCloudToken` prompt |
| Uninstall script | never run against a real installation, by anyone | 29 checks on fake installations | everything on a real installation (owner's step below) |
| Notice cards | 29-step test, steps 14, 15, 17, 21 and 22 (no x while the microphone is silent, a dot and a pointer to an unseen card, dismiss and back, pile and spread) | in the window | a screen reader, real Tab and Enter |
| History page, edits, citations, deleting a meeting | 29-step test, steps 17 to 20 and 28 | in the window with synthetic meetings | the search with real input |
| Output device choice and fallback | no | synthetic sources and the decision function | a real device chosen and recorded, a real device removed before a meeting |
| Waiting for GPU memory, model fallback | agent: three real final passes at once, a 16 GB stand-in model, an unusable model file | `ModelAttempts` and the load decisions (pure) | a real game or browser beside a real meeting |
| Smart App Control | reproduced on a PC with it on (2026-11-03) | error mapping (pure) | a block of the CUDA backend or the NVIDIA libraries |
| Closing the window during a meeting, Quit | 29-step test, steps 13 and 29 | in the window | – |
| Helper processes end with Private Flow | agent, with a stand-in llama-server and a forced end (2026-11-12) | – | the real llama-server; a setup's PowerShell and curl during a real download |

## Not verified, and why

Grouped by area. Each item is either not checked at all or checked only by reading the code or with stand-ins; the reason follows. Items that only the owner can try are also in the steps below.

### Dictation, shortcuts and insertion

- Real global shortcuts, profile shortcuts and the global Escape: pressing them sends keyboard input to the desktop, which AGENTS.md rules out for tests. Key validation and the hold decisions are pure checks; the shortcut fields are checked in the window with the focus a click gives and keys routed through WPF (a click raised in-process does not focus a TextBox, because WPF reads the real mouse button state).
- Hold to talk: why repeated hotkey messages arrive while the keys are down despite `MOD_NOREPEAT` is not established; the fix ignores them. With an elevated window or the secure desktop in front, `GetAsyncKeyState` cannot read the keys, so hold to talk discards at once there. Escape pressed with the shortcut's modifiers held still reaches the foreground app (Ctrl + Escape opens Start); the recording is cancelled either way. In toggle mode, holding the shortcut down for seconds while a dictation starts or records is not tried (owner's step).
- Typing with real AltGr while a Ctrl + Alt shortcut is registered, and hold to talk with AltGr held: real modifiers cannot be held in-process; `Native.Held` reads the generic Ctrl and Alt state, which AltGr sets. The AltGr warning follows the keyboard layout active in Private Flow, so an app with another layout can lose an AltGr character without a warning.
- How long a real microphone takes to open after the press, and whether the first word of a hold-to-talk dictation survives: the press path is measured with a stand-in source, so the microphone's own start (WASAPI) is not in the figures. The warm-up at start runs only in the normal start, which the evaluation modes bypass; it was measured in a throwaway harness of the same start.
- Quit of a starting dictation through the tray, Escape as a real key, and a press that arrives while a real device opens: Quit ends the verification instance and keys cannot be sent; Quit's first step is the same cancel the Escape check calls, and the event from inside the open is checked with a stand-in.
- A queued dictation in the running app: Escape released when the wait for meeting notes begins and registered again when it ends, Cancel during the wait, Escape or Cancel during the polish after it, the status texts, and the hold-to-talk Escape after the wait. They need a live recording and the global Escape; the decisions are pure checks of the dictation's transition table (`DictationStateChecks.cs`) and of the pipeline, the controller wiring was read; owner steps 51 and 52.
- The "not pasted" path (text left on the clipboard with a notice) and the restore delays: they need a live recording; the policy and the delays are pure.
- Paste into the owner's applications (browsers, Office, terminals, elevated apps) and the clipboard-history exclusion: sending input to existing apps is forbidden and reading clipboard history would read the owner's data; the fixture paste covers the mechanism. Accessibility metadata cannot identify every custom password control; the foreground check and the paste have a narrow race in Windows; elevated apps can reject the paste; clipboard managers may ignore the exclusion formats. Automatic insertion is on by default and there is no universal undo of an insertion.
- The silence warning with a real muted microphone, and Outlook or a remote-desktop session with its longer clipboard hold.
- A recording longer than three minutes with a real microphone: the 2026-10-06 fix was checked by a probe of the timer at 179 s to 3,600 s, not by an hour-long recording. Dictation audio is held in RAM in a bounded queue; worker requests time out after five minutes.
- The Escape fix for a hold-to-talk start whose microphone failed (#39): checked by reading; a failing microphone was not simulated.
- Recover the last result: needs the main window with a live recording.
- Reading the foreground process name for profile matching: the controller reads it from the window selected at recording start; the matching itself is pure.
- The status line and dots at a real start with Ctrl + Alt + M saved: the verification raises the saved card with Settings closed, as the start does, in a running instance.
- Long live sessions, and partial input delivery during a paste.
- Accuracy on real microphone speech (noisy, quiet, mixed-language, real room acoustics) and microphone reconnect and default-device events: the owner's microphone may not be used in tests. The recorder's windowing is reproduced from files with the same constants.

### Text styles

- Concise with a real model in the checks: tests must not call the API; the loopback stub covers the client. Its 10 s timeout needs an endpoint that hangs; the boundary is pure.
- Concise quote protection: unpaired stray quotation marks in the transcript are not tokenized, so a stray mark the model moves keeps the count and is not detected. The question-count guard rejects "We ship today, right? Who reviews the build? Sorry, I meant who tests the build?" and uses Clean: an accepted false positive.
- Local polish in the checks: llama-server and its model are not installed on the verification PC; the checks report it as not run. Local polish ignores Literal mode: it applied a spoken correction that Literal mode keeps (the one critical item lost in the 2026-11-17 measurement).
- Qwen3-4B, the Local polish model, for notes or for polish since 2026-10: not installed on this PC and tests may not download.
- "Vendor B, wait not Vendor C" is ambiguous in text (an exclusion or a correction): both rewriters keep "not Vendor C" and Clean keeps it as spoken.
- The rewrite guards are conservative safeguards, not proof that a rewrite keeps every meaning.

### Meetings

- Real microphone and system audio (WASAPI loopback) in the checks, and the silence-gap skipping: it would record the owner's real playback (the owner ran one real meeting with a video as the other side, steps 12 to 16); the suite feeds a continuous system channel. The recorder's real device names and levels, a device removed mid-meeting, and the progress window and tray notices as the shell shows them run in the window only with synthetic sources.
- The live Finalizing text and Cancel from the Meetings page during a real meeting; retained audio after the final pass (retention on is not in the workspace verification); the post-update download of large-v3 in an installed copy.
- The echo rule on real loudspeaker audio and with headphones and real people; any real call audio. Synthetic voices and read speech are cleaner than conversation, and Piper mispronounces Priya and Okafor in English. Two people on the system channel are not separated.
- The meeting-model timeout fallback: needs a worker that hangs for five minutes; the same fallback is verified with an unusable model file. Memory taken by other work after the memory check still slows a pass until the five-minute request timeout, the retry and the fallback.
- The wait for GPU memory in the running app (a final pass waiting for the local notes model, a dictation showing the low-memory card while its model loads, the tray notice of a refused dictation): no check can fill or free GPU memory on cue; three final passes at once show the wait. The memory estimate is measured for the two installed models only. Neither the final pass nor a dictation was run while the local notes model wrote notes.
- Which limit the two original fallbacks of 2026-11-16 hit is not recorded (the code of the time dropped the reason).
- Dictations shorter than the model load (about 2 s when the model is not loaded) still wait for the rest of the load. With Keep speech model warm off, a dictation cancelled after its model loaded and before anything was transcribed leaves the model loaded until the five-minute idle unload, which itself is not checked (a five-minute wait per run).
- The output device: a real device chosen and recorded and a real device removed before a meeting; `OpenPlayback` itself was read, the fallback runs with synthetic sources. The chip drop-down menus by a real click (the checks set the selections).
- A crash can lose the meeting chunk being written; completed chunks and manifests support retry. The meeting disk-space guard (under 256 MB free) and the junction refusal in the archive are not checked (filling the disk is unsafe; junctions need a crafted folder; follow-up for the junction case).
- Delete their audio through its confirmation dialog (needs a click; the deletion itself is pure).
- Search is lexical and notes without a model list candidate rows; there are no semantic summaries or speaker separation.

### Meeting notes

- Real meetings, in every notes style.
- Rules on this PC: recall of decisions and open questions stays low on meetings phrased differently from the rules (the caveat on the notes says so).
- Local model: meetings above 64 minutes, a PC without an NVIDIA GPU, notes while a meeting's large-v3 final pass holds the GPU, and the interface staying responsive while the notes model stops a warm polish server (the verification uses a stand-in notes model). About one action item in five has a wrong or missing due date.
- API: meetings above its 100,000-character limit.
- The Quit race of the local model server: no check stops the server while a notes request starts.

### Settings and setup

- `install.ps1` end to end (it may not be run here): the order of 0.5.2 (setup-engine.ps1 with `-ResultFile` before stopping the app, the shared settings step after the swap and before the new app starts) is checked statically, the step runs in both shells, the swap code is unchanged.
- The setup scripts after their downloads (the `-ResultFile` write and the call of the shared step): they need the pinned downloads, which tests may not make.
- The "try again or quit" message box at start for a settings file in use, and `--diagnostic-ui`'s exit code 3: not shown in a test; the load's refusal is pure.
- A real setup during a real save, a real in-app update while Private Flow runs, a real profile chosen in the tray, a real unreadable settings.json in the installed app: owner's steps.
- The question before Update, the update notice and the update's order (confirmation, question, notice until the installer ends): Update needs an installed copy; it uses the question and Save path that closing Settings uses, which is checked; the order was read. The question before Quit is not in the checks (Quit ends the verification instance); the owner saw it before Quit from the tray (step 11).
- The start-up notice of a cleared shortcut as the tray shows it: the text is checked, the clearing is pure; the window was not started with such a file.
- The settings migration on the owner's real settings file: it runs at the first start of each installed update; no check of its result on that file is recorded.
- Real downloads through Install, Install recommended and Apply; Remove of real model files: tests may not download. Sol's follow-ups from #68: validate the `models.json` next to the scripts against the built-in catalogue at install or start; verify the recorded byte sizes against upstream metadata (the two whisper.cpp archives were read from the release's asset list, the others from files on this PC or earlier pins); measure GPU contention and warm-model eviction under realistic load.
- The real setup scripts' output on the progress bar: the stand-ins print the scripts' lines. `setup-engine.ps1` prints no percentages, so within its step the bar shows the step and the elapsed time; `setup-rewrite.ps1` prints the model download's percentage.
- The setup's PowerShell and curl ending when Private Flow quits during a real download (they join the helper job that ends the speech worker; checked by reading).
- The CPU branch of `setup-engine.ps1` on a PC without NVIDIA tooling: not exercised on another machine.
- `install.ps1 -Autostart`: not exercised.
- Apply pressed by hand under Speech recognition; Set up Local polish from its card with a real download, and the cards ending after it (a stand-in replaced the script).
- A killed app may leave a stale tray icon until the pointer passes over it.

### Updates and installation

- The real update-server wake, package publishing, the interactive `-SetCloudToken` prompt (Read-Host needs a console) and an update from the Update button or the tray prompt in a real installed copy. Follow-up: a loopback cloud stub mode.
- The recommended setup after an update in a real installed copy (the owner's running instance holds the single-instance lock) and a download over the network during it.
- The single-instance lock and the hidden start in the tray: the owner's running instance holds the lock and the evaluation modes bypass it on purpose.
- `build.sh` and the workflow run only in the CI pool; locally, `build.sh`'s packaging step was run once on Windows with a fake `GITHUB_SHA` (2026-11-07).
- Windows versions other than the development PCs, a fresh machine install and the reputation of unsigned files.
- Smart App Control blocking the CUDA backend or the NVIDIA libraries; app signing, which needs a trusted certificate.

### Uninstall

- Scope, by design: a default installation only (`%LOCALAPPDATA%\Programs\PrivateFlow` and `%LOCALAPPDATA%\PrivateFlow`, the Start-menu shortcut and Run value `install.ps1` creates, the installer's leftovers in `%TEMP%`). A program or data folder that is a junction or symbolic link or has one on its path, a program folder that holds files but no `PrivateFlow.exe`, and an unset, relative, UNC or device-path `LOCALAPPDATA` are refused before anything is listed; links inside the folders are removed as links and never followed.
- Never run against a real installation by anyone: it would delete the installation, meetings and models. Owner's step below.
- What it cannot rule out: another program on the same account replacing a subfolder with a link in the instant between the script's check and the deletion. A process that cannot be ended makes it delete nothing (not tested). Windows' own records of the program (notification-area icon settings, Prefetch, jump lists) are not removed.

### Interface

- A screen reader announcing a new error: `LiveSetting` is set and `LiveRegionChanged` raised; no screen reader was run.
- Real mouse clicks, Shift + click, real Ctrl + A, Delete, Tab, Enter, Space, Escape and `/` in the checks: they raise clicks and routed key events. The owner's 29 steps used real clicks and keys for what they cover. Tab order was checked by focusability.
- The Dictate page's error card for a microphone that fails to open is raised with the call the controller makes.
- The two failures of starting an update are cards on About and updates, not exercised; the update check itself fails silently.
- While the status line points to an unseen notice, a transient confirmation such as "Pasted • 1.2s" is hidden behind the pointer (follow-up).
- The header's unsaved-preferences group is drawn past the window's right edge after the settings popup moved it (1,545 against 1,225 px); the window clips it and nothing visible is affected. Left as it is.
- Wording on screen that no check asserts: language names in the Profiles per app and Vocabulary tables, "Local polish" in the style column, profile shortcuts as "Ctrl + Alt + P" or "Not set", the export choices, the "Release it" hint, the quiet Cancel on Dictate, and the Settings search terms other than "retention" and "hold to talk". Hover and focus visuals, the 256 px icon frame and the tray icon on a live desktop were not captured.
- A confirmation shown over the open Settings; keyboard-only navigation beyond the UI smoke test.
- The Vocabulary page's Import and Export file dialogs and how the handlers show their results: the dialogs need a click; the handlers only call the pure file rules.
- The tray icon, its balloons and its profile menu: clicking the notification area acts on the owner's shell.
- Non-WAV imports (mp3, m4a, wma, flac, aac): no pinned encoder in the suite; Media Foundation decoding is not exercised.
- Worker timeout and crash messages, a runtime that does not match its model, disk-full behaviour, cancellation of a long real-engine operation and recovery from an abrupt shutdown: they need a hanging or crashing native runtime or a full disk.

## Owner's manual test steps

One current list. Steps 1 to 29 are the flow the owner ran on 0.4.0+5af303e on 2026-11-17 with real keys and a real microphone, Notepad as the target and a video with speech as the other side of the meeting; all 29 worked. None has been repeated on 0.5.0 to 0.5.3 yet. Steps 30 onwards are new since that test. Use a disposable text document for every paste.

### The owner's test of 0.4.0+5af303e

Start

1. Start Private Flow: it runs in the tray; Settings opens from the gear and shows the version.

Dictation

2. Toggle: press the dictation shortcut in Notepad, speak, press again: the text is pasted.
3. Press Escape during a dictation: nothing is pasted.
4. Hold to talk: hold the shortcut, speak, release: the text is pasted. A short tap: nothing is pasted.

Shortcuts

5. Click the Meeting shortcut field and press Ctrl alone: the hint to keep holding and press the last key.
6. Press Ctrl + Alt + M in the field: it is accepted with the AltGr warning naming the character it types; Save preferences stores it.
7. Press Ctrl + Alt + Space in the Meeting shortcut field: it says this is the dictation shortcut.

Saving settings

8. Change the meeting notes default: "Unsaved changes" with Discard and Save preferences appears at once.
9. Close Settings with a change pending: "Save your changes?" with Save at the bottom right and Don't save at the bottom left. Escape returns to Settings with the change; Don't save reverts it.
10. Change a value in three sections, Save once, reopen Settings: all three are stored.
11. Make a change, then Quit from the tray: the same question appears first.

Meeting

12. Play a video with speech and press the meeting shortcut: the window stays hidden, the pill shows the mirrored wave of both channels and the tray shows recording.
13. Press the window's close button during the meeting: it is refused, nothing crashes and the meeting goes on.
14. Mute the microphone for a while: the "No sound from the microphone" card appears without an x and goes when sound returns.
15. With the meeting running and History open, press the dictation shortcut: Dictate gets a dot and the status line a clickable pointer to it.
16. Press the meeting shortcut again: the finishing progress shows and the meeting ends Complete.

History and notes

17. The rule-based caveat card on the Notes tab: dismiss it; select another meeting and come back: it is back.
18. Write notes with the local model: notes with citations; a citation jumps to its row on the Transcript tab.
19. Edit a transcript row and save: the stale-notes card appears.
20. Edit a row without saving and press Write notes: it asks "Save and write notes"; Cancel neither saves nor writes.
21. Write notes through the API on a short meeting: the result shows as a note card and the earlier notes stay. (The owner's short test meeting gave no points, and the app said so.)
22. With two or more cards: "N more · show all" spreads them, "Pile up" stacks them again.

Vocabulary

23. Add a term with Enter, then paste a list of three: all are added; a duplicate is refused.
24. Tick three rows, press Delete selected (3): they go; Discard brings them back.
25. Type a term without adding it, then Save preferences: it is in the list.

Queued dictation (text style Local polish)

26. Write notes with the local model, then dictate and stay in the target: Escape there during the wait does not cancel, and the text is pasted after the notes. (In the owner's run Local polish was not set up, so the dictation used Clean: the wait and the paste were seen, the polish itself was not.)
27. The same, with Cancel on the Dictate page during the wait: the Clean text shows there, nothing is pasted and the clipboard is unchanged.

Closing

28. Delete a test meeting: the confirmation has Cancel prominent on the right and the quiet delete action on the left.
29. Quit from the tray: Private Flow exits without hanging.

### Uninstall script (#66)

30. Only when ready to lose that installation's data (export meetings first): `powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\PrivateFlow\scripts\uninstall.ps1" -WhatIf`, check the list, then run it without `-WhatIf` and type `UNINSTALL`. Both folders, the Start-menu entry and the autostart entry are gone, and Private Flow and its helpers are no longer in Task Manager.

### 0.5.0

31. Quit from the tray and start again; after a few seconds press the dictation shortcut in the document: the pill appears at once, dimmed, and turns into the wave when the microphone records. How long it stays dimmed is the microphone's start. Speak, press again: pasted.
32. Hold to talk right after a fresh start: say the first word as soon as the pill is no longer dimmed: it is in the text. Then say it the moment the pill appears and note whether it is cut while the pill is dimmed.
33. Meeting shortcut right after a fresh start, window hidden: the meeting pill appears at once, dimmed, then the wave.
34. Toggle mode, press the shortcut twice quickly: one short recording that ends, not two. Hold the shortcut down for three seconds while starting a dictation and again while recording, and note what happens.
35. With Ctrl + Alt + M saved, quit and start again: no yellow dot on the settings button and no pointer in the status line; Settings → Shortcuts still shows the AltGr card. Press Ctrl + Alt + Q in the field: a warning card while it is pending; Discard.
36. Local polish chosen with its model not installed: Dictate shows "Local polish is not set up" with "Set up Local polish (<size>)", and Text style shows the same card. Dictate once: the "Used Clean" card says "Local polish is not set up on this PC." with the button. Press it: Settings opens at Models and installs the model chosen for Local polish with its bar; once done, the cards are gone and the next dictation is polished.
37. Settings → About and updates: "Version 0.5.3 (beta)" and "Build 0.5.3+<commit>"; the tooltip shows the whole build identity.

### 0.5.1

38. Settings → Models: every model with its size, state and where it runs; Install and Remove of each row side by side.
39. Install Whisper medium: the bar runs with Cancel installing; afterwards the row says "Installed. Nothing uses it now." and the speech settings are unchanged. Remove it: the question appears and Remove deletes it. Remove of large-v3-turbo is refused with "Dictation uses it."
40. Install the Local polish model from Models (2.5 GB); remove it again once nothing uses it.
41. In the Local processing models card, "Local polish uses": Meeting notes model, Save, Local polish as text style, dictate a sentence: it is polished (the first takes about 5 s to load).
42. "Meeting notes model runs on": Processor only, Save, Write notes with the local model on a short meeting: several times slower and nothing loads on the GPU (Task Manager, GPU memory). Set it back to GPU.

### 0.5.2

43. Press Set up Local polish (or Install a model under Models); while it downloads, turn Auto-paste off on Dictate and Save preferences. After the setup, quit and start: Auto-paste is still off.
44. Choose a profile in the tray: Dictate shows "Profile <name> active ×", the chips show that profile's values greyed out, the Vocabulary page keeps your values, nothing is unsaved. Dictate once, Save preferences: your vocabulary and language are unchanged. Press the chip: the profile ends and the chips show your own choices.
45. Quit Private Flow, copy settings.json aside, put a wrong value into it (`"AutoInsert": "no"`), start: an error card on Dictate names `settings.json.unreadable-…` and the reason. Restore your copy with Private Flow closed.
46. Type a path into Speech engine path without saving, install a model under Models: the typed path is still there.
47. With Private Flow running, run `scripts\setup-rewrite.ps1` from the installed copy in a terminal: it stops at once with "Private Flow is running on this data folder…" and downloads nothing. Then install an update from About and updates: the console says it stored the new speech paths (or warns why not), Private Flow restarts, and your saved preferences are unchanged.

### 0.5.3

48. Nothing should look or behave differently. Repeat steps 8 to 11 (change a value in several sections, Discard, Save, "Save your changes?" when closing Settings and before Quit) and step 46; then quit and start: the saved values are there.

### 0.5.4

0.5.4 changes how a dictation keeps its state (one state and a transition table) without changing what it does. Every step here should behave as in 0.5.3; anything else is a regression. Use Notepad as the target.

49. Toggle: press the dictation shortcut, say a sentence, press it again: the pill dimmed, then recording; the text is pasted; no line break is added.
50. Escape: start with the shortcut, speak, press Escape: the pill closes, nothing is pasted, the status says "Cancelled. No pending insertion."
51. Hold to talk (Settings → Shortcuts, saved): hold, say a sentence, release: pasted on release. Tap it briefly: "Released too soon", nothing pasted. Hold and start speaking at once: the first word is in the text. Hold, press Escape with the modifiers still held: cancelled.
52. Queued dictation, Local polish chosen: press Write notes with the local model on a long meeting; while the notes run, dictate into Notepad: the card "Waiting for the meeting notes", and Escape works in other programs meanwhile. Press Cancel on Dictate during the wait: the Clean text stays on Dictate, nothing is pasted, the clipboard is unchanged. Again, and switch to another window before the notes end: afterwards "Not pasted: the selected window is no longer in front", the clipboard unchanged, the text on Dictate. Again without switching: pasted into Notepad, which Private Flow did not bring to the front.
53. A press during Starting: press the shortcut twice quickly as the first dictation after starting Private Flow: the pill shows and goes, nothing is recorded, "Cancelled. No pending insertion."
54. Quit during Starting: press the shortcut and choose Quit in the tray at once: Private Flow ends; nothing is recorded or pasted.
55. Toggle mode, hold the shortcut down for three seconds, then release: as in 0.5.3, one dictation that keeps recording after the release, no flicker of the pill; the next press transcribes. In toggle a repeated hotkey message counts as a press, so if it stops while still held, Windows repeated the message despite MOD_NOREPEAT; note it, it is not new.

## Measured figures

Hardware named where it matters: an RTX 4060 Laptop GPU (8 GB) with an AMD Ryzen 7 7745HX unless stated otherwise. Measurements on synthetic speech and invented meetings are cleaner than real use, and timings are single samples, not latency distributions of daily use.

### Checks in parallel

`scripts/verify.ps1 -Tier pure` on a Linux test host (10 cores, PowerShell 7.7 on Linux), 2026-11-18, 48 groups: one test process with all groups in sequence 3.8 s; every group in its own process one at a time 24.5 s; the same at 10 jobs 3.4 s, with the same results for all 48 jobs (`-CheckDeterminism -Seed 42`). Most of a job's half second is starting PowerShell and .NET, so the gain shows on the engine tier, whose jobs take minutes. A broken check is named with its group and log on the `FAILED` line, a filter that matches no job fails the run, and a time-dependent check planted for the purpose was reported as different between the two runs.

### Meeting recognition goals

Recommended GPU setup (large-v3-turbo-q8_0 for dictation and the preview, large-v3 for the final pass, Automatic languages), full evaluation of 2026-11-16 01:20 (reports `cuda12-large-v3-turbo-q8_0+large-v3-piper-auto*.json` under `artifacts\meeting-eval\reports`). The suite has 26 scenarios: 23 generated with Piper voices (three speakers in English, Spanish and German, code-switching, quiet and short remarks, overlaps, silences, cuts, a show under speech or on the system channel, low levels, a 10-minute run, the channel layouts, 42 more spoken times, replies that repeat the system channel) and 3 of real FLEURS speech. Each scenario is mixed into microphone and system channels, cut into chunk files like a live meeting, previewed and then finished; search and notes run on the saved meeting. The oracle transcribes each reference utterance alone in its known language with the final-pass model. The vocabulary run adds the names the user gave the app (vocabulary "Claude Code, Codex, GPT, Zephyr, Priya, Okafor, Lindqvist", corrections Mariano → Mariana, Joaquin → Joaquín, Tobi → Tobias, title "Design review"). The **shared system-channel layout** has one speaker on the microphone, two remote speakers and a show mixed on the system channel.

| Goal | Target | Result | Status |
| --- | --- | --- | --- |
| 1. Final pass against the oracle, per set | at most 0.5 points below | synthetic −2.5, mixed-channel −7.6, real +0.4 points | not met (real met) |
| 2. Every time and number the recognizer wrote found by search as typed ("12:34", "1234", words in en/es/de) at its row, times also in the notes | 100 % | 411/411 | met |
| 3. Critical items with the user's vocabulary, synthetic set; vocabulary names | 95 % each | 581/677 = 85.8 %; 55/70 = 78.6 % | not met |
| 4. Real speech (FLEURS), each recording once | 97 % words, 93 % items | 97.5 %; 59/63 = 93.7 % | met |
| 5. Owner-like spoken times of the people found by search and notes, show on the system channel | 90 % | 47/52 = 90.4 % (written as the time: 41/52) | met |
| 5. The same, shared system-channel layout | 90 % | 19/52 = 36.5 % (written as the time: 18/52) | not met |
| Replies that repeat the system channel, show layout / shared system-channel layout | all | 6/6, 7/7 | met |
| 6. The owner's own recording | – | – | set aside by the owner |

Per set: synthetic (23 scenarios) 90.6 % words, critical 561/677 = 82.9 % (people 88.5 %, 443/556; show 96.7 %, 118/121), microphone insertions 226, system 2; mixed-channel (5) 84.0 %, 190/254 = 74.8 % (people 70.0 %, 95/156; show 96.3 %, 95/98), insertions microphone 196, system 2; real (2) 97.5 %, 59/63, insertions microphone 4. Oracle: synthetic 93.1 %, mixed-channel 91.6 %, real 97.1 %.

A later run for #48 (2026-11-17, `meeting-eval.ps1 -Configuration auto -Scenario * -RealSpeech 12`, identical before and after that change): findability 412/412; goal 5 47/52 with the show on its own channel and 21/52 in the shared system-channel layout; synthetic 91.2 % words, 554 of 677 critical items; real speech 97.5 %, 59 of 63. The 88 critical clock times and minute marks of the meeting evaluation's references are listed at their row by the rule-based notes (88/88).

Remaining misses, synthetic set, 116 of 677: the shared system-channel layout 48 (the show talks over the two people on the system channel and Whisper writes the show; without the show the same scenarios found 42/52 times instead of 18/52; their speech also leaks into the microphone, most of its insertions); names 36 (Piper says Priya and Okafor wrongly in English; Lindqvist heard "Lindquist", Mariana "Mariano"); accented English of the L2-ARCTIC voices and mixed-channel remarks 14; formatting only 12 (for example "1234" without its colon, still findable); quiet, overlapped or low-level speech 6. Real speech, 4 of 63: proper nouns of English FLEURS sentences. No pipeline change separates two talkers on one channel; source separation would, or pausing or lowering the show while people talk.

What each part of the final pass contributes (each switched off alone, same 26 scenarios):

| Part | Without it |
| --- | --- |
| Stretches cut at Silero pauses (64 ms), runs of one language up to 25 s, decoded with large-v3 | the live preview: synthetic words 92.6 against 94.9 %, real speech 72.6 against 97.1 % |
| Language per stretch among the meeting's languages, switch penalty 0.5 | synthetic −0.6 points, mixed-channel −1.1 points (+2 items) |
| Level normalization to −20 dBFS per stretch | replies 5/6 instead of 6/6, shared system-channel times 17/52 instead of 19/52, −2 items |
| Beam 8 instead of 5 | synthetic −0.3 points, −3 items, same time; beam 5 roughly halves the microphone insertions (226 → 120), worth a look if inserted show words bother the owner |
| Rows end at sentence ends | −1 item, mixed-channel times 47/52 |
| Echo removal (a microphone row goes only when the system channel says most of its words at the same moment and it is 10 dB below the person's speech) | replies that repeat the system channel are deleted |
| Prompt from the vocabulary, short correction replacements and the title | 411 → 438 of 465 items with the user's names |
| Live windows of at most 20 s cut at the quietest 200 ms of their last 6 s | with fixed 20 s cuts, windows starting inside speech collapsed and held 331 of 529 missed words |

Tried and dropped: runs of at most 12 s; context carried between runs; choosing per run between turbo and large-v3 by confidence, or decoding low-confidence runs again in another language (confidence did not separate right from wrong); a clock-time prompt; a clock-time rewrite and one spelling per name (they rewrote ordinary numbers and names); recovering speech a run left without text (−1 item when removed); NVIDIA Parakeet TDT 0.6B v3 as a second recognizer (oracle on the same clips: meetings 84.7 % against Whisper large-v3's 93.2 %, real dictation 93.6 against 96.1 %).

Known limits of search and notes: Spanish "un"/"una" are still the number one for search ("a la una" is 1:00); echo rows that mix the person's speech with the show are kept by design; the installed medium model collapses timestamps (findability 305/407 = 74.9 % with medium; the recommended setup is not affected).

### Dictation recognition

- Speech models (2026-11-12, `whisper-cli` with the pinned CUDA runtime, language auto, generated SAPI speech: four English sentences with CRC, UART, SPI, DMA, I2C and Zephyr, four German with umlauts, a negation and a time): word errors medium 5, large-v3-turbo 6, large-v3-turbo-q8_0 6 of 114. A 40 s mixed-language clip: inference medium 1.15 s, turbo 0.65 s, turbo q8_0 0.60 s; model load 1.25 s, 1.23 s and 0.67 s. q8_0 and full turbo wrote identical text, so q8_0 (874 MB instead of 1.6 GB) is the CUDA default. Workspace verification on the same build: turbo cold 1.43 s, warm 0.25 s; medium 2.13 s and 0.35 s.
- Word error rate on 40 SAPI utterances (2026-10-19; 25 English with three voices, 15 German): medium on CUDA with vocabulary 2.2 % (English 3.0, German 0.7), without vocabulary 2.9 % (4.1, 0.7), base on the processor 10.2 % (7.8, 14.8). Negations survived everywhere; "IEC 62304" was misheard in all three, "pull request" heard as "poll request". Re-scoring those transcripts after the Backtrack fixes applied 12 of 12 typed corrections on medium and 10 of 12 on base, whose two misses are recognition errors and stay uncorrected by design.
- The dictation evaluation's per-language figures of the full evaluation are in its summary under `artifacts\full-eval`, not in this file.
- Earlier single samples: CPU base cold 1.44 s, warm 0.94 s (2026-10-13); CUDA medium cold 2.2 s, warm 0.27 s (2026-10-19). These are individual measurements including startup, not latency distributions.

### Transcription speed and GPU memory

2026-11-17, generated SAPI speech in an isolated data folder with the installed engine (CUDA, large-v3-turbo-q8_0, large-v3 for meetings, speech detection on), one run per build in which the GPU showed only 113 to 121 MB in use:

| Measurement | Before #55 | After |
| --- | --- | --- |
| Dictation stop to text, model not loaded (3.3, 9.7, 15.5 s clips) | 2.63–4.07 s | 0.57–1.15 s |
| Dictation stop to text, model warm | 0.50–0.92 s | 0.54–0.91 s |
| Final pass of a 69 s two-channel meeting | 18.72–18.79 s | 16.68–16.94 s |
| of which loading the dictation model again at the end | 2.09–2.25 s | none (loads in the background) |

- In the owner's 29-step test (0.4.0+5af303e, real microphone), a cold dictation took about 3 s from stop to text, with accurate text.
- Worker start and runtime 0.6 to 0.8 s; loading turbo 2.05 to 2.10 s, large-v3 5.3 to 5.5 s; the first decode after a load about 0.25 s slower than a warm one; Silero on a 15.5 s clip 0.06 to 0.09 s; in the final pass, language detection takes longer than decoding (6.5 s against 4.3 to 4.5 s). All 45 transcripts each run keeps were identical before and after.
- GPU memory: turbo 1.15 GB; large-v3 3.55 GB after loading and 4.31 GB after its first beam-search decode (the decoder cache for 8 beams). The worker compares the free memory with 1.5 times the model file (turbo took 1.38 times, large-v3 with its cache 1.46).
- Contention: with another process holding 6.1 GB, one large-v3 beam-search decode of a 15.5 s clip took 170 s instead of 1.8 s on the old build; three final passes at once (7.9 GB in use) took 151 to 503 s and one of six fell back after a 90 s load timeout. The driver moves what does not fit to system memory, so nothing fails quickly. On the current build, three passes at once: two ran large-v3, the third was refused within 0.9 s, waited and finished with turbo in one round and got large-v3 after 36 s in the other; passes took 40 to 70 s, nothing timed out, progress never fell.

### Press to pill

Throwaway harness that builds the main window hidden as the app's start does, isolated data folder, self-contained Release build, a stand-in microphone that opens instantly (so the microphone's own start is not in these figures); first press / later presses:

| Stage | Before 0.5.0 | After |
| --- | --- | --- |
| First UI Automation call (`Native.CaptureTarget`) | 49–78 ms / 2.3–3.5 ms | 5.9–6.0 ms after the warm-up |
| Speech worker start on the interface thread | 20.7–26.9 ms / 8–12 ms | moved to a pool thread |
| Total to the dictation pill's first frame | 128–174 ms / 32–53 ms, then a 160 ms fade-in, all after the microphone opened | 25.4–25.9 ms with the warm-up (87.9–89.1 ms without) / 15.6–19.8 ms |
| Meeting pill's first frame | 141–144 ms / 39–53 ms | 17.0–17.6 ms (31.6 ms without the warm-up) / 12.5–14.2 ms |

In the owner's 29-step test of 0.4.0+5af303e, before this change, the first press after start took about one second until the pill appeared: the reason for 0.5.0. The stand-in figures above account for about 0.1 s of it; the rest is in what a stand-in cannot measure, the microphone's open and the fade-in on the interface thread. The microphone now opens after the pill's first frame: about 10 to 35 ms sooner at the first press, about 17 to 25 ms later at later presses. In the workspace verification (no warm-up in evaluation modes) the instance's first dictation press drew the pill in 87 to 126 ms and the second in 33 to 36 ms; the meeting shortcut's devices opened 52 to 161 ms after its press with the pill already drawn. The verification's bound is 250 ms (flaky, above).

### Meeting notes

**Local model** (2026-11-17, #54). Bar: notes for a 30-minute meeting in under about 60 s, at least 80 % of decisions and action items, at most one wrong citation per meeting, valid notes every run. Four invented meetings with hand-written ground truth (15 minutes English, 30 minutes mostly Spanish, 30 minutes German, 60 minutes English), `--write-notes` with the local style, one model server at a time, quiet GPU; eleven candidates compared (table in #54). Chosen: Qwen3.5-4B Q4_K_M, CUDA 12.4 build, 32,768 tokens of context, 3.9 GB of GPU memory.

| Qwen3.5-4B | Language | Decisions | Actions found (owner and due right) | Open questions | Times | Wrong citations | Time |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 15 min English | en | 3/3 | 4/4 (4) | 1/2 | 5/5 | 0 | 19 s |
| 30 min Spanish | es | 4/4 | 4/4 (3) | 3/3 | 5/5 | 0 | 28 s |
| 30 min German | de | 4/4 | 5/5 (4) | 2/2 | 5/5 | 0 | 24 s |
| 60 min English | en | 6/6 | 5/6 (4) | 3/3 | 7/7 | 0 | 29 s |

Over two runs per meeting (identical, temperature 0): decisions 100 %, action items 95 % (owner and due date right 79 %), open questions 90 %, times 100 %, no wrong citation, 19 to 29 s. Qwen3.5-9B with the same prompt: decisions 94 %, actions 95 % (owner and due right 95 %), questions 90 %, times 91 %, one wrong citation, 40 to 54 s, 6.1 GB. The API (gpt-6-luna): every decision, action, owner, due date and question, 95 % of times, the right language, 14 to 20 s. Weaknesses of the 4B: about one action in five has a wrong or missing due date; it listed an answered question as open and a deferred review as a decision in the 15-minute meeting, and missed one action in the 60-minute meeting. Through the packaged app with the installed files, the 30-minute Spanish meeting took 24.9 s.

**Qwen3-4B for notes** (2026-11-16, #44, the model Local polish installs): on the processor with 8,192 tokens of context and the prompt of the time, constrained to the answer schema, it took 165 to 280 s (other agents used the PC, so the timings are indicative), cited unrelated rows (a decision citing 13 rows from the greeting on, three points citing a timestamp the transcript lacks) and missed both pricing decisions; through the app one run used up its 3,072 answer tokens after 445 s. One invented 53-row meeting. The notes path refuses it for that reason; the GPU and today's prompt are unmeasured.

**Rules on this PC** (2026-11-17, #46): thirteen invented meetings in `SyntheticMeetings.cs`, scored by the rows each line cites. Development: seven meetings (329 rows) and the three held back in round 1 (107 rows); held back in round 2 and not used to change the rules: three meetings (88 rows). R is recall, P precision; "both" counts actions whose owner and due date are both right; noise rows match no ground-truth item.

| Notes | Meetings | Decisions R / P | Actions R / P | Owner and due both right | Open questions R / P | Times R / P | Topics in summary | Noise rows |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| rules | first 7 | 16/22 / 16/17 | 28/28 / 28/29 | 26/28 | 11/15 / 11/11 | 38/45 / 75/84 | 17/23 | 10 |
| API (gpt-6-luna) | first 7 | 18/22 / 18/23 | 23/28 / 23/25 | 19/28 | 14/15 / 14/16 | 37/45 / 40/41 | 22/23 | 10 |
| rules | round-1 held back | 1/3 / 1/1 | 9/9 / 9/9 | 8/9 | 2/5 / 2/2 | 12/14 / 15/17 | 5/10 | 1 |
| API (gpt-6-luna) | round-1 held back | 2/3 / 2/5 | 9/9 / 9/9 | 9/9 | 5/5 / 5/7 | 13/14 / 13/14 | 8/10 | 6 |
| rules | **round-2 held back** | 1/7 / 1/1 | 7/11 / 7/7 | 5/11 | 1/6 / 1/1 | 20/22 / 26/29 | 3/11 | 3 |
| API (gpt-6-luna) | **round-2 held back** | 5/7 / 5/6 | 10/11 / 10/12 | 7/11 | 5/6 / 5/5 | 20/22 / 20/21 | 9/11 | 4 |

On unseen meetings the rules list few decisions and open questions, and what they list is right; the summary covers fewer topics than a model's. Reversals: 2 of 3 marked in the development meetings. Over all 13 meetings after #59: action items 44/48 found at precision 44/45, times at precision 116/129. Cost: 655 rows (an hour) take 0.31 to 0.43 s.

### Local models for polish and notes

2026-11-17 (#68), one model on the GPU at a time, 308 to 337 MiB in use at the start of every run, isolated data folders.

The meeting notes model for Local polish: the 36 synthetic text cases of `dictation-eval.ps1` (18 English, 10 German, 8 Spanish) with each case's options, through the app's dictation pipeline and guards, in a harness outside the repository (text, not audio):

| Notes model for polish | Polished (rest fell back to Clean) | Critical items kept | First (load) | Warm median (range) | GPU memory |
| --- | --- | --- | --- | --- | --- |
| GPU, automatic layers, 8,192 tokens | 33/36 (twice, identical) | 81/82 (Clean 82/82) | 4.8–5.0 s | 0.42 s (0.16–1.92 s) | 3.1 GB above idle while warm |
| Processor only (`-ngl 0 --device none`) | 33/36 | 81/82 | 14.1 s | 2.48 s (0.99–10.54 s) | none |

The three fallbacks (de-numbers, de-long, es-numbers) were numbers written as words; the lost item was the Literal-mode case. Beside it, with the polish server warm (3.4 GB), large-v3-turbo-q8_0 transcribing 60 s peaked at 4.7 GB (1.6 GB alone) and large-v3 at 7.5 GB of 8.2 GB (4.4 GB alone); both fit.

The meeting notes model on the processor: `--write-notes` on the invented meeting `german-long` (106 rows, 50 minutes, 7,997 characters):

| Notes model, german-long | Time to result | Points dropped | GPU memory peak |
| --- | --- | --- | --- |
| GPU, automatic layers | 28.1 s | 1 | 4.2 GB (3.9 GB above idle) |
| Processor only | 166.2 s | 1 | 448 MiB (the app window, no model) |

With a GPU build of llama.cpp, `-ngl 0` alone still hands large operations to the GPU; the processor setting adds `--device none`. An earlier record put a 30-minute meeting at 4 to 5 minutes on the processor. llama-server with the notes model took 5.6 GB of memory on the processor, hence no local model is recommended below 8 GB of memory; with less than 4.5 GB of GPU memory part of it runs on the processor; the local model is left out first when the disk cannot take everything with a 25 % margin.

Qwen3-4B for polish (2026-10-19, llama.cpp b11577 on the processor, isolated data, warm): 20 of 22 synthetic text cases, 0.5 to 1.6 s; it missed two German phrase corrections. The guard turned two outputs that dropped "not" into Clean fallbacks.

### Concise through the API

- 22 Backtrack cases on synthetic text through the configured API with gpt-6.1-sol: 22/22, 1.5 to 3.7 s (2026-10-19).
- Quotation cases, three runs each with gpt-6.1-sol: all nine kept the quote boundary and the quoted correction, 1.8 to 4.8 s (2026-10-06).
- gpt-6-luna: a 150-word dictation took 3.9 to 11.4 s, beyond the old fixed 8 s timeout; ten correction cases applied (2026-10-04). claude-haiku-4-5 answered a dictated question as an assistant despite the delimiters; the length guard rejects such replies.

### Smart App Control and the CPU backends

On a Ryzen 7 9800X3D with Smart App Control on (2026-11-03), the CodeIntegrity log showed policy `VerifiedAndReputableDesktop` blocking unsigned `ggml-base.dll`, `ggml-cpu-alderlake.dll` and `ggml-cpu-cannonlake.dll` in the speech worker, and `PrivateFlow.exe` launches; the verdicts changed from day to day. `ggml_backend_score` of the shipped variants on that processor: icelake and cascadelake 1161, cannonlake and skylakex 137, alderlake 108, haswell 44, sandybridge 17, sse42 5, x64 1. The worker loads only the GPU backend and the best-scoring CPU variant, falling back to the next; a runtime under a folder name outside the ANSI code page loads.

### Sizes

Speech models: large-v3-turbo-q8_0 874 MB, medium about 1.5 GB, base about 148 MB, large-v3 3.1 GB. Local polish model Qwen3-4B Q4_K_M 2,497,280,256 bytes; meeting notes model Qwen3.5-4B Q4_K_M 2,740,937,888 bytes, about 3.4 GB with the CUDA build of llama.cpp. whisper.cpp archives 8,194,445 and 670,611,449 bytes. Every download is pinned with its SHA-256 in `scripts/models.json`.
