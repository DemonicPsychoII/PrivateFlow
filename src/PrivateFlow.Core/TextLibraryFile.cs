using System.Text.Json;

namespace PrivateFlow.Core;

// The JSON file Import reads and Export writes on the Vocabulary page: the corrections and the snippets.
public sealed record TextLibraryFile(List<PhraseRule> Corrections, List<PhraseRule> Snippets)
{
    public const long MaximumBytes = 2 * 1024 * 1024;
    public static void CheckLength(long bytes)
    {
        if (bytes > MaximumBytes) throw new InvalidOperationException("The file is too large.");
    }
    public static string Export(IEnumerable<PhraseRule> corrections, IEnumerable<PhraseRule> snippets) =>
        JsonSerializer.Serialize(new TextLibraryFile(corrections.ToList(), snippets.ToList()), new JsonSerializerOptions { WriteIndented = true });
    // The entries of the file, once they pass validation together with the current ones they are appended to.
    // Throws JsonException for a damaged file and InvalidOperationException for one that is not valid.
    public static TextLibraryFile Import(string json, IEnumerable<PhraseRule> corrections, IEnumerable<PhraseRule> snippets)
    {
        var file = JsonSerializer.Deserialize<TextLibraryFile>(json) ?? throw new InvalidOperationException("Not a file of corrections and snippets.");
        var imported = new TextLibraryFile(file.Corrections ?? [], file.Snippets ?? []);
        TextLibraryValidation.Validate(corrections.Concat(imported.Corrections).ToList(), snippets.Concat(imported.Snippets).ToList());
        return imported;
    }
    // A correction taught from the Dictate page: the trimmed mistaken and corrected phrases in the language of that dictation.
    public static PhraseRule LearnCorrection(string mistaken, string corrected, string language, IEnumerable<PhraseRule> corrections, IEnumerable<PhraseRule> snippets)
    {
        var rule = new PhraseRule { Original = mistaken.Trim(), Replacement = corrected.Trim(), Language = language };
        if (rule.Original.Length > 200 || rule.Original == rule.Replacement) throw new InvalidOperationException("Choose a changed phrase of at most 200 characters.");
        TextLibraryValidation.Validate(corrections.Append(rule).ToList(), snippets);
        return rule;
    }
}
