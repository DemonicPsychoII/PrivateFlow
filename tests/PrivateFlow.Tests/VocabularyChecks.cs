using PrivateFlow.Core;
using static Harness;

static class VocabularyChecks
{
    public static void VerifyVocabularyValidation()
    {
        var aliases = new[] { new PhraseRule { Original = "code x", Replacement = "Codex" } };
        var germanOnly = new[] { new PhraseRule { Original = "Hallo", Replacement = "Guten Tag", Language = "de" } };
        Same(new PreparedDictation("Hallo", germanOnly, [], "en", false, false).Text, "Hallo", "language-scoped dictionary does not leak into another language");
        var spanishOnly = new[] { new PhraseRule { Original = "hola", Replacement = "Buenos días", Language = "es" } };
        TextLibraryValidation.Validate(spanishOnly, []);
        Same(new PreparedDictation("hola", spanishOnly, [], "es", false, false).Text, "Buenos días", "Spanish-scoped dictionary applies to Spanish dictation");
        Same(new PreparedDictation("hola", spanishOnly, [], "de", false, false).Text, "hola", "Spanish-scoped dictionary does not leak into German");
        Reject(() => TextLibraryValidation.Validate([new() { Original = "bonjour", Replacement = "hello", Language = "fr" }], []), "library entries accept only offered languages");
        Reject(() => TextLibraryValidation.Validate(aliases, [new() { Original = "code x", Replacement = "collision" }]), "colliding alias and snippet require explicit resolution");
    }

