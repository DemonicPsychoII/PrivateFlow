using System.Text;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

public enum AlignmentKind { Match, Substitution, Deletion, Insertion }
// Reference is the reference word a step belongs to; an insertion belongs to the reference word before it.
// FormattingOnly marks a step of an error run whose words were heard but written differently ("1234" for "12 34").
public sealed record AlignmentStep(AlignmentKind Kind, int Reference, int Hypothesis, bool FormattingOnly = false);
// FormattingOnly counts the substituted or deleted reference words that only differ in formatting; Recall still counts them as missed.
public sealed record WordErrors(int ReferenceWords, int Substitutions, int Deletions, int Insertions, int FormattingOnly = 0)
{
    public double Rate => ReferenceWords == 0 ? (Insertions == 0 ? 0 : 1) : (Substitutions + Deletions + Insertions) / (double)ReferenceWords;
    public double Recall => ReferenceWords == 0 ? 1 : (ReferenceWords - Substitutions - Deletions) / (double)ReferenceWords;
    public double RecallIgnoringFormatting => ReferenceWords == 0 ? 1 : (ReferenceWords - Substitutions - Deletions + FormattingOnly) / (double)ReferenceWords;
    public static WordErrors operator +(WordErrors first, WordErrors second) =>
        new(first.ReferenceWords + second.ReferenceWords, first.Substitutions + second.Substitutions, first.Deletions + second.Deletions, first.Insertions + second.Insertions,
            first.FormattingOnly + second.FormattingOnly);
    public static readonly WordErrors None = new(0, 0, 0, 0);
}
// FormattingOnly: the words were heard but written differently, such as "1234" for "12:34".
public sealed record CriticalItem(string Text, int Utterance, bool Recalled, bool FormattingOnly, string Heard);

public sealed class TranscriptScore(IReadOnlyList<int> wordUtterances, IReadOnlyList<string> hypothesisWords, IReadOnlyList<AlignmentStep> steps, IReadOnlyList<CriticalItem> criticalItems)
{
    public IReadOnlyList<CriticalItem> CriticalItems => criticalItems;
    public WordErrors Errors(Func<int, bool>? includeUtterance = null)
    {
        bool Included(int reference) => includeUtterance is null || reference >= 0 && includeUtterance(wordUtterances[reference]);
        var selected = steps.Where(step => Included(step.Reference)).ToArray();
        return new(selected.Count(step => step.Kind != AlignmentKind.Insertion), selected.Count(step => step.Kind == AlignmentKind.Substitution),
            selected.Count(step => step.Kind == AlignmentKind.Deletion), selected.Count(step => step.Kind == AlignmentKind.Insertion),
            selected.Count(step => step.FormattingOnly && step.Kind is AlignmentKind.Substitution or AlignmentKind.Deletion));
    }
    public string Heard(int utterance) => string.Join(" ", steps.Where(step => step.Kind != AlignmentKind.Deletion && step.Reference >= 0 && wordUtterances[step.Reference] == utterance).Select(step => hypothesisWords[step.Hypothesis]));
}

// Scores a transcript against a reference whose critical items are marked with square brackets,
// for example "We are at [minute 42]." Both sides are normalized the same way, so number words
// and digits ("forty-two", "42"; "twelve thirty-four", "12:34"; "doce y media", "12.30") compare equal.
public static class TranscriptScoring
{
    public static TranscriptScore Score(IReadOnlyList<string> markedUtterances, string hypothesis) => Score(markedUtterances, [new TranscriptSegment(0, 0, hypothesis)]);
    public static TranscriptScore Score(IReadOnlyList<string> markedUtterances, IReadOnlyList<TranscriptSegment> hypothesis)
    {
        var words = new List<string>(); var owners = new List<int>(); var ranges = new List<(string Text, int Utterance, int First, int Count)>();
        for (var utterance = 0; utterance < markedUtterances.Count; utterance++)
            foreach (var piece in Regex.Split(markedUtterances[utterance], @"(\[[^\]]*\])"))
            {
                var critical = piece.StartsWith('[') && piece.EndsWith(']');
                var pieceWords = Words(critical ? piece[1..^1] : piece);
                if (critical && pieceWords.Count > 0) ranges.Add((piece[1..^1], utterance, words.Count, pieceWords.Count));
                words.AddRange(pieceWords); owners.AddRange(pieceWords.Select(_ => utterance));
            }
        var hypothesisWords = hypothesis.SelectMany(segment => Words(segment.Text)).ToList();
        var steps = MarkFormattingOnly(Align(words, hypothesisWords), words, hypothesisWords);
        var items = ranges.Select(range =>
        {
            var inside = steps.Where(step => step.Reference >= range.First && step.Reference < range.First + range.Count).ToArray();
            var heard = inside.Where(step => step.Kind != AlignmentKind.Deletion).Select(step => hypothesisWords[step.Hypothesis]).ToArray();
            var recalled = inside.Count(step => step.Kind == AlignmentKind.Match) == range.Count && !inside.Any(step => step.Kind == AlignmentKind.Insertion && step.Reference < range.First + range.Count - 1);
            return new CriticalItem(range.Text, range.Utterance, recalled, !recalled && string.Concat(heard) == string.Concat(words.Skip(range.First).Take(range.Count)), string.Join(" ", heard));
        }).ToArray();
        return new(owners, hypothesisWords, steps, items);
    }
    public static string Unmarked(string marked) => marked.Replace("[", "").Replace("]", "");

