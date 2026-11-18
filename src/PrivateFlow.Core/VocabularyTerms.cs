namespace PrivateFlow.Core;

// The shared names and terms as a list. Settings keep them as one string, joined as the single text field once took them,
// so a term cannot contain a separator.
public static class VocabularyTerms
{
    public static List<string> Split(string? vocabulary) => [.. (vocabulary ?? "").Split([',', ';', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];
    public static string Join(IEnumerable<string> terms) => string.Join(", ", terms);
    // The terms of a typed or pasted entry the list lacks, each once, and those it already has, both ignoring case.
    public static (List<string> Added, List<string> Present) Additions(IEnumerable<string> list, string entry)
    {
        var known = list.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> added = [], present = [];
        foreach (var term in Split(entry)) (known.Add(term) ? added : present).Add(term);
        return (added, present);
    }
}
