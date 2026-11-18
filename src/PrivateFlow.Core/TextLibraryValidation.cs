namespace PrivateFlow.Core;

public static class TextLibraryValidation
{
    public static void Validate(IEnumerable<PhraseRule> corrections, IEnumerable<PhraseRule> snippets)
    {
        var rules = corrections.Concat(snippets).ToArray();
        if (rules.Length > 2000) throw new InvalidOperationException("Use at most 2,000 library entries.");
        foreach (var rule in rules)
            if (rule is null || string.IsNullOrWhiteSpace(rule.Original) || rule.Original.Length > 200 ||
                string.IsNullOrEmpty(rule.Replacement) || rule.Replacement.Length > 10000 || !SpeechLanguages.IsChoice(rule.Language) ||
                rule.Original.Contains("[[", StringComparison.Ordinal) || rule.Replacement.Contains("[[S", StringComparison.Ordinal) || rule.Replacement.Contains("[[Q", StringComparison.Ordinal))
                throw new InvalidOperationException("Each entry needs a spoken phrase (up to 200 characters), replacement text (up to 10,000), and language auto/en/de/es. Internal markers are reserved.");
        foreach (var group in rules.GroupBy(r => r.Original.Trim(), StringComparer.OrdinalIgnoreCase))
            if (group.Count() > 1 && (group.Any(r => r.Language == "auto") || group.GroupBy(r => r.Language).Any(g => g.Count() > 1)))
                throw new InvalidOperationException("A spoken phrase collides across corrections/snippets: " + group.Key);
    }
}
