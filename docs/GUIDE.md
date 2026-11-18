# Private Flow user guide

A personal tool published as is, without a support or maintenance promise.

A private Windows voice workspace. Press **Ctrl + Alt + Space** once, speak,
then press again to transcribe and paste into the window selected at start.
The shortcut is configurable, also as Ctrl + Alt with a letter such as
Ctrl + Alt + M. On a keyboard with AltGr, such as German, Windows takes AltGr as
Ctrl + Alt, so a warning card under Shortcuts says when AltGr types a character with that key
(AltGr + M types µ); while Private Flow runs, that character cannot be typed with
AltGr: pressing AltGr + M fires the shortcut instead. The warning follows the
keyboard layout active in Private Flow at the time; in an app that uses another
layout, the shortcut can take over an AltGr character there without a warning
having been shown. To hold the shortcut while you speak instead, choose **Hold to
talk** under Settings → Shortcuts: releasing it transcribes and pastes,
and a press that captured less than about 0.3 s of audio is discarded. Audio and speech
recognition stay on this PC. Text, never audio, goes to the API you configure
only as the dictated text with the Concise style, or as one meeting's title,
languages, vocabulary and transcript text when you press its notes button with
the API notes style. Windows 10/11 x64; Mac and mobile are outside scope.

## Dictation

The tray app starts hidden. Double-click its icon to open Private Flow. One
window holds everything: the sidebar lists Dictate, Meetings, History and
Vocabulary. The settings button (the gear) at the bottom of the sidebar, next to the
status line, opens Settings over the dimmed window: Microphone and speakers, Shortcuts,
Language and pasting, Text style, Profiles per app, Meeting defaults, Models,
Speech recognition and About and updates, with their own search;
Escape or the close button closes it. Press `/` to search; the sidebar search
also finds settings and opens Settings at the matching section. The header
shows where you are and the page's actions. Every setting in Settings, and the
chips under the Dictate and Meetings composers, works the same way: a changed
value shows **Unsaved changes** with **Discard** and **Save preferences** in
the header, and a dot on the settings button, until you save or discard it. The
next dictation uses the values shown; everything else changes when saved.
Install, Apply, Set up, Run check, Update and Delete act at once. Unsaved values
never act elsewhere: meetings, Delete expired meetings, Write notes and setup use
the stored ones. Leaving
Settings, opening a page from outside it, Quit or a confirmed Update with unsaved changes asks
**Save your changes?** first: **Save** stores them, **Don't save** discards
them and Escape goes back. When something cannot be saved, Settings stays open
at its section and says why on an error card there. Closing the
window keeps it running in the tray; while a meeting records, it opens the
Meetings page instead. **Quit** under Settings → About and updates exits. A waveform pill and
red tray mark indicate recording. Stop explicitly or press Escape to cancel.
There is no recording-duration cutoff and no Enter key is sent.

Warnings, errors and notes about an outcome show as cards above the content they
concern, on every page and settings section: a yellow edge for a warning, red for
an error and blue for a neutral note, each with a short title, one or two lines,
sometimes a link such as **Choose how notes are written**, and an **x**. Several
cards pile up with the newest in front and the edges of up to two more behind it;
**N more · show all** or a click on the front card spreads them out, **Pile up**
piles them again. The x hides a card until its condition changes or its context is
opened again (the meeting is selected again, the operation runs again, a new
dictation starts); it never changes the condition itself. A card that asks for
something only you can settle, such as setting up speech recognition before the
first dictation, has no x; neither has "No sound from the microphone" while a
meeting records, which ends by itself when sound returns. A long reason is cut
after its first sentence; the card's tooltip has all of it. Cards about one
meeting belong to it on the History page: they stay while another meeting is
selected and show again when it is. The status line at the bottom of the
sidebar shows progress and confirmations such as "Recording • 12s" or
"Preferences saved". A warning or an error raised on a page you do not see takes
its place, naming the page ("System audio from the Windows default. See
Meetings."; a click opens it), and that page gets a dot in the sidebar (on the
settings button for a settings section) until you open it or the notice ends.
With the window hidden, the tray notice says it as before.

Under Settings → Microphone and speakers, choose **Follow Windows default
microphone** or a specific microphone; dictation and meetings use the same one.
The endpoint ID is saved with preferences; connecting/removing devices refreshes
the list. A disconnected pinned microphone produces an error, rather than
silently switching to another device. The same page holds the **Output device**
whose sound meetings record (see Meetings).

