namespace PrivateFlow.Core;

// How the last press of Write notes for a meeting ended when it stored no new notes. Detail is the reason of a failure, or the model
// that found nothing to note.
public enum NotesRunOutcome { Failed, Cancelled, NothingFound }
public sealed record NotesRun(NotesRunOutcome Outcome, string Detail);

// The notice cards above the notes on the Notes tab, the newest condition in front: how the last run ended, why the meeting notes model
// was not installed, notes a model wrote for an older transcript, and the caveat of the rule-based notes. Each is one card with a short
// text; a long reason is cut after its first sentence and is whole in Detail.
public static class NotesStatus
{
    public const string FailedKey = "notes-failed", NothingFoundKey = "notes-nothing-found", CancelledKey = "notes-cancelled",
        InstallFailedKey = "notes-install-failed", StaleKey = "notes-stale", RuleBasedKey = "notes-rule-based";
    public static readonly string[] Keys = [FailedKey, NothingFoundKey, CancelledKey, InstallFailedKey, StaleKey, RuleBasedKey];
    public const string RuleNotesCaveat = "Built by rules on this PC without a model. It misses most decisions and open questions.";
    public const string StaleNotes = MeetingHistory.StaleNotes + " Write them again or go back to the rule-based notes.";
    public const string ChooseHowNotesAreWritten = "Choose how notes are written";

    // modelNotesShown: notes a model wrote are shown, otherwise the rule-based ones (with a transcript). installFailure: why the meeting
    // notes model was not installed, while it is still missing. chooseNotesStyle opens where a model for the notes is chosen.
    public static IReadOnlyList<Notice> For(bool modelNotesShown, bool stale, NotesRun? lastRun, string? installFailure, Action chooseNotesStyle)
    {
        var notices = new List<Notice>();
        switch (lastRun)
        {
            case { Outcome: NotesRunOutcome.Failed, Detail: var reason }:
                var kept = modelNotesShown ? " The notes written before stay." : " The rule-based draft shows.";
                notices.Add(new(FailedKey, NoticeKind.Error, "Notes could not be written", NoticeText.Short(reason) + kept, NoticeText.Shortened(reason) ? reason + kept : null));
                break;
            case { Outcome: NotesRunOutcome.NothingFound, Detail: var model }:
                notices.Add(new(NothingFoundKey, NoticeKind.Note, "Nothing to note", $"{model} found no decisions, action items, open questions or times in this meeting."));
                break;
            case { Outcome: NotesRunOutcome.Cancelled }:
                notices.Add(new(CancelledKey, NoticeKind.Note, "Writing the notes was cancelled", "The notes shown are unchanged."));
                break;
        }
        if (installFailure is not null)
            notices.Add(new(InstallFailedKey, NoticeKind.Error, "The meeting notes model was not installed", NoticeText.Short(installFailure), NoticeText.Detail(installFailure)));
        if (modelNotesShown && stale) notices.Add(new(StaleKey, NoticeKind.Warning, "Notes are older than the transcript", StaleNotes));
        if (!modelNotesShown) notices.Add(new(RuleBasedKey, NoticeKind.Warning, "Rule-based draft", RuleNotesCaveat, Action: new(ChooseHowNotesAreWritten, chooseNotesStyle), Standing: true));
        return notices;
    }
}
