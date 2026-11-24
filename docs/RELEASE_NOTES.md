# Private Flow 0.5.29 (beta): less work while idle (2026-11-23)

- **Private Flow wakes up less while idle.** Its window's timer ran 20 times a second all the time, also hidden in the tray. It now does so only while a dictation records or is processed, and every 2 seconds otherwise; idle unloads of the models and the update notice may come up to 2 seconds later.
- **The meetings are read once at start.** The History and the unfinished meetings read every saved meeting up to four times when Private Flow started, and twice on every refresh of the list; now once each.
- **A final pass slowed by other GPU work gives way sooner.** Every step of speech recognition could take five minutes before Private Flow gave up on it. A step of large-v3 in the final pass of a meeting or file now gives up after half a minute to about a minute on the GPU (about a minute for 30 s of audio), so a step that other GPU work slows down ends sooner, and the final pass tries once more and then finishes with the smaller speech model. Steps that no other model could take over keep the five minutes: dictations, and the speech model's own steps in a final pass.
- Local polish with the Local polish model starts its server with one slot that has the whole context, as the meeting notes do.

# Private Flow 0.5.28 (beta): Local polish stops without freezing the window (2026-11-23)

- **Escape during Local polish no longer freezes the window.** Escape or Cancel while the Local polish model was still loading, a polish that failed or ran out of time, and the first polish after its model or settings changed waited for the model's server to end before the window responded again, for up to 10 seconds. The server now ends in the background and the window stays responsive. The Clean text appears once the server has ended.
- A polish cancelled just as it begins no longer starts the model only to stop it again, and no longer stops a model that is already loaded. One cancelled while the previous model stops no longer starts the next one.

# Private Flow 0.5.27 (beta): times in corrections

- "Es bleibt bei 20, nein, 50 am Tag sind zu viel." is left as spoken again; 0.5.26 read "50 am" as a time and lost the 20. "Not at 2, no, 3 pm works better." is left as spoken again for the same reason. "Meet at 3, no wait, 4:30pm." and "Meet at 3, actually 4:30 pm." are corrected again.

# Private Flow 0.5.26 (beta): repeated words and weekday file names in corrections (2026-11-23)

- A correction that repeats the word before the value ("Nimm 3, Korrektur, nimm 4,5-mm-Schrauben.", "Open file 3, no wait, file 4,5.pdf.") and a weekday at the start of a file name or a lowercase compound ("Open Monday, no wait, Tuesday-report.pdf.") are left as spoken: the old value was lost.
- "Meet at 3, no wait, 4:30 pm." is corrected again to "Meet at 4:30 pm."; 0.5.25 left a time with minutes after a value without a unit as spoken.

# Private Flow 0.5.25 (beta): decimals and file names in corrections (2026-11-23)

- A correction whose new value is a decimal or ratio in a hyphenated compound ("Nimm 3, Korrektur, 4,5-mm-Schrauben.", "16:9-format", "1,000-foot"), a number followed by a unit without a space ("4,5mm") or a file name such as "4-6.pdf" is left as spoken: the old value was lost. An en dash counts as a hyphen too.
- Weekday corrections into a hyphenated word and label corrections to a single letter apply again ("Monday, no wait, Tuesday-Thursday", "Room 3, no wait, Room 4-A"); 0.5.24 left them as spoken. A label correction into a compound ("Room 4-wheel") stays as spoken.

# Private Flow 0.5.24 (beta): a value in a hyphenated word is no correction (2026-11-23)

- A correction whose new value is part of a hyphenated compound is left as spoken: "Use 3, no wait, 4-wheel drive." became "Use 4-wheel drive.", with the 3 lost; the same for "4-1/2-inch" or "4-20-mA", and with the hyphen and non-breaking hyphen of Unicode. A plain range ("Pages 3, no wait, 4-6.") is still corrected.

# Private Flow 0.5.23 (beta): clearer paste and update notices (2026-11-23)

- **The window you switch to keeps the focus.** When you switched to another window while a dictation was transcribing, Private Flow brought the window you started in back to the front before pasting, and then often did not paste anyway. It now leaves your window in front, keeps the text on the clipboard and says "Not pasted: the selected window is no longer in front".
- **The not-pasted card names the reason.** A key still held (Ctrl, Alt, Shift or Windows) showed "Windows blocked the paste"; it now says "Ctrl, Alt, Shift or Windows was still held". Within the same window the card says "the focus moved or Windows blocked the paste", because a move to another field right before the paste and a paste Windows refused cannot be told apart afterwards.
- **Update says when you are up to date.** Update under About and updates showed the warning "The update did not start" both when no newer version exists and when the check failed. It now says "Private Flow is up to date" with the version, and keeps the warning for a failed check.
- For screen readers, the History entry of the meeting recording now no longer says that Private Flow ended unexpectedly while recording it.
- Settings → Models says that the first Local polish after a start, or after five idle minutes, loads the model first: about 10 s for the Local polish model on the processor.

# Private Flow 0.5.22

- **Cancelling a dictation no longer shows an error.** Escape, Cancel, Quit or a hold-to-talk tap during a recording showed the red card "The recording stopped with an error", because the cancel ended the speech recognition the way a failure does. It now says only that the dictation was cancelled, or released too soon.
- **Literal mode keeps the words as spoken also with Concise and Local polish.** These styles rewrote the text anyway and applied spoken self-corrections. In Literal mode they now give the Clean text, without a request to the API or the local model.
- **Spanish negations are protected in rewrites.** Concise and Local polish fall back to Clean when a rewrite drops "nunca", "jamás", "nadie", "nada", "ningún", "tampoco", "sin" or "ni", as they already did for English and German negations.
- **A copy you make while Private Flow checks the field is kept.** A copy made after the dictation was put on the clipboard, while Private Flow read the focus right before Ctrl+V, could be pasted in place of the dictation. The dictation is now not pasted and your copy stays, with the card "Not pasted: the clipboard could not be saved". The same now holds for a copy made right after the dictation was put on the clipboard, which could be taken for the dictation, pasted and then replaced by the previous clipboard.
- **The GPU is used when the CUDA runtime loads.** Private Flow decided GPU or processor by whether the engine's folder name contained "cuda"; it now decides by whether the CUDA backend actually loaded.
- **Missing meeting audio is named.** When audio a meeting listed was gone at the final transcript and had no live preview either, the transcript was marked complete without a word about the gap. The notes now start with how many seconds of audio were missing.
- **Setups no longer run over files in use.** Setting up Local polish or the meeting notes model keeps the local models stopped until its runtime is in place, with a Local polish dictation waiting meanwhile as it does for meeting notes, and is refused while a local model still writes, instead of failing partway when its files are locked. After an update that brings a new speech runtime, the recommended setup runs as Apply does: dictation and meetings wait for it, and with a dictation running it waits for the next start.
- An update whose installer ends at once no longer leaves the window covered and Quit refused.

# Private Flow 0.5.21 (beta): signed releases and safer updates (2026-11-20)

- **Releases are signed.** Every public release now carries `SHA256SUMS.sig`, the Private Flow release key's signature of its `SHA256SUMS`. An update from a public release, in the app or with `install.ps1 -FromRelease`, refuses a release without a valid signature, or whose package or manifest differs from what the signed `SHA256SUMS` lists, before anything from it runs. The README's code signing policy says how to check a download by hand.
- **An update is no longer cut short by a second start.** While an update installs, Quit in the tray is refused with "An update is installing. Private Flow restarts when it is done.", and starting Private Flow by hand shows the same and exits. Before, a copy started during the download could record a meeting until the installer stopped it, and the end of that meeting was lost.
- **A paste goes only to the field that was checked.** Right before Ctrl+V, Private Flow now also compares the field itself, not only the window: in a browser, whose text and password fields share one window, a move from the checked field into a password field beside it means "Not pasted".
- **A copy you make while Private Flow pastes is kept.** When you copy something while Private Flow saves the clipboard, or after that but before it puts the dictation there, the dictation is not pasted and your copy stays, with the card "Not pasted: the clipboard could not be saved".
- A second installation or update started while one runs is refused with "Another installation of Private Flow is running" and changes nothing.
- `install.ps1` refuses a `PRIVATE_FLOW_DATA_DIR` on a network share at once; the speech setup refused the paths it would write there later.

# Private Flow 0.5.20 (beta): nothing internal in the public release (2026-11-19)

- Release builds name no commit of the development repository: their build identity is "<version>+release", and the release manifest carries no commit, build time or tree state.
- The in-app check for update-server builds reads the repository it compares builds in from `update-server.json`; `install.ps1 -SetCloudToken -SourceRepository <owner/name>` stores it. Without it, or in a release build, the newest server build is offered without the list of changes it brings.
- **Updates never go back.** A server build with a lower version than the installed one is neither offered nor installed, also when it is the build you confirmed; a release build is offered only a higher version, and a development build the same version only from another commit.

# Private Flow 0.5.19 (beta): safer pasting, updates and uninstall (2026-11-19)