Local Whisper transcription uses a pinned whisper.cpp runtime, a multilingual
model and Silero speech detection. A private child process holds the model for
subsequent requests and unloads after five idle minutes. The model loads when
recording starts. When other GPU work leaves too little free GPU memory, it still
loads; a card on the Dictate page says "GPU memory is low (…), so the speech model
loads slowly" while it loads, and after the result that recognition may have been
slower. Only a model larger than the whole GPU is refused, with the reason in an
error card on the Dictate page (a tray notice while the window is hidden). Audio is processed in
windows of at most 20 seconds, each cut at the quietest moment of its last
6 seconds, through a bounded RAM queue. An overloaded
queue ends the session without inserting a partial result. **Automatic** language
detection chooses among English, German and Spanish only. Only the final
dictation is pasted after Stop. Language and processing preferences are frozen
when recording starts. The names and terms on the **Vocabulary** page provide
preferred spellings as soft hints, for dictation and meetings.

Four text styles are available under Settings → Text style:

- **Exact:** recognized words, with whitespace cleanup only.
- **Clean:** local filler/stutter removal; preserves technical tokens, numbers,
  units and negation. Spoken quotation markers become quotation marks.
- **Concise:** explicitly selected API text rewriting. Transcript text leaves
  this PC; audio never does. The API key is read from an environment variable,
  never saved in settings. The default endpoint is the configured configured API.
- **Local polish:** optional llama.cpp/Qwen rewriting entirely on this PC. It
  runs the Local polish model (Qwen3-4B) on the processor, or, chosen under
  Settings → **Models**, in the Local processing models card, the meeting notes model (Qwen3.5-4B) on the
  GPU, so one download can serve both features. Install either under Models.
  Chosen without its model, a card under Text style says so and leads there.

