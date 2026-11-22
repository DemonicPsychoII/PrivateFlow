namespace PrivateFlow.Core;

public enum MeetingShortcutAction { Start, Stop, Refuse }

public readonly record struct MeetingShortcutDecision(MeetingShortcutAction Action, string Reason = "");

// The meeting shortcut starts a meeting from idle and stops the one that records, as Stop and finish does. Anything else is
// refused with a reason for a tray notice, because the window may be hidden.
public static class MeetingShortcut
{
    public static MeetingShortcutDecision Decide(bool diagnostic, bool capturing, bool finishing, bool dictationActive, bool otherOperation, bool updating = false) =>
        diagnostic ? Refuse("Diagnostic instance: the meeting shortcut does not record.")
        : finishing ? Refuse("A meeting transcript is being finished. Press the meeting shortcut again once it is saved.")
        : capturing ? new(MeetingShortcutAction.Stop)
        : updating ? Refuse(UpdateGate.StartRefusal)
        : dictationActive ? Refuse("Dictation is running. Press the meeting shortcut again once it has finished.")
        : otherOperation ? Refuse("Speech recognition is being set up or checked under Settings. Press the meeting shortcut again once it has finished.")
        : new(MeetingShortcutAction.Start);
    private static MeetingShortcutDecision Refuse(string reason) => new(MeetingShortcutAction.Refuse, reason);
}