- **A field Private Flow cannot check is not pasted into.** Before pasting, Private Flow asks Windows whether the field in front is a password field. When the app in front could not say (no answer from its accessibility interface, and no answer from a classic Windows edit control), the dictation was pasted anyway. It is now left on the clipboard instead, with the card "Not pasted: could not check whether this is a password field"; press Ctrl+V where you want it. The same happens when the focus moves to another field while Private Flow asks, and Private Flow reads the focus once more right before it pastes: the text goes only to the field it checked. Apps whose fields answer are pasted into as before.
- **Your clipboard is never lost.** When the clipboard could not be saved completely before a paste, because another program held it or one of its formats could not be read or had no data to read, the paste went ahead and the restore afterwards could empty the clipboard or drop part of it. Private Flow now leaves the clipboard as it is and keeps the text on the Dictate page, with the card "Not pasted: the clipboard could not be saved".
- **A missing Visual C++ runtime is named.** On a fresh Windows the speech engine cannot load without the Microsoft Visual C++ Redistributable 2015-2022 (x64), and the first dictation failed with "Could not load ggml.dll". The installer, the speech setup in the app and that error now say what to install, with Microsoft's download address. Private Flow does not download it itself.
- **An update waits for your work, and installs what it offered.** Update now refuses to start while a dictation, a meeting or a setup runs, and once it runs, the dictation and meeting shortcuts, Record and Start meeting refuse with a card until the restart, so the installer never stops Private Flow in the middle of a recording. Update first checks for the newest version and names the exact release or build in its question; when the check finds nothing or fails, it says so and starts nothing. The installer is told what you confirmed and refuses anything else it finds, such as a release published while the question was open.
- **Uninstall removes your kept test clips.** `uninstall.ps1` now lists and deletes `%USERPROFILE%\PrivateFlowEvaluation\dictation-clips` (the dictations kept with Keep as test clips, audio and corrected text) with the rest, under the same confirmation, and refuses when that folder or the one above it is a link.
- **Removing a model is one operation like the others.** While a model is being removed, the meeting shortcut, dictation, an update and Quit wait for it.
- **The installer keeps an unreadable settings file.** `install.ps1` reads settings.json under the same lock as the app. When the file cannot be opened or read, it is left unchanged and the speech setup is skipped; Private Flow sets the file aside at its next start. What the speech setup hands back is checked as the app checks it, and refused with the reason when it is not.
- **A speech worker that ends while loading says its exit code** again, instead of "exited unexpectedly".

# Private Flow 0.5.18 (beta): release checks and private debug symbols (2026-11-19)

- Releases now carry a `SHA256SUMS` file beside the ZIP and its manifest. `install.ps1 -FromRelease` and the in-app update check read it and refuse a release whose checksum file is malformed, larger than 64 KiB, repeats a name in any letter case, or disagrees with the manifest. Releases without one, such as 0.5.5, are checked as before.
- `install.ps1` can require a signed inventory in a downloaded release, and in an extracted release ZIP it runs from: a file listing the path, size and SHA-256 of every file of the package, signed by one named publisher. The package must then hold exactly those files before anything from it runs. The check stays off until releases are signed, and builds from your own update server are never checked for a signature.
- The debug symbols in the package no longer name the folder the app was built in or the development repository.
- The release manifest is read with exact types: a value of the wrong type, such as a list where a text is expected, is refused.

# Private Flow 0.5.17 (beta): a smaller download (2026-11-19)

- The download is smaller: the verification, evaluation and diagnostic modes left the app. `PrivateFlow.exe` ends with exit code 2 when started with one of their switches, and `--transcribe-file` runs only with an isolated `PRIVATE_FLOW_DATA_DIR`.

# Private Flow 0.5.16 (beta): the idle unload under test (2026-11-19)

- No user-visible changes.

# Private Flow 0.5.15 (beta): speech worker failures under test (2026-11-19)

- No user-visible changes.

# Private Flow 0.5.14 (beta): choices in tables read by name (2026-11-19)

- Screen readers and other assistive tools now hear the language and style chosen in the Profiles per app and corrections tables by their names, such as "Spanish" or "Local polish", instead of "Named { Value = es, Label = Spanish }".

# Private Flow 0.5.13 (beta): compressed audio files under test (2026-11-19)

- No user-visible changes.

# Private Flow 0.5.12 (beta): keep a dictation as a test clip (2026-11-19)

- New, off unless you turn it on: Settings, Speech recognition, **Keep dictations as test clips**. While it is on, the last dictation's audio stays in memory until the next one, and **Keep as test clip** on Dictate saves it with the text as you corrected it to `%USERPROFILE%\PrivateFlowEvaluation\dictation-clips`, on this PC only. The automated tests can then play your own dictations; their reports count words and name the clip's folder, never its text. Dictations longer than ten minutes are not kept.

# Private Flow 0.5.11 (beta): one install path for all models (2026-11-19)

- During a dictation, the Install button of the meeting notes model under Models now waits like every other Install button there.

# Private Flow 0.5.9 (beta): the Concise time limit under test (2026-11-19)

- No user-visible changes.

# Private Flow 0.5.7 (beta): a test build with recorded clips in place of the microphone (2026-11-19)

- No user-visible changes.

# Private Flow 0.5.6 (beta): installer shortcut and download size (2026-11-19)

- **A second installation keeps your shortcut.** `install.ps1` with `-Destination` elsewhere, such as a test copy, pointed the Start-menu shortcut "Private Flow" at that copy, so the main installation no longer opened from the Start menu. A shortcut that opens another installation that still exists is now kept, and the installer names both paths. A missing shortcut, or one whose program is gone, is still created.
- **The installer says how much it downloads.** It announced "~1.5 GB with an NVIDIA GPU" but downloaded about 4.6 GB, because Automatic on the GPU also installs `large-v3` for meetings. The setup now adds up what is missing from `scripts/models.json` and prints it before the first download ("Download size: 4.6 GB"). The README and the guide give the disk space with and without a GPU.

# Private Flow 0.5.5 (beta): publication defaults and updates (2026-11-18)

- Removed the private update server host and default API endpoint. Explicit saved API settings stay; older files with no endpoint key show a setup notice and use the existing visible local fallbacks.
- Updates use the latest stable public GitHub release by default, anonymously and only when its version is higher. A stored update-server URL plus reader token selects integration builds instead. A legacy token alone falls back to releases with a proactive app notice and tray notification; the installer prints the same recovery command: `install.ps1 -SetCloudToken -CloudUrl "<server URL>" -CloudWakeUrl "<wake URL>"`. Run it once per Windows user and press Enter to retain the stored token.
- Both installer download modes verify size and SHA-256 before replacing the app; releases also check the GitHub asset digest when present. Build manifests now include the ZIP size. Missing releases, offline checks and API failures produce no background offer.
- The installer rejects equal or older releases, including an update-server fallback, unless `-Force` is given. A missing public repository or stable release (HTTP 404) gets an explanatory message.
- Added the MIT licence, security reporting guidance, checked third-party notices and a tracked-text leak gate. Removed private infrastructure detail and obsolete planning documents.
- `scripts/export-public.ps1` exports a committed tree without history or workflows and scans it for private detail and the internal runner label. Non-ASCII PowerShell scripts retain UTF-8 BOMs for Windows PowerShell 5.1; the pure checks enforce this and exercise accented evaluation literals.

# Private Flow 0.5.4 (beta): one explicit dictation state (2026-11-18)

- Nothing changes for you. Inside, a dictation now has one explicit state (starting, recording, transcribing, waiting for the meeting notes, polishing after the wait, pasting, cancelling) and one table that says what each shortcut press, release, Escape, Cancel and Quit does in each of them, so the safety rules for pasting are kept in one place.
# Private Flow 0.5.3 (beta): settings described once (2026-11-18)

- Nothing changes for you. Inside, every stored setting is now described once, with the settings section that saves it and whether a setup installs it; showing, reading, discarding and the "Save your changes?" question follow from that one description, and the checks fail when a new setting is left out of it.

# Private Flow 0.5.2 (beta): fixes from a review of 0.5.1 (2026-11-18)

- **A setup no longer undoes saved preferences.** A setup that ran in the background (Apply, Set up Local polish, Install under Models, the meeting notes model, the recommended setup after an update) read settings.json at its start and wrote all of it back at its end, so a preference saved in between, such as turning automatic pasting off, was back after the next restart while Private Flow showed it as saved. Run by Private Flow, the setup scripts now never write settings.json: they hand back the paths they installed, and Private Flow stores exactly those like any other save. Run by hand, they change settings.json only while no Private Flow runs on that data folder, and while one runs they stop before downloading anything; `install.ps1`, and with it the in-app update, downloads first and stores the new paths only after it has stopped the running app. Every result a setup hands back is checked: only paths inside the data folder's own models and runtime folders, links resolved, are taken. They also no longer garble names with accents in settings.json under Windows PowerShell.
- **A chosen profile no longer replaces your preferences.** A profile chosen in the tray or by its shortcut filled the language, text style, pasting and vocabulary on Dictate as unsaved changes, so Save preferences, or Enter on "Save your changes?" at Quit, stored the profile's vocabulary, often empty, over yours. A chosen profile now applies to the following dictations only, shown by a chip on Dictate ("Profile Code active ×", press it to end the profile); nothing becomes unsaved and Save never stores a profile's values.
- **An unreadable settings file is kept.** A settings.json with a mistyped value or cut short made Private Flow start with the defaults, and the next save replaced the file with its profiles, corrections and vocabulary, without a sign; so did one that another program merely held open. One held open or not accessible is now left alone: Private Flow says so at start and offers to try again or quit. One whose content is not settings is renamed to `settings.json.unreadable-<date and time>` beside it and never touched again, and a card on Dictate (and a tray notice) names it and the reason until preferences are saved. Saves are flushed to the disk before they replace the file, so a power loss cannot leave it cut short.
- **Paths you typed stay.** Installing a model that changes no setting no longer resets the engine and model paths typed under Speech recognition and not saved yet; a setup refreshes only the fields it changed.
- **The chips show what a profile does.** While a profile is active, the language, text style and pasting chips on Dictate show the profile's values, read-only, so the text style chip never says Clean while a Concise profile sends text to the API.
- **The Local polish card tells the truth.** The "Local polish is not set up" card followed the model chosen under Models even before it was saved, while a dictation uses the saved one: choosing an installed model without saving hid the card although the next dictation still used Clean. The card, and its button, now follow what the next dictation uses, an active profile included.
- **Cancelling Apply cleans up.** Cancelling Apply under Speech recognition now removes the partial download it was writing (up to 3 GB for large-v3), as Cancel installing under Models already did.
- **No freeze when removing a local model or after five idle minutes.** Stopping the Local polish or meeting notes model server, which can wait up to 10 s for it to exit, now happens off the interface thread when a model is removed under Models and when the idle server is unloaded.
- **The uninstall script finishes after an interruption.** When another console had the program folder as its current folder, the script deleted PrivateFlow.exe but could not remove the folder, then refused to continue to the data folder and refused every rerun ("holds no PrivateFlow.exe"). An empty program folder, which is all such a run leaves, is now treated as Private Flow's leftover; anything else without PrivateFlow.exe is still refused.
- **Diagnostic modes stay away from your data.** `--verify-overlay`, `--verify-insert` and the insertion fixture now run only with an isolated `PRIVATE_FLOW_DATA_DIR`, like the other diagnostic modes.
- The README says that a Ctrl + Alt shortcut is also AltGr on keyboards that have it, and that the warning follows the keyboard layout active in Private Flow. The workspace verification runner is now in the repository as `scripts/verify-workspace.ps1`.

