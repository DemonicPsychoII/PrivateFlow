using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

public static partial class RewriteIntegrity
{
    public static bool PreservesMeaning(string original, string rewritten, bool technical)
    {
        // A narrowly specified numerical self-correction can drop the superseded value.
        // General prose corrections are left to the rewriter; ambiguous facts fall back to Clean.
        var expected = NumericCorrection().Replace(original, m => m.Groups["new"].Value);
        // The "no" of a correction cue ("no wait, Maria", "wait, no, Maria") is no fact the speaker
        // states; an applied correction drops it with the cue. Without this, the guard rejected
        // every such correction and silently pasted the uncorrected Clean text. The exemption
        // holds only when the rewrite keeps no "no … wait" in any punctuation, so a kept cue cannot
        // stand in for a deleted content negation. "Wait, not X" gets no exemption: its "not" may
        // be the speaker's point.
        if (!Critical(expected).SequenceEqual(Critical(rewritten)) &&
            (KeptCue().IsMatch(rewritten) || !Critical(WithoutCueNo(expected)).SequenceEqual(Critical(rewritten)))) return false;
        if (!technical) return true;
        var spans = new List<string>();
        TechnicalSpans.Protect(expected, spans, '\ue600', '\ue601');
        return spans.All(span => rewritten.Contains(span, StringComparison.Ordinal));
    }

    private static IEnumerable<string> Critical(string text) => CriticalToken().Matches(text).Select(m => m.Value.ToLowerInvariant())
        .Concat(Numbers().Matches(text).Select(m => Regex.Replace(m.Value.Replace(',', '.'), @"\s+", "")))
        .OrderBy(s => s, StringComparer.Ordinal);
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:not|never|no|cannot|\p{L}+n['’]t|nicht|nie|niemals|kein\p{L}*|niemand\p{L}*|nichts|ohne|weder)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex CriticalToken();
    [GeneratedRegex(@"[+\-−]?\d+(?:[.,:]\d+)*(?:\s*(?:mAh|kWh|MHz|GHz|kHz|µm|µs|mm|cm|km|nm|mg|kg|ms|ns|ml|mV|mA|Ah|Hz|V|A|W|K|%)(?!\p{L}))?")]
    private static partial Regex Numbers();
    [GeneratedRegex(@"\b\d+(?:[.,:]\d+)*(?:\s*(?:am|pm|uhr|v|a|mm|ms))?\s*,?\s*(?:actually|no wait|wait,? no|sorry,? i meant|ich meine|nein)\s*,?\s*(?<new>\d+(?:[.,:]\d+)*(?:\s*(?:am|pm|uhr|v|a|mm|ms))?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumericCorrection();
    private static string WithoutCueNo(string text) => NoCue().Replace(text, m => m.Value.Remove(m.Groups["no"].Index - m.Index, 2));
    // Only an interjection that starts a clause and is followed by a replacement: "There is no
    // wait, so proceed.", "There is no wait time" and "Wait, no one came" are content.
    [GeneratedRegex(@"(?<=(?:^|[,.;:!?])\s*)(?:(?<no>no),?\s+wait|wait,?\s+(?<no>no))(?=\s*,\s*[\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex NoCue();
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:no[^\p{L}\p{N}]+wait|wait[^\p{L}\p{N}]+no)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex KeptCue();
}
