namespace PrivateFlow.Core;

// The languages offered for dictation, library entries and profiles. "auto" detects which of them is spoken.
public static class SpeechLanguages
{
    public static readonly string[] Choices = ["auto", "en", "de", "es"];
    public static bool IsChoice(string? language) => Choices.Contains(language);
    public static bool IsSpoken(string? language) => language is "en" or "de" or "es";
    // The spoken languages Private Flow offers. Automatic chooses among them, never one of Whisper's other languages.
    public const string Offered = "en,de,es";
    // A comma-separated list of offered spoken languages, in order and without repeats; anything else is ignored.
    public static IReadOnlyList<string> Parse(string? languages) =>
        (languages ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(language => language.ToLowerInvariant()).Where(IsSpoken).Distinct().ToArray();
}

public sealed class PhraseRule
{
    public string Original { get; set; } = "";
    public string Replacement { get; set; } = "";
    public string Language { get; set; } = "auto";
}

public sealed class DictationProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public string Language { get; set; } = "auto";
    public OutputStyle OutputStyle { get; set; } = OutputStyle.Clean;
    public string Vocabulary { get; set; } = "";
    public bool AutoInsert { get; set; } = true;
    public bool Formatting { get; set; }
    public bool Technical { get; set; }
    public bool Literal { get; set; }
    public string Applications { get; set; } = "";
    public uint ShortcutModifiers { get; set; }
    public uint ShortcutKey { get; set; }
    public override string ToString() => Name;
}

public static class DictationProfiles
{
    // The first profile whose comma-separated applications name the executable, such as "Code.exe", in any case.
    public static DictationProfile? Match(IEnumerable<DictationProfile> profiles, string executableName) =>
        profiles.FirstOrDefault(profile => profile.Applications.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(name => string.Equals(name, executableName, StringComparison.OrdinalIgnoreCase)));
}

public sealed record TranscriptSegment(double Start, double End, string Text, string Speaker = "Speaker");
public sealed record SpeechResult(IReadOnlyList<TranscriptSegment> Segments, double Seconds, string Language = "auto")
{
    public string Text => string.Join(" ", Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));
}

public static class SegmentAssembly
{
    public static void Append(List<TranscriptSegment> destination, IEnumerable<TranscriptSegment> next, double offset)
    {
        foreach (var segment in next)
        {
            var current = segment with { Start = segment.Start + offset, End = segment.End + offset };
            if (destination.Count > 0 && destination[^1].Speaker == current.Speaker && current.Start < destination[^1].End)
            {
                var earlier = destination[^1].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var words = current.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var overlap = 0;
                // Only reconcile text inside the audio overlap, never ordinary repeated sentences.
                for (var n = Math.Min(12, Math.Min(earlier.Length, words.Length)); n > 0; n--)
                    if (earlier[^n..].Select(Normalize).SequenceEqual(words[..n].Select(Normalize))) { overlap = n; break; }
                if (overlap > 0) current = current with { Text = string.Join(" ", words[overlap..]), Start = destination[^1].End };
            }
            if (!string.IsNullOrWhiteSpace(current.Text)) destination.Add(current);
        }
    }
    private static string Normalize(string word) => word.Trim('.', ',', '!', '?', ':', ';').ToLowerInvariant();
    // Whisper sometimes ends a segment in mid-sentence ("no pone las siete" | "y cuarenta"); a segment that does not end a sentence
    // takes the next one, so a row, its search terms and its times hold the whole sentence. Unpunctuated run-on text stops at
    // maximumSeconds, so a row stays a row.
    public static IReadOnlyList<TranscriptSegment> JoinSentences(IEnumerable<TranscriptSegment> segments, double maximumSeconds = 10)
    {
        var joined = new List<TranscriptSegment>();
        foreach (var segment in segments)
        {
            if (joined.Count > 0 && !EndsSentence(joined[^1].Text) && segment.End - joined[^1].Start <= maximumSeconds) joined[^1] = joined[^1] with { End = segment.End, Text = joined[^1].Text.TrimEnd() + " " + segment.Text.TrimStart() };
            else joined.Add(segment);
        }
        return joined;
    }
    private static bool EndsSentence(string text) => text.TrimEnd().TrimEnd('"', '\'', '”', '’', ')', '»') is { Length: > 0 } trimmed && trimmed[^1] is '.' or '!' or '?' or '…';
}