# Private Flow 0.5.1 (beta): Settings → Models (2026-11-17)

- **One place for every model.** Settings → **Models**, before Speech recognition, lists all seven downloadable models: Whisper large-v3-turbo, medium and base for dictation, large-v3 for the final pass of meetings and files, the Silero voice detector, the Local polish model (Qwen3-4B, 2.5 GB) and the meeting notes model (Qwen3.5-4B, 2.7 GB). Each row says what it is for, its download size and its size on disk, whether it is installed and what uses it, and where it runs, read from the configuration in use: "Runs on: GPU (the CUDA build of the speech engine is in use), about 1.3 GB of GPU memory", "Runs on: processor (llama.cpp processor build, no GPU layers)", "Runs on: GPU, as many layers as fit". **Install** (with its size) and **Remove** are side by side. Install shows the progress bar and turns into **Cancel installing**; Cancel removes the partial download. A speech model is only downloaded; **Apply** under Speech recognition switches to it. Remove asks in the window first, deletes only a model Private Flow installed in its own folder, and refuses with the reason a model something uses ("Dictation uses it. Choose another model under Speech recognition and press Apply first.") and any model while a dictation, a meeting, a setup or notes run. Install and Remove act at once; nothing is downloaded before a press. Set up Local polish under Speech recognition, the install on the Notes tab and the link under Meeting defaults run the same installs or open Models.
- **Recommended for this PC.** The top of Models shows what Private Flow read on this PC (graphics card and its memory, NVIDIA or not, processor, memory, free disk space; nothing is sent anywhere) and three buttons for what it recommends: the transcription setup Automatic installs here, asked from the setup script itself so it always matches Apply, and the meeting notes model for both meeting notes and Local polish. **Install recommended** installs everything missing in one go; **Install transcription models** (the same as Apply with Automatic, so dictation switches to them) and **Install local processing models** install one group. Each shows its size and says "installed" once nothing is missing; they run one at a time on one progress bar with Cancel. Without an NVIDIA GPU it recommends the processor build and base, and says the notes model then runs on the processor; with less than 8 GB of memory or too little disk it leaves the local model out and says why.
- **Shortcut hints as cards.** What a shortcut field under Settings → Shortcuts is doing, such as "Ctrl + Alt + … Keep holding and press the last key of the combination.", "Press Save preferences to apply." or "Ctrl + Alt + Space is the current dictation shortcut.", is a note card above the fields instead of grey text under the field, where it was easy to miss. The field's line keeps its help. A combination Save preferences cannot use is an error card, "Dictation shortcut unchanged", like a combination another app holds.
- **One operation at a time.** Every install, removal, Apply and check on the Models page and under Speech recognition waits for the one that runs, with its buttons disabled; Install recommended runs its two steps itself, in order. Before, the install of the meeting notes model could start while another setup ran.
- **A changed choice takes effect at once.** A local model server that runs with other settings than those just saved (Local polish moved to the processor only, or to the other model) is started again at the next polish, instead of running on as it was.
- **The GPU for the final pass.** A Local polish server idle on the GPU is stopped before a meeting's final pass checks GPU memory, so large-v3 is not pushed to its fallback by it.
- **A hand-chosen speech setup is not reset silently.** Where speech recognition was chosen by hand, the recommended card says so, and Install recommended or Install transcription models asks, naming what changes, before it switches to Automatic.
- **Remove deletes only Private Flow's own files.** It refuses a model whose folder is reached through a junction or symbolic link, and a model whose file another feature reads, also through a path entered by hand, each with the reason.
- **Two cards.** The models stand in two cards, **Transcription models** and **Local processing models**, each with the choices that concern it.
- **Which model for which feature.** In the **Local processing models** card under Models: Local polish runs the Local polish model on the processor or, new, the meeting notes model, so one download can serve both features (measured: 33 of 36 test dictations polished, 0.4 s typical on the GPU where it holds about 3.1 GB until five idle minutes pass, 2.5 s on the processor). The meeting notes model can run on the processor only (a 50-minute test meeting: 166 s instead of 28 s on the GPU). How meeting notes are written (rules, the meeting notes model or the API) moved here from Meeting defaults. The speech models dictation and the final pass use are shown with a link to Speech recognition, where they are chosen. These choices follow Save preferences and Discard; a choice whose model is missing says so and offers Install next to it.
- **The Local polish model and notes.** Qwen3-4B stays refused for meeting notes, because its one test (on the processor, with an earlier prompt) cited unrelated rows and missed decisions; the refusal now says that instead of calling the model too small. It has not been measured on the GPU or with today's prompt.
- **"Set up Local polish" leads to Models.** The card 0.5.0 shows where Local polish is chosen without its model now opens Models and installs the model chosen for Local polish there, the Local polish model or the meeting notes model, with the size of that model on its button.
- All model names, sources, sizes and hashes now come from one file, `scripts/models.json`, which the setup scripts and the installer read and the app has built in.

# Private Flow 0.5.0 (beta)

The first beta: the state of a manual test with real keys and a real microphone, with the fixes that test asked for. Builds that fix things after this one are 0.5.1, 0.5.2 and so on, each with its section under a new heading "Private Flow 0.5.n" above this one. Settings → About and updates names the version and the build.

## In this beta (2026-11-17)

- **Version 0.5.0 (beta).** Settings → About and updates says "Version 0.5.0 (beta)" and, on a second line, the build with the first seven characters of its commit, such as "Build 0.5.0+1a2b3c4"; its tooltip has the whole build identity. Before, it said only "Version 0.4.0", the same for every build. The update check still tells builds apart by their commit.
- **The pill comes up at once.** A dictation press shows the pill before the microphone opens, with a grey outline and faint bars while it starts, and with the white outline and the moving wave once the microphone records, so starting is not mistaken for a silent recording; the meeting shortcut does the same with the meeting pill. It no longer fades in. The first press after start no longer waits for Windows and WPF to prepare UI Automation, which reads the field the dictation is for, and the pill's window: Private Flow prepares both out of sight right after it starts, without opening the microphone. The speech worker, a process of its own, now starts beside the recording instead of before the pill. Measured with a stand-in for the microphone: the first press drew the pill after 128 to 174 ms of work, then the microphone's own start, then a 160 ms fade-in; now after 25 ms, and later presses after 16 to 20 ms instead of 32 to 53 ms. The meeting pill: 17 ms instead of 141 to 144 ms at the first meeting. The field the dictation pastes into is still the one in front at the press; the pill never takes the focus. From the moment the pill shows, the start ends as a recording does: a second press, Escape, Cancel or Quit ends a starting dictation before the microphone opens, and releasing a hold-to-talk shortcut then is a tap ("Released too soon"); the meeting shortcut, Stop; finish later or Quit ends a starting meeting before its devices open, and no meeting is left behind. The window cannot be closed during a starting meeting, as while one records; preferences can be saved then, as during a recording (only a dictation or a file transcription locks saving).
- **The AltGr card no longer nags.** A saved Ctrl + Alt shortcut whose key types a character with AltGr keeps its card under Shortcuts, but the card no longer takes the status line or puts a yellow dot on the settings button at every start. While a new combination waits in the field for Save preferences, the card is a warning as before.
- **Local polish without its model says so where it is chosen.** With Local polish chosen on Dictate or under Text style and its model not set up, a card on the Dictate page says that such dictations use Clean and offers **Set up Local polish (2,497 MB)**, which opens Settings → Speech recognition and runs the setup there with its progress bar; the same card stands quietly under Text style, and under Profiles per app while a profile uses Local polish. The "Used Clean" card after a dictation now gives the reason, "Local polish is not set up on this PC.", with the same button. Nothing is downloaded before the button is pressed. Local polish and the meeting notes model stay two separate installs.

## New since 0.4.0

### Uninstall script (2026-11-17)

- **`scripts\uninstall.ps1` removes a default installation of Private Flow**: the program folder `%LOCALAPPDATA%\Programs\PrivateFlow` and copies the installer left next to it, the data folder `%LOCALAPPDATA%\PrivateFlow` with everything in it (settings, the cloud reader token, models and runtimes, downloads, saved meetings with their transcripts, notes and audio, and files you put there yourself), the Start-menu shortcut and the autostart entry when they start this installation, and the installer's and updater's leftovers in `%TEMP%`. It ships in the package's `scripts` folder; run it with `powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\PrivateFlow\scripts\uninstall.ps1"`.
- **It shows first what it removes**: each location with its size, the number of saved meetings, each model, and that everything in the folders is deleted for good. Nothing is deleted until you type `UNINSTALL`. `-WhatIf` only lists; `-Force` skips the question for unattended use. After the confirmation the folders are checked again, and the shortcut and autostart entry are read again right before they are removed.
- **A relocated or custom installation is refused** before anything is listed (a folder that is a link or has one on its path, a program folder without `PrivateFlow.exe`, an unusual `LOCALAPPDATA`); the README says what to delete by hand.
- **A running Private Flow** is named; quit it from the tray, or confirm and the script ends the programs running from the two folders and the PowerShell and curl processes they started. A file that cannot be deleted is named with the reason, the exit code is 1, and a rerun finishes the job.
- Not yet run by anyone on a real installation; see docs/VALIDATION.md.

