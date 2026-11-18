using System.Text;

namespace PrivateFlow.Core;

// Finds technical spans that Clean must never edit and swaps each for a placeholder.
// A single left-to-right pass over whitespace tokens with bounded look-ahead keeps this
// linear in the input length; regex alternation over \S* was quadratic on long tokens.
internal static class TechnicalSpans
{
    private const int PathLookahead = 8;
    private const string TechnicalChars = "\\/_@=+#~^|<>[]{}()*$%&`";
    private const string TrailingPunctuation = ".,!?;:…";
    private static readonly char[] OpeningBrackets = ['(', '[', '{'];
    private static readonly char[] ClosingPunctuation = [')', ']', '}', '.', ',', '!', '?', ';', ':', '…'];
    // Lower-case hesitation words that must not be mistaken for a unit after a number.
    private static readonly HashSet<string> Hesitations = new(StringComparer.Ordinal)
    { "uh", "uhm", "um", "umm", "er", "erm", "ah", "eh", "ehm", "hm", "hmm", "äh", "ähm", "öhm" };
    // Lower-case-first unit symbols whose case is their meaning (m = milli, M = mega); mixed-case
    // symbols such as mAh, kWh or dB are caught by the inner-capital rule in IsTechnical.
    private static readonly HashSet<string> CaseSensitiveUnits = new(StringComparer.Ordinal)
    { "mm", "cm", "km", "nm", "µm", "mg", "kg", "ms", "ns", "µs", "ml", "kb", "mb", "gb" };

