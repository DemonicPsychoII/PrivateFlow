using PrivateFlow.Core;
using static Harness;

static class NoticeChecks
{
    public static void VerifyNotesStatus()
    {
        {
            var chosen = 0; void Choose() => chosen++;
            string[] Keys(IReadOnlyList<Notice> notices) => [.. notices.Select(notice => notice.Key)];
            const string Install = "Setup failed: Checksum mismatch; nothing installed. Check network, available disk space and pinned download availability.";
            var caveat = NotesStatus.For(false, false, null, null, Choose);
            var nothing = NotesStatus.For(false, false, new(NotesRunOutcome.NothingFound, "gpt-test through the API"), null, Choose);
            var nothingOverWritten = NotesStatus.For(true, false, new(NotesRunOutcome.NothingFound, "gpt-test through the API"), null, Choose);
            var failed = NotesStatus.For(false, false, new(NotesRunOutcome.Failed, "The API did not answer within 60 s. Try again later or check the endpoint under Settings → Text style."), null, Choose);
            var failedOverStale = NotesStatus.For(true, true, new(NotesRunOutcome.Failed, "The API did not answer within 60 s."), null, Choose);
            var failedOverCurrent = NotesStatus.For(true, false, new(NotesRunOutcome.Failed, "The API did not answer within 60 s."), null, Choose);
            var installFailedOverStale = NotesStatus.For(true, true, null, Install, Choose);
            var cancelled = NotesStatus.For(false, false, new(NotesRunOutcome.Cancelled, "Writing the notes was cancelled."), null, Choose);
            var installFailed = NotesStatus.For(false, false, null, Install, Choose);
            var stale = NotesStatus.For(true, true, null, null, Choose);
            var current = NotesStatus.For(true, false, null, null, Choose);
            var longReason = NotesStatus.For(false, false, new(NotesRunOutcome.Failed, new string('x', 400)), null, Choose);
            caveat[0].Action!.Invoke();
            Check(Keys(caveat).SequenceEqual([NotesStatus.RuleBasedKey]) && caveat[0] is { Kind: NoticeKind.Warning, Text: NotesStatus.RuleNotesCaveat, Action.Label: NotesStatus.ChooseHowNotesAreWritten, Standing: true } && chosen == 1
                && Keys(nothing).SequenceEqual([NotesStatus.NothingFoundKey, NotesStatus.RuleBasedKey])
                && nothing[0] is { Kind: NoticeKind.Note, Text: "gpt-test through the API found no decisions, action items, open questions or times in this meeting." }
                && Keys(nothingOverWritten).SequenceEqual([NotesStatus.NothingFoundKey])
                && failed[0] is { Key: NotesStatus.FailedKey, Kind: NoticeKind.Error, Text: "The API did not answer within 60 s. The rule-based draft shows." } && failed[0].Detail!.Contains("Try again later")
                && Keys(failed).SequenceEqual([NotesStatus.FailedKey, NotesStatus.RuleBasedKey])
                && Keys(failedOverStale).SequenceEqual([NotesStatus.FailedKey, NotesStatus.StaleKey]) && failedOverStale[1] is { Kind: NoticeKind.Warning, Text: NotesStatus.StaleNotes }
                && failedOverCurrent is [{ Text: "The API did not answer within 60 s. The notes written before stay.", Detail: null }]
                && Keys(installFailedOverStale).SequenceEqual([NotesStatus.InstallFailedKey, NotesStatus.StaleKey]) && installFailedOverStale[0] is { Kind: NoticeKind.Error, Text: "Setup failed: Checksum mismatch; nothing installed.", Detail: Install }
                && Keys(cancelled).SequenceEqual([NotesStatus.CancelledKey, NotesStatus.RuleBasedKey]) && cancelled[0].Kind == NoticeKind.Note
                && Keys(installFailed).SequenceEqual([NotesStatus.InstallFailedKey, NotesStatus.RuleBasedKey])
                && Keys(stale).SequenceEqual([NotesStatus.StaleKey]) && current.Count == 0
                && longReason[0].Text.Length < 200 && longReason[0].Text.Contains('…') && longReason[0].Detail!.Length > 400,
                "the Notes tab's states are cards with the newest condition first: how the last run ended (an error, or a note for nothing found or cancelled), an install failure, stale notes and the rule-based caveat with its link; a stale notes card and a failed run are two cards; a long reason is shortened with the whole as its detail");
        }
    }