### Warning card stack (2026-11-17)

- **Warnings, errors and notes are cards** above the content they concern, on every page and settings section, instead of free text and the status line. Each card has a coloured left edge and a small kind label (Warning yellow, Error red, Note blue), a short title, one or two lines, sometimes a link such as **Choose how notes are written** or **Try again**, and an **x** in the top right corner. A long reason is cut after its first sentence; the tooltip has all of it.
- **Several cards pile up**: the newest in front, the edges of up to two more behind it and "N more · show all" under them. That link or a click on the front card spreads them out one under the other; **Pile up** piles them again. Cards, their links and their x are reachable with Tab; the x is named "Dismiss" for screen readers, and a new error is announced.
- **The x hides a card** until its condition changes or its context is opened again: the meeting is selected again, the operation runs again, a new dictation starts. Redrawing the page does not bring it back, and dismissing never changes the condition (dismissed stale notes stay stale). "Speech recognition is not set up" has no x, as dictation cannot run until it is.
- **The Notes tab** has no "one yellow line" any more: stale notes and a failed run are two cards, a model that found nothing is a blue note, a failed install of the meeting notes model an error card (also under Meeting defaults, with Try again), and the rule-based caveat a warning with its link. Dismissed, the caveat stays away until the meeting is opened again.
- **The Meetings page** shows a silent microphone, a fallback to the Windows default output, a stopped live preview, a meeting that could not start, a finish that failed or was cancelled, a smaller model a final pass fell back to and a channel that recorded no sound as cards above the composer. **The Dictate page** does so for a microphone that could not start, no sound, Used Clean and why, a paste that did not happen, a hold released too soon, a dictation waiting for the meeting notes, low GPU memory, a shortcut taken by another app and a failed setup of the recommended model. Settings sections show setup, save and update failures, the shortcut fields' rejections, "already in use" and the locked note as cards.
- **The status line** at the bottom of the sidebar shows progress and confirmations, such as "Recording • 12s" or "Preferences saved". A warning or an error raised on a page you do not see takes its place, naming the page ("The capture stopped on its own. See Meetings."; a click opens it), and that page gets a red or yellow dot in the sidebar until you open it or the notice ends; settings sections mark the settings button. A meeting saved with such a problem says "Meeting saved with a problem; see Meetings." instead of a bare success. Tray notices stay as they were, and now also come when the microphone cannot start or no microphone is selected while the window is hidden.
- **Cards end with their reason**: "A meeting is recording" when the capture stops, "Notes are being written" when the notes are done, a refused dictation or file transcription when the other task has ended, "Preferences not saved" after any later successful save, and "Speech recognition is not set up" as soon as the engine and model files are there, by setup, paths entered by hand or the recommended setup at start. A failed action's card goes at the next run of that action or with its x.
- **Cards about a meeting stay with it** on the History page: notes that fail while another meeting is selected show when you select it again, also after others. The page's own cards, such as a failed Delete expired meetings, show with or without a selected meeting.
- "No sound from the microphone" has no x while a meeting records; it goes when sound returns. Each x is named "Dismiss: <title>" for screen readers. Recover the last result shows that result's own GPU-memory note.

### Vocabulary list and the gear for Settings (2026-11-17)

- **Names and terms are a list.** On the Vocabulary page, type a name or term and press **Add** or Enter; it is appended, the field clears and stays ready for the next one. Pasting several terms separated by commas, semicolons or line breaks adds each, so an existing comma list can be pasted. A term already in the list, in any case, is not added twice: the list scrolls to it and a line below the field names it. Select terms with a click, Ctrl+click, Shift+click, the box on each row or Ctrl+A, and remove them with **Delete selected (N)** or the Delete key, without a question: **Discard** brings them back until the preferences are saved. The Delete key works wherever the focus is in the list's card, also right after clicking a row's box, except in the entry field while it holds text. A term stored twice in an older vocabulary stays twice until one is deleted; deleting it removes exactly the row selected. The page shows the number of terms and scrolls within a bounded list, so a few hundred stay usable. Text typed in the entry field but not added counts as an unsaved change: **Save preferences** adds it to the list first, a dictation started meanwhile uses it as it used the text typed in the former field, and **Discard** clears it. To change a term, delete it and add it again. This replaces the single field with all terms separated by commas.
- The vocabulary is stored as before, as one comma-separated text, so existing settings load unchanged and dictation and meetings get the same text for the same terms. A list that is not changed keeps the text it was loaded from exactly, also when another preference is saved, and **Discard** restores the saved text exactly, also after a profile with the same terms in other spelling was chosen; a changed list is stored with ", " between terms, as the field suggested. Profiles keep their own vocabulary field.
- **Gear for Settings.** The settings button at the bottom left of the sidebar shows a gear instead of the sliders. The sliders stay on the Language and pasting section.

### A meeting notes model on this PC (2026-11-17)

- An optional **meeting notes model**, Qwen3.5-4B (2.7 GB, Apache-2.0), with the GPU build of llama.cpp (about 3.4 GB together on a PC with an NVIDIA GPU), writes meeting notes on this PC in the local notes style; nothing is sent anywhere. On an 8 GB NVIDIA GPU it wrote notes for a 30-minute meeting in 24 to 28 s and found every decision and 95 % of action items on invented test meetings; about one action in five has a wrong or missing due date, so check owners and dates against the cited rows. It is loaded only while notes are written and unloaded right after. It is installed on request with `scripts\setup-rewrite.ps1 -Part notes` (an install button in the app followed in "One interface"); the button to write notes is on the History page's Notes tab.
- Meetings of up to about 80 minutes fit the notes model, instead of about 20 minutes before.
- Notes written on this PC no longer use the Local polish paths (`LocalRewriteModelPath`); they use the meeting notes model. The model that Local polish installs is still refused for notes, now also under another file name.
- Model-written notes are in the language most of the meeting was held in, which the app now names to the model.
- A dictation in the **Local polish** style that arrives while notes are written waits for them: the status line and a tray notice say that another task, the meeting notes, is still running and the polish follows. Escape cancels it at once. It waits at most the time set under **Settings → Text style → Wait for meeting notes** (two minutes by default, 10 seconds to 30 minutes) and then pastes Clean, saying the polish was skipped. When the polish is done, the text is pasted only if the window you dictated into is still in front; otherwise it stays on the Dictate page and the clipboard is left alone. Without the Local polish model the dictation does not wait and uses Clean at once.

### History with one search, a transcript and a notes tab, and notes written by a model on request (2026-11-17)

- **History** lists past meetings on the left (title, date, length, an "unfinished" tag with the reason) and shows the selected one on the right with the tabs **Transcript** and **Notes**, **Export** and a menu with **Re-transcribe** and **Delete meeting**. Speaker labels and text stay editable in place with **Save edits**. The meeting recording now is listed but cannot be opened; other meetings stay readable while one records.
- One search box in the History header searches all meetings with the same word and time matching as before. The list shows only the meetings with hits and their counts, the transcript highlights the hit rows and opens at the first with its neighbouring rows, and **Only this meeting** limits the search to the selected meeting. The separate Find evidence box is gone.
- The **Notes** tab shows the rule-based notes at once with their notice in yellow and a link to the new **Meeting notes** choice under Settings → Meeting defaults (Rules on this PC, Local model, API; each says what leaves the PC). With Local model or API, a button for the selected meeting names where the text goes and writes notes only when pressed, with progress and Cancel; a failure keeps the rule-based notes with the reason in yellow. Written notes are stored with the meeting with the model and the time, used by the Markdown export, and **Back to rule-based notes** discards them.
- Defaults saved under Settings → Meeting defaults while an older meeting is being finished now become the Meetings page's choices when the pass ends; a choice changed on the page before stays.
- The Meetings page chips announce their current choice to screen readers, such as "Meeting languages: English only".

### Meeting shortcut and a mirrored wave for both channels (2026-11-16)

- **Meeting shortcut** (then on the General page, today under Settings → Shortcuts) starts a meeting with the choices on the Meetings page without showing the window, and stops it as **Stop and finish** does. It is not set by default, must differ from the dictation shortcut, and does nothing in a diagnostic instance, while dictation is active or while a transcript is being finished (a tray notice says why).
- While a meeting captures, a minimal pill at the bottom of the screen shows the microphone upwards in white and the system audio downwards in a second colour; the Meetings page shows the same wave next to the status line (in the record card since 2026-11-17), and the tray shows the recording mark. The pill appears for every meeting and closes when the capture stops.
- **System audio colour** on the Meetings page (moved to Settings → Microphone and speakers on 2026-11-17): eight swatches, the accent blue by default, applied at once.

### Hold to talk (2026-11-16)

- **Shortcut behavior** sits next to the shortcut (then on the General page, today under Settings → Shortcuts). **Toggle** stays the default and works as before: press once to record, again to transcribe. **Hold to talk** records while you hold the shortcut and transcribes and pastes into the field selected at the start when you release it. A press that captured less than about 0.3 s of audio is discarded without transcribing or pasting. Escape still cancels, also while the shortcut's modifiers are held. Profile shortcuts follow the same setting.
- Settings saved before this version keep Toggle.

### Names, finding times, echo and meeting goals (2026-11-15)

