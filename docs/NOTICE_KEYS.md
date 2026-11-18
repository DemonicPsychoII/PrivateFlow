# Notice keys and where each ends

Every warning, error or note card has a key that names its condition (see `Notice` in `src/PrivateFlow.Core/Notices.cs`). A **condition** card is raised while its condition holds and ends when it ends. An **outcome** card reports how an action ended. It ends at the next run of that action, or when the user dismisses it. The context end is listed too where a new context starts without the old one's outcomes: a new dictation, a new meeting or final pass, another run for the same meeting. Dismissing never changes the condition. A **standing** card shows where it is raised but is never pointed to from the status line and puts no dot on the sidebar or the settings button: it is kept for states the user chose knowing what the card says, such as a saved shortcut that takes over an AltGr character, and for cards repeated where a choice is made next to one that is pointed to. This table must name every key in the code; a check in `tests/PrivateFlow.Tests/NoticeChecks.cs` fails when a key is missing here or a row names a key the code no longer has.

| Key | Page | Sort | Ends |
| --- | --- | --- | --- |
| `dictate-audio-devices` | Dictate | condition | the next device refresh that succeeds |
| `dictate-copy-failed` | Dictate | outcome | the next successful Copy text |
| `dictate-export-failed` | Dictate | outcome | the next successful Export text |
| `dictate-no-microphone` | Dictate | condition | a device refresh that finds a microphone |
| `dictate-nothing-recovered` | Dictate | outcome | the next Recover that finds a result |
| `dictation-escape` | Dictate | condition | the end of the recording it is about |
| `dictation-failed` | Dictate | outcome | the next dictation or file transcription |
| `dictation-gpu-memory` | Dictate | condition, then outcome | the model has loaded; after a result, the next result without low memory or the next dictation |
| `dictation-model-fallback` | Dictate | outcome | the next dictation or file transcription |
| `dictation-no-microphone-selected` | Dictate | condition | a microphone is selected |
| `dictation-not-pasted` | Dictate | outcome | the next dictation |
| `dictation-outcome` | Dictate | outcome (no text, released too soon) | the next dictation |
| `dictation-queued` | Dictate | condition | the wait for the meeting notes ends |
| `dictation-refused` | Dictate | condition | the meeting, setup or check it waited for ends |
| `dictation-silent` | Dictate | outcome | the next dictation |
| `dictation-used-clean` | Dictate | outcome | the next dictation or file transcription |
| `history-delete-failed` | History, meeting | outcome | the next successful delete of the meeting |
| `history-edits-kept` | History, meeting | condition | the unsaved edits are saved or discarded |
| `history-expired-not-deleted` | History | outcome | the next successful Delete expired meetings |
| `history-export-failed` | History, meeting | outcome | the next successful export of the meeting |
| `history-no-audio` | History, meeting | outcome | the next Re-transcribe |
| `history-not-read` | History | outcome | the next successful read of a saved meeting |
| `history-not-saved` | History, meeting | outcome | the next successful Save edits |
| `history-unfinished` | History, meeting | condition | the meeting is finished |
| `history-unsaved-edits` | History, meeting | condition | the edits are saved or discarded |
| `libraries-problem` | Vocabulary | outcome | the next successful save, import or export of corrections and snippets |
| `missing-api-endpoint` | Dictate | condition | a nonempty API endpoint is saved under Text style |
| `update-source-changed` | About and updates, also announced in the tray | outcome | dismissed; a later start with server URLs configured raises no notice |
| `local-polish-not-set-up` | Dictate; Text style and Profiles per app (standing) | condition | Local polish is no longer chosen there (on Dictate and under Text style the style shown, under Profiles per app any profile shown), or the model chosen for it under Models is installed (by Set up Local polish, Install under Models, or paths entered by hand and saved) |
| `meeting-audio-not-deleted` | Meetings | outcome | the next successful Delete their audio |
| `meeting-capture-stopped` | Meetings | outcome | the next meeting or final pass |
| `meeting-close-refused` | Meetings | condition | the capture stops |
| `meeting-could-not-start` | Meetings | outcome | the next meeting or final pass |
| `meeting-ended` | Meetings | outcome (finishing failed or cancelled) | the next meeting or final pass |
| `meeting-leftover-audio` | Meetings | outcome | dismiss only: the deletion runs once at start, and nothing in the session runs it again |
| `meeting-model-fallback{meeting}` | Meetings | outcome | the next meeting or final pass |
| `meeting-playback-fallback` | Meetings | condition | the meeting stops recording |
| `meeting-preview-stopped` | Meetings | outcome | the next meeting or final pass |
| `meeting-silent-{channel}-{meeting}` | Meetings | outcome | the next meeting or final pass |
| `meeting-silent-microphone` | Meetings | condition | sound returns or the recording stops |
| `model-install-failed` | Models | outcome | the next install of a model from Models |
| `model-remove-failed` | Models | outcome | the next Remove |
| `model-remove-refused` | Models | outcome | the next Remove that passes its checks |
| `notes-being-written` | Meetings, History | condition | the notes it waited for are done |
| `notes-cancelled` | History, meeting | outcome | the next notes run for the meeting |
| `notes-destination-changed` | History, meeting | outcome | the next notes run for the meeting |
| `notes-failed` | History, meeting | outcome | the next notes run for the meeting |
| `notes-install-failed` | History, meeting; Models | condition | the model is installed or an install runs; on the Notes tab also when another notes style is chosen |
| `notes-install-refused` | Models | condition | no meeting records or is finished |
| `notes-not-discarded` | History, meeting | outcome | the next successful Back to rule-based notes |
| `notes-not-saved` | History, meeting | outcome | the next notes run for the meeting |
| `notes-nothing-found` | History, meeting | outcome | the next notes run for the meeting |
| `notes-row-gone` | History, meeting | outcome | the next citation that finds its row |
| `notes-rule-based` | History, meeting | condition | a model writes the notes |
| `notes-stale` | History, meeting | condition | the notes are written again or the rule-based notes are shown |
| `polish-setup-failed` | Speech recognition | outcome | the next Set up Local polish there |
| `preferences-not-saved` | the page or section saving, or the section whose value cannot be stored | outcome | the next successful Save preferences or Discard, on every page |
| `profile-shortcut` | Profiles per app | outcome | the next combination the field takes |
| `profile-shortcut-altgr` | Profiles per app | condition | the selected profile's shortcut is one AltGr types nothing with, or none; raised only while a profile is selected on that page, so it never points from elsewhere |
| `profile-shortcuts-taken` | Profiles per app | condition | every profile shortcut registers |
| `recommended-install-failed` | Models | outcome | the next Install recommended or Install transcription models |
| `settings-unreadable` | Dictate | outcome (start), without an x | the next successful Save preferences; the settings.json whose content was not settings was set aside at start and is never read or removed again |
| `shortcut-dictation-altgr` | Shortcuts | condition; standing (no pointer, no dot) while the combination shown is the saved one | the dictation shortcut shown is one AltGr types nothing with |
| `shortcut-dictation-hint` | Shortcuts | outcome (note) | the field's next state: a combination saved or discarded, the field back at its help, or the next hint |
| `shortcut-dictation-rejected` | Shortcuts | outcome | the next combination the field takes, Discard, or a successful save |
| `shortcut-meeting-altgr` | Shortcuts | condition; standing (no pointer, no dot) while the combination shown is the saved one | the meeting shortcut shown is one AltGr types nothing with, or none |
| `shortcut-meeting-hint` | Shortcuts | outcome (note) | the field's next state: a combination saved or discarded, the field back at its help, or the next hint |
| `shortcut-meeting-rejected` | Shortcuts | outcome | the next combination the field takes, Backspace, Discard, or a successful save |
| `shortcut-taken-dictation` | Dictate | condition | the dictation shortcut registers |
| `shortcut-taken-meeting` | Meetings | condition | the meeting shortcut registers |
| `shortcuts-locked` | Shortcuts | condition | the dictation or file transcription ends |
| `shortcuts-removed-at-start` | Shortcuts | outcome | dismiss only: the shortcuts are cleared once at start and stored cleared, so no later start raises it again |
| `speech-check-failed` | Speech recognition | outcome | the next Run check |
| `speech-choice-not-applied` | Speech recognition | condition (note) | the processor and model chosen are those in use again, or Apply set them up |
| `speech-export-failed` | Speech recognition | outcome | the next successful diagnostics export |
| `speech-not-set-up` | Speech recognition | condition | the engine and model files are there |
| `speech-recommendation-failed` | Dictate | outcome | the next start's recommendation that succeeds, or Apply that sets up speech |
| `speech-setup-failed` | Speech recognition | outcome | the next Apply |
| `update-not-installed` | About and updates | condition | dismiss only: it is a property of this copy, which nothing in the session changes |
| `update-ended-without-restart` | About and updates | outcome | the next Update |
| `update-not-started` | About and updates | outcome | the next Update |