    // A run of errors between two matches whose reference and transcript words spell the same characters.
    private static IReadOnlyList<AlignmentStep> MarkFormattingOnly(IReadOnlyList<AlignmentStep> steps, IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        var marked = steps.ToArray();
        for (var first = 0; first < marked.Length;)
        {
            if (marked[first].Kind == AlignmentKind.Match) { first++; continue; }
            var end = first;
            while (end < marked.Length && marked[end].Kind != AlignmentKind.Match) end++;
            var run = marked[first..end];
            var heard = string.Concat(run.Where(step => step.Kind != AlignmentKind.Deletion).Select(step => hypothesis[step.Hypothesis]));
            var spoken = string.Concat(run.Where(step => step.Kind != AlignmentKind.Insertion).Select(step => reference[step.Reference]));
            if (spoken.Length > 0 && spoken == heard) for (var step = first; step < end; step++) marked[step] = marked[step] with { FormattingOnly = true };
            first = end;
        }
        return marked;
    }

    // Written units and abbreviations are the same words as spoken ones: "512 KB", "60 dB", "5 V", "10 %".
    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["kilobyte"] = "kb", ["kilobytes"] = "kb", ["decibel"] = "db", ["decibels"] = "db", ["dezibel"] = "db", ["decibelios"] = "db",
        ["millimetre"] = "mm", ["millimetres"] = "mm", ["millimeter"] = "mm", ["millimeters"] = "mm", ["milímetros"] = "mm",
        ["millisecond"] = "ms", ["milliseconds"] = "ms", ["millisekunden"] = "ms", ["milisegundos"] = "ms",
        ["millivolt"] = "mv", ["millivolts"] = "mv", ["milivoltios"] = "mv", ["milliamp"] = "ma", ["milliamps"] = "ma", ["milliampere"] = "ma", ["milliamperes"] = "ma",
        ["volt"] = "v", ["volts"] = "v", ["voltios"] = "v", ["euros"] = "euro", ["prozent"] = "percent", ["ok"] = "okay",
    };
    public static IReadOnlyList<string> Words(string text) => FoldNumbers(Tokens(text, mixed: false), articles: false);
    // The words search compares: the scoring words, except that a name mixing letters and digits stays whole ("I2C" is not the
    // number 2), the German article "ein" is the number one only before a number or "Uhr" ("ein Problem", but "ein Uhr dreißig"
    // is 1:30), and "minuto" is "minute". Minutes marks a single digit that is the minutes of the hour before it, as the 5 of "12:05",
    // "twelve oh five" and "doce y cinco".
    public static IReadOnlyList<(string Text, bool Minutes)> SearchWords(string text)
    {
        var found = new List<(string Text, bool Minutes)>();
        foreach (var clause in Clauses(text))
        {
            var words = FoldNumbers(Tokens(clause, mixed: true), articles: true).Select(word => word == "minuto" ? "minute" : word).ToList();
            // "twelve oh five" is 12:05: the spoken zero goes, as the written one does in "12:05".
            for (var index = words.Count - 2; index > 0; index--)
                if (words[index] == "oh" && IsHour(words[index - 1]) && IsNumber(words[index + 1]) && words[index + 1].Length == 1) words.RemoveAt(index);
            // Not after a time that already has its minutes: the 5 of "9 15 5" is not 15:05.
            found.AddRange(words.Select((word, index) => (word, index > 0 && IsHour(words[index - 1]) && !(index > 1 && IsNumber(words[index - 2])) && IsMinuteDigit(words, index))));
        }
        return found;
    }
    private static bool IsHour(string word) => IsNumber(word) && word.Length <= 2 && int.Parse(word) <= 23;
    // A time ends at punctuation ("at 9, one hour") and after its written minutes ("9:00", "17.30", "five o'clock"): a number after that is not its minutes.
    private static string[] Clauses(string text) =>
        Regex.Split(text, @"[,.?!;:](?=\s|$)|(?<=(?<![0-9])(?:[01]?[0-9]|2[0-3])[:.][0-5][0-9])(?![0-9])|(?<=o['’]clock)", RegexOptions.IgnoreCase);
    // A number before one of these words is a quantity, not minutes: "at 9 one hour", "um neun eine Stunde", "a las nueve cinco personas".
    private static readonly HashSet<string> QuantityUnits = new(StringComparer.Ordinal)
    {
        "second", "seconds", "minute", "minutes", "hour", "hours", "day", "days", "week", "weeks", "month", "months", "year", "years", "times", "people", "persons", "percent", "euro", "dollar", "dollars",
        "sekunde", "sekunden", "minuten", "stunde", "stunden", "tag", "tage", "tagen", "woche", "wochen", "monat", "monate", "monaten", "jahr", "jahre", "jahren", "personen", "leute", "leuten",
        "segundo", "segundos", "minuto", "minutos", "hora", "horas", "día", "días", "semana", "semanas", "mes", "meses", "año", "años", "veces", "personas",
    };
    private static bool BeforeUnit(IReadOnlyList<string> words, int index) => index + 1 < words.Count && QuantityUnits.Contains(words[index + 1]);
    // Minutes below ten lose their zero in every form ("12:05", "doce y cinco", "zwölf Uhr fünf"), so a single digit after an hour can be its minutes.
    private static bool IsMinuteDigit(IReadOnlyList<string> words, int index) => words[index].Length == 1 && IsNumber(words[index]) && !BeforeUnit(words, index);
    private static List<string> Tokens(string text, bool mixed)
    {
        var prepared = text.Normalize(NormalizationForm.FormC).ToLowerInvariant().Replace('’', '\'');
        prepared = Regex.Replace(prepared, @"(?<=[0-9])[,.](?=[0-9]{3}(?![0-9]))", "");
        // Spanish and SI style group thousands with a space: "400 000" is 400000.
        prepared = Regex.Replace(prepared, @"(?<![0-9])[0-9]{1,3}(?:[   ][0-9]{3})+(?![0-9])", group => Regex.Replace(group.Value, @"[   ]", ""));
        // A whole hour reads as the hour: "5:00", "17.00 Uhr", "five o'clock".
        prepared = Regex.Replace(prepared, @"(?<![0-9])([0-9]{1,2})[:.]00(?![0-9])", "$1").Replace("%", " percent ").Replace("€", " euro ");
        var tokens = Regex.Matches(prepared, (mixed ? @"[\p{L}\p{M}0-9]*(?:[\p{L}\p{M}][0-9]|[0-9][\p{L}\p{M}])[\p{L}\p{M}0-9]*|" : "") + @"[0-9]+|[\p{L}\p{M}]+(?:'[\p{L}\p{M}]+)*")
            .Select(match => !IsNumber(match.Value) ? Synonyms.GetValueOrDefault(match.Value, match.Value) : match.Value.TrimStart('0') is { Length: > 0 } digits ? digits : "0")
            .Where(token => token != "o'clock").ToList();
        for (var index = 0; index + 1 < tokens.Count; index++)
            if (tokens[index] == "por" && tokens[index + 1] == "ciento") { tokens[index] = "percent"; tokens.RemoveAt(index + 1); }
        return tokens;
    }
    // Times a text names, written as "12:34" or "minute 42": an hour followed by minutes in any of the forms above, a time with a colon,
    // a minute mark ("minute", "minuto", "Minute"), and digits such as "1149" right after a word that introduces a time ("at", "to",
    // "until", "a las", "hasta", "um", "bis"). Candidates for notes, not rewrites, so "move it to 345" is listed as 3:45 for the reader to check.
    private static readonly HashSet<string> TimeCues = new(StringComparer.Ordinal) { "at", "to", "until", "till", "from", "by", "las", "hasta", "desde", "um", "bis", "ab", "gegen" };
    public static IReadOnlyList<string> TimeReferences(string text)
    {
        var times = new List<string>();
        foreach (Match match in Regex.Matches(text, @"(?<![0-9])([01]?[0-9]|2[0-3]):([0-5][0-9])(?![0-9])")) times.Add($"{int.Parse(match.Groups[1].Value)}:{match.Groups[2].Value}");
        foreach (var words in Clauses(text).Select(Words))
            for (var index = 0; index + 1 < words.Count; index++)
            {
                var next = words[index + 1];
                if (words[index] is "minute" or "minuto" && IsNumber(next)) times.Add("minute " + next);
                // A time with its minutes is complete: the minutes are not the hour of another time ("at 9 15 30 people"). Two digits
                // before a unit are a quantity, as a single digit is ("at 12 15 percent"), except before the word that ends a spoken
                // time ("at 14 30 hours", "a las 18 30 horas").
                else if (IsHour(words[index]) && IsNumber(next) && next.Length == 2 && int.Parse(next) is >= 10 and <= 59
                    && (!BeforeUnit(words, index + 1) || words[index + 2] is "hours" or "horas")) { times.Add($"{words[index]}:{next}"); index++; }
                else if (TimeCues.Contains(words[index]) && IsNumber(next) && next.Length is 3 or 4 && int.Parse(next[..^2]) <= 23 && int.Parse(next[^2..]) <= 59) times.Add($"{next[..^2]}:{next[^2..]}");
                // Minutes below ten count only after a time word or a spoken "oh".
                else if (index + 2 < words.Count && IsMinuteDigit(words, index + 2) && (TimeCues.Contains(words[index]) && IsHour(next) || IsHour(words[index]) && next == "oh"))
                { times.Add($"{(next == "oh" ? words[index] : next)}:0{words[index + 2]}"); index += 2; }
            }
        return times.Distinct().ToArray();
    }

    public static IReadOnlyList<AlignmentStep> Align(IReadOnlyList<string> reference, IReadOnlyList<string> hypothesis)
    {
        int rows = reference.Count, columns = hypothesis.Count;
        // Directions only: a one-hour meeting stays within about 100 MB instead of an integer matrix.
        var direction = new byte[(rows + 1) * (columns + 1)];
        var previous = new int[columns + 1]; var current = new int[columns + 1];
        for (var column = 0; column <= columns; column++) { previous[column] = column; direction[column] = 2; }
        for (var row = 1; row <= rows; row++)
        {
            current[0] = row; direction[row * (columns + 1)] = 1;
            for (var column = 1; column <= columns; column++)
            {
                var diagonal = previous[column - 1] + (reference[row - 1] == hypothesis[column - 1] ? 0 : 1);
                var deletion = previous[column] + 1; var insertion = current[column - 1] + 1;
                var (cost, way) = diagonal <= deletion && diagonal <= insertion ? (diagonal, (byte)0) : deletion <= insertion ? (deletion, (byte)1) : (insertion, (byte)2);
                current[column] = cost; direction[row * (columns + 1) + column] = way;
            }
            (previous, current) = (current, previous);
        }
        var reversed = new List<AlignmentStep>();
        for (int row = rows, column = columns; row > 0 || column > 0;)
            switch (direction[row * (columns + 1) + column])
            {
                case 0: row--; column--; reversed.Add(new(reference[row] == hypothesis[column] ? AlignmentKind.Match : AlignmentKind.Substitution, row, column)); break;
                case 1: row--; reversed.Add(new(AlignmentKind.Deletion, row, -1)); break;
                default: column--; reversed.Add(new(AlignmentKind.Insertion, -1, column)); break;
            }
        reversed.Reverse();
        var lastReference = rows > 0 ? 0 : -1;
        return reversed.Select(step => step.Kind == AlignmentKind.Insertion ? step with { Reference = lastReference } : step with { Reference = lastReference = step.Reference }).ToArray();
    }

    // Languages spoken inside a recognition window, longest first; brief edge overlaps do not count.
    public static IReadOnlyList<string> SpokenLanguages(IEnumerable<(double Start, double End, string Language)> utterances, double windowStart, double windowEnd) =>
        utterances.Where(utterance => utterance.Language.Length > 0).GroupBy(utterance => utterance.Language)
            .Select(group => (Language: group.Key, Seconds: group.Sum(utterance => Math.Max(0, Math.Min(windowEnd, utterance.End) - Math.Max(windowStart, utterance.Start)))))
            .Where(language => language.Seconds > 0.25).OrderByDescending(language => language.Seconds).Select(language => language.Language).ToArray();

    private enum NumberKind { Unit, Closed, Tens, Hundreds, Hundred, Thousand }
    private static readonly Dictionary<string, (int Value, NumberKind Kind)> NumberWords = BuildNumberWords();
    private static Dictionary<string, (int Value, NumberKind Kind)> BuildNumberWords()
    {
        var words = new Dictionary<string, (int, NumberKind)>(StringComparer.Ordinal);
        void Add(NumberKind kind, int first, int step, string list) { var value = first; foreach (var word in list.Split(' ')) { foreach (var form in word.Split('/')) words[form] = (value, kind); value += step; } }
        Add(NumberKind.Unit, 0, 1, "zero one two three four five six seven eight nine");
        Add(NumberKind.Closed, 10, 1, "ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen");
        Add(NumberKind.Tens, 20, 10, "twenty thirty forty fifty sixty seventy eighty ninety");
        Add(NumberKind.Unit, 0, 1, "cero uno/un/una dos tres cuatro cinco seis siete ocho nueve");
        Add(NumberKind.Closed, 10, 1, "diez once doce trece catorce quince dieciséis diecisiete dieciocho diecinueve");
        Add(NumberKind.Closed, 21, 1, "veintiuno/veintiún/veintiuna veintidós veintitrés veinticuatro veinticinco veintiséis veintisiete veintiocho veintinueve");
        Add(NumberKind.Tens, 20, 10, "veinte treinta cuarenta cincuenta sesenta setenta ochenta noventa");
        Add(NumberKind.Hundreds, 100, 100, "cien/ciento doscientos/doscientas trescientos/trescientas cuatrocientos/cuatrocientas quinientos/quinientas seiscientos/seiscientas setecientos/setecientas ochocientos/ochocientas novecientos/novecientas");
        Add(NumberKind.Unit, 0, 1, "null eins/ein/eine zwei/zwo drei vier fünf sechs sieben acht neun");
        Add(NumberKind.Closed, 10, 1, "zehn elf zwölf dreizehn vierzehn fünfzehn sechzehn siebzehn achtzehn neunzehn");
        Add(NumberKind.Tens, 20, 10, "zwanzig dreißig vierzig fünfzig sechzig siebzig achtzig neunzig");
        foreach (var word in new[] { "hundred", "hundert" }) words[word] = (100, NumberKind.Hundred);
        foreach (var word in new[] { "thousand", "mil", "tausend" }) words[word] = (1000, NumberKind.Thousand);
        return words;
    }
    private static (int Value, NumberKind Kind)? NumberWord(string word) => NumberWords.TryGetValue(word, out var known) ? known : null;
    // German writes numbers as one word: "zweiundvierzig", "dreihundertzwölf", "zweitausendfünf".
    private static int? GermanCompound(string word)
    {
        if (!word.Contains("und") && !word.Contains("hundert") && !word.Contains("tausend")) return null;
        int? Unit(string part) => part == "ein" ? 1 : NumberWords.TryGetValue(part, out var unit) && unit is { Kind: NumberKind.Unit, Value: > 0 } ? unit.Value : null;
        int? BelowHundred(string part)
        {
            if (part.Length == 0) return 0;
            if (NumberWords.TryGetValue(part, out var direct) && direct.Kind is NumberKind.Unit or NumberKind.Closed or NumberKind.Tens && direct.Value > 0) return direct.Value;
            var and = part.IndexOf("und", StringComparison.Ordinal);
            return and > 0 && Unit(part[..and]) is { } units && NumberWords.TryGetValue(part[(and + 3)..], out var tens) && tens.Kind == NumberKind.Tens ? tens.Value + units : null;
        }
        int? BelowThousand(string part)
        {
            var hundred = part.IndexOf("hundert", StringComparison.Ordinal);
            if (hundred < 0) return BelowHundred(part);
            return (hundred == 0 ? 1 : Unit(part[..hundred])) is { } multiplier && BelowHundred(part[(hundred + 7)..]) is { } rest ? multiplier * 100 + rest : null;
        }
        var thousand = word.IndexOf("tausend", StringComparison.Ordinal);
        if (thousand < 0) return BelowThousand(word) is > 0 and var value ? value : null;
        return (thousand == 0 ? 1 : BelowThousand(word[..thousand])) is > 0 and var thousands && BelowThousand(word[(thousand + 7)..]) is { } remainder ? thousands * 1000 + remainder : null;
    }
    private static List<string> FoldNumbers(List<string> tokens, bool articles)
    {
        var output = new List<string>();
        int total = 0, group = 0; NumberKind? last = null;
        void Flush() { if (last is not null) output.Add((total + group).ToString()); total = group = 0; last = null; }
        bool Accept(int value, NumberKind kind)
        {
            var allowed = kind switch
            {
                NumberKind.Unit => last is null || value > 0 && last is NumberKind.Tens or NumberKind.Hundred or NumberKind.Hundreds or NumberKind.Thousand,
                NumberKind.Closed or NumberKind.Tens => last is null or NumberKind.Hundred or NumberKind.Hundreds or NumberKind.Thousand,
                NumberKind.Hundreds => last is null or NumberKind.Thousand,
                NumberKind.Hundred => last is null || last == NumberKind.Unit && group < 10,
                _ => total == 0 && last != NumberKind.Thousand
            };
            if (!allowed) return false;
            if (kind == NumberKind.Hundred) group = (last is null ? 1 : group) * 100;
            else if (kind == NumberKind.Thousand) { total = (group == 0 ? 1 : group) * 1000; group = 0; }
            else group += value;
            last = kind; return true;
        }
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var next = index + 1 < tokens.Count ? NumberWord(tokens[index + 1]) : null;
            if ((last is not null || output.Count > 0 && IsNumber(output[^1])) && token == "y" && index + 1 < tokens.Count && tokens[index + 1] is "media" or "cuarto")
            { Flush(); output.Add(tokens[index + 1] == "media" ? "30" : "15"); index++; continue; }
            if (token == "y" && last == NumberKind.Tens && next is { Kind: NumberKind.Unit, Value: > 0 }) continue;
            // A Spanish clock time joins hour and minutes with "y": "doce y treinta y cuatro" is 12:34.
            if (token == "y" && index + 1 < tokens.Count && (next is not null || IsNumber(tokens[index + 1]))
                && (last is not null ? total + group : output.Count > 0 && IsNumber(output[^1]) && output[^1].Length <= 2 ? int.Parse(output[^1]) : 99) <= 23)
            { Flush(); continue; }
            if (token == "and" && last is NumberKind.Hundred or NumberKind.Hundreds or NumberKind.Thousand && next is { Kind: NumberKind.Unit or NumberKind.Closed or NumberKind.Tens }) continue;
            // Digits with a written multiplier, as Spanish and German write large numbers: "20 mil", "3 Tausend".
            if (IsNumber(token) && token.Length <= 3 && next is { Kind: NumberKind.Thousand })
            { Flush(); group = int.Parse(token); last = NumberKind.Hundreds; continue; }
            var article = articles && token is "ein" or "eine" && next is null && !(index + 1 < tokens.Count && (tokens[index + 1] == "uhr" || IsNumber(tokens[index + 1])));
            if (NumberWord(token) is { } number && !article)
            {
                if (!Accept(number.Value, number.Kind)) { Flush(); Accept(number.Value, number.Kind); }
                continue;
            }
            Flush(); output.Add(GermanCompound(token) is { } compound ? compound.ToString() : token);
        }
        Flush();
        // German clock times: "zwölf Uhr dreißig", "12.30 Uhr" and "12.30" all become "12 30"; "um 5 Uhr" keeps its "Uhr".
        for (var index = 0; index + 2 < output.Count; index++)
            if (IsNumber(output[index]) && (output[index + 1] == "uhr" && IsNumber(output[index + 2]) || IsNumber(output[index + 1]) && output[index + 2] == "uhr"))
                output.RemoveAt(output[index + 1] == "uhr" ? index + 1 : index + 2);
        return output;
    }
    private static bool IsNumber(string token) => token.Length > 0 && token.All(char.IsAsciiDigit);
}