- The meeting final pass uses the names and terms Private Flow already has: the dictation **Vocabulary**, the replacement side of short **Corrections** and the meeting **Title** are added to the meeting's own Vocabulary as hints for Whisper (superseded on 2026-11-17: meetings use the one shared Vocabulary page). The transcript itself is not rewritten: names, corrections and numbers stay as recognized.
- **Find evidence** (replaced on 2026-11-17 by the one search on the History page, which matches the same way) and the notes find a time however it was said or written: "12:34", "1234", "12.34", "twelve thirty-four", "doce y treinta y cuatro" and "zwölf Uhr vierunddreißig" match each other, a time Whisper wrote without its colon ("finished at 1149") is found as 11:49, and "minute 40" finds "minuto cuarenta". Names with digits are searched whole ("I2C" no longer finds "two"), a compact query finds its spaced form ("9am" finds "9 a.m."), and the German article "ein" is the number one only in a number or time ("um ein Uhr dreißig" is found as 1:30). **Build notes** (today the Notes tab on History) lists the times a meeting mentions, also digits after "to", "until", "hasta" or "bis" ("move the call to 345"), for you to check.
- Transcript rows end at the end of a sentence, so a time is no longer split over two rows. Show audio the microphone hears from the loudspeakers is removed only when the system channel says the same words at the same moment and the microphone row is at least 10 dB quieter than the speech on the microphone: a reply that repeats what was just said ("At 12:30?") stays, and with headphones nothing is removed. In the shared system-channel layout this keeps the replies (7 of 7 in the new test, 0 of 7 before) and leaves more show words in the microphone transcript where they share a row with the person's speech.
- The final pass decodes with a beam of 8 instead of 5, for about 3 % more time.
- A speech request longer than 30 s is refused at once instead of waiting five minutes for the worker.
- While the final pass runs after Stop, **Stop; process later** or **Cancel** (both **Cancel finishing** since 2026-11-16) ends it; the meeting keeps its live preview and its audio for Re-transcribe, and a new meeting can start.
- Engine or model paths typed on the Speech engine page (today Settings → Speech recognition) now also apply to meetings instead of the setup's large-v3; a meeting model that times out falls back to the speech model.
- The evaluation and diagnostic modes refuse to run on Private Flow's own data folder.
- Meeting quality is now judged on goals (docs/VALIDATION.md): the final pass against the per-utterance ceiling (met on real speech; not met on the synthetic sets, mainly in the shared system-channel layout), every recognized time and number findable (met, 100 %), names and critical items with the user's vocabulary (not met, 85.8 %), real speech as a floor (met, 97.5 % words, 93.7 % items), and the spoken times found by search and notes (met with the show on the system channel, 90.4 %; not met in the shared system-channel layout, where two people and the show share the system channel and are not separated, 36.5 %).

### Meeting final pass and better language handling (2026-11-15)

- **Stop and finish** now runs a final pass over the saved audio and replaces the live transcript, which is a preview while recording. It cuts at pauses, writes each stretch of speech in the most likely of the meeting's languages and decodes neighbouring stretches in one language together. On the evaluation suite, meeting word recall rose from 90.8 % to 94.9 % (synthetic), 87.2 % to 95.2 % (English/Spanish show discussion) and 64.4 % to 97.1 % (real speech). The finalizing state shows its progress; a 10-minute meeting takes about 1.5 minutes per recorded channel on an 8 GB NVIDIA GPU.
- The Meetings page has **Languages** (English, German and Spanish by default, or a pair, or one fixed language) and **Vocabulary** for names and terms. Both are kept for the next meeting. (Superseded on 2026-11-17: the languages are a chip with defaults under Settings → Meeting defaults, and meetings use the shared Vocabulary page.)
- Audio stays until the final transcript is saved, then it is deleted unless **Retain processed audio** is on. **Re-transcribe** (formerly Retry transcription) runs the final pass again, for example with other languages or a vocabulary; it replaces the transcript, including edits.
- Show audio that the microphone hears from the loudspeakers is dropped when the system channel says the same words at the same time (narrowed the same day to rows at least 10 dB quieter than the person, see the entry above).
- With an NVIDIA GPU, Automatic setup also downloads Whisper large-v3 (3.1 GB, once, in the background after this update) for the final pass of meetings and files. Dictation keeps large-v3-turbo, which is as accurate for dictation and twice as fast.
- Automatic language detection in dictation, files and meetings only chooses among English, German and Spanish; quiet speech is no longer written in Welsh. Recognition windows are cut in a pause instead of every 20 s, so a word is no longer split between two windows. Dictation latency rose by about 0.04 s.
- **Transcribe file** uses the meeting final pass.

### Speech engine selectors (2026-11-12)

- The Speech engine page (today Settings → Speech recognition) has **Runs on** (Automatic, GPU, CPU) and **Model** (Automatic, large-v3-turbo, medium, base) with **Apply**, which downloads what is missing and switches.
- Automatic follows the recommended setup. After each update Private Flow reapplies it once in the background and notifies when the model changed. Specific choices and hand-typed paths are kept. The update dialog no longer asks about the speech engine.
- Existing installs whose paths setup wrote count as Automatic, so the next update moves NVIDIA PCs from medium to large-v3-turbo. A setup Automatic would not pick, such as the CPU engine on an NVIDIA PC, stays an explicit choice.

### Integration builds from the update server (2026-11-07)

- `install.ps1 -FromCloud` replaces `-FromCI`: it wakes the update server, fetches the newest `integration` build (or `-CloudChannel`), and installs it only after its size and SHA-256 match the cloud listing. `install.ps1 -SetCloudToken` stores this PC's reader token DPAPI-encrypted in `%LOCALAPPDATA%\PrivateFlow\cloud-reader.token`.
- The in-app update check and **Update** use the same cloud build. The GitHub CLI is now optional: when signed in, it still lists the merged pull requests. Installed copies from before this change need one manual `install.ps1 -FromCloud`.

## Changed since 0.4.0

### One save rule for Settings, Ctrl + Alt shortcuts (2026-11-17)

- **One rule for every setting.** A changed value that is not stored yet shows **Unsaved changes** with **Discard** and **Save preferences** in the header and the dot on the settings button the moment it changes, in every section of Settings and with the chips on Dictate and Meetings. Before, only the dictation preferences did: Profiles per app, Meeting defaults (languages, retained audio, retention days, notes style) and the speech detection, keep-warm switches and model paths under Speech recognition each had their own Save button and showed nothing while unsaved, and the system audio colour was stored at once. **Save profiles**, **Save meeting settings** and **Save speech recognition settings** are gone; Save preferences stores everything, and Discard undoes everything. A system audio colour applies to the pill and the page wave once saved. Install, Apply, Set up Local polish, Run check, Export, Update, Quit and Delete stay immediate. Runs on and Model under Speech recognition are what Apply sets up; a choice there that differs from the setup in use now shows a note card, "Not applied yet". The next dictation uses the values shown, saved or not, as before.
- **Leaving with unsaved changes asks.** Closing Settings (close button or Escape), a page opened from outside it, such as by a review-first dictation or the tray, and Quit ask **Save your changes?** in the window, naming the sections, for example "Your changes under Language and pasting and Meeting defaults are not saved yet." **Save**, blue at the bottom right, has the focus and is pressed by Enter; it stores and goes on. **Don't save**, red text at the bottom left, discards and goes on. Escape goes back with the changes still pending. Before, closing Settings kept unsaved changes silently and Quit dropped them. When something cannot be saved, for example 0 retention days or a shortcut another app uses, Settings stays open at that section and says why on an error card there ("Preferences not saved", or "Dictation shortcut unchanged" for a shortcut); a save whose file write fails says "Nothing was saved, so nothing changed". Discard or the next successful save ends the card. Hiding the window to the tray keeps unsaved changes as before. Quit still drops unsaved transcript edits on History. **Update** asks about the update first and about unsaved changes only once it will really run; until Private Flow restarts a notice covers the window, so nothing can be changed that the restart would drop; Quit still works over it. An update that ends without restarting leaves a warning card under About and updates.
- **Saving no longer shares a temporary file with the setup scripts.** Each save writes its own uniquely named file next to settings.json and renames it over it; a setup script's settings.json.partial is never touched, and a file a crash left behind is removed at the next start.
- **Learn correction** on the Vocabulary page gives the correction the language of the dictation it came from again, as before this change, also when that language was chosen on Dictate but not saved; before the first dictation it takes the stored language.
- **Unsaved values never act.** Everything that runs (dictation, meetings, Delete expired meetings, Write notes, setup) uses only stored preferences, except that a dictation uses the choices shown for itself. Before, a dictation or a profile chosen in the tray copied unsaved values into the running preferences, so a meeting start or Save corrections and snippets stored them, and a Save whose file write failed left its values in effect after Discard: Delete expired meetings could delete meetings under a retention you never saved. Now a Save takes effect only once settings.json is written, and a failed one changes nothing. A meeting start stores only its own audio and language choices as the new defaults and leaves unsaved meeting defaults in Settings as they are.
- **Refused shortcuts from earlier versions** (Alt + F4, Win + L, Ctrl + Alt + Delete) are cleared at start, also on profiles, and named once on a warning card under Shortcuts, which the status line points to, and in a tray notice, for example "Alt + F4 was removed from the profile “Code”: …". A Save blocked by a profile's shortcut names the profile.
- **Ctrl + Alt shortcuts** such as Ctrl + Alt + M are accepted. Windows takes AltGr as Ctrl + Alt, so when the key types a character with AltGr on the keyboard layout in use, a warning card under Shortcuts (or Profiles per app) says so, and you can still save it: "Ctrl + Alt + M also is AltGr + M, which types µ on this keyboard; while Private Flow runs, that character cannot be typed with it." Where AltGr types nothing with the key (Ctrl + Alt + A, Ctrl + Alt + Space on German), there is no warning. Before, every Ctrl + Alt + letter, digit or punctuation key was rejected; this replaces that rejection in "Hold to talk, shortcut fields …" below. Still rejected, with the reason: plain keys without Ctrl, Alt or Win except function keys, Shift with a typing key, Escape, Tab and Enter, and Ctrl + Alt + Delete, Win + L and Alt + F4. A shortcut another app uses or Windows reserves is named under its field when saving.

