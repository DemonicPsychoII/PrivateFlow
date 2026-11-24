using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

// Backtrack without a language model: "Use Vendor B, no wait, Vendor C" becomes "Use Vendor C".
// Only a narrow grammar is applied: an explicit cue between two values of one kind (a number
// with the same unit, a weekday, a month, or a repeated label such as "Vendor B"/"Vendor C"),
// and a standalone "Scratch that." that withdraws the sentence before it. Names, phrases and
// restarts need context and stay for the optional rewrite styles. "Wait, not X" stays: in text
// it can exclude X as well as correct to X. Quotations are content and are never corrected.
public static partial class SpokenCorrections
{
    public static string Apply(string text)
    {
        // Quotations become opaque tokens; no rule matches across them.
        var quotes = new List<string>();
        var result = TextCleanup.ReplaceQuotations(text, quote => { quotes.Add(quote); return "\ue800" + (quotes.Count - 1) + "\ue801"; });
        try
        {
            // A chained repair ("Vendor A, no wait, Vendor B, sorry, I meant Vendor C") resolves
            // one link per pass.
            for (var pass = 0; pass < 4; pass++)
            {
                // Each stage resolves positions against the exact string it matched.
                var next = result;
                foreach (var rule in new[] { Quantity(), Calendar(), Label() })
                {
                    var input = next;
                    next = rule.Replace(input, m => Resolve(m, input));
                }
                if (next == result) break;
                result = next;
            }
            // The kept text keeps its case: Clean capitalizes later and protects "mV" or "config.json".
            foreach (var rule in new[] { Retraction(), InlineRetraction(), FinalRetraction() })
            {
                var input = result;
                result = rule.Replace(input, m => SeparatorBefore(input, m.Groups["cue"].Index) && SeparatorAfter(input, m.Groups["cue"].Index + m.Groups["cue"].Length)
                    ? m.Value : m.Groups["lead"].Value);
            }
        }
        // The patterns are linear on dictation, but a pathological transcript must not stall the
        // paste: it is pasted without corrections instead.
        catch (RegexMatchTimeoutException) { return text; }
        return QuoteToken().Replace(result, m => quotes[int.Parse(m.Groups[1].Value)]);
    }

