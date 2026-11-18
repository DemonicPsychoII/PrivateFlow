using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

public sealed class PreparedDictation
{
    private readonly Dictionary<string, string> snippets = new(StringComparer.Ordinal);
    public string Text { get; private set; }
    public PreparedDictation(string text, IEnumerable<PhraseRule> aliases, IEnumerable<PhraseRule> expansions,
        string language, bool literal, bool formatting)
    {
        // Corrections run first: technical spans below hide the numbers and units they compare.
        Text = literal ? text : OutsideQuotes(SpokenCorrections.Apply(text), part =>
        {
            var expanded = Replace(part, expansions, language, body =>
            {
                var token = "[[S" + Guid.NewGuid().ToString("N") + "]]";
                snippets.Add(token, body);
                return token;
            });
            expanded = Replace(expanded, aliases, language, body => body);
            return formatting ? SpokenFormatting.Apply(expanded) : expanded;
        }, formatting);
    }
    public string Restore(string output)
    {
        foreach (var (token, body) in snippets)
        {
            if (Count(output, token) != 1) throw new InvalidOperationException("Rewrite changed a snippet.");
            output = output.Replace(token, body, StringComparison.Ordinal);
        }
        return output;
    }
    public bool PreservesSnippets(string output) => snippets.Keys.All(token => Count(output, token) == 1);
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;

    public static string OutsideQuotes(string text, Func<string, string> transform, bool spellFirst = false)
    {
        var protectedQuotes = new Dictionary<string, string>();
        var protectedText = TextCleanup.ReplaceQuotations(text, quote =>
        {
            var token = "[[Q" + Guid.NewGuid().ToString("N") + "]]";
            protectedQuotes.Add(token, quote);
            return token;
        });
        if (spellFirst) protectedText = SpokenFormatting.Spell(protectedText);
        var technical = new List<string>();
        protectedText = TechnicalSpans.Protect(protectedText, technical, '\ue700', '\ue701');
        var result = transform(protectedText);
        for (var i = 0; i < technical.Count; i++) result = result.Replace($"\ue700{i}\ue701", technical[i], StringComparison.Ordinal);
        foreach (var (token, quote) in protectedQuotes) result = result.Replace(token, quote, StringComparison.Ordinal);
        return result;
    }

    private static string Replace(string text, IEnumerable<PhraseRule> rules, string language, Func<string, string> replacement)
    {
        var active = rules.Where(r => !string.IsNullOrWhiteSpace(r.Original) &&
            (r.Language is "" or "auto" || r.Language == language)).OrderByDescending(r => r.Original.Length)
            .GroupBy(r => r.Original.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        if (active.Length == 0) return text;
        var byPhrase = active.ToDictionary(r => r.Original.Trim(), StringComparer.OrdinalIgnoreCase);
        var pattern = @"(?<![\p{L}\p{N}_/\\\ue700\ue701])(?:" + string.Join("|", active.Select(r => Regex.Escape(r.Original.Trim()))) + @")(?![\p{L}\p{N}_/\\\ue700\ue701])";
        return Regex.Replace(text, pattern, m => replacement(byPhrase[m.Value].Replacement), RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));
    }
}