### One interface (2026-11-17)

- **Meeting notes model install in the app.** While the notes style is Local model and the model is missing, the Notes tab on History and a **Meeting notes model** row under Settings → Meeting defaults offer **Install the meeting notes model (N GB)** with the download size. It runs the pinned, hash-verified setup script with its progress in both places, **Cancel installing** stops it, a failure is shown with the first error the script wrote, and once installed the Notes tab offers **Write notes with the local model**. Nothing is downloaded before the press. This replaces the Notes tab's advice to run `scripts\setup-rewrite.ps1 -Part notes`; the script still works by hand.
- Setup scripts (speech recognition, Local polish and the meeting notes model) run tied to Private Flow: quitting or a crash ends them and the downloads they started, and Quit waits for a cancelled install to stop. A cancelled notes model install removes its partial download. Setup errors on screen show file names instead of full paths. The install waits while a meeting records.
- On the Notes tab a citation reads as its time and speaker ("[04:30 · You]") and leads to that row on the Transcript tab; stored and exported notes keep the full citation.
- **Save edits** on History is the prominent button only while there are unsaved edits. Unsaved edits are compared with the saved meeting: choosing another meeting asks whether to discard them, a search or a finished pass keeps the meeting with them open, and saving during a search keeps the meeting shown.
- The Meetings page, About → Privacy, the Speech recognition status and the API notes style say what leaves the PC: the dictated text with Concise, and one meeting's title, languages, vocabulary and transcript text when its notes button is pressed with the API style. Sizes use the system's number format everywhere.
- Meeting notes by the local model check for the model before they take the shared local-model lock, so a missing model neither waits for a running Local polish dictation nor stops its warm server.
- While the local model writes notes, the progress line says that a Local polish dictation waits until the notes are done.
- The notes language of model notes counts only the spoken text, not the speaker labels, so "You" no longer counts as English.
- Model notes list at most 20 times mentioned and say "Times mentioned stop at 20; the transcript may name more." when the list reached that cap.
- A final pass waiting for GPU memory tries one last load after the wait, tries every 10 s instead of every 3 s while the meeting notes hold the GPU, and updates its step text when the notes end.
- A dictation whose speech model loads into too little free GPU memory says so in the status line while that load lasts ("GPU memory is low (…), so the speech model loads slowly").
- History: a search that leaves out the shown meeting no longer switches away from it while it has unsaved row edits; it stays shown and the status line says why. Clearing the search also clears **Only this meeting**. The API notes button names host and port ("notes.example:8443") when the port is not the scheme's default. The Notes tab removes only the header of the rule-based notes and keeps a line before it about a silent channel. **Re-transcribe** asks before it replaces unsaved edits, and a cancelled or failed Re-transcribe keeps them shown and unsaved with a notice.
- Notes built by rules: a short acceptance, with or without a date ("Sure, by Friday.", "Ja, bis Freitag.", "Sí, para el viernes.", "Gerne.", "On it."), keeps the action item, with the date as its due date. A short reply that says something else is still no acceptance.
- Hold to talk: after a failed microphone start in Hold to talk, Escape during a later file transcription no longer cancels it. The documentation now says what the 0.3 s threshold measures: a press that captured less than about 0.3 s of audio is discarded.
- Meeting wave: the wave redraws only while a bar moves, and the page wave stops while the Meetings page is not shown.
- Consistent wording:
  - About and updates → Privacy, the Concise API connection description and the Speech recognition status say that transcript text goes to the API only with the Concise style, or for one meeting when you press its button with the API notes style.
  - Dictation languages read Automatic, English, German and Spanish everywhere, also in the Profiles per app and Vocabulary tables, which no longer show codes. The Profiles style column shows "Local polish" instead of "LocalPolish"; profile shortcuts show as "Ctrl + Alt + P" or "Not set".
  - Vocabulary: Import and Export name "corrections and snippets"; **Load from Dictate** replaces "Load editor text".
  - Speech recognition: **Save speech recognition settings** replaces "Save engine settings"; **Set up Local polish** is the Local polish setup button. Messages ask to finish "the setup or check under Speech recognition" instead of a "speech engine operation".
  - Dictate: the hint under the shortcut says "Release it" with Hold to talk; **Cancel** uses the same quiet style as the Meetings page's secondary action.
  - History export choices read **Notes with cited rows** and **Meeting backup with kept audio**.
  - The Settings search finds Text style by "wait", "url" and "variable", and Meeting defaults by "install", "download" and "qwen".

### Faster dictation start, and meetings that say when the GPU is busy (2026-11-17)

- Dictation loads the speech model when recording starts, not when it ends. After the model was unloaded (Keep speech model warm off, five idle minutes, a restart or a meeting), the text now arrives 0.6–1.2 s after you stop instead of 2.6–4.1 s, as fast as with a warm model, for anything you say for longer than about two seconds.
- A meeting's final pass no longer waits about 2 s at the end to load the dictation model again. The meeting model's GPU memory is freed at once, and with Keep speech model warm on the dictation model loads in the background.
- When other GPU work (a game, a local language model, meeting notes being written) leaves too little GPU memory for the meeting model, the final pass no longer loads it anyway and slows to a crawl for minutes. It shows "Waiting for GPU memory" with the free and needed memory for up to 30 seconds (while Private Flow writes meeting notes with its local model, it waits for the notes to finish and says so), then finishes with the smaller speech model, or, when nothing fits, keeps the audio and says to close what uses the GPU and finish the transcript again.
- Dictation still loads when GPU memory is low, and its result then notes that recognition may have been slower. Only a model larger than the whole GPU is refused at once; the status line says why, and with the window hidden a tray notice does, without opening the window.
- Any failure of the meeting model, also one while waiting for memory, gets one more try with a fresh worker before the speech model takes over. A model that is still loading slowly is given up only after 60 s without any progress or after 3 minutes.
- When the speech model finishes a meeting instead of the meeting model, the final message, the tray notification and the top of the meeting's notes say so and why, and the meeting record keeps which model produced the transcript. Transcribe file says it in its final message. The progress bar continues from where it stood instead of dropping to zero.
- Recognition is unchanged: same models, beam size, prompts and language choice, and identical transcripts on the same audio.

### A Meetings page like Dictate, settings titles that say what is set, and a meeting output device (2026-11-17)

- The **Meetings** page has the shape of the Dictate page: the meeting shortcut as key caps (or a link to set one under Settings → Shortcuts), one card with the meeting title, the live transcript (the final one after Stop), the mirrored wave and the round record control (**Start meeting**, then **Stop and finish**, then **Continue in background** while the transcript is finished), and chips for the microphone, the system audio, the languages and keeping the audio. Unfinished meetings and the silence warning appear under it only when they apply. A double click on Start meeting no longer reaches Stop and finish.
- Settings sections are named by what is set there: **Microphone and speakers** (was Audio), **Shortcuts**, **Language and pasting** (was Dictation), **Text style** (was Output style), **Profiles per app** (was Profiles), **Meeting defaults** (was Meetings), **Speech recognition** (was Speech engine) and **About and updates** (was About). The search still finds them by their old names.
- **Output device** under Settings → Microphone and speakers chooses whose sound a meeting records: **Follow Windows default output** (the default, as before, also for settings saved earlier) or a specific device. A chosen device that is gone when a meeting starts falls back to the Windows default, and the page, the status line and, with the window hidden, the tray say so. The meeting record keeps the device actually recorded.
- The microphone chosen on the Meetings page's chip, like the Dictate page's, is the selection under Settings → Microphone and speakers; a meeting now records that selection even before it is saved, as dictation does.

### Settings in a popup, a History page and one shared Vocabulary (2026-11-17)

- The sidebar lists **Dictate**, **Meetings**, **History** and **Vocabulary**. The settings button next to the status line opens **Settings** over the dimmed window, with its own sections (Audio, Shortcuts, Dictation, Output style, Profiles, Meetings, Speech engine, About; renamed by content the same day, see above), a search and a close button; Escape closes it. The sidebar search still finds settings and opens Settings at the matching section. Unsaved changes stay when Settings closes; a dot on the settings button shows them until you save or discard.
- **History** holds what the Meetings page called Archive, Search and notes, and Delete expired meetings. The Meetings page keeps recording, its choices and Unfinished meetings, and shows the live transcript in its record card.
- **Vocabulary** holds names and terms, corrections and snippets. Meetings use the same names and terms as dictation; the separate meeting vocabulary is gone, and one saved before is merged once into the shared vocabulary without duplicates. Meetings recorded with their own vocabulary keep it for Re-transcribe.
- One microphone setting under **Settings → Audio** (today Microphone and speakers) serves dictation and meetings; the system audio colour moved there too. **Settings → Meetings** (today Meeting defaults) holds the languages and audio retention new meetings start with and the retention days.

### Speech model large-v3-turbo (2026-11-12)

- Speech setup on PCs with an NVIDIA GPU now installs Whisper large-v3-turbo (8-bit, 874 MB) instead of medium (1.5 GB). On generated speech it transcribes about twice as fast, loads in about half the time and made about as many errors as medium (6 against 5 of 114 words). The CPU setup keeps base.
- Existing installs keep their model. To switch, answer Yes to the engine question when updating, or use **Set up speech** on the Speech engine page. (Superseded the same day by the speech engine selectors above: Automatic installs follow the recommendation after an update, and **Apply** under Settings → Speech recognition switches by hand.) After switching, `models\ggml-medium.bin` (1.5 GB) is unused and can be deleted.

## Fixed since 0.4.0

### Hold to talk, shortcut fields, install progress, notes (2026-11-17)