    private static readonly HashSet<string> Conjunctions = new(StringComparer.OrdinalIgnoreCase)
    { "or", "and", "to", "between", "from", "oder", "und", "bis", "zwischen", "von" };
    private static readonly HashSet<string> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
      "Montag", "Dienstag", "Mittwoch", "Donnerstag", "Freitag", "Samstag", "Sonntag" };

    private static string Resolve(Match m, string text)
    {
        // "3 or 4 copies, sorry, I meant 5 copies" and "from 3 V to 5 V" have two candidates for the
        // old value; "set the timeout to 500 ms" has one.
        // "Monday or Tuesday, no wait, Wednesday" and "Vendor A or Vendor B, no wait, Vendor C" likewise.
        if (m.Groups["w"].Success && Conjunctions.Contains(m.Groups["w"].Value) &&
            (m.Groups["day"].Success ? CalendarBefore() : ValueBefore()).IsMatch(text, m.Index)) return m.Value;
        if (m.Groups["head"].Success && AlternativeBefore().IsMatch(text, m.Index)) return m.Value;
        if (string.Equals(m.Groups["old"].Value, m.Groups["new"].Value, StringComparison.OrdinalIgnoreCase)) return m.Value;
        if (m.Groups["day"].Success && Weekdays.Contains(m.Groups["old"].Value) != Weekdays.Contains(m.Groups["new"].Value)) return m.Value;
        // "actually" is also an ordinary adverb; accept it only when the new value ends the clause.
        // The full stop of "4:30 p.m." also ends the clause or sentence, unless the sentence goes on
        // in lowercase ("Actually, 4 p.m. works better").
        var end = m.Index + m.Length;
        var valueEnd = m.Value.EndsWith('.') && !LowercaseAfter().IsMatch(text, end) ? end - 1 : end;
        if (m.Groups["cue"].Value.Equals("actually", StringComparison.OrdinalIgnoreCase) && !ClauseEnd().IsMatch(text, valueEnd)) return m.Value;
        // Whisper often ends the sentence before the cue ("Tuesday. Sorry, I meant Wednesday.").
        // Across a full stop the repair must end its sentence: only then is it a bare replacement.
        if (m.Groups["stop"].Success && !SentenceEnd().IsMatch(text, valueEnd)) return m.Value;
        // A path may hold spaces and any words ("C:/draft Vendor B, no wait, Vendor C final/a.txt").
        // Path detection is bounded, so a correction between path separators in one sentence is
        // left alone, however far apart they are; the sentence boundaries stay untouched.
        if (SeparatorBefore(text, m.Index) && SeparatorAfter(text, end)) return m.Value;
        return m.Groups["pre"].Value + m.Groups["repair"].Value;
    }
    private static bool SeparatorBefore(string text, int index)
    {
        for (var i = index - 1; i >= 0 && !SentenceBreak(text, i); i--) if (text[i] is '/' or '\\') return true;
        return false;
    }
    private static bool SeparatorAfter(string text, int index)
    {
        for (var i = index; i < text.Length && !SentenceBreak(text, i); i++) if (text[i] is '/' or '\\') return true;
        return false;
    }
    // A line break, or sentence punctuation followed by whitespace or the end ("report.txt." ends,
    // "report.txt" does not).
    private static bool SentenceBreak(string text, int i) =>
        text[i] == '\n' || text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));

    // The interregnum: an explicit editing term, optionally after a hesitation. A bare "no" or
    // "nein" counts only between commas, so "No, …" answering a question never matches; an
    // explicit multi-word cue may also follow a full stop, never a question mark.
    // Whitespace runs are atomic: overlapping optional runs made a 12,000-character
    // transcript without any cue take 25 s.
    private const string Cue =
        @"(?:(?>\s*)(?:(?<stop>\.)|,)?(?>\s*)(?:(?:uh|um|uhm|er|äh|ähm),?\s+)?(?<cue>(?:no,?\s+)?wait,?\s+no|no,?\s+wait|sorry,?\s+I\s+mean[t]?|I\s+mean[t]?|correction|make\s+that|or\s+rather|actually|" +
        @"(?:moment|warte),?\s+nein|nein,?\s+ich\s+mein(?:e|te)?|(?:Entschuldigung|sorry),?\s+ich\s+meinte|ich\s+mein(?:e|te)|Korrektur)(?>\s*),?(?>\s*)" +
        @"|(?>\s*),(?>\s*)(?:(?:uh|um|äh|ähm),?\s+)?(?<cue>no|nein|nee)(?>\s*),(?>\s*))";
    private const string Start = @"(?<![\p{L}\p{N}_/\\.:\-])(?<pre>(?<w>\p{L}+)(?>\s+))?";
    // A value ends at a word boundary.
    private const string End = @"(?![\p{L}\p{N}_/\\]|\.[\p{L}\p{N}])";
    // A number or label value also ends before a hyphenated compound: after a hyphen (also U+2010, the non-breaking U+2011 or an en
    // dash) only a plain number or a single capital may follow, as in the range "4-6" or "Room 4-A"; "4-wheel", "4-1/2-inch", "4-20-mA"
    // and the file name "4-6.pdf" are no repair. Weekdays keep their compounds ("Tuesday-Thursday").
    private const string NumberEnd = @"(?![\p{L}\p{N}_/\\]|[\-\u2010\u2011\u2013](?!(?>\d+(?:[.,:]\d+)*)(?![\p{L}\p{N}_/\\\-\u2010\u2011\u2013]|\.[\p{L}\p{N}])|(?-i:\p{Lu})(?![\p{L}\p{N}_/\\\-\u2010\u2011\u2013]|\.[\p{L}\p{N}]))|\.[\p{L}\p{N}])";
    // A weekday or month keeps a capitalized compound ("Tuesday-Thursday", "Dienstag-Nachmittag"), not a lowercase word or a file name
    // ("Tuesday-report", "Tuesday-Report2.pdf").
    private const string CalendarEnd = End + @"(?![\-\u2010\u2011\u2013](?:(?-i:\p{Ll})|[\p{L}\p{N}_\-\u2010\u2011\u2013]+\.[\p{L}\p{N}]))";
    // Atomic: a value the end refuses ("4,5-mm") is not retried as a shorter one ("4" before ",5-mm").
    private const string Number = @"(?>[+\-−]?\d+(?:[.,:]\d+)*)";
    // A bound per search; Apply then pastes the text without corrections.
    private const int TimeoutMs = 1000;

    // "at 3 pm, no wait, 4 pm", "um 15 Uhr, nein, um 16 Uhr": the unit must repeat, and a new
    // value without one must not be followed by a word that could be a unit, except after a time
    // with minutes after a colon ("at 3, no wait, 4:30 pm", which matched before the number became atomic);
    // "bei 20, nein, 50 am Tag" and "2.50 am Tag" stay as spoken.
    [GeneratedRegex(Start + "(?<old>" + Number + @")(?:(?>\s*)(?<unit>[ap]\.m\.|\p{L}{1,16}))?" + Cue +
        @"(?:\k<w>\s+)?(?<repair>(?<new>" + Number + @")(?(unit)(?>\s*)\k<unit>|(?:(?!\s*\p{L})|(?<=:\d{2})(?>\s*)(?:[ap]\.m\.|[ap]m|Uhr)(?![\p{L}\p{N}_/\\\-\u2010\u2011\u2013]|\.[\p{L}\p{N}]))))" + NumberEnd, RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex Quantity();

    [GeneratedRegex(Start + "(?<day>)(?<old>" + CalendarName + ")" + Cue + @"(?:\k<w>\s+)?(?<repair>(?<new>" + CalendarName + "))" + CalendarEnd, RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex Calendar();
    // Names are capitalized; "may" as a verb is not May.
    private const string CalendarName = "(?-i:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Montag|Dienstag|Mittwoch|Donnerstag|Freitag|Samstag|Sonntag|" +
        "January|February|March|April|May|June|July|August|September|October|November|December|Januar|Februar|März|Mai|Juni|Juli|Oktober|Dezember)";

    // "Vendor B, no wait, Vendor C": the repair retraces the head word with a new identifier.
    [GeneratedRegex(@"(?<![\p{L}\p{N}_/\\.:\-])(?<pre>)(?<head>\p{L}{2,})(?>\s+)(?<old>(?-i:\p{Lu})|\d+)(?![\p{L}\p{N}])" + Cue +
        @"(?<repair>\k<head>\s+(?<new>(?-i:\p{Lu})|" + Number + "))" + NumberEnd, RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex Label();

    // Right-to-left from the match start: does an alternative value end just before it?
    [GeneratedRegex(@"\d(?:\s*\p{L}{1,16})?[ \t]*\G", RegexOptions.RightToLeft, TimeoutMs)]
    private static partial Regex ValueBefore();
    [GeneratedRegex(CalendarName + @"[ \t]*\G", RegexOptions.RightToLeft, TimeoutMs)]
    private static partial Regex CalendarBefore();
    [GeneratedRegex(@"\b(?:or|and|oder|und|bzw|sowie)[ \t]+\G", RegexOptions.RightToLeft | RegexOptions.IgnoreCase, TimeoutMs)]
    private static partial Regex AlternativeBefore();
    [GeneratedRegex(@"\G[ \t]*(?:[.!?](?=\s|$)|\r?\n|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"\G[ \t]*(?:[,.;:!?]|\r?\n|$)")]
    private static partial Regex ClauseEnd();

    [GeneratedRegex(@"\G\s+\p{Ll}")]
    private static partial Regex LowercaseAfter();

    // A "Scratch that." sentence removes itself and the one sentence before it on the same line;
    // "Scratch that, the meeting is on Friday." keeps what follows the comma. "Scratch that itch"
    // and "Streich das Holz blau" are content. A dot inside a number ("3.5 V") or after an
    // abbreviation ("e.g.", "z.B.", "Dr.") is no sentence boundary. A quotation hides its own
    // full stop, so no withdrawn sentence or clause may contain one; without a sentence before
    // the cue, nothing is removed.
    private const string Lead = @"(?<lead>^|(?<!(?<![\p{L}\p{N}])\p{L})(?<!\b(?:etc|bzw|usw|ca|Dr|Mr|Mrs|Ms|Nr|vs))[.!?][ \t]+)";
    private const string Withdraw = @"(?:(?:uh|um|äh|ähm|oh),?\s+)?(?<cue>(?:scratch|strike)\s+that|streich\s+das|vergiss\s+das)";
    private const string Clause = @"[^,;:.!?\n\ue800\ue801]+";
    [GeneratedRegex(Lead + @"[^.!?\n\ue800\ue801]*(?:\.(?=\S)[^.!?\n\ue800\ue801]*)*[.!?][ \t]+" + Withdraw + @"(?:[.!](?=\s|$)|(?=[ \t]*$)|,[ \t]*(?=\p{L}))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeoutMs)]
    private static partial Regex Retraction();

    // Whisper often keeps the retraction inside the sentence. Set off by commas and followed by
    // the repair, it withdraws only the clause right before it: "Do not delete the backup, book
    // Monday, scratch that, book Tuesday." keeps the backup instruction.
    [GeneratedRegex(@"(?:" + Lead + @"|(?<lead>,[ \t]*))" + Clause + ",[ \t]*" + Withdraw + @",[ \t]*(?=\p{L})",
        RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeoutMs)]
    private static partial Regex InlineRetraction();

    // "The draft is ready, scratch that." withdraws its sentence only when that sentence is one
    // clause; with several clauses the withdrawn part is ambiguous and stays.
    [GeneratedRegex(Lead + Clause + ",[ \t]*" + Withdraw + @"[.!](?=\s|$)[ \t]*", RegexOptions.IgnoreCase | RegexOptions.Multiline, TimeoutMs)]
    private static partial Regex FinalRetraction();

    [GeneratedRegex("\ue800(\\d+)\ue801")]
    private static partial Regex QuoteToken();
}