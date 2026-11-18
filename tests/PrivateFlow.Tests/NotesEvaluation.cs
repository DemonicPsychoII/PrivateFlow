using System.Globalization;
using System.Text.RegularExpressions;
using PrivateFlow.Core;

// A synthetic meeting with hand-written ground truth, one transcript row per line: "Speaker: text ## tag | tag". Tags:
// D:key  the row states decision key (any such row may be cited for it)
// R:key  the row changes decision key, which then has to be marked as changed or left out
// A:key; owner=Name; due=Friday 10:00  a row of action key; owner and due once per key, "-" for none, "Marta/Remote speaker" for either;
//        a due date with alternatives ("Dienstag/nächste Woche Dienstag") is for one exchange that names the same deadline in two ways
// Q:key  an open question (unanswered or deferred)
// T:atom, atom  the clock times, dates and weekdays said in the row
// S  small talk
// Header lines: "Title:", "Vocabulary:", "Languages:", "Topics: topic = keyword, keyword; ...". "@mm:ss" on its own line moves the
// clock (a pause or a shared screen); otherwise a row lasts as long as its words take at 2.5 words per second, plus a second.
sealed record GroundTruthAction(string Key, string? Owner, string? Due);
sealed class SyntheticMeeting
{
    public required string Name { get; init; }
    public required MeetingDocument Document { get; init; }
    public Dictionary<string, HashSet<string>> DecisionRows { get; } = [];
    public Dictionary<string, string> ChangedBy { get; } = [];
    public Dictionary<string, HashSet<string>> ActionRows { get; } = [];
    public Dictionary<string, GroundTruthAction> Actions { get; } = [];
    public Dictionary<string, string> QuestionRows { get; } = [];
    public Dictionary<string, HashSet<string>> TimeAtoms { get; } = [];
    public HashSet<string> SmallTalk { get; } = [];
    public List<(string Topic, string[] Keywords)> Topics { get; } = [];
    public double Minutes => Document.Segments.Count == 0 ? 0 : Document.Segments[^1].End / 60;