- **Hold to talk** should no longer flicker, not yet retested with real keys. Holding the shortcut kept starting and discarding recordings, so the pill flickered and nothing was transcribed: Windows sent the shortcut's hotkey message again while the keys were still held, and each repeat was taken as a release, which discarded the short recording, and then as a new press, which started the next one. Now a hotkey message for the shortcut that started a running hold-to-talk recording is ignored while that shortcut's keys are down at that moment; the recording goes on until the release, as before. A hold that captured under 0.3 s of audio is still discarded, and the bounded wait for the modifiers before the paste is unchanged. Toggle, Escape, the meeting shortcut and profile shortcuts in toggle mode behave as before. If an app run as administrator is in front, Windows may not let Private Flow read the keys, and hold to talk still discards at once.
- **Shortcut fields** under Settings → Shortcuts react to every key. Pressing Ctrl, Alt, Shift or Win shows "Ctrl + Alt + … Keep holding and press the last key of the combination." A rejected combination is named, with the reason, for example "Ctrl + Alt + M: Ctrl + Alt with a letter, digit or punctuation key types a character with AltGr on keyboards such as German, so it is reserved. Use Ctrl + Shift, Ctrl + Alt + Space, Win, or a function key." Before, every rejection, including a single modifier key, showed one general grey text that looked much like the field's description. The profile shortcut field works the same way.
- A shortcut that Private Flow has registered, pressed in a shortcut field, reaches Private Flow as that shortcut and not as keys of the field. Before, nothing happened. Now the field says what it is: "Ctrl + Alt + Space is the current dictation shortcut.", or that it is already the meeting shortcut or a profile's shortcut. In the profile shortcut field it no longer starts a dictation.
- While a dictation or a file transcription runs, the shortcut fields lose their field look and the Shortcuts section says "Locked while a dictation or a file transcription runs. The shortcuts can be changed once it has finished." Before, the locked fields looked almost like enabled ones and ignored clicks.
- **The meeting notes model install** shows its progress on the same bar as the end of a meeting's transcription: the step the setup script names ("Downloading Qwen3.5-4B-Q4_K_M.gguf") with its share from the script's percentages. The bar never falls and keeps its value through steps without a percentage. It shows on the Notes tab next to **Cancel installing** and in the Meeting notes model row under Meeting defaults. **Set up Local polish** and **Apply** under Speech recognition show their steps on the same bar. The speech setup script prints no percentages, so its bar stays at 0 % and shows the named step and the time taken.
- **Notes the model found nothing in** are a result, not a failure. When the model lists no decision, action item, open question or time and drops none, the Notes tab says in one neutral line "<model> found no decisions, action items, open questions or times in this meeting." Its summary alone is still not stored as notes. An answer whose points all lacked a real transcript row is still a failure.
- **A failed or empty run keeps what was shown.** Notes a model wrote before stay on the Notes tab, with one line about the new run above them. Before, they were replaced by the rule-based notes until the meeting was reopened.
- **One yellow line at most** on the Notes tab. The caveat of the rule-based notes is shorter: "Built by rules on this PC without a model: a rough draft that misses most decisions and open questions." It keeps the link to the notes setting. A failure, a failed install of the notes model, or a transcript changed since the notes were written takes its place, each as a short line with the full text in its tooltip. Notes written for an older transcript say so first in every state, for example "The transcript changed since these notes were written; writing them again failed: …". A cancelled run is a neutral line. The status line no longer repeats the reason; it points to the Notes tab.
- The diagnostic instance takes a saved dictation shortcut as if it had registered it, so the Dictate page shows the new key caps there too.

### Escape after the wait, notes kept (2026-11-17)

- A **Local polish** dictation that waited for meeting notes takes Escape back as soon as the wait ends, when its polish starts or is skipped for the time limit, until the paste is done; in Hold to talk, Escape with the shortcut's modifiers held works again too. Before, from the end of the wait to the paste, only Cancel in the window could stop a paste that came long after speaking. Escape or Cancel while the polish runs after the wait keeps the Clean text on the Dictate page, pastes nothing and leaves the clipboard untouched, as Cancel during the wait does. During the wait itself Escape stays with the other programs.
- The status line and tray say what a queued dictation is doing: "waits for the meeting notes" only during the wait, then "Polishing the dictation that waited for the meeting notes", each with how to keep the Clean text.
- **Delete expired meetings** does nothing while notes are written for an expired meeting and says which meeting and to wait for the notes or cancel them, as Delete meeting, Re-transcribe and Finish transcripts do. Before, the meeting was deleted and the notes could not be saved.
- **Write notes** with unsaved transcript edits asks first, in the window, whether to save them and write the notes from the saved transcript; Cancel neither saves nor sends anything. Before, it saved the edits without asking.
- The progress line of notes written by the local model no longer only says "You can keep using Private Flow": it adds that a Local polish dictation waits for the notes and locks the page meanwhile, and that Cancel on the Dictate page keeps that dictation's Clean text and unlocks the page. Every dictation locks the Meetings and History pages until it is done, so the notes' Cancel cannot be reached during that wait.
- A notes request that starts while Private Flow quits fails with "The local model was stopped." instead of an internal error text.
- Correction to "Notes kept, queued dictations kept, fewer false items" below: Quit during the wait pastes nothing and leaves the clipboard untouched; the app then closes, and the recognized text is not kept.

### Notes kept, queued dictations kept, fewer false items (2026-11-17)

- **Finish transcripts** no longer saves an unfinished meeting over notes that are being written for it. While notes are written for one of the unfinished meetings it does nothing and the status line says which meeting and to wait for the notes or cancel them; Re-transcribe and Delete meeting already did so. Write notes still waits while a transcript is finished, and notes written before a pass stay with the meeting.
- A **Local polish** dictation that waits for meeting notes on this PC no longer holds Escape for the whole wait: once its speech is recognized, Escape goes back to the other programs, so pressing it there neither reaches Private Flow nor cancels the dictation. **Cancel** on the Dictate page during the wait now keeps the recognized text: it shows the Clean text on the Dictate page with "Used Clean: the polish was cancelled.", pastes nothing and leaves the clipboard alone. Quit during the wait pastes nothing and leaves the clipboard alone; the app closes, so the text is not kept. Before, one Escape anywhere during the wait discarded the text. This replaces "Escape cancels it at once" of "A meeting notes model on this PC".
- Notes built by rules no longer list a bare acceptance such as "Will do." or "Mach ich." as an action item without a task when the row before it is not recognized as a request ("Call me back at 1230 about the invoice."). A commitment that names its task is still listed.
- "At 12 15 percent were ready" no longer lists the time 12:15 in the notes: two-digit minutes followed by a unit or count word are a quantity, as single digits already were. "At 12 15", "12 15 pm" and "at 12 15 we meet" are still 12:15. Search is unchanged.
- Numbers and dates in the interface follow English, not the region format of Windows: "3.4 GB" and "42%" instead of "3,4 GB" and "42 %". This replaces "Sizes use the system's number format everywhere" of "One interface".
- The Notes tab says once who wrote model notes: the line above them names the model and the time, and the notes text on the tab no longer repeats "Written by the model …". Stored and exported notes keep it.
- Cancelling the install of the meeting notes model removes only that install's partial downloads, not partial downloads of the speech setup or of a Local polish setup.
- Writing notes with the local model stops a warm Local polish server and the notes server off the interface thread, so the window no longer freezes for up to 10 s while a server exits.

### A time followed by a quantity is not a time (2026-11-17)

- "Monday at 9:00, one hour" lists only 9:00 in the notes, no longer also 9:01, and a search for 9:01 no longer finds it; the same in German and Spanish. Punctuation between an hour and a number ends the time, a single-digit number followed by a unit or count word (hours, minutes, percent, euros, people; Stunden, Prozent; horas, por ciento, personas, and more) is a quantity, and nothing after a time that has its minutes adds minutes ("at 9 15 30 of us" is 9:15).
- "At twelve five", "12 oh 5", "doce y cinco", "zwölf Uhr fünf" and "12.05 Uhr" are still 12:05. No longer read as 12:05: "at twelve, five" with a transcribed pause and "las doce y cinco minutos".
- The meeting evaluation on the installed engine was identical before and after (docs/VALIDATION.md).

### Meetings finish reliably, show their progress and warn about a silent microphone (2026-11-16)

- After **Stop and finish** the Meetings page names each step (stopping the capture, loading the model, detecting speech, detecting languages and transcribing per channel, saving) with a percentage that ends at 100 % and the seconds the step has run. Before, the page showed a fixed text for up to a minute and the percentage stopped at 50 % when a channel had no speech.
- Finishing runs in the background and the window stays responsive. **Continue in background**, closing or minimizing the window hides it while a small window with a taskbar progress button shows the steps; a tray notification reports the result. Dictation stays unavailable until the transcript is saved.
- **Cancel finishing** (formerly **Stop; process later** during finishing) says what it does; **Stop; process later** while recording is now **Stop; finish later**. A capture that stops on its own (a removed device, a full disk, a failing live preview) no longer ends the meeting unfinished: the recording continues after a preview failure, and what was saved is finished.
- Meetings that were not finished (cancelled, failed, Quit or a crash) are listed under **Unfinished meetings** with the size of the audio kept for them and announced in the tray at start. **Finish transcripts** finishes all of them in one action; **Delete their audio** keeps the live preview as the transcript. A failed pass is not retried on its own.
- With **Retain processed audio** off, audio stays on disk only for a meeting that is not finished, and the page says how much. Audio that an earlier run left in finished meetings is deleted at start. A chunk whose write completed just before a crash is recovered.
- A microphone that delivers no sound for 10 seconds is shown on the Meetings page while it lasts and announced once per meeting in the tray. The recording indicator and the meeting record name the microphone and the playback device; when a channel stayed silent for the whole meeting, the final message and the notes say so.
- Transcribe file shows its progress as a percentage that also moves while speech and languages are detected.

# Private Flow 0.4.0