    public static void VerifyNoticeList()
    {
        {
            Notice Card(string key, NoticeKind kind = NoticeKind.Warning, string text = "Text.", bool dismissable = true) => new(key, kind, "Title", text, Dismissable: dismissable);
            string Shown(NoticeList list) => string.Join(",", list.Visible.Select(notice => notice.Key));
            var list = new NoticeList(); var changes = 0; list.Changed += () => changes++;
            list.Raise(Card("a")); list.Raise(Card("b")); list.Raise(Card("a"));
            var deduplicated = Shown(list) == "b,a" && changes == 2;
            list.Raise(Card("a", text: "New text."));
            Check(deduplicated && Shown(list) == "b,a" && list.Find("a")!.Text == "New text." && changes == 3,
                "a notice raised again by its key is one card in its place with its text brought up to date, newer keys in front, and an unchanged raise redraws nothing");
            var dismissed = list.Dismiss("b");
            for (var redraw = 0; redraw < 3; redraw++) list.Raise(Card("b"));
            list.Sync(["a", "b"], [Card("b"), Card("a")]);
            Check(dismissed && Shown(list) == "a" && list.Find("b") is not null, "a dismissed notice stays hidden however often the page raises it again while its condition lasts, and the condition itself stays");
            list.Clear("b"); list.Raise(Card("b"));
            var backAfterChange = Shown(list) == "b,a";
            list.Dismiss("a"); list.Report(Card("a", NoticeKind.Error));
            Check(backAfterChange && Shown(list) == "a,b", "a dismissed notice shows again when its condition ended and came back, and when an operation reports it anew, in front");
            list.Clear("a"); list.Clear("b");
            list.Raise(Card("condition")); list.Report(Card("outcome", NoticeKind.Error));
            list.Dismiss("condition"); list.Dismiss("outcome");
            var conditionHidden = list.Find("condition") is not null && list.Find("outcome") is null;
            list.Reopen();
            Check(conditionHidden && Shown(list) == "condition",
                "dismissing an outcome ends it, dismissing a condition hides it while it lasts; reopening the context shows dismissed conditions again and brings no ended outcome back");
            list.Clear("condition"); list.Raise(Card("b"));
            list.Raise(Card("decide", NoticeKind.Error, dismissable: false));
            Check(!list.Dismiss("decide") && Shown(list) == "decide,b" && !list.Dismiss("missing"), "a notice that asks for a decision cannot be dismissed, and dismissing an unknown key changes nothing");
            list.Sync(["b", "decide", "c"], [Card("c")]);
            Check(Shown(list) == "c", "Sync ends the page's notices that no longer hold and raises the current ones");
            var board = new NoticeBoard();
            NoticeList dictate = new(), meetings = new(), meetingA = new(), meetingB = new();
            var selected = "B";
            board.Add(dictate, "Dictate"); board.Add(meetings, "Meetings");
            board.Add(meetingA, "History", "“A”", () => selected == "A"); board.Add(meetingB, "History", "“B”", () => selected == "B");
            board.Show("History");
            meetings.Raise(Card("fallback", NoticeKind.Warning) with { Title = "System audio from the Windows default" });
            var pointedAway = (board.Pointer, Meetings: board.Marker("Meetings"), History: board.Marker("History"));
            dictate.Report(Card("refused", NoticeKind.Error) with { Title = "Dictation did not start" });
            var newestFirst = board.Pointer;
            dictate.Report(Card("note", NoticeKind.Note)); meetings.Raise(Card("caveat") with { Standing = true });
            var quietOnes = board.Pointer == newestFirst && board.Marker("Dictate") == NoticeKind.Error;
            meetingB.Report(Card("shown", NoticeKind.Error));
            var onScreenSeen = !board.Unseen(meetingB, "shown");
            meetingA.Report(Card("failed", NoticeKind.Error) with { Title = "Notes could not be written" });
            var otherMeeting = board.Pointer;
            selected = "A"; board.Refresh();
            var meetingSeen = !board.Unseen(meetingA, "failed") && board.Pointer == newestFirst;
            board.Show("Dictate");
            var pageSeen = board.Marker("Dictate") is null && board.Pointer == "System audio from the Windows default. See Meetings.";
            meetings.Clear("fallback");
            var endedNotUnseen = board.Pointer is null && board.Marker("Meetings") is null;
            board.Show(null); meetings.Raise(Card("silent", NoticeKind.Warning) with { Title = "No sound from the microphone" });
            var hiddenWindow = board.Marker("Meetings") == NoticeKind.Warning;
            meetings.Dismiss("silent");
            var dismissedNotUnseen = board.Marker("Meetings") is null;
            var opened = new List<string>();
            meetingA.Report(Card("again", NoticeKind.Warning)); selected = "B";
            var revealed = false; var revealing = new NoticeList(); board.Add(revealing, "History", "“C”", () => false, () => revealed = true);
            revealing.Report(Card("c", NoticeKind.Error)); board.OpenNewest(opened.Add);
            Check(pointedAway == ("System audio from the Windows default. See Meetings.", NoticeKind.Warning, null) && newestFirst == "Dictation did not start. See Dictate."
                && quietOnes && onScreenSeen && otherMeeting == "“A”: Notes could not be written. See History." && meetingSeen && pageSeen && endedNotUnseen && hiddenWindow && dismissedNotUnseen
                && opened.SequenceEqual(["History"]) && revealed,
                "a warning or an error raised where the user is not is unseen: the newest is named with its page (and its meeting), its page is marked; notes and standing caveats never are; it is seen when its page (and meeting) shows, and ends with its notice or its dismissal; while the window is hidden everything raised is unseen; opening it goes to its page and meeting");
            Check(NoticeText.Short("First sentence. Second one.") == "First sentence." && NoticeText.Detail("First sentence. Second one.") == "First sentence. Second one." && NoticeText.Detail("One sentence.") is null,
                "a card says the first sentence of a reason and keeps the whole as its detail only when it shortened it");
        }
    }

