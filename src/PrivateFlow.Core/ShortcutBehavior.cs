namespace PrivateFlow.Core;

public enum ShortcutBehavior { Toggle, HoldToTalk }

public enum HeldShortcutOutcome { KeepRecording, Transcribe, Discard }

// Hold to talk ends the recording when the shortcut is released; toggle waits for the next press.
public static class HeldShortcut
{
    // A shorter press is an accidental tap, not speech.
    public static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(0.3);

    // A hotkey message for the shortcut that started a running hold-to-talk recording, while that shortcut's keys are down at the moment
    // the message arrives, repeats the same hold: Windows can send it again although MOD_NOREPEAT asks it not to. The recording goes on
    // and ends at the release the timer observes. With the keys up, or for any other shortcut, it is a press. Toggle presses, and Escape,
    // which is never an activation shortcut, are never repeats; a release and press again too fast to tell apart continues the recording.
    public static bool RepeatsHold(bool holdToTalkRecording, ActivationShortcut? recordingShortcut, ActivationShortcut pressed, bool keysDown) =>
        holdToTalkRecording && pressed.IsValid && recordingShortcut == pressed && keysDown;

    public static HeldShortcutOutcome Decide(ShortcutBehavior behavior, bool shortcutHeld, TimeSpan heldFor) =>
        behavior != ShortcutBehavior.HoldToTalk || shortcutHeld ? HeldShortcutOutcome.KeepRecording
        : heldFor < MinimumHold ? HeldShortcutOutcome.Discard
        : HeldShortcutOutcome.Transcribe;
}