One window: settings and the former separate workspace window are now pages of
the main window. A sidebar with search (`/`) and grouped pages, a breadcrumb
header with each page's actions, and grouped settings cards replace the long
single page and the tabbed workspace. Dictate shows the shortcut as key caps,
the last transcription in a composer, and pickers for microphone, language,
output style and auto-paste. Preferences show Save and Discard only while they
differ from what is saved. Engine and model paths moved from Advanced to the
Speech engine page and are saved there. While a meeting records, closing the
window opens the Meetings page instead of hiding it in the tray. (The page
layout was superseded on 2026-11-17: the sidebar lists Dictate, Meetings,
History and Vocabulary, and settings open in the Settings popup, where the
Speech engine page is Speech recognition.)

Smart App Control: the speech worker no longer loads all nine CPU backend
variants to pick one. Smart App Control rates each unsigned file separately and
blocked unused variants, so dictation triggered "part of this app was blocked"
alerts. It now loads only the GPU backend and the best CPU backend, falls back
to the next variant when one is blocked, and reports a blocked core file by name
instead of "could not initialize". See README, Smart App Control.

Setup and installation now honor an absolute custom data directory and relative
install paths. Relative data-directory overrides are rejected before setup. The setup page (today Settings → Speech recognition) refreshes model paths after setup and reports setup errors. Setup
scripts run only from the application folder. Remote Concise endpoints require
HTTPS; HTTP remains available for loopback servers. Redirects are rejected. Invalid meeting manifests
no longer hide valid meetings, and backup exports can safely replace an existing
archive. Rejected local rewrites and cancellation keep an initialized local model
loaded; a cancelled model start is cleaned up. Speech checks reject a missing
configured VAD model.

Spoken formatting and correction features: spoken numbered items and
an email layout for greeting and closing (Spoken formatting), a tray warning when
the microphone delivers silence, and a longer clipboard hold for Office, Teams and
remote-desktop clients, which read the clipboard late. Clean no longer lets a
bracketed filler (", uh,") swallow a dictated line or paragraph break.

Backtrack: spoken self-corrections work again in Concise and Local polish, and
Clean now applies clear ones on this PC. Since 0.4.0's integrity guard, every
correction whose cue contained "no" ("no wait, Maria", "wait, no, Vendor C") was
rejected as a removed negation, and the uncorrected Clean text was pasted with a
"Used Clean" note. The "no" of such a cue is now exempt; real negations stay
protected. Clean replaces a value after an explicit cue when both values are of
one kind (number with unit, weekday, month, repeated label) and removes a sentence
withdrawn with a standalone "Scratch that." / "Streich das.". The rewrite prompt
adds Spoken cues ("actually", "make that", "scratch that", "nee", "Moment",
"Korrektur") and keeps "wait, not X" as a stated exclusion.

The voice workspace now includes persistent/reconnecting microphone selection,
a warm isolated native speech worker, Silero speech detection and bounded long
dictation. Rewrite responses must be complete and preserve protected facts;
fallback to Clean is visible.

Local corrections, explicit learning, protected spoken snippets,
English/German formatting, literal/technical modes and task profiles are editable
in the workspace. Profiles support tray selection, safe shortcuts and optional
executable matching. The latest result/difference stays recoverable in memory.
Local files transcribe to a review result with TXT/Markdown/SRT export. The
synthetic speech check and metadata-only diagnostics export cover the speech
setup. Optional local polishing uses pinned llama.cpp/Qwen downloads and CPU inference.

Meetings are a separate explicit microphone/system-audio workflow: durable local
channel chunks, recoverable pending transcription, timestamps, editable speaker
labels, extractive notes/actions with evidence, search across meetings and cited excerpts
for cross-meeting questions (the excerpts box was replaced on 2026-11-17 by the
one search on the History page). Processed audio retention is off by default; expired
completed meetings are deleted explicitly. Private ZIP export supports backups.

The workspace and meeting pages share the main settings look: section headings,
labelled fields, dark tables with switches and slim scroll bars.

Automatic paste remains enabled by default and preserves cancellation, the
start-window target, password/self exclusions, modifier checks and clipboard
restoration. No Enter events are sent. Audio and native ASR stay local; only
explicit API Concise sends transcript text (since 2026-11-16 also the API notes
style, for one meeting when its button is pressed). Application matching cannot opt in.

The synthetic and isolated checks are recorded in VALIDATION.md. Real device,
loopback and cross-app trials remain manual. This release does not claim
automatic remote-speaker diarization or generated semantic archive answers.

# Livelier recording pill and update prompt (2026-10-09)

- The recording pill now shows how loud you are speaking, measured every 25 ms on a decibel scale. The newest level appears in the middle bars and moves outward over about a quarter second. Pauses between words show as dips; loud and quiet syllables differ. Before, a single peak value scaled a rolling sine pattern, and normal speech filled it to the top. With reduced motion, the bars follow the current level in place. The transcribing wave is unchanged.
- (Superseded on 2026-11-07: the check reads the update server, and the GitHub CLI is optional.) Installed copies check for a newer build shortly after start and every six hours. When there is one, a tray notification (shown between dictations, once per build) offers the update. **Update** and the tray menu then read "Update available"; the dialog lists the merged pull requests before installing. The check only reads build and commit data from GitHub; without gh, a network or sign-in, it stays silent.

# Recording duration fix (2026-10-06)

- Removed the timer's automatic stop at three minutes. Recording continues until you press the shortcut again, click Stop, cancel or quit. Audio remains local and buffered in RAM; the separate five-minute transcription timeout remains.

# Private Flow v0.4.0 (prepared)

- Better recognition: setup installs the CUDA engine and the multilingual medium model when an NVIDIA GPU is present (base on CPU-only PCs). Rerun `scripts\setup-engine.ps1` to upgrade an existing install. (Superseded on 2026-11-12: NVIDIA PCs get large-v3-turbo.)
- New Vocabulary preference: names and terms you use (e.g. "Claude Code, Codex, GPT") steer Whisper's spelling and Concise's corrections.
- Concise applies spoken corrections: "I did that with Claude Code, sorry, I meant Codex" becomes "I did that with Codex". Quoted text (literal quotation marks or spoken "quote … end quote") never leaves the PC editable: it is sent as a token and restored verbatim.
- Concise no longer falls back to Clean on longer dictations: its timeout grows with the text (10-45 s instead of a fixed 8 s). Transcripts are delimited so the model does not answer dictated questions. Output that looks like a reply (much longer, fewer question marks than dictated, or an unsaid "Sure"/"Certainly" opener) falls back to Clean; this is a heuristic, and a correction replacing a whole question also falls back. The default rewrite model is now gpt-6-luna.

- Spoken "quote … end quote" becomes quotation marks in Clean and Concise, so Concise no longer pulls the following sentence into a quote. Whisper's segment line breaks no longer split pasted text mid-paragraph.

Verified locally: zero-warning build, 158 safety/preference checks, medium CUDA plus vocabulary on synthetic speech, live Concise correction cases through the configured API.

# Private Flow v0.3.0 (prepared)

- Paste works in browsers, Word, editors, Windows Settings and other custom fields: the text goes into the field selected when recording started, via clipboard and Ctrl+V, with the previous clipboard restored and dictations kept out of clipboard history. Password fields are excluded.
- When a paste is not possible the text is left on the clipboard with a tray notification; the settings window no longer pops up over your work.
- New look: plain black settings window with white type, no visible scrollbar and controls that brighten on hover; dark title bar. (The separate settings window was superseded by the one window of 0.4.0.)
- New recording pill: black with a white outline and 18 flowing bars, no text.
- Private Flow icon for the exe, taskbar, Start menu and tray (with recording/transcribing states).
- `scripts/install.ps1` installs per user without the SmartScreen warning, sets up the engine, adds a Start-menu entry and optional autostart.
- Output styles replace the whitespace-only toggle: Exact (1:1), Clean (default, on-device filler removal) and Concise (opt-in API rewrite with Clean fallback). Settings migrate to Clean once.

Verified locally: zero-warning build, 100 safety/preference checks, isolated cross-process paste check (plain field, rich document field, refused password field, clipboard restored), UI smoke test, real-engine synthetic speech, live Concise rewrite through the configured API.

# Private Flow v0.2.0

- Press the shortcut once to start recording, again to transcribe; holding keys is unnecessary. Configure the combination in Recording shortcut and Save preferences. Conflicts preserve the previous working binding.
- A small, floating dark pill shows microphone-reactive rounded waveform bars while recording and a travelling wave while transcribing. It stays click-through and does not steal focus; processing animation respects reduced-motion preferences.
- Automatic insertion is enabled by default, with one migration of legacy settings. Later saved opt-outs persist. Existing focus/password/cancellation safeguards continue to route unsafe targets to preview.
- Close and minimize hide to the tray. Quit stops the app.

Verified locally: zero-warning Windows build, 40 safety/preference checks, isolated UI defaults/Quit, synthetic recording/processing animation and focus retention. Real microphone and cross-app insertion remain on the manual validation matrix.

# Private Flow v0.1.0

First Windows-only local dictation MVP: push-to-talk, microphone picker, nonactivating recording indicator, English/German/auto language selection, editable preview, original text, light whitespace cleanup, cancellation, and optional guarded Unicode insertion. Automatic insertion is off by default. Audio and inference stay local. (Superseded: v0.2.0 made the shortcut a toggle and automatic insertion the default, v0.3.0 replaced Unicode insertion with clipboard paste, and Hold to talk returned as an option on 2026-11-16.)

Download the portable zip, extract it, and run `scripts/setup-engine.ps1` once to install the hash-verified CPU engine and multilingual model. Then run `PrivateFlow.exe`. Internet is required only for setup, not dictation.

Verified locally: build, 18 safety checks, real-engine synthetic English speech with negation/number preservation, temporary-file cleanup, and native UI initialization/Quit. Actual microphone and app-insertion trials remain on the manual validation matrix. This first release does not include richer rewriting, vocabulary/snippets, or meeting notes.