    public static void VerifyNoticeKeysDocumented()
    {
        {
            // Every notice key in the code is in docs/NOTICE_KEYS.md with where it ends, and every key there is still in the code. A key is a
            // string constant named ...Key, or a string passed right before a NoticeKind; a key built with a meeting or a channel counts up to
            // its first {.
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
            if (repository is null) throw new Exception("The checkout with src and docs was not found above " + AppContext.BaseDirectory);
            static string Stem(string key) => key.Split('{')[0];
            var inCode = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repository.FullName, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !file.EndsWith("WorkspaceVerification.cs")))
            {
                var source = File.ReadAllText(file);
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, @"\b\w+Key\s*=\s*""([^""]+)""")) inCode.Add(Stem(match.Groups[1].Value));
                foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(source, @"(?:\(|new\()\s*\$?""([^""]+)""\s*,\s*(?:[\w.]*NoticeKind\.|kind\b)")) inCode.Add(Stem(match.Groups[1].Value));
            }
            var inTable = new SortedSet<string>(File.ReadAllLines(Path.Combine(repository.FullName, "docs", "NOTICE_KEYS.md"))
                .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^\| `([^`]+)` \|")).Where(match => match.Success).Select(match => Stem(match.Groups[1].Value)), StringComparer.Ordinal);
            var missing = inCode.Except(inTable).ToArray(); var stale = inTable.Except(inCode).ToArray();
            Check(inCode.Count > 60 && missing.Length == 0 && stale.Length == 0,
                $"every notice key in the code says in docs/NOTICE_KEYS.md where it ends ({inCode.Count} keys; missing there: {string.Join(", ", missing)}; no longer in the code: {string.Join(", ", stale)})");
        }
    }

    public static void VerifyProgressAndDifferences()
    {
        var forward = new ForwardProgress();
        var forwardShares = new List<double> { forward.Map(0), forward.Map(0.4), forward.Map(0.3) };
        forward.Restart(); forwardShares.AddRange([forward.Map(0), forward.Map(0.5)]);
        forward.Restart(); forwardShares.AddRange([forward.Map(0), forward.Map(1.2)]);
        Check(forwardShares.Zip(new[] { 0, 0.4, 0.4, 0.4, 0.7, 0.7, 1 }).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9),
            "progress over a retry and a fallback continues from where the bar stands, never falls and ends at 100 %");
        Same(TextDifference.Describe("same", "same"), "No changes.", "an unchanged dictation shows no changes");
        Same(TextDifference.Describe("first\nUm, second", "first\nSecond"), "− Um, second\n+ Second", "the changes view lists removed and added lines after the common start");
        Same(TextCleanup.JoinSegments(" one\n two \r\n three "), "one two three", "Whisper's segment lines join into one paragraph");
        var speakers = new List<TranscriptSegment> { new(0, 20, "ship it tomorrow", "You") };
        SegmentAssembly.Append(speakers, [new(0, 3, "ship it tomorrow", "Remote speaker")], 19.5);
        Check(speakers.Count == 2 && speakers[1].Text == "ship it tomorrow", "overlap reconciliation never removes another speaker's words");
    }
}