**Backtrack (spoken corrections).** Clean, Concise and Local polish apply
corrections you speak. On this PC, without a model, Clean replaces a value when an
explicit cue (`no wait`, `wait, no`, `sorry, I meant`, `actually`, `correction`,
`make that`, `nein, ich meine`, `Korrektur`, …) sits between two values of one
kind: a number with the same unit, a weekday, a month or a repeated label.
"Use Vendor B, no wait, Vendor C" becomes "Use Vendor C"; "um 15 Uhr, nein, um
16 Uhr" becomes "um 16 Uhr". A multi-word cue may also start a new sentence
("Tuesday. Sorry, I meant Wednesday.") when the repair ends that sentence.
"Scratch that" / "Streich das" withdraws what came before it: the previous
sentence when it stands alone or starts a sentence, the clause before it when
commas set it off inside a sentence. Names, phrases and restarts ("send it to
Alice, sorry, I meant Bob") need a rewrite style. "Wait, not X" stays as spoken:
it can exclude X. Quotations, Exact and Literal mode are never corrected.

Both rewriters require complete responses and preserve protected facts, quotes
and snippets. Technical mode also checks protected technical spans. Failure,
timeout or an ambiguous rewrite visibly falls back to Clean. These checks are
conservative safeguards, not proof that a rewrite preserves every meaning.
Transcripts over 12,000 characters use Clean rather than risking truncation.

**Auto-paste is on by default.** Password fields and Private Flow itself are
excluded. Paste uses clipboard/Ctrl+V, excludes the text from Windows clipboard
history/cloud sync, and restores the previous clipboard. Held modifiers block
insertion. If insertion is unavailable, the text stays on the clipboard and a
tray notification offers manual paste. Uncheck Auto-paste and Save preferences
to review instead. Session IDs reject cancelled or stale results. Outlook, Word,
Excel, PowerPoint, OneNote and Teams keep the dictated text on the clipboard for
3 s, remote-desktop clients (mstsc, Windows App, Citrix, VMware, AnyDesk,
TeamViewer) for 5 s, other apps for 0.4 s, because they read it late.

If the microphone delivers silence for the first 3 seconds (muted, wrong input
channel or device), Private Flow warns once in the tray while recording continues.

The latest original/final result and a line difference stay in RAM. **Recover
the last result** on Dictate or in the tray restores it; Clear or exit forgets it.
No persistent dictation history is enabled. **Transcribe file** processes local
audio with the meeting final pass (cuts at pauses, a language per stretch of
speech) and opens a review result without pasting.
Export text/Markdown or the original timestamped speech as SRT.

## Vocabulary and profiles

Open **Vocabulary** in the sidebar: names and terms, corrections and snippets.
Names and terms form a list. Type one and press **Add** or Enter; a pasted list
separated by commas, semicolons or line breaks adds each term. A term already in
the list, in any case, is not added twice and is named below the field. Select
terms with a click, Ctrl+click, Shift+click, their boxes or Ctrl+A, and remove
them with **Delete selected** or the Delete key. Like the other preferences, the
list is kept with **Save preferences**, and **Discard** brings deleted terms back.
Text still typed in the field counts too: **Save preferences** adds it to the
list, a dictation uses it, and **Discard** clears it.
A term cannot contain a comma or a semicolon; to change one, delete it and add it
again.
Corrections replace whole spoken phrases;
snippets insert plain text once, with longest-match precedence and protected
bodies that bypass polishing. Quotes and technical tokens remain literal.
Each entry has a language: English, German, Spanish, or Automatic, which applies
to all three. Collisions between aliases and snippets must be resolved before
saving. **Import** and **Export** read and write the corrections and snippets as
JSON. **Load from Dictate** and **Learn correction** teach a correction from the
text on the Dictate page; edits in other applications are never monitored.

Enable **Spoken formatting** for `new paragraph` / `neuer Absatz`, `new line` /
`neue Zeile`, punctuation such as `comma` / `Komma`, and `bullet point` /
`Aufzählungspunkt`, `numbered item` / `nummerierter Punkt` (1., 2., …). A
dictated email gets its own layout: a greeting with a name at the start ("Hi
Anna," / "Hallo Anna," / "Sehr geehrter Herr Müller,") and a closing with a name
at the end ("Best regards, Alex" / "Viele Grüße, Alex") move to their own lines.
Use a profile to enable Spoken formatting only in your mail app. Use `spell A B C 1 end spelling` or
`buchstabiere A B C 1 buchstabieren Ende` for identifiers. These compose text,
including newlines, without sending Enter. Literal mode skips corrections,
snippets and spoken commands; Exact skips the full processing pipeline.

Profiles capture language, style, vocabulary, insertion and formatting options.
A profile chosen in the tray or by its shortcut applies to the following
dictations until you end it with its chip on Dictate ("Profile Code active ×")
or quit; your own preferences stay as they are, nothing becomes unsaved, and
Save preferences never stores a profile's values in them.
Choose one in the tray, or select its row under Settings → **Profiles per app** and set its shortcut and
options under Selected profile (the table names languages and styles in words,
the shortcut shows as "Ctrl + Alt + P" or "Not set"). The
profile shortcut starts recording and, like the main shortcut, ends it with a
second press or, with Hold to talk, on release. Optional executable matching
uses exact names separated by commas, e.g. `Code.exe, notepad.exe`. Automatic
matching never activates API Concise; explicit profile selection can.

## Meetings

The Meetings page looks like the Dictate page: the meeting shortcut as key caps
(or, while none is set, a link to set one under Settings → Shortcuts), one card
with the meeting title, the transcript, the mirrored wave and the round record
control, and chips under it for the microphone, the system audio, the languages
and whether the audio is kept. **Start meeting** explicitly starts microphone
and/or Windows system-audio capture. It runs separately from dictation, shows a
recording indicator, and never pastes into another app. Each channel saves durable local
WAV chunks about every 20 seconds. The live transcript is a preview. **Stop and
finish** then runs a final pass over the saved audio of each channel: Silero
finds the pauses, each stretch of speech is written in the most likely of the
meeting's **Languages** (English, German and Spanish by default; one language
fixes it), neighbouring stretches in one language are decoded together, and the
result replaces the preview. Each transcript row ends at the end of a sentence. Show audio the
microphone picks up from the loudspeakers is dropped only when the system channel
says the same words at the same moment and the microphone row is at least 10 dB
quieter than the speech on the microphone; a reply that repeats what was just
said ("At 12:30?") stays, and with headphones nothing is dropped. When the
evidence is unclear, the row stays. Two people on the system channel are not
separated: they share the Remote speaker label. The names and terms on the
**Vocabulary** page, the replacements of short corrections and the meeting title
give Whisper the spellings to prefer, the same vocabulary dictation uses, so
names need to be typed only once. A meeting recorded with its own vocabulary
keeps it for Re-transcribe, and a separate meeting vocabulary in an older
settings file is merged into the shared one once. The transcript itself is not rewritten:
corrections, names and numbers stay as Whisper wrote them, and a time written
without its colon ("at 1149") is still found by search and listed in the notes.
With an NVIDIA GPU, Automatic setup adds large-v3 for this pass; typing engine
or model paths by hand under Settings → Speech recognition makes meetings use the typed
model too. When other GPU work (a game, a local language model, meeting notes
being written) leaves too little GPU memory for large-v3, the final pass shows
"Waiting for GPU memory" with the free and needed memory and tries again every
3 s for up to 30 s; while Private Flow's own meeting notes hold the GPU, it waits
for them to finish, tries every 10 s and updates the step when they end. After
the wait it tries one last load. A load that still fails, or any other failure,
gets one more try with a fresh worker; then the speech model finishes the
meeting, and the final message, the tray notification and the top of the notes
say so and why. When no model fits, the pass ends with the reason and keeps the
audio.

System audio comes from the **Output device** under Settings → Microphone and
speakers: **Follow Windows default output** (the default, also for older settings
files) records whatever device Windows plays through when the meeting
starts; a chosen device is recorded while it is connected. When the chosen
device is gone at meeting start, the meeting records the Windows default
instead and says so in a card on the Meetings page and, with the
window hidden, in the tray. The microphone and output chips on the Meetings page
change the same selections as the settings page.

While recording, the indicator names the microphone and the playback device in
use, and the meeting record keeps both names. A microphone that delivers no
sound (digital or near silence) for 10 seconds is shown on the Meetings page for
as long as it lasts and announced once per meeting in the tray, so muting it on
purpose is not announced again. When a channel stayed silent for the whole
meeting, the final message and the notes say so.

While any meeting captures, however it was started, a pill as minimal as the
dictation pill sits at the bottom of the screen: black, white outline, no text,
click-through and never focused. It shows both channels as one mirrored wave:
the microphone grows upwards in white, the system audio downwards in a second
colour; a channel that is not recorded or delivers nothing stays flat. The same
wave sits in the card on the Meetings page, flat while no meeting records, and
the tray shows the recording mark. The pill closes when the capture stops.
**System audio colour** under Settings → Microphone and speakers offers eight swatches (the accent blue by default);
a saved colour applies at once, also while recording.

**Settings → Shortcuts → Meeting shortcut** starts and stops a meeting from anywhere. It is
not set by default; click the field and press a combination (Backspace removes
it), then Save preferences. It must differ from the dictation shortcut and is
refused when another app uses it, as the dictation shortcut is. Pressed while
idle, it starts a meeting with the current choices on the Meetings page (chips,
title) without showing the window; pressed again, it does what
**Stop and finish** does, and the progress window shows the finishing while the
main window is hidden. It does nothing in a diagnostic instance, while dictation
records or transcribes, or while a transcript is being finished; a tray notice
says why.

After **Stop and finish** the page shows each step (stopping the capture,
loading the model, detecting speech and languages and transcribing per channel,
saving) with a percentage that ends at 100 % and the seconds the step has run.
The work runs in the background: **Continue in background** hides the window
(closing or minimizing it does the same), and a small window with a taskbar
progress button takes over; a tray notification reports the result.
**Cancel finishing** stops the pass and keeps the live preview and the audio.
**Stop; finish later** ends the capture without the pass. A capture that stops on
its own (device removed, disk full) is still finished with what was saved.

A meeting that was not finished (cancelled, failed, Quit or a crash) is listed
under **Unfinished meetings** with the size of the audio kept for it, and
announced in the tray at start. **Finish transcripts** finishes all of them in
one action; **Delete their audio** keeps the live preview as the transcript. A
failed pass is not retried automatically: it would load the large model on the
GPU without being asked and fail again for the same cause.

Meetings are stored in `%LOCALAPPDATA%\PrivateFlow\meetings`, on this PC. Audio is
kept until the final transcript is saved and then deleted by default; audio of a
complete meeting that an earlier run left behind is deleted at start. **Retain
processed audio** preserves it, so **Re-transcribe** on the **History** page can
run again with the languages chosen on the Meetings page and the current
vocabulary. It replaces the transcript, including edits, and asks before it
replaces a finished transcript or edits not yet saved; when it is cancelled or
fails, unsaved edits stay shown and unsaved, and a card on the History page says so. Settings → Meeting defaults
holds the languages and the audio retention new meetings start with and how
many days completed meetings are kept; a meeting started on the Meetings page
saves its own choices as the new defaults.
A crash may lose the chunk being written. Transcript speaker/text edits are saved explicitly.
Microphone speech starts labelled You; system audio starts as Remote speaker.
Automatic separation of multiple remote speakers is not included; edit labels
per segment when needed.

The **History** page lists past meetings on the left with their date, length
and, for an unfinished one, why it ended unfinished; the meeting being recorded
is listed but cannot be opened. The selected meeting shows on the right with two
tabs, **Transcript** (speaker labels and text editable in place, **Save edits**)
and **Notes**, next to **Export** and a menu with **Re-transcribe** and
**Delete meeting**. One search box in the page header searches every meeting:
the list then shows only the meetings with hits and how many, the transcript
highlights the hit rows and opens at the first with the rows around it, and
**Only this meeting** limits the search to the selected one. A search that
leaves out the shown meeting opens the first meeting with hits, unless the shown
one has unsaved edits: then it stays shown and a card above it says why.
Clearing the box lists everything again and clears Only this meeting. Unsaved
edits are compared with the meeting as saved: **Save edits** is the prominent
button only while there are some, and choosing another meeting then asks
whether to discard them (Cancel keeps the meeting open with its edits).

The Notes tab shows notes built by rules on this PC at once, with a warning
card above them that carries their caveat and a link to the notes setting; a
line saying that a channel recorded no sound stays above the notes. They are made without a model, in the sections a
model writes: a summary of quoted rows (the agenda row, then the most salient
row of each stretch of the meeting), decision candidates (an explicit decision,
or a proposal with the rows that plainly agree to it), action items (a
commitment belongs to its speaker, an accepted request to whoever accepts it,
also with a short reply such as "Sure, by Friday.", "Ja, bis Freitag.", "Gerne."
or "On it.", whose date becomes the due date; a reply that says something else
is no acceptance, and a declined request is left out; the due date only from the
task's own words or the owner's answer), open
question candidates (deferred, said to stay open, or left when the meeting
closes) and the times mentioned (clock times, weekdays, dates and minute marks
such as "minute 42"). Rules for English, German and Spanish phrases decide
this, and where they are unsure they leave a point out; every point quotes and
cites its transcript rows. Measured on invented meetings, listed action items
and times are usually right but some are missing, while most decisions and open
questions are missed; the notice says so (see docs/VALIDATION.md).

Notes can also be written by a model, chosen under Settings → Models →
Meeting notes: **Rules on this PC** (the default), **Local model** (the meeting
notes model on this PC; nothing leaves it) or **API** (the Concise API
connection, which receives the title, languages, vocabulary and transcript text
of that one meeting, never audio); in the settings file this is `MeetingNotesStyle`
(`Extractive`, `LocalModel`, `Api`). The Notes tab then offers a button for the
selected meeting that names where the text goes, such as "Write notes with
gpt-6-luna at notes.example", with the port when it is not the scheme's default
("notes.example:8443"), or "Write notes with the local model"; nothing is sent
before it is pressed for that meeting. While the model writes, the tab shows the
progress and a Cancel, and the rest of the app stays usable; with the local
model the progress line adds that a Local polish dictation waits until the notes
are done and locks the page meanwhile, as every dictation locks the Meetings and
History pages until it is done, and that Cancel on the Dictate page keeps the
dictation's Clean text and unlocks the page. Notes a model wrote are stored with the meeting with the model and the
time, shown on later visits and exported as the Markdown notes; **Back to
rule-based notes** discards them, and an edited or new transcript is marked as
newer than the notes. The model answers in JSON; every point must cite transcript rows that
exist, times must be clock times, weekdays or dated days, and anything else is
left out and counted. At most 20 times are listed; when the list reaches that
cap, the notes say "Times mentioned stop at 20; the transcript may name more."
The notes language is the one most of the spoken text is in; speaker labels
such as "You" do not count. On any failure the extractive notes are shown with
the reason.

The meeting notes model is Qwen3.5-4B Q4_K_M (Apache-2.0) with llama.cpp. While
the Local model style is chosen and the model is missing, the Notes tab and the
notes style under Settings → Models offer **Install
the meeting notes model (N GB)** with the download size: the 2.7 GB model plus
the CUDA build of llama.cpp, about 3.4 GB together, or the processor build
without an NVIDIA GPU. Nothing is downloaded before the press. It runs the
pinned, hash-verified `setup-rewrite.ps1 -Part notes` with its progress in both
places, **Cancel installing** stops it and removes the partial download (a new
try downloads it again), a failure is shown with the script's first error
without the user's folders, it waits while a meeting records, and once installed the Notes tab offers **Write notes with the
local model**. The script can also be run by hand. Notes with a missing model
fail at once with the reason; they neither wait for a running Local polish
dictation nor stop its warm server. The model
is loaded only while notes are written and unloaded right after, with all
layers on the GPU that fit beside the speech models (about 3.9 GB at its 32,768
token context, enough for meetings of about 80 minutes; settings
`MeetingNotesGpuLayers` and `MeetingNotesContextTokens`). On invented meetings
of 15 to 60 minutes it wrote notes in 19 to 29 s, each in the meeting's main
language, and found every decision and 95 % of action items
(docs/VALIDATION.md). The Qwen3-4B model that Local polish installs, about the
same size, is refused for notes, also under another file name: in its one test,
on the processor with an earlier prompt, it cited unrelated rows and missed
decisions. It has not been measured on the GPU or with today's prompt. In the Local
processing models card under Models, the meeting notes model can run on the processor only: a
synthetic 50-minute meeting took 166 s there against 28 s on the GPU. A Local polish dictation that arrives
while notes are written waits for them, up to the time set under Settings →
Text style → **Wait for meeting notes** (two minutes by default) and says so in a card on the Dictate page and the tray;
past the limit it pastes Clean. During the wait Escape is released to the other
programs, so only **Cancel** on the Dictate page cancels it: it keeps the Clean
text there, pastes nothing and leaves the clipboard untouched. Once the wait is
over, Escape cancels again until the paste; while the polish runs after the wait,
Escape and Cancel likewise keep the Clean text without pasting it. After a wait the
text is pasted only if its window is still in front by itself, never brought
back to the front; otherwise it stays on the Dictate page with a notice and the
clipboard is left alone. Without the Local polish model the dictation does not
wait and uses Clean at once.

Search is lexical, not a semantic answer generator; numbers compare like the accuracy suite compares
them, so "12:34", "1234", "12.34", "twelve thirty-four", "doce y treinta y
cuatro" and "zwölf Uhr vierunddreißig" find each other, and "minute 40" finds
"minuto cuarenta". Names with digits are searched whole ("I2C" does not find
"two"), a compact query finds its spaced form ("9am" finds "9 a.m.", "10km"
finds "10 km"), and the German article "ein" is the number one only in a number
or time ("um ein Uhr dreißig" is found as 1:30, "ein Problem" not as one).
**Export** writes the Transcript (TXT), Timestamped subtitles (SRT), Notes with
cited rows (Markdown) or a Meeting backup with kept audio (private ZIP).
Retention is configurable under Settings → Meeting defaults; **Delete expired
meetings** on the History page explicitly deletes completed meetings older than
that period. Unfinished meetings are kept. **Delete meeting** removes its local
folder.

## Install and setup

Download `PrivateFlow-win-x64.zip` from the [public releases](https://github.com/DemonicPsychoII/PrivateFlow/releases), extract it, and run the included installer from the extracted folder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Autostart
```

The installer downloads and verifies the local speech engine and models on first
install. You can also choose **Install recommended** under Settings → Models.
Windows x64 and PowerShell 5.1 or newer are required. The app runs locally after
setup; model downloads need a network connection.

From a checkout, with .NET 10 SDK:

```powershell
.\scripts\build.ps1 -Portable
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -Autostart
```

It installs to `%LOCALAPPDATA%\Programs\PrivateFlow`, adds a Start-menu shortcut,
preserves settings/models, and removes the browser download mark. Autostart is
opt-in. No code-signing certificate is included.

### Uninstall

The package ships `scripts\uninstall.ps1`. For an installed copy:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\Programs\PrivateFlow\scripts\uninstall.ps1"
```

It removes a default installation and nothing else: the program folder
`%LOCALAPPDATA%\Programs\PrivateFlow` and copies the installer left next to it
(`PrivateFlow.new-*`, `PrivateFlow.old-*`), the data folder
`%LOCALAPPDATA%\PrivateFlow` with everything in it (settings, the cloud reader
token, models and runtimes, downloads, **saved meetings with their transcripts,
notes and audio**, and any file you put there yourself), the Start-menu shortcut
and the autostart entry when they start this installation, and the installer's
and updater's leftovers in `%TEMP%`. First it lists each location with its size,
the number of saved meetings and each model, then asks you to type `UNINSTALL`;
anything else cancels. `-WhatIf` only lists. `-Force` skips the question and ends
a running Private Flow, for unattended use. Deleted meetings cannot be recovered;
export what you want to keep first.

If Private Flow is running, quit it from the tray icon (Quit) first, or let the
script end it and its helpers after the confirmation; only programs running from
the two folders and the PowerShell and curl processes they started are ended. A
file still in use is named in the report and the exit code is 1; run the script
again once it is free. Links inside the folders are removed as links, never
followed; the one case the script cannot rule out is another program on the same
account replacing a subfolder with a link in the instant between the script's
check and the deletion.

A relocated or custom installation is refused before anything is listed: a
program or data folder that is a junction or symbolic link or has one on its
path, a program folder without `PrivateFlow.exe`, or an unusual `LOCALAPPDATA`.
Remove such an installation by hand: quit Private Flow, then delete its program
folder, its data folder (also the one `PRIVATE_FLOW_DATA_DIR` names, and the
target of a link), the Start-menu shortcut "Private Flow" and the value
`PrivateFlow` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Engine
or model files you keep outside the data folder and an API key environment
variable stay; delete them by hand if you no longer need them.

### Updates

**Update** and the tray notification use the latest stable
[public release](https://github.com/DemonicPsychoII/PrivateFlow/releases) by
default. The check queries GitHub anonymously, ignores drafts and pre-releases,
and offers only a higher `vX.Y.Z` version. With no release yet, no network, or a
GitHub error or rate limit, the background check stays silent. To install the
latest release by hand with an existing installer:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -FromRelease
```

Each release needs `PrivateFlow-win-x64.zip` and `manifest.json`. The installer
checks the ZIP size and SHA-256 against the manifest and GitHub's asset digest
when present. Downloads and engine setup finish before the installed app is
replaced. This checks integrity, not a publisher signature.
The installer refuses an equal or older release unless `-Force` is supplied;
this also applies when `-FromCloud` falls back to releases. A missing public
repository or stable release is explained explicitly (HTTP 404).

An optional compatible update server can supply internal integration builds.
Store its URLs and reader token once per Windows user (the token prompt hides
input; press Enter to keep an already stored token):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\install.ps1 -SetCloudToken -CloudUrl https://updates.example.org -CloudWakeUrl https://updates.example.org/healthz
```

The token is DPAPI-encrypted in `%LOCALAPPDATA%\PrivateFlow\cloud-reader.token`;
the URLs are stored beside it in `update-server.json`. Omit `-CloudWakeUrl` if
the server needs no wake request. With both usable configuration and a token,
the app and `install.ps1 -FromCloud` use that server's `integration` channel,
including its existing build comparison and optional GitHub CLI change list.
A server outage stays silent and does not change the selected source.
Without both, the app uses public releases; `-FromCloud` also falls back to
releases and prints a configuration notice. Older installs with only a token
show a proactive app notice and tray notification with the command above to
restore integration updates, keeping the stored token. Installed copies check
two minutes after start and once a day; the tray offers updates between
dictations. The public snapshot repository has Actions disabled; its release
ZIP and manifest come from the internal build of the same tree.

To prepare a public source snapshot, run `scripts/export-public.ps1 -Target
<empty folder> [-Commit <commit>]`. It exports the committed tree (HEAD by
default) without history or workflows and rejects private-detail patterns or
the internal runner label before reporting success.

The API-based text features have no default endpoint. Set the API base URL,
model and key environment-variable name under Settings → Text style to use
them. Explicit saved endpoints survive updates. An older settings file with no
endpoint key shows a notice to configure it; until then Concise uses Clean and
API meeting notes use extractive notes, with a visible fallback reason.

**Speech recognition** in Settings chooses where speech recognition runs (Automatic, GPU or CPU)
and which model it uses (Automatic, large-v3-turbo, medium or base); **Apply**
downloads what is missing and switches. It also holds **Set up Local polish** (the same
install as under Models), the speech detection and keep-warm switches and the engine and model paths
(**Save preferences** stores them), shows the status of the
engine and models and what may leave this PC, runs the synthetic speech check
with cold/warm timings, and exports metadata-only diagnostics. Diagnostics
exclude audio, transcripts, prompts, credentials, endpoints and private paths.
While a setup or check runs there, dictation and Transcribe file do not start;
a card on the Dictate page asks to finish the setup or check under Speech recognition
first. Downloads happen on Apply, at the first setup and once
after each update; subsequent local inference is offline.

Automatic follows the recommended setup for this PC. After each update, the
restarted app reapplies it once in the background and shows a notification when
the model changed; a failed download keeps the current model and retries at the
next start. A specific choice is kept, and paths typed in by hand are never
replaced by an update. Existing installs whose paths setup wrote count as
Automatic.

`setup-engine.ps1` pins whisper.cpp v1.9.2, Whisper weights and Silero v6.2.0.
It chooses CUDA with `large-v3-turbo-q8_0` when NVIDIA tooling is present,
CPU/base otherwise; override with `-Backend cpu|cuda12 -Model
base|medium|large-v3-turbo-q8_0`. The turbo model uses about 874 MB of weights,
medium about 1.5 GB and base about 148 MB. Automatic on CUDA also installs
`large-v3` (3.1 GB) for the final pass of meetings and files, which can wait
for it; dictation keeps the faster turbo model. It unpacks the runtime only when the
archive changed, so a running speech worker keeps its files. Upgrades install a
missing Silero detector without resetting a deliberate engine/model choice.

### Models

Settings → **Models**, before Speech recognition, starts with **Recommended for
this PC**: what it read on this PC (graphics card and its memory, total and free
now, whether it is an NVIDIA GPU the CUDA builds use, processor, memory, free
disk space; read locally, nothing is sent) and three buttons. What they install
is the transcription setup Automatic installs here (asked from
`setup-engine.ps1 -Recommend`, so it is exactly what Apply with Automatic
installs) and the meeting notes model for meeting notes and Local polish; a
line says so where this PC needs otherwise: the notes model on the processor
without an NVIDIA GPU, none with less than 8 GB of memory, or too little disk
space. **Install recommended** installs what is missing of both, and
**Install transcription models** and **Install local processing models** each
group, every button with its size and disabled once nothing is missing. The
transcription group is Apply with Automatic, so it switches dictation to those
models; where speech recognition was chosen by hand (another processor or
model, or paths entered by hand) the card says so and the press asks first,
naming what changes. One install, removal, Apply or check runs at a time on the
Models page and under Speech recognition, with Cancel on its bar; every other
Install, Remove and Apply is disabled until it has finished. Below it, the models stand in two cards,
**Transcription models** and **Local processing models**, which list every
model Private Flow can download: Whisper large-v3-turbo, medium and base for
dictation, large-v3 for the final pass of meetings and files, the Silero voice
detector, the Local polish model and the meeting notes model. Each row names
what it is for, its download size and the size of its file on disk, whether it
is installed and what uses it, and where it runs, read from the configuration in
use: the speech engine build, the GPU layers the polish and notes servers are
started with. **Install** (with the size) and **Remove** sit side by side.
Install runs the same pinned setup script as everywhere else, shows the shared
progress bar and turns into **Cancel installing** while it runs; a speech model
is only downloaded and verified, and **Apply** under Speech recognition switches
to it. Remove asks in the window first, deletes only a model file Private Flow
installed in its own models folder, and refuses with the reason a model
something uses (the dictation model, the final-pass model, the speech detection
model, the model chosen for Local polish or for meeting notes) and any model
while a dictation, a meeting, a setup or notes run. Install and Remove act at
once; nothing is downloaded before a press.

The transcription card also shows which speech models dictation and the final
pass use (chosen under Speech recognition, with a link); the local processing
card holds the choices that are preferences: which model Local polish runs, whether the meeting notes model
runs on the GPU or the processor only (on a PC with an NVIDIA GPU), and how
meeting notes are written (rules, the meeting notes model or the API; this moved
here from Meeting defaults, which links to it). A choice whose model is missing
says so and offers Install next to it. All names, sources, sizes and hashes come
from `scripts/models.json`, which the setup scripts and the installer read and
the app has built in.

### Smart App Control

Smart App Control runs only code signed with a certificate Microsoft trusts or
rated safe by Microsoft's cloud reputation service. Private Flow and the pinned
whisper.cpp runtime are unsigned, so every file depends on a reputation verdict
that is per file and can change from day to day. The speech worker therefore
loads only the GPU backend and the one CPU backend this processor uses, instead
of every CPU variant; if that variant is blocked, it falls back to the next
one. A blocked core runtime file or a freshly built `PrivateFlow.exe` still
stops speech or launch until Windows rates it; the error card names the file
being loaded, which may be the one that needs the blocked file. Blocks are listed in Event Viewer under Applications and Services Logs,
Microsoft, Windows, CodeIntegrity, Operational (event 3077). Signing with a
Microsoft-trusted certificate, such as Azure Artifact Signing, removes the
dependency on reputation; switching Smart App Control off in Windows Security
also does, at the cost of that protection.

Optional `setup-rewrite.ps1`, run by **Set up Local polish** or Install under
Models, pins llama.cpp b11577 and Qwen3-4B Q4_K_M (about 2.5 GB), as listed in
`scripts/models.json`. It verifies every download by SHA-256, runs rewriting on
the processor, and does not change the selected text style.
`setup-engine.ps1 -Download <model>` downloads and verifies one speech model or
the Silero detector into the models folder without changing a setting.
`-Part notes`, run by **Install the meeting notes model**, installs the meeting
notes model the same way. Run by Private Flow, the setup scripts never write
`settings.json`: they hand back what they installed and the app stores it like
any other save, so preferences saved during a setup stay. Run by hand from a
terminal, they write `settings.json` themselves only while no Private Flow runs
on that data folder, holding its `settings.lock`; while one runs, they stop
before downloading anything: close Private Flow, or install from Settings →
Models inside it. `install.ps1`, and with it the in-app update, downloads first
and stores the new paths only after it has stopped the running app, before it
starts the new one. A running Private Flow holds `instance.lock` in its data
folder; both lock files stay there and are released when their program ends. The server binds only to loopback,
requires a random bearer key, discards logs, and stops on cancellation/failure
or after five idle minutes. Models, recordings, settings and packages stay
outside Git. There is no telemetry or silent cloud fallback.

A `settings.json` whose content is not valid settings, for example after a
mistyped value in a hand edit, is never overwritten: Private Flow renames it to
`settings.json.unreadable-<date and time>` beside it, starts with the default
preferences and says so on Dictate, with the reason, until preferences are
saved. Correct the kept file and rename it back while Private Flow is closed to
use it again. A `settings.json` that another program holds open or that cannot
be accessed is not touched: Private Flow says so at start and offers to try
again or quit, instead of starting with the defaults.

## Verification and limits

```powershell
.\scripts\build.ps1 -Portable
.\scripts\smoke-test.ps1
.\scripts\ui-smoke-test.ps1
```

The build treats warnings as errors and runs the pure safety checks; the smoke
tests use generated audio and isolated data folders and can run beside your own
Private Flow. The diagnostic and evaluation modes run only with a
`PRIVATE_FLOW_DATA_DIR` outside `%LOCALAPPDATA%\PrivateFlow`. What each check
covers is in [test coverage](TEST_COVERAGE.md); what is verified, what has
been tried for real, what is not verified and the measured figures are in
[validation](VALIDATION.md). CI cross-builds and packages Windows
in an isolated Linux guest; Windows UI and audio checks stay local.

Limits to keep in mind: accessibility metadata cannot identify every custom
password control, and the foreground check and the paste have a narrow race in
Windows. Elevated apps can reject insertion. Clipboard managers may ignore
Windows exclusion formats. Review sensitive outputs and use a disposable
document for your first insertion tests. Deleting a meeting is not forensic
erasure; private exports and retained recordings need storage you control.

See [implementation scope](IMPLEMENTATION.md) and
[release notes](RELEASE_NOTES.md) for the changes by date.