    public static SyntheticMeeting Parse(string name, string text)
    {
        var document = new MeetingDocument { Id = Guid.NewGuid().ToString("N") };
        var meeting = new SyntheticMeeting { Name = name, Document = document };
        var clock = 0.0;
        foreach (var raw in text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0))
        {
            if (raw.StartsWith("Title:")) { document.Title = raw[6..].Trim(); continue; }
            if (raw.StartsWith("Vocabulary:")) { document.Vocabulary = raw[11..].Trim(); continue; }
            if (raw.StartsWith("Languages:")) { document.Languages = raw[10..].Trim(); continue; }
            if (raw.StartsWith("Topics:"))
            {
                foreach (var topic in raw[7..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    meeting.Topics.Add((topic[..topic.IndexOf('=')].Trim(), topic[(topic.IndexOf('=') + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)));
                continue;
            }
            if (raw.StartsWith('@')) { var parts = raw[1..].Split(':'); clock = Math.Max(clock, int.Parse(parts[0]) * 60 + int.Parse(parts[1])); continue; }
            var tagStart = raw.IndexOf(" ## ", StringComparison.Ordinal);
            var spoken = tagStart < 0 ? raw : raw[..tagStart];
            var colon = spoken.IndexOf(": ", StringComparison.Ordinal);
            var segment = new MeetingSegment { Id = $"{document.Segments.Count + 1:x32}", Speaker = spoken[..colon].Trim(), Text = spoken[(colon + 2)..].Trim(),
                Channel = spoken[..colon].Trim() == "You" ? "mic" : "system", Start = clock };
            var words = segment.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            segment.End = clock + Math.Max(1.2, words / 2.5); clock = segment.End + 1;
            document.Segments.Add(segment);
            if (tagStart < 0) continue;
            foreach (var tag in raw[(tagStart + 4)..].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var kind = tag.Split(':', 2)[0]; var value = tag.Contains(':') ? tag.Split(':', 2)[1].Trim() : "";
                switch (kind)
                {
                    case "D": Add(meeting.DecisionRows, value, segment.Id); break;
                    case "R": meeting.ChangedBy[value] = segment.Id; break;
                    case "Q": meeting.QuestionRows[value] = segment.Id; break;
                    case "S": meeting.SmallTalk.Add(segment.Id); break;
                    case "T": meeting.TimeAtoms[segment.Id] = [.. NotesScoring.Atoms(value, relative: false)]; break;
                    case "A":
                        var fields = value.Split(';', StringSplitOptions.TrimEntries);
                        Add(meeting.ActionRows, fields[0], segment.Id);
                        string? Field(string field) => fields.FirstOrDefault(entry => entry.StartsWith(field + "="))?[(field.Length + 1)..].Trim() is { } found && found != "-" ? found : null;
                        if (fields.Any(entry => entry.StartsWith("owner=")) || !meeting.Actions.ContainsKey(fields[0])) meeting.Actions[fields[0]] = new(fields[0], Field("owner"), Field("due"));
                        break;
                    default: throw new InvalidDataException($"Unknown tag {tag} in {name}.");
                }
            }
        }
        return meeting;
    }
    private static void Add(Dictionary<string, HashSet<string>> rows, string key, string id) { if (!rows.TryGetValue(key, out var set)) rows[key] = set = []; set.Add(id); }
}

sealed record SectionScore(int Truth, int Lines, int Found, int Noise)
{
    public double Recall => Truth == 0 ? 1 : Found / (double)Truth;
    public double Precision => Lines == 0 ? (Truth == 0 ? 1 : 0) : (Lines - Noise) / (double)Lines;
    public static SectionScore operator +(SectionScore first, SectionScore second) =>
        new(first.Truth + second.Truth, first.Lines + second.Lines, first.Found + second.Found, first.Noise + second.Noise);
}
// Times count listed times for precision; NoisyTimeLines counts the lines of that section with no correct time, the noise rows.
sealed record NotesScore(SectionScore Decisions, int Reversals, int ReversalsHandled, SectionScore Actions, int OwnerRight, int DueRight, int BothRight,
    SectionScore Questions, SectionScore Times, int NoisyTimeLines, int Topics, int TopicsCovered, int OverviewLines, int OverviewSmallTalk)
{
    public int NoiseRows => Decisions.Noise + Actions.Noise + Questions.Noise + NoisyTimeLines + OverviewSmallTalk;
    public static NotesScore operator +(NotesScore first, NotesScore second) => new(first.Decisions + second.Decisions, first.Reversals + second.Reversals,
        first.ReversalsHandled + second.ReversalsHandled, first.Actions + second.Actions, first.OwnerRight + second.OwnerRight, first.DueRight + second.DueRight,
        first.BothRight + second.BothRight, first.Questions + second.Questions, first.Times + second.Times, first.NoisyTimeLines + second.NoisyTimeLines, first.Topics + second.Topics,
        first.TopicsCovered + second.TopicsCovered, first.OverviewLines + second.OverviewLines, first.OverviewSmallTalk + second.OverviewSmallTalk);
}

// Scores notes of any kind (the extractive notes before and after, or notes written by a model) against the ground truth by the rows
// their lines cite, so a line counts only where it rests on the row that states the point.
static class NotesScoring
{
    private sealed record Line(string Text, string[] Rows);
    public static NotesScore Score(SyntheticMeeting meeting, string notes)
    {
        var sections = Sections(notes);
        IReadOnlyList<Line> Section(params string[] headings) => sections.Where(section => headings.Any(heading => section.Heading.StartsWith(heading, StringComparison.Ordinal))).SelectMany(section => section.Lines).ToArray();

        // Each line is credited with at most one ground-truth item, the one most of its citations rest on; lines matching nothing or an
        // item already credited are noise.
        (SectionScore Score, Dictionary<string, Line> Credited) Match(IReadOnlyList<Line> lines, IReadOnlyDictionary<string, HashSet<string>> truth)
        {
            var credited = new Dictionary<string, Line>(); var noise = 0;
            foreach (var line in lines)
            {
                var best = truth.Select(item => (item.Key, Count: line.Rows.Count(item.Value.Contains))).Where(item => item.Count > 0 && !credited.ContainsKey(item.Key))
                    .OrderByDescending(item => item.Count).Select(item => item.Key).FirstOrDefault();
                if (best is null) noise++; else credited[best] = line;
            }
            return (new(truth.Count, lines.Count, credited.Count, noise), credited);
        }
        var (decisions, decided) = Match(Section("Decision"), meeting.DecisionRows);
        var reversalsHandled = meeting.ChangedBy.Count(change => !decided.TryGetValue(change.Key, out var line) || line.Rows.Contains(change.Value)
            || Regex.IsMatch(line.Text, @"(?i)chang|geändert|cambi|instead|statt|en lugar|en vez|moved|verschoben|movido|replac|supersed"));
        var (actions, acted) = Match(Section("Action"), meeting.ActionRows);
        int ownerRight = 0, dueRight = 0, bothRight = 0;
        foreach (var (key, line) in acted)
        {
            var truth = meeting.Actions[key];
            var owner = Labelled(line.Text, "Owner"); var due = Labelled(line.Text, "Due");
            var ownerOk = truth.Owner is null ? owner is null : owner is not null && truth.Owner.Split('/').Any(accepted => owner.Equals(accepted.Trim(), StringComparison.OrdinalIgnoreCase));
            // A due date is right only when it says what the ground truth says, nothing more: "Friday, Monday" for "Friday" is wrong.
            var dueOk = truth.Due is null ? due is null : due is not null && truth.Due.Split('/').Any(accepted => DueAtoms(accepted).SetEquals(DueAtoms(due)));
            ownerRight += ownerOk ? 1 : 0; dueRight += dueOk ? 1 : 0; bothRight += ownerOk && dueOk ? 1 : 0;
        }
        var (questions, _) = Match(Section("Open question"), meeting.QuestionRows.ToDictionary(entry => entry.Key, entry => new HashSet<string> { entry.Value }));

        var timeLines = Section("Times mentioned");
        var listed = 0; var correct = 0; var noisyTimeLines = 0; var found = new HashSet<string>();
        foreach (var line in timeLines)
        {
            var field = line.Text.StartsWith("- ") ? line.Text[2..] : line.Text;
            var atoms = Atoms(field.Contains(" · ") ? field[..field.IndexOf(" · ", StringComparison.Ordinal)] : field, relative: false).Distinct().ToArray();
            var lineCorrect = 0;
            foreach (var atom in atoms)
            {
                listed++;
                if (line.Rows.Any(row => meeting.TimeAtoms.TryGetValue(row, out var truth) && truth.Contains(atom))) { correct++; lineCorrect++; found.Add(atom); }
            }
            if (lineCorrect == 0) noisyTimeLines++;
        }
        var truthAtoms = meeting.TimeAtoms.Values.SelectMany(atoms => atoms).ToHashSet();
        var times = new SectionScore(truthAtoms.Count, listed, found.Count, listed - correct);

        var overview = Section("Summary", "Extractive overview", "Overview");
        var overviewText = string.Join("\n", overview.Select(line => line.Text));
        var covered = meeting.Topics.Count(topic => topic.Keywords.Any(keyword => overviewText.Contains(keyword, StringComparison.OrdinalIgnoreCase)));
        var smallTalk = overview.Count(line => line.Rows.Length > 0 && line.Rows.All(meeting.SmallTalk.Contains));
        return new(decisions, meeting.ChangedBy.Count, reversalsHandled, actions, ownerRight, dueRight, bothRight, questions, times, noisyTimeLines,
            meeting.Topics.Count, covered, overview.Count, smallTalk);
    }

    // A month counts once even when a date of that month is also said ("17. März" is "date 3-17", not also "month 3").
    public static HashSet<string> DueAtoms(string text)
    {
        var atoms = Atoms(text, relative: true).ToHashSet();
        atoms.RemoveWhere(atom => atom.StartsWith("month ", StringComparison.Ordinal) && atoms.Any(other => other.StartsWith($"date {atom[6..]}-", StringComparison.Ordinal)));
        return atoms;
    }
    private static string? Labelled(string text, string label) =>
        Regex.Match(text, $@" · {label}: (.+?)(?= · | \[|$)") is { Success: true } match && match.Groups[1].Value.Trim() is { Length: > 0 } value && value != "null" ? value : null;

    private static List<(string Heading, List<Line> Lines)> Sections(string notes)
    {
        var sections = new List<(string Heading, List<Line> Lines)>();
        foreach (var block in notes.Replace("\r\n", "\n").Split("\n\n"))
        {
            var lines = block.Split('\n');
            if (lines.Length == 0) continue;
            var entries = new List<Line>();
            foreach (var line in lines.Skip(1).Where(line => line.Trim().Length > 0 && !line.StartsWith("None ")))
                entries.Add(new(line, Citations().Matches(line).Select(match => match.Groups[1].Value).ToArray()));
            // A summary written by a model has no citations; keep its sentences so topics can be found in them.
            sections.Add((lines[0], entries));
        }
        return sections;
    }
    private static Regex Citations() => new(@"\[[^\[\]]* · \d{2}:\d{2}:\d{2} · [^\[\]]* · ([0-9a-f]{32})\]");

    // Clock times, weekdays, dates and (for due dates) the relative days, each in one canonical form whatever language or notation it
    // was said in: "4 pm", "16 Uhr" and "16:00" are "16:00"; "Friday", "viernes" and "Freitag" are "weekday 5".
    private static readonly string[][] WeekdayNames = [["monday", "lunes", "montag"], ["tuesday", "martes", "dienstag"], ["wednesday", "miércoles", "miercoles", "mittwoch"],
        ["thursday", "jueves", "donnerstag"], ["friday", "viernes", "freitag"], ["saturday", "sábado", "sabado", "samstag"], ["sunday", "domingo", "sonntag"]];
    private static readonly string[][] MonthNames = [["january", "enero", "januar"], ["february", "febrero", "februar"], ["march", "marzo", "märz"], ["april", "abril"],
        ["may", "mayo", "mai"], ["june", "junio", "juni"], ["july", "julio", "juli"], ["august", "agosto"], ["september", "septiembre"], ["october", "octubre", "oktober"],
        ["november", "noviembre"], ["december", "diciembre", "dezember"]];
    private static readonly (string Pattern, string Atom)[] RelativeDays = [(@"tomorrow|mañana(?! por)|morgen(?! früh)|morgen früh|mañana por la mañana", "tomorrow"),
        (@"today|tonight|hoy|heute", "today"), (@"übermorgen|pasado mañana|day after tomorrow", "day after tomorrow"),
        (@"this week|esta semana|diese woche", "this week"), (@"next week|la semana que viene|la próxima semana|nächste[n]? woche", "next week"), (@"end of (?:the |next )?week|fin(?:al)?(?:es)? de (?:la )?semana|ende der woche", "end of week"),
        (@"end of (?:the )?month|fin(?:al)?(?:es)? de mes|ende des monats|monatsende", "end of month")];
    public static IEnumerable<string> Atoms(string text, bool relative)
    {
        var lower = text.ToLowerInvariant();
        var month = string.Join("|", MonthNames.SelectMany(names => names));
        foreach (Match match in Regex.Matches(lower, @"(?<![\d.:])(\d{1,2})(?:[:.](\d{2}))?\s*(a\.m\.|p\.m\.|am\b|pm\b|uhr\b)|(?<![\d.:])(\d{1,2})[:.](\d{2})(?![\d.:])|\b(noon|midday|mediodía|mittag|midnight|medianoche|mitternacht)\b|\bminut[eo] (\d{1,3})\b"))
        {
            if (match.Groups[6].Success) { yield return match.Groups[6].Value is "midnight" or "medianoche" or "mitternacht" ? "0:00" : "12:00"; continue; }
            if (match.Groups[7].Success) { yield return "minute " + match.Groups[7].Value; continue; }
            var hour = int.Parse(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value, CultureInfo.InvariantCulture);
            var minutes = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[5].Success ? match.Groups[5].Value : "00";
            var suffix = match.Groups[3].Value;
            if (suffix is "pm" or "p.m." && hour < 12) hour += 12;
            if (suffix is "am" or "a.m." && hour == 12) hour = 0;
            yield return $"{hour}:{minutes}";
        }
        foreach (Match match in Regex.Matches(lower, $@"\b(?:({month})\s+(\d{{1,2}})(?:st|nd|rd|th)?|(\d{{1,2}})(?:st|nd|rd|th|\.)?\s+(?:of\s+|de\s+)?({month}))\b"))
        {
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[4].Value;
            var day = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            yield return $"date {Array.FindIndex(MonthNames, names => names.Contains(name)) + 1}-{int.Parse(day, CultureInfo.InvariantCulture)}";
        }
        for (var index = 0; index < WeekdayNames.Length; index++)
            if (Regex.IsMatch(lower, $@"\b(?:{string.Join("|", WeekdayNames[index])})(?:s|abend|morgen|nachmittag|vormittag|mittag)?\b")) yield return $"weekday {index + 1}";
        if (relative)
            for (var index = 0; index < MonthNames.Length; index++)
                if (Regex.IsMatch(lower, $@"(?:{string.Join("|", MonthNames[index])})")) yield return $"month {index + 1}";
        if (relative)
            foreach (var (pattern, atom) in RelativeDays)
                if (Regex.IsMatch(lower, $@"\b(?:{pattern})\b")) yield return atom;
    }
}

// "--notes-report" prints the scores of the extractive notes per meeting; "--notes-report export <folder>" writes each meeting as a
// meeting.json for --write-notes; "--notes-report show <meeting>" prints its notes; "--notes-report probe 'Ana|text' ..." prints the notes of the rows given; "--notes-report score <meeting> <notes file>" scores notes written elsewhere, such as by the API.
static class NotesReport
{
    public static IReadOnlyList<SyntheticMeeting> Development { get; } =
    [
        SyntheticMeeting.Parse("status", SyntheticMeetings.Status), SyntheticMeeting.Parse("one-to-one", SyntheticMeetings.OneToOne),
        SyntheticMeeting.Parse("brainstorm", SyntheticMeetings.Brainstorm), SyntheticMeeting.Parse("customer", SyntheticMeetings.CustomerCall),
        SyntheticMeeting.Parse("short-de", SyntheticMeetings.Short), SyntheticMeeting.Parse("spanish", SyntheticMeetings.Spanish),
        SyntheticMeeting.Parse("german-long", SyntheticMeetings.German),
        // Held back in round 1, so development meetings since round 2.
        SyntheticMeeting.Parse("disagreement", SyntheticMeetings.Disagreement), SyntheticMeeting.Parse("one-label", SyntheticMeetings.OneLabel),
        SyntheticMeeting.Parse("code-switch", SyntheticMeetings.CodeSwitch),
    ];
    // Held back in round 2: written after the rules were fixed at commit bd101bc.
    public static IReadOnlyList<SyntheticMeeting> HeldBack { get; } =
    [
        SyntheticMeeting.Parse("planning", SyntheticMeetings.Planning), SyntheticMeeting.Parse("escalation", SyntheticMeetings.Escalation),
        SyntheticMeeting.Parse("stand-up", SyntheticMeetings.StandUp),
    ];

    public static void Run(string[] arguments)
    {
        var meetings = Development.Concat(HeldBack).ToArray();
        if (arguments is ["export", var folder])
        {
            Directory.CreateDirectory(folder);
            foreach (var meeting in meetings)
                File.WriteAllText(Path.Combine(folder, meeting.Name + ".meeting.json"), System.Text.Json.JsonSerializer.Serialize(meeting.Document));
            return;
        }
        if (arguments is ["probe", .. var spoken])
        {
            Console.WriteLine(MeetingArchive.Notes(new MeetingDocument { Title = "Probe", Segments = [.. spoken.Select((row, index) => new MeetingSegment { Start = 10 * index, End = 10 * index + 5, Speaker = row.Split('|')[0], Text = row.Split('|')[1] })] }));
            return;
        }
        if (arguments is ["show", var shown])
        {
            Console.WriteLine(MeetingArchive.Notes(meetings.Single(candidate => candidate.Name == shown).Document));
            return;
        }
        if (arguments is ["score", var name, var file])
        {
            var meeting = meetings.Single(candidate => candidate.Name == name);
            Console.WriteLine(Table([(meeting, Score(meeting, File.ReadAllText(file)))]));
            return;
        }
        Console.WriteLine(Table(meetings.Select(meeting => (meeting, Score(meeting, MeetingArchive.Notes(meeting.Document))))));
    }
    public static NotesScore Score(SyntheticMeeting meeting, string notes) => NotesScoring.Score(meeting, notes);

    public static string Table(IEnumerable<(SyntheticMeeting Meeting, NotesScore Score)> rows)
    {
        static string Ratio(int part, int whole) => whole == 0 ? "–" : $"{part}/{whole}";
        static string Section(SectionScore score) => $"R {Ratio(score.Found, score.Truth),-6} P {Ratio(score.Lines - score.Noise, score.Lines),-6}";
        var lines = new List<string> { $"{"meeting",-14} {"rows",4} {"min",4} | {"decisions",-16} {"rev",-4} | {"actions",-16} {"owner",-6} {"due",-6} {"both",-6} | {"open questions",-16} | {"times",-16} | {"topics",-6} | noise" };
        var total = default(NotesScore);
        foreach (var (meeting, score) in rows)
        {
            total = total is null ? score : total + score;
            lines.Add(Line(meeting.Name, meeting.Document.Segments.Count.ToString(), $"{meeting.Minutes:0}", score));
        }
        if (total is not null) lines.Add(Line("total", "", "", total));
        return string.Join("\n", lines);
        string Line(string name, string rowCount, string minutes, NotesScore score) =>
            $"{name,-14} {rowCount,4} {minutes,4} | {Section(score.Decisions)} {Ratio(score.ReversalsHandled, score.Reversals),-4} | {Section(score.Actions)} {Ratio(score.OwnerRight, score.Actions.Truth),-6} {Ratio(score.DueRight, score.Actions.Truth),-6} {Ratio(score.BothRight, score.Actions.Truth),-6} | {Section(score.Questions)} | {Section(score.Times)} | {Ratio(score.TopicsCovered, score.Topics),-6} | {score.NoiseRows}";
    }
}