    public static string Protect(string text, List<string> store, char start, char end)
    {
        var tokens = Tokenize(text);
        var quoteEnds = QuotedSpanEnds(tokens);
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < tokens.Count;)
        {
            var last = quoteEnds[i] ?? PathSpanEnd(tokens, i) ?? i;
            var single = last == i;
            if (single && !IsTechnical(Core(tokens[i].Text)) && !UnitFollows(tokens, i))
            {
                output.Append(tokens[i].Text).Append(tokens[i].Separator);
                i++;
                continue;
            }
            if (single && UnitFollows(tokens, i)) last = i + 1;
            // Join the span, then keep sentence punctuation after it outside the placeholder so
            // capitalization still sees the sentence boundary ("config.json. Then").
            var span = new StringBuilder();
            for (var t = i; t <= last; t++) span.Append(tokens[t].Text).Append(t < last ? tokens[t].Separator : "");
            var raw = span.ToString();
            var core = Core(raw);
            store.Add(core);
            output.Append(start).Append(store.Count - 1).Append(end).Append(raw[core.Length..]).Append(tokens[last].Separator);
            i = last + 1;
        }
        return output.ToString();
    }

    // Character ranges [Start, End) of unquoted paths with spaces. Words inside them are folder
    // names, so "C:/release quote notes end quote final/a.txt" holds no spoken quote markers.
    public static List<(int Start, int End)> PathRanges(string text)
    {
        var tokens = Tokenize(text);
        var ranges = new List<(int Start, int End)>();
        var offset = 0;
        for (var i = 0; i < tokens.Count;)
        {
            if (PathSpanEnd(tokens, i) is { } last)
            {
                var end = offset;
                for (var t = i; t < last; t++) end += tokens[t].Text.Length + tokens[t].Separator.Length;
                ranges.Add((offset, end + tokens[last].Text.Length));
                offset = end + tokens[last].Text.Length + tokens[last].Separator.Length;
                i = last + 1;
                continue;
            }
            offset += tokens[i].Text.Length + tokens[i].Separator.Length;
            i++;
        }
        return ranges;
    }

    private readonly record struct Token(string Text, string Separator);
    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var position = 0;
        while (position < text.Length)
        {
            var wordEnd = position;
            while (wordEnd < text.Length && text[wordEnd] is not (' ' or '\n' or '\r')) wordEnd++;
            var gapEnd = wordEnd;
            while (gapEnd < text.Length && text[gapEnd] is ' ' or '\n' or '\r') gapEnd++;
            tokens.Add(new(text[position..wordEnd], text[wordEnd..gapEnd]));
            position = gapEnd;
        }
        return tokens;
    }

    // Trailing sentence punctuation is prose, not part of the technical token. Paths ending in a
    // separator ("C:\") keep it because separators are not in the trimmed set.
    private static string Core(string token)
    {
        var length = token.Length;
        while (length > 0 && TrailingPunctuation.Contains(token[length - 1])) length--;
        return length == 0 ? token : token[..length];
    }

    private static bool IsTechnical(string token)
    {
        if (CaseSensitiveUnits.Contains(token)) return true;
        if (token.Length > 1 && token[0] == '-' && (char.IsLetter(token[1]) || token[1] == '-')) return true;
        for (var c = 0; c < token.Length; c++)
        {
            var ch = token[c];
            if (char.IsDigit(ch) || TechnicalChars.Contains(ch)) return true;
            // A capital after a lower-case letter marks a unit or identifier whose case is its
            // meaning: mAh, kWh, dB, macOS. Capitalization must not touch these.
            if (c > 0 && char.IsUpper(ch) && char.IsLower(token[c - 1])) return true;
            // Inner dot or colon between word characters: "config.json", "ah.txt", "C:Data".
            if (ch is '.' or ':' && c > 0 && c + 1 < token.Length && char.IsLetterOrDigit(token[c - 1]) && char.IsLetterOrDigit(token[c + 1])) return true;
        }
        return false;
    }

    // "5 Ah", "12 mm" keep their unit. A number followed by punctuation ("5, uh,") or by a
    // lower-case hesitation is ordinary speech, so the next word stays editable.
    private static bool UnitFollows(List<Token> tokens, int i)
    {
        var number = tokens[i].Text;
        // Any whitespace may separate them, including an ASR segment line break ("5\nAh"); the
        // protected span keeps that line break verbatim.
        if (i + 1 >= tokens.Count || number.Length == 0 ||
            !char.IsDigit(number[^1]) || !number.Any(char.IsDigit)) return false;
        var unit = tokens[i + 1].Text;
        var letters = Core(unit);
        // Punctuation after a real unit is ordinary ("5 Ah, 12 V"); only hesitation words are
        // left editable, so "5 uh, copies" still loses its filler.
        return letters.Length is >= 1 and <= 4 && letters.All(char.IsLetter) && !Hesitations.Contains(letters);
    }

    // A closed quote that contains anything technical is protected whole, whatever its length:
    // "C:/Projects/release uh notes/report.txt". One backward pass precomputes, for every token,
    // the next closing quote and whether technical content lies before it, so this stays linear.
    // An unclosed quote that starts with technical content ("C:/release uh notes …) is protected
    // conservatively to the end of its line: leaving a filler is safer than editing a path.
    private static int?[] QuotedSpanEnds(List<Token> tokens)
    {
        var ends = new int?[tokens.Count];
        int? nextClose = null;
        var technicalBeforeClose = false;
        var lineEnd = tokens.Count - 1;
        for (var j = tokens.Count - 1; j >= 0; j--)
        {
            // Quotes sit inside prose brackets and punctuation: ("C:/a b/c.txt"). Look past them.
            var core = tokens[j].Text.TrimStart(OpeningBrackets).TrimEnd(ClosingPunctuation);
            if (tokens[j].Separator.Contains('\n')) { nextClose = null; technicalBeforeClose = false; lineEnd = j; }
            var opens = core.Length > 0 && core[0] is '"' or '“' or '„';
            var closes = core.Length > 0 && core[^1] is '"' or '”' or '“';
            var technical = IsTechnical(core.Trim('"', '“', '”', '„'));
            if (opens)
            {
                // A quote around one token ("C:/a/b.txt") closes itself; a lone opener spans to the next closer.
                if (closes && core.Length > 1) ends[j] = technical ? j : null;
                else if (nextClose is { } close) ends[j] = technical || technicalBeforeClose ? close : null;
                else if (technical) ends[j] = lineEnd;
            }
            // Only a token that closes without opening ends an earlier quote.
            if (closes && !opens) { nextClose = j; technicalBeforeClose = technical; }
            else technicalBeforeClose |= technical;
        }
        return ends;
    }

    // An unquoted path with spaces ("C:/Duran Duran/track.txt", "C:/release uh notes/a.txt"):
    // from a token with a path separator to the next one within a short window, never across a
    // sentence end or line break. Over-protecting only means a filler stays; never an edit.
    private static int? PathSpanEnd(List<Token> tokens, int i)
    {
        if (!HasSeparator(tokens[i].Text) || EndsSentence(tokens[i].Text)) return null;
        for (var j = i + 1; j < tokens.Count && j <= i + PathLookahead; j++)
        {
            if (tokens[j - 1].Separator.Contains('\n')) return null;
            var text = tokens[j].Text;
            if (HasSeparator(text) && !StartsWithSeparator(text)) return j;
            if (EndsSentence(text)) return null;
        }
        return null;
    }
    private static bool HasSeparator(string token) => token.IndexOfAny(['/', '\\']) >= 0;
    private static bool StartsWithSeparator(string token) => token.Length > 0 && token[0] is '/' or '\\';
    private static bool EndsSentence(string token) => token.Length > 0 && token[^1] is '.' or '!' or '?';
}
