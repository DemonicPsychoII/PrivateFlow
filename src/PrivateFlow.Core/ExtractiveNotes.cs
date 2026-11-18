using System.Text;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

// Meeting notes built on this PC without a model, in the sections ModelNotes renders. Every line quotes the transcript rows it rests on
// and cites them. Rules, not a model: English, German and Spanish phrases mark proposals, agreements, commitments, requests, declines
// and deferrals, and the order of the rows connects them. Where a rule is unsure it leaves the point out rather than guess.
public static partial class ExtractiveNotes
{
    public const string Header = "Built by rules on this PC, without a model. Listed action items and times are usually right, but some are missing; decisions and open questions "
        + "are only candidates and most are missing; the summary quotes only a few rows. Every point quotes the transcript rows it cites, so check it there. An owner is the row's speaker label or the person addressed; a due date "
        + "is only words of the task's own rows.";
    public const string SummaryHeading = "Summary — quoted rows";
    public const string DecisionsHeading = "Decision candidates — verify against the cited rows";
    public const string ActionsHeading = "Action items — owner is the speaker or the person addressed, due date only as spoken";
    public const string QuestionsHeading = "Open question candidates — verify against the cited rows";
    public const string TimesHeading = "Times mentioned";
    // Rows after a proposal, request or question in which an agreement, acceptance or answer still counts.
    private const int AnswerRows = 3;
    // Every pattern is bounded, so no transcript can stall the window that builds the notes.
    private const int MatchTimeoutMilliseconds = 1000;

    private sealed record Row(int Index, MeetingSegment Segment, string[] Sentences, string Lower, string[] Words, HashSet<string> Terms, bool SmallTalk)
    {
        public string Speaker => Segment.Speaker;
    }
    private sealed class Decision { public List<Row> Rows { get; } = []; public Row? ChangedBy; }
    private sealed class Action { public List<Row> Rows { get; } = []; public List<string> DueSentences { get; } = []; public string? Owner; }
    private sealed record Question(Row Row, Row? Reason, bool Deferred);

