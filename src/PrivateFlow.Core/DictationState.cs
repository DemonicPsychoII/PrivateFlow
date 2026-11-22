using static PrivateFlow.Core.DictationActions;
using static PrivateFlow.Core.DictationEvent;
using static PrivateFlow.Core.DictationState;

namespace PrivateFlow.Core;

// The one state of a dictation. DictationController holds it in one field and sets it only to what DictationTable.Next returns.
public enum DictationState
{
    Idle,
    // The pill shows dimmed, Escape is registered and Cancel enabled; the microphone is not open and recording yet.
    Starting,
    Recording,
    // The recording stopped: it is recognized and, unless the polish has to wait for another local model task, polished.
    Transcribing,
    // The polish waits for another local model task, such as the meeting notes; Escape belongs to other programs meanwhile.
    WaitingForNotes,
    // The wait ended: the polish runs, or is skipped because the wait ran out.
    PolishingAfterWait,
    // The text is final: shown on the Dictate page and, unless it stays there, pasted after the bounded wait for released modifiers.
    Pasting,
    // Cancelled: the microphone stops, or recognition, polish or paste ends, before another dictation may start.
    Cancelling,
    // A file chosen on the Dictate page is transcribed.
    TranscribingFile,
}

public enum DictationEvent
{
    // The dictation shortcut, a profile's shortcut or Record. In hold to talk a press that is not a repeat means the keys were released.
    Press,
    // A hotkey message for the hold's own shortcut while its keys are down (DictationTable.Pressed).
    HeldRepeat,
    // Hold to talk: the window's timer reads the shortcut's keys up.
    Release,
    // The registered Escape or, in hold to talk, Escape read by the timer while the shortcut's modifiers are held.
    Escape,
    // Cancel on the Dictate page.
    Cancel,
    Quit,
    // The start's checks passed and the session began with the target window read at the press.
    Begin,
    // The pill has its frame drawn.
    PillShown,
    DeviceOpened,
    // The microphone did not open or start recording.
    StartFailed,
    // The microphone or the recognition failed while recording.
    RecordingFailed,
    RecognitionDone,
    WaitBegan,
    // The wait ended and the polish runs.
    WaitEnded,
    // The wait ran out: the polish is skipped and Clean is used.
    WaitTimedOut,
    // The polish returned, was skipped or was cancelled.
    PolishDone,
    // The dictation or file transcription is over and its microphone released.
    Ended,
    FileChosen,
}

[Flags]
public enum DictationActions
{
    None = 0,
    // Runs the start's checks (another task, no microphone, speech not set up); when they pass, Begin follows.
    Start = 1 << 0,
    // Shows the pill dimmed as starting and the Dictate page as recording with Cancel enabled, and registers Escape; a card says when
    // another app holds Escape.
    ShowPill = 1 << 1,
    OpenDevice = 1 << 2,
    // The opened microphone records and recognition starts; the pill shows recording.
    Record = 1 << 3,
    // A microphone that opened for a start that ended is closed at once; nothing is recorded.
    CloseDevice = 1 << 4,
    // Stops the recording and transcribes it.
    Transcribe = 1 << 5,
    // Moves the session identity on, so no late result of this dictation is pasted, and cancels its recognition, polish and paste.
    CancelSession = 1 << 6,
    // The microphone stops and recognition ends; then the dictation has Ended and what follows this action is said.
    StopDevice = 1 << 7,
    Polish = 1 << 8,
    // Registers Escape again where it is not registered.
    RegisterEscape = 1 << 9,
    ReleaseEscape = 1 << 10,
    // Says what the polish waits for: status, card and tray notice.
    AnnounceWait = 1 << 11,
    SayPolishing = 1 << 12,
    ClearQueuedNotice = 1 << 13,
    // Says why a press starts nothing: the last dictation waits, or is polished after waiting.
    ShowWaiting = 1 << 14,
    // Skips the polish: the Clean text stays on the Dictate page, neither pasted nor copied.
    KeepClean = 1 << 15,
    // Escape released, the pill closed, the Dictate page idle.
    End = 1 << 16,
    SayCancelled = 1 << 17,
    SayReleasedTooSoon = 1 << 18,
    ReportStartFailed = 1 << 19,
    TranscribeFile = 1 << 20,
}

