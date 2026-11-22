using PrivateFlow.Core;
using static Harness;
using static PrivateFlow.Core.DictationActions;
using static PrivateFlow.Core.DictationEvent;
using static PrivateFlow.Core.DictationState;

// The dictation's transition table (DictationTable): each line the insertion-safety rules and the council named, as a check on the table.
static class DictationStateChecks
{
    static readonly DictationFacts Toggle = new(), Hold = new(HoldToTalk: true);
    static DictationFacts Held(double seconds) => new(HoldToTalk: true, Captured: TimeSpan.FromSeconds(seconds));
    static DictationStep Next(DictationState state, DictationEvent happened, DictationFacts facts = default) => DictationTable.Next(state, happened, facts);
    static bool Has(DictationStep step, DictationActions actions) => (step.Actions & actions) == actions;
    static readonly DictationState[] States = Enum.GetValues<DictationState>();
    static readonly DictationEvent[] Events = Enum.GetValues<DictationEvent>();
    static IEnumerable<(DictationState State, DictationEvent Event, DictationFacts Facts)> All() =>
        from state in States from happened in Events from facts in new[] { Toggle, Hold, Held(0.1), Held(2) } select (state, happened, facts);

    public static void VerifyDictationState()
    {
        // The session identity.
        Check(All().All(item => Next(item.State, item.Event, item.Facts with { Stale = true }) == new DictationStep(item.State, item.Event == DeviceOpened ? CloseDevice : None)),
            "an event of a dictation that was cancelled or followed by a newer one changes no state and does nothing; only a microphone it opened late is closed at once");
        Check(Next(Transcribing, RecognitionDone).Actions == Polish && Next(Transcribing, RecognitionDone, new(Stale: true)).Actions == None,
            "a recognition result is polished, and so can be pasted, only while its session is current");
        Check(All().Where(item => Has(Next(item.State, item.Event, item.Facts), ShowPill)).All(item => item is { State: Idle, Event: Begin }),
            "the pill shows only once the session began with the target window read at the press, never before");

        // A start that was ended opens no device; a device that opens after its start ended is closed at once.
        Check(Next(Starting, PillShown) == new DictationStep(Starting, OpenDevice) && Next(Idle, PillShown).Actions == None
            && Next(Starting, PillShown, new(Stale: true)).Actions == None, "only a start that is still starting opens the microphone once its pill is drawn");
        Check(Next(Starting, DeviceOpened) == new DictationStep(Recording, Record)
            && States.Where(state => state != Starting).All(state => Next(state, DeviceOpened) == new DictationStep(state, CloseDevice)),
            "a microphone records only for the start it was opened for; opened after that start ended it is closed at once in every other state, nothing recorded");

        // Starting is cancellable exactly as Recording.
        var ends = new[] { (Press, Toggle, "a second press"), (DictationEvent.Escape, Toggle, "Escape"), (Cancel, Toggle, "Cancel"), (Quit, Toggle, "Quit"),
            (DictationEvent.Escape, Hold, "Escape in hold to talk"), (Cancel, Hold, "Cancel in hold to talk") };
        Check(ends.All(end => Next(Starting, end.Item1, end.Item2) == new DictationStep(Idle, CancelSession | End | SayCancelled)),
            $"a starting dictation ends without a device on {string.Join(", ", ends.Select(end => end.Item3))}: the session is cancelled, the pill goes, it says Cancelled");
        Check(ends.All(end => Next(Recording, end.Item1, end.Item2).Next is Cancelling or Transcribing && Next(Recording, end.Item1, end.Item2).Actions != None),
            "each of them ends a recording too: a second press transcribes it, Escape, Cancel and Quit cancel it");
        Check(Next(Starting, Release, Hold) == new DictationStep(Idle, CancelSession | End | SayReleasedTooSoon) && Next(Starting, Press, Hold) == Next(Starting, Release, Hold),
            "in hold to talk a release during Starting, also told by another press, is a tap: nothing recorded, “Released too soon”");
        Check(Next(Recording, Release, Held(0.29)) == new DictationStep(Cancelling, CancelSession | StopDevice | SayReleasedTooSoon) && Next(Recording, Release, Held(0)).Next == Cancelling
            && Next(Recording, Release, Held(0.3)) == new DictationStep(Transcribing, Transcribe) && Next(Recording, Press, Held(0.1)).Actions.HasFlag(SayReleasedTooSoon),
            "a hold that captured under 0.3 s of audio is discarded, from 0.3 s it is transcribed; captured audio counts, not time since the press");
        Check(Next(Starting, StartFailed) == new DictationStep(Idle, CancelSession | End | ReportStartFailed) && Next(Recording, StartFailed).Actions.HasFlag(ReportStartFailed),
            "a microphone that does not open or start removes the pill and reports the error");
        Check(Next(Starting, Release, Toggle).Actions == None && Next(Recording, Release, Toggle).Actions == None && Next(Recording, Press, Toggle) == new DictationStep(Transcribing, Transcribe),
            "in toggle a release changes nothing and the next press transcribes");

        // A repeated hotkey message while the keys are down is ignored only in a hold session.
        var shortcut = ActivationShortcut.Default; var profileShortcut = new ActivationShortcut(3, 0x70);
        Check(DictationTable.Pressed(Starting, shortcut, shortcut, true) == HeldRepeat && DictationTable.Pressed(Recording, shortcut, shortcut, true) == HeldRepeat
            && Next(Starting, HeldRepeat, Hold).Actions == None && Next(Recording, HeldRepeat, Hold).Actions == None,
            "a hotkey message for the hold's own shortcut with its keys down, while starting or recording, repeats the hold and changes nothing");
        Check(DictationTable.Pressed(Recording, null, shortcut, true) == Press && DictationTable.Pressed(Recording, shortcut, shortcut, false) == Press
            && DictationTable.Pressed(Recording, shortcut, profileShortcut, true) == Press && DictationTable.Pressed(Idle, shortcut, shortcut, true) == Press
            && DictationTable.Pressed(Transcribing, shortcut, shortcut, true) == Press && DictationTable.Pressed(Recording, shortcut, null, true) == Press,
            "a toggle dictation, keys already up, another shortcut, Record, or no recording: always a press");
        {
            // The owner's flicker sequence, replayed through the table: messages repeat while the keys stay down, then the keys come up.
            var state = Idle; var starts = 0; var transcribed = 0; var discarded = 0;
            void Send(DictationEvent happened, DictationFacts facts)
            {
                var step = Next(state, happened, facts);
                state = step.Next;
                if (step.Actions.HasFlag(Start)) { starts++; state = Next(Next(state, Begin).Next, DeviceOpened).Next; }
                if (step.Actions.HasFlag(Transcribe)) { transcribed++; state = Idle; }
                if (step.Actions.HasFlag(SayReleasedTooSoon)) { discarded++; state = Idle; }
            }
            void Hotkey(bool keysDown, double seconds) => Send(DictationTable.Pressed(state, shortcut, shortcut, keysDown), Held(seconds));
            Hotkey(true, 0); Hotkey(true, 0.1); Hotkey(true, 0.2); Hotkey(true, 0.5); Hotkey(true, 1.5);
            var heldThrough = state == Recording && starts == 1 && transcribed == 0 && discarded == 0;
            Send(Release, Held(3));
            Check(heldThrough && state == Idle && starts == 1 && transcribed == 1 && discarded == 0,
                "hold to talk through the table: repeated messages while the keys are down keep the one recording, the release after speech transcribes it");
        }

        // Escape and Cancel around the wait for another task.
        Check(Next(Transcribing, WaitBegan) == new DictationStep(WaitingForNotes, ReleaseEscape | AnnounceWait)
            && All().Where(item => Has(Next(item.State, item.Event, item.Facts), ReleaseEscape)).All(item => item is { State: Transcribing, Event: WaitBegan }),
            "Escape is released to other programs only when the wait behind the notes begins");
        Check(Next(WaitingForNotes, WaitEnded) == new DictationStep(PolishingAfterWait, RegisterEscape | ClearQueuedNotice | SayPolishing)
            && Next(WaitingForNotes, WaitTimedOut) == new DictationStep(PolishingAfterWait, RegisterEscape | ClearQueuedNotice)
            && new[] { Transcribing, WaitingForNotes, PolishingAfterWait }.All(state => Next(state, PolishDone) == new DictationStep(Pasting, RegisterEscape | ClearQueuedNotice)),
            "Escape is registered again when the wait ends, runs out or the polish is done, and the waiting card goes");
        Check(new[] { WaitingForNotes, PolishingAfterWait }.All(state => new[] { DictationEvent.Escape, Cancel, Quit }.All(happened => Next(state, happened) == new DictationStep(state, KeepClean))),
            "Escape, Cancel and Quit during the wait or the polish after it keep the Clean text: the session goes on, unpasted, nothing cancelled");
        Check(new[] { Transcribing, Pasting }.All(state => Next(state, Cancel) == new DictationStep(Cancelling, CancelSession | SayCancelled)),
            "without a wait, cancelling ends the dictation as before");
        Check(!DictationTable.ReadsHeldKeys(WaitingForNotes) && !DictationTable.ReadsHeldKeys(Cancelling) && !DictationTable.ReadsHeldKeys(Idle) && !DictationTable.ReadsHeldKeys(TranscribingFile)
            && new[] { Starting, Recording, Transcribing, PolishingAfterWait, Pasting }.All(DictationTable.ReadsHeldKeys),
            "the hold-to-talk Escape is read until the text is pasted, except during the wait for the notes and once cancelled");
        Check(new[] { WaitingForNotes, PolishingAfterWait }.All(state => Next(state, Press) == new DictationStep(state, ShowWaiting))
            && new[] { Transcribing, Pasting, Cancelling, TranscribingFile }.All(state => Next(state, Press).Actions == None),
            "a press while the last dictation waits or is polished after waiting says why nothing starts; otherwise a busy dictation ignores it");
        Check(DictationTable.Insertion(true, "text", true, true) == DictationInsertion.KeepClean && DictationTable.Insertion(true, "text", true, false) == DictationInsertion.KeepClean
            && DictationTable.Insertion(false, " ", true, false) == DictationInsertion.NothingRecognized && DictationTable.Insertion(false, "text", false, true) == DictationInsertion.Review,
            "a skipped polish keeps the text on the Dictate page before anything else; empty text and review-first insert nothing");
        var waited = DictationTable.Insertion(false, "text", true, true); var direct = DictationTable.Insertion(false, "text", true, false);
        Check(waited == DictationInsertion.PasteAfterWait && direct == DictationInsertion.Paste && !DictationTable.RestoresForeground(waited) && !DictationTable.CopiesWhenNotPasted(waited)
            && DictationTable.RestoresForeground(direct) && DictationTable.CopiesWhenNotPasted(direct) && !DictationTable.CopiesWhenNotPasted(DictationInsertion.KeepClean),
            "a dictation that waited for the notes is not brought back to its window and leaves the clipboard alone; one that did not wait is restored and copied as before");
        Check(!DictationTable.CopiesWhenNotPasted(direct, clipboardUnreadable: true) && DictationTable.CopiesWhenNotPasted(direct, clipboardUnreadable: false),
            "a clipboard the paste could not save is not replaced by the copy either");
        // The test build pastes only in the disposable test VM, a diagnostic instance never; review-first and empty text stay as they are.
        Check(DictationTable.PasteAllowed(testBuild: false, diagnostic: false, inTestVm: false) && DictationTable.PasteAllowed(testBuild: true, diagnostic: false, inTestVm: true)
            && !DictationTable.PasteAllowed(testBuild: true, diagnostic: false, inTestVm: false) && !DictationTable.PasteAllowed(testBuild: false, diagnostic: true, inTestVm: true),
            "the app pastes; the test build only in the test VM; a diagnostic instance never");
        var refused = DictationTable.Insertion(false, "text", true, false, pasteAllowed: false);
        Check(refused == DictationInsertion.PasteRefused && !DictationTable.RestoresForeground(refused) && !DictationTable.CopiesWhenNotPasted(refused)
            && DictationTable.Insertion(false, "text", false, false, pasteAllowed: false) == DictationInsertion.Review && DictationTable.Insertion(false, " ", true, false, pasteAllowed: false) == DictationInsertion.NothingRecognized,
            "where pasting is refused the text stays on the Dictate page: nothing is pasted, brought to the front or copied");

        // The one state: what is recording, busy and idle, and how everything ends.
        Check(States.Count(state => DictationTable.IsRecording(state) || DictationTable.IsBusy(state)) == States.Length - 1 && !DictationTable.IsRecording(Idle) && !DictationTable.IsBusy(Idle)
            && States.All(state => !(DictationTable.IsRecording(state) && DictationTable.IsBusy(state))), "every state but Idle is either recording or busy, never both");
        Check(States.Where(DictationTable.IsBusy).All(state => Next(state, Ended) == new DictationStep(Idle, End)) && Next(Idle, FileChosen) == new DictationStep(TranscribingFile, TranscribeFile)
            && States.Where(state => state != Idle).All(state => Next(state, FileChosen).Actions == None && Next(state, Begin).Actions == None),
            "every busy dictation ends idle with its Escape, pill and controls released; a file or a new dictation begins only from Idle");
        Check(Next(Idle, Press) == new DictationStep(Idle, Start) && Next(Idle, Cancel) == new DictationStep(Idle, CancelSession | SayCancelled),
            "a press when idle runs the start's checks; Cancel or Quit when idle cancels nothing pending and says so");
    }
}