    public static string Write(MeetingDocument document)
    {
        var silence = MeetingArchive.Caveats(document);
        // A row the editor emptied carries no words; the order of the others is kept, identical timestamps included.
        var segments = document.Segments.Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).OrderBy(segment => segment.Start).ToArray();
        if (segments.Length == 0) return silence + "No transcript evidence yet.";
        try { return silence + Build(document, segments); }
        catch (RegexMatchTimeoutException) { return silence + Header + "\n\nThese notes could not be built: a transcript row took too long to read. The transcript itself is unchanged."; }
    }

    private static string Build(MeetingDocument document, MeetingSegment[] segments)
    {
        var rows = segments.Select((segment, index) => Read(index, segment)).ToArray();
        var names = document.Vocabulary.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var decisions = Decisions(rows);
        var actions = Actions(rows, names);
        var questions = OpenQuestions(rows, decisions, actions);
        var summary = Summary(rows, names, decisions, actions);

        string Cited(IEnumerable<Row> cited) => string.Join(" ", cited.DistinctBy(row => row.Index).OrderBy(row => row.Index).Select(row => new EvidenceHit(document.Id, document.Title, row.Segment, 0).Citation));
        static string Said(Row row, string text) => $"{row.Speaker}: “{text}”";
        string Section(string heading, IReadOnlyList<string> lines) => "\n\n" + heading + "\n" + (lines.Count == 0 ? "None identified." : string.Join("\n", lines));
        var text = new StringBuilder(Header);
        text.Append(Section(SummaryHeading, summary.Select(row => "- " + Quote(row, sentence => sentence == row.Sentences[0] || row.Sentences.Length > 1 && sentence == row.Sentences[1]) + " " + Cited([row])).ToArray()));
        text.Append(Section(DecisionsHeading, decisions.Select(decision =>
        {
            var line = "- " + Quote(decision.Rows[0], IsDecisionSentence) + string.Concat(decision.Rows.Skip(1).Select(row => " · " + Said(row, Quote(row, IsDecisionSentence))));
            if (decision.ChangedBy is { } change) line += " · Later changed: " + Said(change, Quote(change, IsDecisionSentence));
            return line + " " + Cited(decision.ChangedBy is null ? decision.Rows : decision.Rows.Append(decision.ChangedBy));
        }).ToArray()));
        text.Append(Section(ActionsHeading, actions.Select(action =>
        {
            var due = Due(action.DueSentences);
            return "- " + string.Join(" ", action.Rows.Where(row => !IsAgreementOnly(row) && !IsShortAcceptance(row)).Select(row => Quote(row, IsActionSentence)).DefaultIfEmpty(action.Rows[0].Segment.Text.Trim()))
                + (action.Owner is { } owner ? " · Owner: " + owner : "") + (due.Length > 0 ? " · Due: " + due : "") + " " + Cited(action.Rows);
        }).ToArray()));
        text.Append(Section(QuestionsHeading, questions.Select(question => "- " + QuestionText(question.Row)
            + (question.Reason is not { } reason ? "" : question.Deferred ? " · Answer deferred: " + Said(reason, Quote(reason, sentence => DeferralPattern().IsMatch(Lower(sentence))))
                : " · Not answered before: " + Said(reason, reason.Segment.Text.Trim()))
            + " " + Cited(question.Reason is null ? [question.Row] : [question.Row, question.Reason])).ToArray()));
        text.Append(Section(TimesHeading, rows.Select(row => (Row: row, Times: TimesIn(row.Segment.Text))).Where(found => found.Times.Count > 0)
            .Select(found => "- " + string.Join(", ", found.Times) + " · " + Quote(found.Row, sentence => TimesIn(sentence).Count > 0) + " " + Cited([found.Row])).ToArray()));
        return text.ToString();
    }

    // A row of more than three sentences, such as a long status report, is quoted by the sentences the point rests on.
    private static string Quote(Row row, Func<string, bool> relevant)
    {
        if (row.Sentences.Length <= 3) return row.Segment.Text.Trim();
        var quoted = row.Sentences.Where(relevant).ToArray();
        return quoted.Length == 0 || quoted.Length == row.Sentences.Length ? row.Segment.Text.Trim() : (relevant(row.Sentences[0]) ? "" : "… ") + string.Join(" … ", quoted) + (relevant(row.Sentences[^1]) ? "" : " …");
    }
    private static string Lower(string text) => text.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('’', '\'');
    private static bool IsDecisionSentence(string sentence) => Lower(sentence) is var lower && (DecisionPattern().IsMatch(lower) || ProposalPattern().IsMatch(lower) || StrongAgreementStart().IsMatch(lower));
    private static bool IsActionSentence(string sentence) => Lower(sentence) is var lower && (IsCommitment(lower) || IsRequestSentence(lower) || UnassignedTaskPattern().IsMatch(lower) || DueQuestionPattern().IsMatch(lower));

    private static Row Read(int index, MeetingSegment segment)
    {
        var text = segment.Text.Trim();
        var sentences = SentenceBreak().Split(text).Where(sentence => sentence.Trim().Length > 0).ToArray();
        var lower = Lower(text);
        var words = Words(lower).ToArray();
        var terms = words.Where(word => word.Length >= 4 && !StopWords.Contains(word) || word.All(char.IsAsciiDigit)).ToHashSet();
        return new(index, segment, sentences, lower, words, terms, sentences.All(sentence => IsSmallTalk(Lower(sentence))));
    }
    private static IEnumerable<string> Words(string text) => WordPattern().Matches(text).Select(match => match.Value.ToLowerInvariant());

    // Greetings, goodbyes, thanks, apologies for the camera or the traffic, and audio checks, each by how a sentence starts, so "Let's
    // deploy on the weekend." or "Morgen liefern wir." stay content.
    private static bool IsSmallTalk(string sentence)
    {
        if (AudioCheckPattern().IsMatch(sentence)) return true;
        var words = Words(sentence).ToArray();
        if (words.Length == 0) return false;
        var start = words.Length > 1 ? words[0] + " " + words[1] : words[0];
        if ((Greetings.Contains(words[0]) || Greetings.Contains(start)) && words.Length <= 5) return true;
        if ((Farewells.Contains(words[0]) || Farewells.Contains(start)) && words.Length <= 7) return true;
        return Apologies.Contains(words[0]) && words.Any(TechnicalExcuses.Contains);
    }

    // A label that stands for several people, so two of its rows may come from different speakers.
    private static bool IsGeneric(string speaker) => speaker is "Remote speaker" or "Unknown" or "Speaker" || speaker.Length == 0;
    private static bool Different(Row first, Row second) => first.Speaker != second.Speaker || IsGeneric(first.Speaker);

    private static bool IsQuestion(string sentence)
    {
        var trimmed = sentence.Trim();
        if (trimmed.EndsWith('?') || trimmed.StartsWith('¿')) return true;
        return trimmed.Length > 0 && trimmed[^1] is not ('.' or '!' or ':' or ';') && QuestionStart().IsMatch(LeadingName().Replace(Lower(trimmed), ""));
    }
    private static bool HasQuestion(Row row) => row.Sentences.Any(IsQuestion);
    private static IEnumerable<string> Statements(Row row) => row.Sentences.Where(sentence => !IsQuestion(sentence)).Select(Lower);
    private static IEnumerable<string> Questions(Row row) => row.Sentences.Where(IsQuestion).Select(Lower);
    private static string QuestionText(Row row) => string.Join(" ", row.Sentences.SkipWhile(sentence => !IsQuestion(sentence)));

    // "Agreed.", "Sí, vale." or "Ja, passt.": nothing but agreement.
    // Nothing but agreement, with at least one word that agrees: a filler alone ("Hmm.", "Well.") agrees to nothing.
    private static bool IsAgreementOnly(Row row) => IsAgreementOnly(row.Words);
    private static bool IsAgreementOnly(string sentence) => IsAgreementOnly(Words(sentence).ToArray());
    private static bool IsAgreementOnly(IReadOnlyCollection<string> words) =>
        words.Any(AgreementWords.Contains) && words.All(word => AgreementWords.Contains(word) || Fillers.Contains(word));
    // Agreement to a proposal: a row of nothing but agreement, or one that opens with an unambiguous phrase ("Sounds good", "Einverstanden",
    // "De acuerdo"). A "yes" with more words only confirms a question when it repeats what was asked ("Yes, 1,250 stays.").
    private static bool Agrees(Row row, Row proposal) =>
        !Declines(row) && !Defers(row) && !HasQuestion(row) && !NegationPattern().IsMatch(row.Lower)
        && (IsAgreementOnly(row) || StrongAgreementStart().IsMatch(row.Lower)
            || IsConfirmationQuestion(proposal) && YesStart().IsMatch(row.Lower) && (row.Words.Length <= 4 || row.Terms.Overlaps(proposal.Terms)));
    private static bool Declines(Row row) => DeclinePattern().IsMatch(row.Lower);
    private static bool Defers(Row row) => DeferralPattern().IsMatch(row.Lower);

    private static bool IsExplicitDecision(Row row) => Statements(row).Any(sentence => DecisionPattern().IsMatch(sentence) && !ReportedPattern().IsMatch(sentence) && !NegatedDecisionPattern().IsMatch(sentence));
    private static bool IsProposal(Row row) => !row.SmallTalk && row.Sentences.Select(Lower).Any(lower => !ReportedPattern().IsMatch(lower) && !AgendaPattern().IsMatch(lower)
        && !IsRequestSentence(lower) && ProposalPattern().IsMatch(lower));
    private static bool IsConfirmationQuestion(Row row) => row.Sentences.Where(IsQuestion).Select(Lower).Any(lower => !IsRequestSentence(lower)
        && (ProposalPattern().IsMatch(lower) || ConfirmationPattern().IsMatch(lower)));
    private static bool IsConsensusCheck(Row row) => ConsensusPattern().IsMatch(row.Lower) && row.Words.Length <= 7;

    // An explicit decision, or a proposal someone else agrees to within the next rows; checks such as "Is that okay for everyone?" are
    // passed over. A later decision marks an earlier one as changed only when it carries a change cue and shares its subject with exactly
    // that one earlier decision.
    private static List<Decision> Decisions(Row[] rows)
    {
        var decisions = new List<Decision>(); var used = new HashSet<int>();
        bool AgreedAfter(Row proposal)
        {
            foreach (var row in rows.Skip(proposal.Index + 1).Take(AnswerRows))
            {
                if (IsConsensusCheck(row) || row.SmallTalk) continue;
                if (!Different(row, proposal)) return false;
                return Agrees(row, proposal);
            }
            return false;
        }
        foreach (var row in rows)
        {
            if (used.Contains(row.Index) || !(IsExplicitDecision(row) || (IsProposal(row) || IsConfirmationQuestion(row)) && AgreedAfter(row))) continue;
            var decision = new Decision(); decision.Rows.Add(row); used.Add(row.Index);
            var last = row.Index;
            for (var index = row.Index + 1; index < rows.Length && index <= last + AnswerRows; index++)
            {
                var next = rows[index];
                if (IsConsensusCheck(next) || next.SmallTalk) continue;
                if (Different(next, row) && Agrees(next, row) || IsExplicitDecision(next) && Subject(next).Intersect(decision.Rows.SelectMany(Subject)).Any())
                { decision.Rows.Add(next); used.Add(index); last = index; }
                else break;
            }
            decisions.Add(decision);
        }
        var decided = decisions.SelectMany(decision => decision.Rows).Select(row => row.Index).ToHashSet();
        foreach (var (later, position) in decisions.Select((decision, position) => (decision, position)))
        {
            var before = rows.Skip(Math.Max(0, later.Rows[0].Index - AnswerRows)).Take(Math.Min(AnswerRows, later.Rows[0].Index)).Where(row => !decided.Contains(row.Index));
            var cue = later.Rows.Concat(before).FirstOrDefault(row => ChangePattern().IsMatch(row.Lower));
            if (cue is null) continue;
            var subject = later.Rows.SelectMany(Subject).ToHashSet();
            var earlier = decisions.Take(position).Where(decision => decision.Rows.SelectMany(Subject).Any(subject.Contains)).ToArray();
            // An earlier entry that already joins two statements ("ship the app", "ship the docs") leaves open which one changed.
            if (earlier.Length == 1 && earlier[0].ChangedBy is null && earlier[0].Rows.Count(member => IsExplicitDecision(member) || IsProposal(member)) == 1) earlier[0].ChangedBy = later.Rows.FirstOrDefault(row => ChangePattern().IsMatch(row.Lower)) ?? later.Rows[0];
        }
        return decisions;
    }
    // What a decision is about: its content words and version names, without weekdays, months, plain numbers and decision words, so
    // two decisions on Friday are not one subject.
    private static IEnumerable<string> Subject(Row row) => IsAgreementOnly(row) ? []
        : row.Terms.Where(term => !term.All(char.IsAsciiDigit) && !SubjectNeutral.Contains(term) && !AgreementWords.Contains(term))
            .Concat(row.Words.Where(word => word.Contains('.') && word.Any(char.IsAsciiDigit)));

    // Commitments ("I'll", "me encargo", "mach ich") belong to their speaker. A request belongs to whoever accepts it in the next rows,
    // with "Sure." alone or with a commitment that refers to it ("I'll do it", "lo hago yo", "Dann mache ich das") or names what was
    // asked: the speaker when the label names one person, otherwise the person addressed by name, unless someone declined first.
    // A due date is only a time said in the request, the commitment or the answer to "by when?" right after it.
    private static List<Action> Actions(Row[] rows, string[] names)
    {
        var actions = new List<Action>(); var used = new HashSet<int>();
        void Add(Action action)
        {
            // A follow-up of the same task ("Could you also include …?" "Sure.") joins it, unless someone else asked a question in
            // between, which starts another subject.
            var previous = actions.LastOrDefault(earlier => earlier.Owner == action.Owner && action.Rows[0].Index - earlier.Rows[^1].Index <= AnswerRows
                && earlier.Rows.SelectMany(row => row.Terms).Intersect(action.Rows.SelectMany(row => row.Terms)).Any()
                && !rows.Skip(earlier.Rows[^1].Index + 1).Take(action.Rows[0].Index - earlier.Rows[^1].Index - 1).Any(between => HasQuestion(between) && between.Speaker != action.Owner));
            if (previous is not null && Due(action.DueSentences).Length == 0) previous.Rows.AddRange(action.Rows.Where(row => !previous.Rows.Contains(row)));
            else actions.Add(action);
        }
        foreach (var row in rows)
        {
            if (used.Contains(row.Index)) continue;
            if (Questions(row).Any(sentence => DueQuestionPattern().IsMatch(sentence)))
            {
                var action = actions.LastOrDefault();
                var answer = row.Index + 1 < rows.Length ? rows[row.Index + 1] : null;
                // The answer counts only from the one who owns the task ("Ben: I'll send it." "By when?" "Ben: Friday."), not from a third person.
                if (action is not null && row.Index - action.Rows[^1].Index <= 2 && answer is not null && Different(answer, row) && answer.Words.Length <= 8 && Due([answer.Segment.Text]).Length > 0
                    && (answer.Speaker == action.Owner || IsGeneric(answer.Speaker) && (action.Owner is null || IsGeneric(action.Rows[^1].Speaker))))
                { action.Rows.Add(row); action.Rows.Add(answer); action.DueSentences.Add(answer.Segment.Text); used.Add(answer.Index); }
                continue;
            }
            if (IsRequest(row) && Accepted(row) is { } accepted)
            {
                var action = new Action { Owner = accepted.Owner };
                action.Rows.Add(row); action.Rows.Add(accepted.Row); used.Add(accepted.Row.Index);
                action.DueSentences.AddRange(row.Sentences.Where(sentence => IsRequestSentence(Lower(sentence))));
                action.DueSentences.AddRange(AcceptanceDue(accepted.Row, row));
                Add(action);
                continue;
            }
            // An acceptance alone ("Will do.") names no task; after a request the rules did not recognise it would be listed without one.
            if (IsAgreementOnly(row) || IsShortAcceptance(row)) continue;
            if (row.Sentences.Where(sentence => !IsQuestion(sentence)).Where(sentence => IsCommitment(Lower(sentence))).ToArray() is { Length: > 0 } committed)
            { var action = new Action { Owner = row.Speaker }; action.Rows.Add(row); action.DueSentences.AddRange(committed); used.Add(row.Index); Add(action); }
            else if (Statements(row).Any(sentence => UnassignedTaskPattern().IsMatch(sentence) && !DeferralPattern().IsMatch(sentence)))
            { var action = new Action(); action.Rows.Add(row); action.DueSentences.AddRange(row.Sentences.Where(sentence => UnassignedTaskPattern().IsMatch(Lower(sentence)))); used.Add(row.Index); actions.Add(action); }
        }
        return actions;

        (Row Row, string Owner)? Accepted(Row request)
        {
            var addressed = Addressed(request, names); var declined = false;
            foreach (var next in rows.Skip(request.Index + 1).Take(AnswerRows))
            {
                if (next.SmallTalk) continue;
                if (!Different(next, request)) { if (IsRequest(next)) return null; continue; }
                if (Declines(next)) { if (declined) return null; declined = true; continue; }
                if (!Accepts(next, request)) return null;
                return (next, !IsGeneric(next.Speaker) ? next.Speaker : addressed is not null && !declined ? addressed : next.Speaker);
            }
            return null;
        }
    }
    // "Sure.", "Sí, claro." or a commitment that refers to the request ("it", "das", a pronoun before the verb as in "lo hago"); a
    // commitment to something else ("I'll book the room", "Reservo la sala") is its own task.
    private static bool Accepts(Row row, Row request)
    {
        if (HasQuestion(row)) return false;
        if (IsAgreementOnly(row) || IsShortAcceptance(row)) return true;
        var commitments = row.Sentences.Select(Lower).Where(IsCommitment).ToArray();
        if (commitments.Any(sentence => !RelatesTo(sentence, request))) return false;
        return commitments.Length > 0 || IsAgreementOnly(Lower(row.Sentences[0]));
    }
    private static bool RelatesTo(string sentence, Row request) => RefersBackPattern().IsMatch(sentence) || Words(sentence).Any(request.Terms.Contains);
    // "Sure, by Friday.", "Ja, bis Freitag.", "Sí, para el viernes.", "Gerne." or "On it.": a few words that open with an acceptance and
    // say nothing else than, perhaps, when.
    private static bool IsShortAcceptance(Row row)
    {
        if (row.Words.Length > 6 || !AcceptanceStart().IsMatch(row.Lower) || Declines(row) || NegationPattern().IsMatch(row.Lower)) return false;
        var dated = Due([row.Segment.Text]).Length > 0;
        return row.Words.All(word => AgreementWords.Contains(word) || AcceptanceWords.Contains(word)
            || dated && (DueWords.Contains(word) || SubjectNeutral.Contains(word) || word.All(char.IsAsciiDigit)));
    }
    // The sentences of an acceptance that may say when: a one-sentence answer, a short acceptance, a commitment to the request, or after
    // "Sure." a sentence naming what was asked ("Sure. Our sales team sends the offer on Monday.").
    private static IEnumerable<string> AcceptanceDue(Row accepted, Row request) => accepted.Sentences.Where(sentence => Lower(sentence) is var lower
        && (accepted.Sentences.Length == 1 || IsShortAcceptance(accepted) || IsCommitment(lower) && RelatesTo(lower, request)
            || IsAgreementOnly(Lower(accepted.Sentences[0])) && Words(lower).Any(request.Terms.Contains)));
    private static bool IsCommitment(string sentence)
    {
        if (!(CommitmentPattern().Match(sentence) is { Success: true } commitment) || NotCommitmentPattern().IsMatch(sentence) || NegatedCommitmentPattern().IsMatch(sentence)) return false;
        // "si lo termino el jueves", "if I finish it": a commitment inside a condition is none.
        return !(ConditionPattern().Match(sentence) is { Success: true } condition && condition.Index < commitment.Index);
    }
    private static bool IsRequestSentence(string lower) => RequestPattern().IsMatch(lower) && !SpeakNowPattern().IsMatch(lower) && !AudioCheckPattern().IsMatch(lower) && !DueQuestionPattern().IsMatch(lower);
    private static bool IsRequest(Row row) => row.Sentences.Select(Lower).Any(IsRequestSentence);

    // The vocabulary name a sentence addresses: "Diego, can you …", "And Priya, …" or "…, Tomás?", compared without regard to case and
    // culture; otherwise a capitalized word followed by a comma at the start of the row.
    private static string? Addressed(Row row, string[] names)
    {
        foreach (var sentence in row.Sentences)
        {
            var words = WordPattern().Matches(sentence).ToArray();
            var first = 0;
            while (first < words.Length - 1 && Connectors.Contains(words[first].Value.ToLowerInvariant())) first++;
            foreach (var name in names)
            {
                if (first < words.Length && At(sentence, words[first].Index, name) is var end && end > 0 && end < sentence.Length && sentence[end] == ',') return name;
                var comma = sentence.LastIndexOf(',');
                if (comma >= 0 && sentence[(comma + 1)..].Trim().TrimEnd('?', '.', '!').Trim().Equals(name, StringComparison.OrdinalIgnoreCase)) return name;
            }
        }
        return VocativePattern().Match(row.Segment.Text) is { Success: true } match && !StopWords.Contains(match.Groups[1].Value.ToLowerInvariant()) && !Connectors.Contains(match.Groups[1].Value.ToLowerInvariant())
            ? match.Groups[1].Value : null;
        static int At(string sentence, int start, string name) =>
            start + name.Length <= sentence.Length && string.Compare(sentence, start, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
            && (start + name.Length == sentence.Length || !char.IsLetterOrDigit(sentence[start + name.Length])) ? start + name.Length : -1;
    }

    // A question is a candidate only on a clear signal: the answer defers it ("we'll see", "no sé", "klären wir"), someone says it stays
    // open, or the meeting closes right after it. Unanswered-looking questions without such a signal are left out.
    private static List<Question> OpenQuestions(Row[] rows, List<Decision> decisions, List<Action> actions)
    {
        var handled = decisions.SelectMany(decision => decision.Rows).Concat(actions.SelectMany(action => action.Rows)).Select(row => row.Index).ToHashSet();
        var open = new List<Question>();
        foreach (var row in rows)
        {
            var asked = Questions(row).Where(sentence => Words(sentence).Count() >= 4 && !IsSmallTalk(sentence) && !RhetoricalPattern().IsMatch(sentence) && !ConsensusPattern().IsMatch(sentence)
                && !SuggestionPattern().IsMatch(sentence) && !DueQuestionPattern().IsMatch(sentence) && !SpeakNowPattern().IsMatch(sentence)).ToArray();
            if (asked.Length == 0 || handled.Contains(row.Index) || row.SmallTalk) continue;
            // The speaker went on after asking, so the question was rhetorical or answered at once.
            if (!IsQuestion(row.Sentences[^1])) continue;
            Question? found = null; var window = rows.Skip(row.Index + 1).Take(AnswerRows).ToArray();
            for (var position = 0; position < window.Length; position++)
            {
                var next = window[position];
                if (ClosingPattern().IsMatch(next.Lower)) { if (position == 0) found = new(row, next, false); break; }
                if (next.SmallTalk) continue;
                var said = next.Sentences.Select(Lower).Where(sentence => !IsFiller(sentence)).ToArray();
                if (said.Length > 0 && (DeferralPattern().IsMatch(said[0]) || asked.Length > 1 && Defers(next))) { found = new(row, next, true); break; }
                if (Different(next, row) && said.Length > 0 && (!said.All(IsQuestion) || SuggestionPattern().IsMatch(next.Lower))) break;
            }
            found ??= window.FirstOrDefault(next => StillOpenPattern().IsMatch(next.Lower)) is { } stillOpen ? new(row, stillOpen, true) : null;
            if (found is null) continue;
            // "Ni idea." followed by "I can check the Apple account.": someone took the question on.
            if (found.Deferred && rows.Skip(found.Reason!.Index + 1).Take(2).Any(next => Different(next, row) && Statements(next).Any(IsCommitment))) continue;
            if (rows.Skip(row.Index + AnswerRows + 1).Any(later => BackReferencePattern().IsMatch(later.Lower) && later.Terms.Overlaps(row.Terms))) continue;
            open.Add(found);
        }
        return open;
    }
    private static bool IsFiller(string sentence) => Words(sentence).ToArray() is { Length: > 0 } words
        && words.All(word => Fillers.Contains(word) || FillerSentenceWords.Contains(word) || word.Length > 1 && word.All(letter => letter is 'h' or 'm'));

    // The opening agenda row when there is one, then from each stretch of the meeting the row that scores highest: one point per term
    // several speakers (or, under one label, several rows) used, up to four, one for a number, one per vocabulary name, up to two, two
    // for stating a decision and one for an action. Stretches follow each other, so the rows follow the meeting's topics in order.
    private static List<Row> Summary(Row[] rows, string[] names, List<Decision> decisions, List<Action> actions)
    {
        var speakersByTerm = rows.SelectMany(row => row.Terms.Where(term => !term.All(char.IsAsciiDigit)).Select(term => (term, row))).GroupBy(entry => entry.term)
            .ToDictionary(group => group.Key, group => (Speakers: group.Select(entry => entry.row.Speaker).Distinct().Count(), Rows: group.Select(entry => entry.row.Index).Distinct().Count()));
        var oneLabel = rows.Select(row => row.Speaker).Distinct().Count() == 1 || rows.All(row => IsGeneric(row.Speaker));
        bool Shared(string term) => speakersByTerm.TryGetValue(term, out var use) && (oneLabel ? use.Rows >= 3 : use.Speakers >= 2 && use.Rows >= 2);
        var decided = decisions.Select(decision => decision.Rows[0].Index).ToHashSet();
        var acted = actions.SelectMany(action => action.Rows).Select(row => row.Index).ToHashSet();
        var nameWords = names.Select(name => Words(Lower(name)).ToArray()).Where(words => words.Length > 0).ToArray();
        bool Names(Row row, string[] name) => Enumerable.Range(0, Math.Max(0, row.Words.Length - name.Length + 1)).Any(start => name.Select((word, offset) => row.Words[start + offset] == word).All(same => same));
        int Score(Row row) => Math.Min(4, row.Terms.Count(Shared)) + (TranscriptScoring.Words(row.Segment.Text).Any(word => word.Length >= 2 && word.All(char.IsAsciiDigit)) ? 1 : 0)
            + Math.Min(2, nameWords.Count(name => Names(row, name))) + (decided.Contains(row.Index) ? 2 : 0) + (acted.Contains(row.Index) ? 1 : 0);
        bool Eligible(Row row) => !row.SmallTalk && !IsSmallTalk(Lower(row.Sentences[0])) && !HasQuestion(row) && !Declines(row) && !IsAgreementOnly(row) && row.Words.Length >= 5;
        var agenda = rows.Take(Math.Max(5, rows.Length / 5)).FirstOrDefault(row => AgendaPattern().IsMatch(row.Lower) && row.Words.Length >= 8);
        var stretches = Math.Clamp(rows.Length / 8, 3, 8);
        var chosen = agenda is null ? new List<Row>() : [agenda];
        for (var stretch = 0; stretch < stretches; stretch++)
        {
            var best = rows.Skip(stretch * rows.Length / stretches).Take((stretch + 1) * rows.Length / stretches - stretch * rows.Length / stretches)
                .Where(row => row != agenda && Eligible(row) && Score(row) >= 2)
                .Where(row => !chosen.Any(other => other.Terms.Count > 0 && row.Terms.Count(other.Terms.Contains) * 2 >= Math.Min(row.Terms.Count, other.Terms.Count)))
                .OrderByDescending(Score).ThenBy(row => row.Index).FirstOrDefault();
            if (best is not null) chosen.Add(best);
        }
        return chosen.OrderBy(row => row.Index).ToList();
    }

    // Clock times, weekdays and dates of a row: the shared time parser's clock times, then the dates and weekdays it does not read. The
    // only correction here: a dotted number is a version ("Lumen 2.10", "8.0.10") unless a time word comes before it or "Uhr" after it.
    public static IReadOnlyList<string> TimesIn(string text)
    {
        var times = TranscriptScoring.TimeReferences(VersionPattern().Replace(text, match => IsVersion(text, match) ? " " : match.Value)).ToList();
        foreach (Match match in ModelNotes.ClockTimeOrDate().Matches(text))
        {
            if (VersionPattern().Matches(text).Any(version => version.Index <= match.Index && match.Index < version.Index + version.Length && IsVersion(text, version))) continue;
            var spoken = match.Value.Trim();
            var clock = ClockForm(spoken);
            if (clock is not null ? !times.Contains(clock) && !times.Contains(spoken) : !times.Any(time => time.Equals(spoken, StringComparison.OrdinalIgnoreCase))) times.Add(clock ?? spoken);
        }
        return times;
    }
    // A dotted date needs its four-digit year ("12.03.2026"); "4.1.30" and "version 8.0.10" are versions.
    private static bool IsVersion(string text, Match match) =>
        !(DottedDatePattern().IsMatch(match.Value) && !VersionCueBefore().IsMatch(text[..match.Index]))
        && !(match.Value.Count(character => character == '.') == 1 && (TimeCueBefore().IsMatch(text[..match.Index]) || ClockUnitAfter().IsMatch(text[(match.Index + match.Length)..])));
    private static string? ClockForm(string spoken) =>
        ClockFormPattern().Match(spoken) is { Success: true } match && (match.Groups[2].Success || spoken.EndsWith("uhr", StringComparison.OrdinalIgnoreCase))
            ? $"{int.Parse(match.Groups[1].Value)}:{(match.Groups[2].Success ? match.Groups[2].Value : "00")}" : null;

    // The clock times, dates, weekdays and relative days ("tomorrow", "pasado mañana", "Ende der Woche") said in the sentences of a task.
    private static string Due(IEnumerable<string> sentences) => string.Join(", ", sentences.Where(sentence => !DeclinePattern().IsMatch(Lower(sentence))).SelectMany(sentence =>
    {
        var relative = RelativeDayPattern().Matches(sentence).Select(match => match.Value.Trim()).ToArray();
        return TimesIn(sentence).Where(time => !time.StartsWith("minute", StringComparison.Ordinal) && !relative.Any(phrase => phrase.Contains(time, StringComparison.OrdinalIgnoreCase))).Concat(relative);
    }).Distinct(StringComparer.OrdinalIgnoreCase));

    private static readonly HashSet<string> AgreementWords = new(StringComparer.Ordinal) { "agreed", "agree", "i", "we", "all", "ok", "okay", "yes", "yeah", "yep", "sure", "fine", "deal", "alright", "right",
        "good", "great", "perfect", "sounds", "me", "to", "too", "with", "works", "for", "that", "do", "will", "it", "by", "same", "here", "absolutely", "exactly",
        "de", "acuerdo", "vale", "sí", "si", "claro", "perfecto", "genial", "bien", "parece", "estoy", "estamos", "hecho", "bueno", "exacto",
        "einverstanden", "ja", "genau", "gut", "passt", "abgemacht", "alles", "klar", "super", "prima", "mach", "ich", "mache", "machen", "so", "sehe", "auch" };
    private static readonly HashSet<string> AcceptanceWords = new(StringComparer.Ordinal) { "gerne", "gern", "natürlich", "on", "of", "course", "can", "por", "supuesto", "con", "gusto" };
    // The words around a due date in a short acceptance: "by Friday", "bis Freitag", "para el viernes", "before 5 pm".
    private static readonly HashSet<string> DueWords = new(StringComparer.Ordinal) { "by", "before", "until", "till", "on", "at", "the", "end", "of", "bis", "vor", "am", "um", "zum", "ende", "der",
        "para", "antes", "del", "de", "el", "la", "hasta", "a", "fin", "pm", "uhr", "noon", "morning", "afternoon", "evening", "mittag", "abend", "tarde" };
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal) { "um", "uh", "hmm", "mmm", "mm", "eh", "äh", "ähm", "also", "pues", "bueno", "well", "so", "then", "entonces", "dann", "oh", "ah" };
    private static readonly HashSet<string> FillerSentenceWords = new(StringComparer.Ordinal) { "good", "great", "question", "buena", "pregunta", "gute", "frage", "okay", "ok", "right", "hm" };
    private static readonly HashSet<string> Greetings = new(StringComparer.Ordinal) { "hi", "hey", "hello", "hallo", "hola", "morning", "moin", "servus", "buenas", "good morning", "buenos días",
        "guten morgen", "guten tag", "morgen", "good afternoon", "buenas tardes" };
    private static readonly HashSet<string> Farewells = new(StringComparer.Ordinal) { "bye", "goodbye", "ciao", "tschüss", "adiós", "thanks", "thank", "gracias", "danke", "cheers", "hasta luego",
        "talk soon", "see you", "bis dann", "bis später", "have a", "schönen tag", "nos vemos", "hasta pronto" };
    private static readonly HashSet<string> Apologies = new(StringComparer.Ordinal) { "sorry", "perdón", "perdona", "perdonad", "entschuldigung", "entschuldigt", "disculpad", "disculpa" };
    private static readonly HashSet<string> TechnicalExcuses = new(StringComparer.Ordinal) { "camera", "kamera", "cámara", "headset", "late", "tarde", "spät", "verspätung", "tráfico", "traffic",
        "connection", "verbindung", "conexión", "microphone", "mikrofon", "micrófono", "audio" };
    private static readonly HashSet<string> Connectors = new(StringComparer.Ordinal) { "and", "y", "und", "also", "so", "okay", "ok", "vale", "gut", "pues", "perfect", "perfecto", "thanks", "gracias", "danke", "great", "bien" };
    private static readonly HashSet<string> SubjectNeutral = new(StringComparer.Ordinal)
    {
        "decided", "decision", "agreed", "agree", "instead", "actually", "einverstanden", "acuerdo", "perfecto", "okay", "genial", "beschlossen", "entschieden", "vereinbart", "stattdessen", "decidimos", "decidido", "acordamos", "lugar",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "montag", "dienstag", "mittwoch", "donnerstag", "freitag", "samstag", "sonntag",
        "lunes", "martes", "miércoles", "jueves", "viernes", "sábado", "domingo", "january", "february", "march", "april", "june", "july", "august", "september", "october",
        "november", "december", "januar", "februar", "märz", "juni", "juli", "oktober", "dezember", "enero", "febrero", "marzo", "abril", "mayo", "junio", "julio", "agosto",
        "septiembre", "octubre", "noviembre", "diciembre", "week", "woche", "semana", "today", "tomorrow", "morgen", "heute", "mañana", "next", "nächste", "próxima",
    };
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "that", "this", "these", "those", "with", "from", "have", "will", "would", "should", "could", "about", "there", "their", "them", "they", "what", "when", "where", "which", "while",
        "were", "been", "being", "just", "also", "then", "than", "into", "only", "some", "more", "most", "very", "really", "okay", "yeah", "maybe", "think", "know", "like", "need", "want",
        "does", "done", "doing", "here", "your", "yours", "ours", "it's", "that's", "let's", "we'll", "i'll", "you'll", "don't", "can't", "won't", "isn't", "didn't", "thing", "things",
        "still", "again", "even", "sure", "good", "great", "thanks", "thank", "right", "well", "because", "before", "after", "every", "other", "another", "anyone", "everyone", "somebody",
        "para", "pero", "como", "porque", "esto", "esta", "este", "estos", "estas", "eso", "esos", "también", "tenemos", "tiene", "tengo", "hay", "muy", "más", "menos", "todo", "todos", "nada",
        "creo", "bueno", "vale", "claro", "entonces", "ahora", "luego", "aquí", "donde", "cuando", "quién", "quien", "cómo", "qué", "sobre", "desde", "hasta", "gracias", "puedes", "vamos",
        "dass", "dann", "aber", "oder", "auch", "noch", "schon", "eine", "einen", "einem", "einer", "eines", "nicht", "sind", "haben", "habe", "hast", "wird", "werden", "kann", "können",
        "muss", "müssen", "soll", "sollen", "doch", "mal", "also", "jetzt", "hier", "danke", "genau", "gerne", "eigentlich", "vielleicht", "glaube", "machen", "mache", "gibt", "wenn", "weil",
        "über", "unter", "nach", "beim", "vom", "zum", "zur", "dieser", "diese", "dieses", "einfach", "ganz", "sehr", "alle", "alles", "euch", "dich", "mich", "uns",
    };

    private const RegexOptions Invariant = RegexOptions.CultureInvariant;
    private const RegexOptions InvariantIgnoringCase = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;
    // A period after a digit ends no sentence: German dates and ordinals ("den 3. April", "Freitag, der 14. März").
    [GeneratedRegex(@"(?<=[!?]|(?<!\d)\.)\s+(?=\S)", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex SentenceBreak();
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:['.][\p{L}\p{N}]+)*", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex WordPattern();
    [GeneratedRegex(@"^(?:who|what|when|where|why|how|which|do|does|did|is|are|was|were|can|could|should|shall|will|would|has|have|wer|was|wann|wo|warum|wieso|wie|welche|welcher|kannst|können|sollen|soll|hast|habt|ist|sind|gibt|qué|quién|cuándo|dónde|por qué|cómo|cuál|cuánto|cuántos)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex QuestionStart();
    [GeneratedRegex(@"^\p{L}+,?\s+(?=(?:are|is|do|did|can|could|will|kannst|hast|bist|puedes|tienes)\b)", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex LeadingName();
    [GeneratedRegex(@"\b(?:(?:can|could) you (?:all |guys )?(?:hear|see) (?:me|us|my screen|the screen)|am i on mute|you're on mute|you are on mute|(?:hört|seht) ihr (?:mich|uns|meinen bildschirm)|(?:hörst|siehst) du (?:mich|meinen bildschirm)|(?:könnt|kannst) (?:ihr|du) mich (?:hören|sehen)|kannst du meinen bildschirm sehen|me (?:oyes|oís|oyen|escuchas|escucháis|escuchan|ves|veis)|se me (?:oye|escucha|ve)|puedes (?:oírme|verme|ver mi pantalla)|(?:ves|veis) mi pantalla)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex AudioCheckPattern();
    [GeneratedRegex(@"^(?:agreed|i agree|we agree|sounds (?:good|great|fine)|fine (?:with|by) me|works for me|that works|let's do (?:it|that)|good idea|deal\b|me parece bien|de acuerdo|estoy de acuerdo|buena idea|einverstanden|abgemacht|machen wir so|sehe ich auch so|gute idee|passt für mich)", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex StrongAgreementStart();
    [GeneratedRegex(@"^(?:yes|yeah|yep|exactly|ja|genau|sí|si|exacto|claro)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex YesStart();
    [GeneratedRegex(@"^(?:sure|of course|on it|will do|can do|yes|yeah|yep|ok|okay|gerne|gern|ja|klar|natürlich|alles klar|mach ich|sí|si|claro|vale|por supuesto|con gusto|hecho)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex AcceptanceStart();
    [GeneratedRegex(@"\b(?:not|never|nicht|nie|nein|kein|keine|no puedo|tampoco)\b|n't\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex NegationPattern();
    [GeneratedRegex(@"\b(?:i can't|i cannot|i won't|i'm afraid|not this week|no time|i'd rather not|imposible|no puedo|yo no puedo|no me da tiempo|kann ich (?:leider )?nicht|ich kann (?:das )?(?:leider )?nicht|das kann ich leider nicht|leider nicht|schaffe ich nicht|keine zeit|geht (?:bei mir )?nicht)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex DeclinePattern();
    [GeneratedRegex(@"\b(?:we'll see|let's see|not sure|no idea|don't know|do not know|later|park (?:that|it)|let's park|figure (?:that|it) out|come back to (?:that|it|you)|get back to you|(?:i|we)(?:'d| would)? (?:need|have) to check|next (?:time|meeting)|offline|tbd|to be decided|depends on|can't promise|ya veremos|lo vemos|más tarde|más adelante|no sé|no lo sé|ni idea|lo dejamos para|lo hablamos|depende|tengo que (?:mirar|consultar|preguntar|comprobar)|lo tengo que (?:mirar|consultar)|mal sehen|schauen wir|weiß ich nicht|keine ahnung|später|klären wir|(?:müssen|sollten) wir (?:noch )?(?:genau )?klären|noch offen|vertagen|nächstes mal|hängt davon ab|muss ich (?:nachschauen|prüfen|klären)|nachfragen)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex DeferralPattern();
    [GeneratedRegex(@"\b(?:(?:we|they|i|all|everyone) (?:have |had |'ve )?(?:decided|agreed)\b(?!\s*[.!]*$)|was decided|it's decided|that's decided|the decision is|we're going with|we'll go with|let's go with|(?:then|so) (?:we(?:'ll)?|let's) (?:go with|take|keep)|\w+ it is[.!]*$|approved|decidimos|hemos decidido|está decidido|acordamos|hemos acordado|quedamos en que|hemos quedado en|aprobado|entonces (?:vamos con|hacemos|nos quedamos con|lo dejamos)|nos quedamos con|beschlossen|entschieden|vereinbart|festgelegt|genehmigt|abgemacht|machen wir so|bleibt es (?:bei|beim)|dann (?:nehmen|machen|liefern|bleiben|gehen|lassen) wir|wir nehmen)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex DecisionPattern();
    [GeneratedRegex(@"(?:\b(?:not|never|nicht|nie|no|nunca|noch)\b|n't\b)(?:\W+\w+){0,3}?\W+(?:decided|agreed|approved|beschlossen|entschieden|vereinbart|genehmigt|decidido|acordado|aprobado)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex NegatedDecisionPattern();
    [GeneratedRegex(@"\b(?:said|says|told|mentioned|claimed|claims|meinte|gemeint|sagte|gesagt|behauptet|dijo|comentó|dice que|decía)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ReportedPattern();
    [GeneratedRegex(@"\b(?:i propose|i suggest|i'd suggest|i would suggest|my (?:proposal|suggestion) is|let's (?!start|begin|move on|continue|go on|wrap up|park|see|talk|stop|hear|not)|let us (?!start|begin)|we should|shall we|should we|why don't we|how about we|what if we|can we agree|we(?:'ll| will| won't| will not| don't| do not)? (?:go with|keep|move|stay|take|use|ship|launch|print|book|drop|skip|cancel|postpone|meet|release|send|switch)\b|propongo|sugiero|mi propuesta|deberíamos|podríamos|qué tal si|y si\b|hagamos|vamos con\b|nos quedamos con|(?:lanzamos|enviamos|mantenemos|movemos|cambiamos|cogemos|tomamos|usamos|imprimimos|reservamos|cancelamos|aplazamos|hacemos|llevamos|compramos|pedimos|quedamos|enseñamos|vemos|publicamos|sacamos)\b|ich schlage vor|schlage ich vor|mein vorschlag|wir sollten|lass(?:t)? uns|sollen wir|wollen wir|wie wäre es|dann lieber|(?:nehmen|machen|bleiben|liefern|gehen|lassen|verschieben|bestellen|starten|treffen|schalten) wir|wir (?:nehmen|machen|bleiben|liefern|gehen|lassen|verschieben|bestellen|starten|treffen|schalten))", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ProposalPattern();
    [GeneratedRegex(@"\b(?:the plan|der plan|el plan)\b|\bwe\b[^?]*, right\?$|\bwir\b[^?]*, oder\?$|(?:\bnosotros\b|mos\b)[^?]*(?:¿verdad\?|¿no\?|, no\?)$", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConfirmationPattern();
    [GeneratedRegex(@"\b(?:okay|ok|fine|alright) (?:for|with) (?:everyone|everybody|all of you)|works for everyone|everyone (?:okay|fine|happy)|any objections|für alle (?:okay|ok|in ordnung)|passt das für alle|einverstanden\?|os parece bien|¿de acuerdo\?|estáis de acuerdo|todos de acuerdo", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConsensusPattern();
    [GeneratedRegex(@"\b(?:instead|after all|anymore|no longer|change of plan|scratch that|on second thought|al final|en lugar|en vez|ya no|cambio de planes|doch|stattdessen|nicht mehr|umentschieden)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ChangePattern();
    [GeneratedRegex(@"\b(?:agenda|topics|three things|two things|four things|points for today|today we have|today we'll|we have (?:\w+ )?(?:topics|points|things)|go through|orden del día|temas|puntos|tagesordnung|themen|punkte auf|stehen \w+ punkte)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex AgendaPattern();
    [GeneratedRegex(@"\b(?:i(?:'ll| will| am going to|'m going to| shall)\b|let me (?:check|send|write|book|look into|find out|prepare|take care)|leave it (?:to|with) me|will do\b|i can (?:do|take|send|check|handle|write|prepare|book|look into)\b|me encargo|me ocupo|lo hago|lo preparo|la preparo|la termino|lo termino|lo reviso|la reviso|te lo (?:mando|envío|paso)|voy a (?!ver|decir|compartir|ser)|la tienes|lo tienes|(?:compro|mando|envío|preparo|reviso|llamo|escribo|reservo|confirmo|termino) (?:yo|el|la|los|las|mañana|hoy|esta)|ich (?:mache|übernehme|kümmere mich|schicke|schreibe|bestelle|frage|kläre|prüfe|buche|melde|sage|werde|kann das machen)|mach(?:e)? ich|übernehme ich|schicke ich|kümmere ich mich|erledige ich|kann ich machen|sag ich|frag ich)", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex CommitmentPattern();
    [GeneratedRegex(@"\b(?:i'll be (?:honest|quick|brief|there|back|right)|i'll (?:share|stop|mute|unmute|hand over|pass|let you|switch)|i will (?:share|stop)|ich (?:werde|mache) (?:kurz )?(?:den bildschirm|das headset)|voy a (?:compartir|cambiar))\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex NotCommitmentPattern();
    [GeneratedRegex(@"\b(?:if|si|wenn|falls)\b(?! (?:you want|you like|quieres|queréis|quiere|du willst|ihr wollt|sie wollen))", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ConditionPattern();
    [GeneratedRegex(@"\b(?:it|that|this|them|those|will do|on it|can do|das|es|dies|mach(?:e)? ich|übernehme ich|kann ich machen|(?:se |te )?(?:lo|la|los|las) (?:hago|preparo|termino|reviso|mando|envío|paso|tienes|miro|compro|reservo|llevo|escribo|confirmo|organizo)|eso|esto|me encargo)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex RefersBackPattern();
    [GeneratedRegex(@"\b(?:can you|could you|would you|will you|are you able|please|can someone|could someone|who (?:can|could|will|wants to|tells|does|takes)|puedes|podrías|te encargas|te ocupas|\w+as tú|\w+es tú|me (?:mandas|pasas|envías)|confirmas|por favor|quién (?:puede|se encarga|hace)|alguien puede|kannst du|könntest du|würdest du|übernimmst du|machst du|schickst du|bestellst du|schaffst du|\w+st du (?:das|die|den|mir)|bitte|wer (?:kann|macht|übernimmt))\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex RequestPattern();
    [GeneratedRegex(@"\b(?:tell us|walk us|update us|how is it going|how's it going|go ahead|cómo va|cuéntanos|cuenta|explica|erzähl|erzählen|berichte|wie (?:geht|läuft) es|stand erzählen|repeat|wiederholen|repetir)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex SpeakNowPattern();
    [GeneratedRegex(@"\b(?:by when|when (?:can|could|will) you|until when|bis wann|wann (?:schaffst|kannst)|para cuándo|cuándo (?:lo|la) (?:puedes|tienes))\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex DueQuestionPattern();
    [GeneratedRegex(@"\b(?:someone|somebody|jemand|alguien) (?:has to|needs to|must|should|muss|sollte|tiene que|debe)|\bhay que\b|\bwe (?:still )?need to\b|\bman muss\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex UnassignedTaskPattern();
    [GeneratedRegex(@"\b(?:who doesn't|who does not|who wouldn't|wer will (?:schon )?nicht|quién no)\b|^\s*(?:right|oder|no|verdad|okay|ok|ja|sí)\?", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex RhetoricalPattern();
    [GeneratedRegex(@"\b(?:back to|going back to|coming back to|regarding|to answer|you asked|on your question|volviendo a|sobre lo de|respecto a|lo de la|lo del|zurück zu|zur frage|zu deiner frage|wegen der frage)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex BackReferencePattern();
    [GeneratedRegex(@"^\s*(\p{Lu}\p{Ll}+),\s+(?=\S)", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex VocativePattern();
    [GeneratedRegex(@"\b(?:maybe|perhaps|we could|vielleicht|wir könnten|könnten wir|quizás|quizá|tal vez|a lo mejor|podríamos)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex SuggestionPattern();
    [GeneratedRegex(@"\b(?:that's it|that's all|let's wrap up|nothing else|das war's|das wär's|das wars|eso es todo|ya está|nada más)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex ClosingPattern();
    [GeneratedRegex(@"\b(?:i won't|i will not|i'll not|i can't|i cannot|i'm not going to|i am not going to|no voy a|no puedo|no lo hago|ich werde (?:das )?nicht|ich kann (?:das )?(?:leider )?nicht|mache ich nicht|kann ich (?:leider )?nicht)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex NegatedCommitmentPattern();
    [GeneratedRegex(@"\b(?:still open|that's open|remains open|open point|ist (?:das )?(?:jetzt )?offen|bleibt offen|sigue pendiente|queda pendiente|queda abierto)\b", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex StillOpenPattern();
    [GeneratedRegex(@"(?<![\d.])\d+\.\d+(?:\.\d+)*(?![\d.])", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex VersionPattern();
    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}\.\d{4}$", Invariant, MatchTimeoutMilliseconds)]
    private static partial Regex DottedDatePattern();
    [GeneratedRegex(@"\b(?:version|versión|release|build|v)\s*$", InvariantIgnoringCase, MatchTimeoutMilliseconds)]
    private static partial Regex VersionCueBefore();
    [GeneratedRegex(@"\b(?:at|um|las|bis|ab|until|from|by|gegen|hasta|desde)\s*$", InvariantIgnoringCase, MatchTimeoutMilliseconds)]
    private static partial Regex TimeCueBefore();
    [GeneratedRegex(@"^\s*(?:uhr|h|horas)\b", InvariantIgnoringCase, MatchTimeoutMilliseconds)]
    private static partial Regex ClockUnitAfter();
    [GeneratedRegex(@"^(\d{1,2})(?:[.:](\d{2}))?(?:\s*Uhr)?$", InvariantIgnoringCase, MatchTimeoutMilliseconds)]
    private static partial Regex ClockFormPattern();
    [GeneratedRegex(@"\b(?:next (?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)|(?:lunes|martes|miércoles|jueves|viernes|sábado|domingo) que viene|nächste[nr]? (?:montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)|tomorrow(?: morning)?|today|tonight|day after tomorrow|this week|next week|end of (?:the )?(?:week|month|day)|pasado mañana|(?<!la |esta |por la )mañana|hoy|esta semana|la semana que viene|la próxima semana|fin(?:al)?(?:es)? de (?:la )?semana|übermorgen|(?<!guten )morgen|heute|(?:montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)(?:abend|morgen|nachmittag|vormittag|mittag)|diese woche|nächste(?:n)? woche|ende der woche|ende des monats|anfang (?:januar|februar|märz|april|mai|juni|juli|august|september|oktober|november|dezember)|(?:early|end of) (?:january|february|march|april|may|june|july|august|september|october|november|december))\b", InvariantIgnoringCase, MatchTimeoutMilliseconds)]
    private static partial Regex RelativeDayPattern();
}