public readonly record struct DictationStep(DictationState Next, DictationActions Actions);

// Stale: the event belongs to a dictation that was cancelled, followed by a newer one or, for the end of a wait, whose polish was skipped.
// HoldToTalk: the dictation was started by a shortcut in hold to talk. Captured: the audio the microphone delivered so far.
public readonly record struct DictationFacts(bool Stale = false, bool HoldToTalk = false, TimeSpan Captured = default);

// What becomes of a dictation's final text.
public enum DictationInsertion
{
    // Cancelled during the wait for another task or the polish after it: it stays on the Dictate page, unpasted, the clipboard untouched.
    KeepClean,
    NothingRecognized,
    // Automatic insertion is off: the Dictate page opens to review it.
    Review,
    // Pasted into the window selected at the press, brought back to the front first; when it cannot be pasted it is left on the clipboard.
    Paste,
    // It waited for another task, maybe minutes: pasted only if the selected window is still in front by itself, without bringing it
    // back, and otherwise the clipboard is left alone.
    PasteAfterWait,
    // Automatic insertion is refused in this instance: the test build outside the disposable test VM, or a diagnostic instance. It stays
    // on the Dictate page with the not-pasted notice; nothing is pasted or copied.
    PasteRefused,
}

// Every change of a dictation's state and what it does. The controller carries out the actions and holds the resources.
public static class DictationTable
{
    public static DictationStep Next(DictationState state, DictationEvent happened, DictationFacts facts = default)
    {
        var stay = new DictationStep(state, None);
        // The session identity: an event of a dictation that was cancelled or followed by a newer one changes nothing, and a microphone it
        // opened late is closed at once instead of completing the newer start.
        if (facts.Stale) return happened == DeviceOpened ? stay with { Actions = CloseDevice } : stay;
        if (state is Starting or Recording)
        {
            // In hold to talk a press that is not a repeat of the hold means its keys were released in between; in toggle nothing is held.
            if (happened == Press && facts.HoldToTalk) happened = Release;
            else if (happened is Release or HeldRepeat && !facts.HoldToTalk) return stay;
        }
        return (state, happened) switch
        {
            (Idle, Press) => new(Idle, Start),
            (Idle, Begin) => new(Starting, ShowPill),
            (Idle, FileChosen) => new(TranscribingFile, TranscribeFile),
            (Idle, DictationEvent.Escape or Cancel or Quit) => new(Idle, CancelSession | SayCancelled),

            // From its pill on a start ends as a recording does (StartingCapture, shared with meetings), and that wins over the open after it.
            (Starting, PillShown) => new(Starting, OpenDevice),
            (Starting, DeviceOpened) => new(Recording, Record),
            (_, DeviceOpened) => new(state, CloseDevice),
            (Starting, _) when AsStartingEvent(happened) is { } startingEvent => StartingCapture.Dictation(startingEvent) switch
            {
                StartingOutcome.Cancel => new(Idle, CancelSession | End | SayCancelled),
                StartingOutcome.Discard => new(Idle, CancelSession | End | SayReleasedTooSoon),
                StartingOutcome.Fail => new(Idle, CancelSession | End | ReportStartFailed),
                _ => stay,
            },

            (Recording, Press or RecordingFailed) => new(Transcribing, Transcribe),
            // Captured audio rather than time since the press: opening a slow microphone must not turn a tap into speech.
            (Recording, Release) => HeldShortcut.Decide(ShortcutBehavior.HoldToTalk, false, facts.Captured) == HeldShortcutOutcome.Discard
                ? new(Cancelling, CancelSession | StopDevice | SayReleasedTooSoon) : new(Transcribing, Transcribe),
            (Recording, DictationEvent.Escape or Cancel or Quit) => new(Cancelling, CancelSession | StopDevice | SayCancelled),
            (Recording, StartFailed) => new(Idle, CancelSession | End | ReportStartFailed),

            (Transcribing, RecognitionDone) => new(Transcribing, Polish),
            (Transcribing, WaitBegan) => new(WaitingForNotes, ReleaseEscape | AnnounceWait),
            (WaitingForNotes, WaitEnded) => new(PolishingAfterWait, RegisterEscape | ClearQueuedNotice | SayPolishing),
            (WaitingForNotes, WaitTimedOut) => new(PolishingAfterWait, RegisterEscape | ClearQueuedNotice),
            (WaitingForNotes or PolishingAfterWait, Press) => new(state, ShowWaiting),
            // The text was spoken minutes before and is not lost: cancelling keeps it as Clean, unpasted.
            (WaitingForNotes or PolishingAfterWait, DictationEvent.Escape or Cancel or Quit) => new(state, KeepClean),
            (Transcribing or WaitingForNotes or PolishingAfterWait, PolishDone) => new(Pasting, RegisterEscape | ClearQueuedNotice),
            (Cancelling, PolishDone) => new(Cancelling, RegisterEscape | ClearQueuedNotice),

            (Transcribing or Pasting or TranscribingFile or Cancelling, DictationEvent.Escape or Cancel or Quit) => new(Cancelling, CancelSession | SayCancelled),
            (Transcribing or WaitingForNotes or PolishingAfterWait or Pasting or Cancelling or TranscribingFile, Ended) => new(Idle, End),
            _ => stay,
        };
    }