public static partial class SpokenFormatting
{
    public static string Apply(string text)
    {
        var result = Paragraph().Replace(text, "\n\n");
        result = NewLine().Replace(result, "\n");
        result = Bullet().Replace(result, "\n• ");
        // Numbered before punctuation: "Punkt" of "nummerierter Punkt" is no full stop.
        var number = 0;
        result = Numbered().Replace(result, _ => $"\n{++number}. ");
        result = Punctuation().Replace(result, m => m.Value.Trim().ToLowerInvariant() switch
        {
            "comma" or "komma" => ",", "colon" or "doppelpunkt" => ":", "semicolon" or "semikolon" => ";",
            "question mark" or "fragezeichen" => "?", "exclamation mark" or "ausrufezeichen" => "!", _ => "."
        });
        result = Spell(result);
        return result.Trim();
    }
    // A dictated email reads as one line: "Hi Anna, thanks … Best regards, Alex". A greeting at
    // the start (with a name) and a closing at the end, each set off by a comma, get their own lines. Only these
    // fixed phrases count, so "Hi" or "Grüße" inside a sentence stay where they are. English
    // continues the body with a capital letter, German (after "Hallo Anna,") with lower case.
    // Runs on the final text, after Clean or a rewrite: fillers are gone by then and dictated
    // "comma"/"full stop" are real punctuation. The greeting and closing must appear, with their
    // commas, in what was dictated (spoken); Clean may then have dropped a comma together with a
    // filler ("Hi Anna, uh, thanks" becomes "Hi Anna thanks"), so the final text may lack it.
    public static string Email(string text, string? spoken = null)
    {
        spoken ??= text;
        var result = text;
        // The greeting found in the dictated text is reused verbatim; guessing the name again on
        // the final text took capitalised body words into it ("Hi Anna I, will send…").
        var greeting = Greeting().Match(spoken);
        var opening = greeting.Groups["greeting"].Value;
        if (greeting.Success && text.StartsWith(opening, StringComparison.Ordinal) && text.Length > opening.Length && text[opening.Length] is ',' or ' ' or '\t')
        {
            var body = text[opening.Length..].TrimStart(',').TrimStart(' ', '\t');
            if (body.Length > 0 && char.IsLetter(body[0]))
                result = opening + ",\n\n" + (greeting.Groups["english"].Success && !StartsTechnical(body) ? char.ToUpperInvariant(body[0]) + body[1..] : body);
        }
        // Whisper often runs the closing on after a comma ("by Friday, best regards, Alex.").
        return !Closing().IsMatch(spoken) ? result : LooseClosing().Replace(result, m =>
            (m.Groups["end"].Value is "" or "," ? "." : m.Groups["end"].Value) + "\n\n" +
            char.ToUpperInvariant(m.Groups["closing"].Value[0]) + m.Groups["closing"].Value[1..] + ",\n" + m.Groups["name"].Value);
    }
    // Case is the meaning of a unit or file name ("mm", "config.json"); the body keeps it.
    private static bool StartsTechnical(string body)
    {
        var first = body.Split([' ', '\t', '\n'], 2)[0];
        var spans = new List<string>();
        TechnicalSpans.Protect(first, spans, (char)0xE702, (char)0xE703);
        return spans.Count > 0;
    }
    private const string GreetingWords = @"(?<greeting>(?:(?<english>Hi|Hello|Hey|Dear|Good morning|Good afternoon)|Hallo|Liebe[rs]?|Sehr geehrte[rs]?|Guten Morgen|Guten Tag|Servus|Moin)" +
        @"(?:(?>[ \t]+)(?:(?:Mr|Mrs|Ms|Dr)\.?(?>[ \t]+)|(?:Herr|Frau)(?>[ \t]+))?\p{Lu}[\p{L}'-]*){1,3}|Sehr geehrte Damen und Herren|(?<english>Dear Sir or Madam))";
    private const string ClosingWords = @"(?<closing>(?i:best regards|kind regards|regards|best wishes|many thanks)|Viele Grüße|Beste Grüße|Liebe Grüße|Herzliche Grüße|Schöne Grüße|Mit freundlichen Grüßen|Freundliche Grüße)" +
        @"(?>[ \t]*),(?>\s*)(?<name>\p{Lu}[\p{L}'-]*(?:[ \t]\p{Lu}[\p{L}'-]*)?)\.?(?>\s*)$";
    public static string Spell(string text) => Spelling().Replace(text, m => string.Concat(m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
    [GeneratedRegex(@"\s*\b(?:new paragraph|neuer absatz|neuen absatz)\b[,.:]?\s*", RegexOptions.IgnoreCase)] private static partial Regex Paragraph();
    [GeneratedRegex(@"\s*\b(?:new line|neue zeile)\b[,.:]?\s*", RegexOptions.IgnoreCase)] private static partial Regex NewLine();
    [GeneratedRegex(@"\s*\b(?:bullet point|bullet|aufzählungspunkt)\b[,.:]?\s*", RegexOptions.IgnoreCase)] private static partial Regex Bullet();
    [GeneratedRegex(@"\s*\b(?:numbered item|numbered point|nummerierter punkt|nummerierten punkt)\b[,.:]?\s*", RegexOptions.IgnoreCase)] private static partial Regex Numbered();
    [GeneratedRegex("^" + GreetingWords + @",(?>\s+)(?<body>\p{L})")] private static partial Regex Greeting();
    [GeneratedRegex(@"(?<end>[.!?,])(?>\s+)" + ClosingWords)] private static partial Regex Closing();
    [GeneratedRegex(@"(?<end>[.!?,])?(?<=[\p{L}\p{N}.!?,])(?>\s+)" + ClosingWords)] private static partial Regex LooseClosing();
    [GeneratedRegex(@"\s*\b(?:comma|komma|colon|doppelpunkt|semicolon|semikolon|question mark|fragezeichen|exclamation mark|ausrufezeichen|full stop|punkt)\b", RegexOptions.IgnoreCase)] private static partial Regex Punctuation();
    [GeneratedRegex(@"\b(?:spell|buchstabiere)\s+((?:[\p{L}\p{N}]\s+)+[\p{L}\p{N}])\s+(?:end spelling|buchstabieren ende)\b", RegexOptions.IgnoreCase)] private static partial Regex Spelling();
}