    public static void VerifyVocabularyLibraryFile()
    {
        // The Vocabulary page's save, Import, Export and Learn correction rules, with the messages its cards show.
        string? Refusal(Action action) { try { action(); return null; } catch (InvalidOperationException exception) { return exception.Message; } }
        const string EntryRule = "Each entry needs a spoken phrase (up to 200 characters), replacement text (up to 10,000), and language auto/en/de/es. Internal markers are reserved.";
        var tooManyEntries = Enumerable.Range(0, 1001).Select(index => new PhraseRule { Original = "phrase " + index, Replacement = "text" }).ToList();
        Check(Refusal(() => TextLibraryValidation.Validate(tooManyEntries, tooManyEntries.Select(rule => new PhraseRule { Original = "snippet " + rule.Original, Replacement = "text" }))) == "Use at most 2,000 library entries."
            && Refusal(() => TextLibraryValidation.Validate(tooManyEntries.Take(1000), tooManyEntries.Take(1000).Select(rule => new PhraseRule { Original = "snippet " + rule.Original, Replacement = "text" }))) is null,
            "saving corrections and snippets allows 2,000 entries together and refuses more");
        foreach (var (entry, description) in new (PhraseRule Entry, string Description)[]
        {
            (new() { Original = "  ", Replacement = "text" }, "a blank spoken phrase"), (new() { Original = new string('a', 201), Replacement = "text" }, "a spoken phrase over 200 characters"),
            (new() { Original = "phrase", Replacement = "" }, "empty replacement text"), (new() { Original = "phrase", Replacement = new string('a', 10001) }, "replacement text over 10,000 characters"),
            (new() { Original = "phrase", Replacement = "text", Language = "fr" }, "a language other than auto/en/de/es"), (new() { Original = "a [[ marker", Replacement = "text" }, "an internal marker in the spoken phrase"),
            (new() { Original = "phrase", Replacement = "text [[S1]]" }, "a snippet marker in the replacement"), (new() { Original = "phrase", Replacement = "text [[Q1]]" }, "a quotation marker in the replacement"),
        })
            Check(Refusal(() => TextLibraryValidation.Validate([entry], [])) == EntryRule, "saving corrections and snippets refuses " + description + " and names the entry rule");
        Check(Refusal(() => TextLibraryValidation.Validate([new() { Original = new string('a', 200), Replacement = new string('b', 10000), Language = "es" }], [])) is null,
            "a spoken phrase of 200 characters and replacement text of 10,000 are allowed");
        Check(Refusal(() => TextLibraryValidation.Validate([new() { Original = "Code X", Replacement = "Codex", Language = "en" }], [new() { Original = " code x ", Replacement = "snippet", Language = "en" }])) == "A spoken phrase collides across corrections/snippets: Code X"
            && Refusal(() => TextLibraryValidation.Validate([new() { Original = "hola", Replacement = "Hello", Language = "auto" }, new() { Original = "Hola", Replacement = "Hallo", Language = "de" }], [])) == "A spoken phrase collides across corrections/snippets: hola"
            && Refusal(() => TextLibraryValidation.Validate([new() { Original = "hola", Replacement = "Hello", Language = "en" }, new() { Original = "Hola", Replacement = "Hallo", Language = "de" }], [])) is null,
            "a spoken phrase twice in one language or once as Automatic collides, named as trimmed, in any case; the same phrase in two languages does not");
        var libraryCorrections = new List<PhraseRule> { new() { Original = "code x", Replacement = "Codex", Language = "en" } };
        var librarySnippets = new List<PhraseRule> { new() { Original = "my address", Replacement = "Musterstraße 1\n12345 Musterstadt", Language = "auto" } };
        var exported = TextLibraryFile.Export(libraryCorrections, librarySnippets);
        var exportedBack = TextLibraryFile.Import(exported, [], []);
        Check(exported.Contains("\n") && exported.Contains("\"Corrections\"") && exported.Contains("\"Snippets\"")
            && exportedBack.Corrections is [{ Original: "code x", Replacement: "Codex", Language: "en" }] && exportedBack.Snippets is [{ Original: "my address", Replacement: "Musterstraße 1\n12345 Musterstadt", Language: "auto" }],
            "Export writes indented JSON with Corrections and Snippets that Import reads back unchanged");
        var importedFile = TextLibraryFile.Import("{\"Corrections\":[{\"Original\":\"pie torch\",\"Replacement\":\"PyTorch\",\"Language\":\"auto\"}]}", libraryCorrections, librarySnippets);
        Check(importedFile.Corrections is [{ Original: "pie torch", Replacement: "PyTorch" }] && importedFile.Snippets.Count == 0 && libraryCorrections.Count == 1 && librarySnippets.Count == 1,
            "Import returns only the file's entries for appending, with a missing list read as empty, and leaves the current entries alone");
        bool ImportThrowsJson(string json) { try { TextLibraryFile.Import(json, [], []); return false; } catch (System.Text.Json.JsonException) { return true; } }
        Check(ImportThrowsJson("{\"Corrections\":[{\"Original\":\"pie torch\"") && ImportThrowsJson("[1, 2]") && Refusal(() => TextLibraryFile.Import("null", [], [])) == "Not a file of corrections and snippets."
            && Refusal(() => TextLibraryFile.Import("{\"Corrections\":[{\"Original\":\"code x\",\"Replacement\":\"Code X\",\"Language\":\"en\"}]}", libraryCorrections, librarySnippets)) == "A spoken phrase collides across corrections/snippets: code x"
            && Refusal(() => TextLibraryFile.Import("{\"Snippets\":[{\"Original\":\"sig\",\"Replacement\":\"\"}]}", libraryCorrections, librarySnippets)) == EntryRule,
            "Import refuses a damaged file, a file that is not a library, and entries that collide with the current ones or break the entry rule");
        Check(Refusal(() => TextLibraryFile.CheckLength(TextLibraryFile.MaximumBytes + 1)) == "The file is too large." && Refusal(() => TextLibraryFile.CheckLength(TextLibraryFile.MaximumBytes)) is null,
            "Import accepts a file of up to 2 MiB and refuses a larger one");
        var loadedFromDictate = new PrivateFlow.Windows.DictationResult("Train it with pie torch.", "Train it with PyTorch.", null);
        Check(TextLibraryFile.LearnCorrection(loadedFromDictate.Original, loadedFromDictate.Final, "en", libraryCorrections, librarySnippets) is { Original: "Train it with pie torch.", Replacement: "Train it with PyTorch.", Language: "en" }
            && TextLibraryFile.LearnCorrection("  pie torch ", " PyTorch\n", "de", libraryCorrections, librarySnippets) is { Original: "pie torch", Replacement: "PyTorch", Language: "de" } && libraryCorrections.Count == 1,
            "Learn correction keeps the texts Load from Dictate filled in, or the reduced phrases, trimmed, in the dictation's language, without changing the current entries");
        Check(Refusal(() => TextLibraryFile.LearnCorrection("pie torch", " pie torch ", "en", libraryCorrections, librarySnippets)) == "Choose a changed phrase of at most 200 characters."
            && Refusal(() => TextLibraryFile.LearnCorrection(new string('a', 201), "b", "en", libraryCorrections, librarySnippets)) == "Choose a changed phrase of at most 200 characters."
            && Refusal(() => TextLibraryFile.LearnCorrection("  ", "PyTorch", "en", libraryCorrections, librarySnippets)) == EntryRule
            && Refusal(() => TextLibraryFile.LearnCorrection("Code X", "Codecs", "en", libraryCorrections, librarySnippets)) == "A spoken phrase collides across corrections/snippets: code x",
            "Learn correction refuses an unchanged or overlong phrase, a blank one, and one that collides with a current entry");
    }
}