    private static StartingEvent? AsStartingEvent(DictationEvent happened) => happened switch
    {
        Press => StartingEvent.Press,
        HeldRepeat => StartingEvent.HeldRepeat,
        Release => StartingEvent.Release,
        DictationEvent.Escape => StartingEvent.Escape,
        Cancel => StartingEvent.Cancel,
        Quit => StartingEvent.Quit,
        StartFailed => StartingEvent.OpenFailed,
        _ => null,
    };

    // A shortcut's hotkey message: a repeat of the hold (HeldShortcut.RepeatsHold) while a hold-to-talk dictation starts or records,
    // otherwise a press. heldShortcut is the shortcut that started a hold-to-talk dictation, null in toggle.
    public static DictationEvent Pressed(DictationState state, ActivationShortcut? heldShortcut, ActivationShortcut? pressed, bool keysDown) =>
        pressed is { } shortcut && HeldShortcut.RepeatsHold(IsRecording(state) && heldShortcut is not null, heldShortcut, shortcut, keysDown) ? HeldRepeat : Press;

    // Hold to talk: the window's timer reads the shortcut's keys and Escape until the text is pasted, except during the wait for another
    // task, when Escape belongs to other programs, and once the dictation is cancelled.
    public static bool ReadsHeldKeys(DictationState state) => state is Starting or Recording or Transcribing or PolishingAfterWait or Pasting;

    public static bool IsRecording(DictationState state) => state is Starting or Recording;
    public static bool IsBusy(DictationState state) => state is Transcribing or WaitingForNotes or PolishingAfterWait or Pasting or Cancelling or TranscribingFile;
    public static bool IsQueued(DictationState state) => state is WaitingForNotes or PolishingAfterWait;

    // pasteAllowed: this instance may paste into other apps (PasteAllowed below).
    public static DictationInsertion Insertion(bool polishSkipped, string text, bool autoInsert, bool waited, bool pasteAllowed = true) =>
        polishSkipped ? DictationInsertion.KeepClean
        : string.IsNullOrWhiteSpace(text) ? DictationInsertion.NothingRecognized
        : !autoInsert ? DictationInsertion.Review
        : !pasteAllowed ? DictationInsertion.PasteRefused
        : waited ? DictationInsertion.PasteAfterWait : DictationInsertion.Paste;
    // The app pastes; the test build (an audio library in place of the microphone) only in the disposable test VM, a diagnostic instance
    // of the verification host never: pastes into real apps belong in the VM (AGENTS.md).
    public static bool PasteAllowed(bool testBuild, bool diagnostic, bool inTestVm) => !diagnostic && (!testBuild || inTestVm);
    public static bool RestoresForeground(DictationInsertion insertion) => insertion == DictationInsertion.Paste;
    // A clipboard the paste could not save is never replaced by the copy either (clipboardUnreadable).
    public static bool CopiesWhenNotPasted(DictationInsertion insertion, bool clipboardUnreadable = false) => insertion == DictationInsertion.Paste && !clipboardUnreadable;
}
