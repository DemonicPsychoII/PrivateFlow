namespace PrivateFlow.Core;

// Names and terms for a meeting's prompt from what the user already gave the app. The transcript itself is never rewritten with them.
public static class MeetingVocabulary
{
    public const string DefaultTitle = "Meeting";

    // Whisper keeps the end of a prompt that is too long, so the most specific source comes last: replacements of corrections,
    // the dictation vocabulary, the meeting title, the meeting's own vocabulary. A term given twice keeps its last place.
    public static string Prompt(string meetingVocabulary, string title, string dictationVocabulary, IEnumerable<PhraseRule> corrections)
    {
        var terms = corrections.Select(rule => rule.Replacement).Concat(VocabularyTerms.Split(dictationVocabulary))
            .Append(title.Trim() == DefaultTitle ? "" : title).Concat(VocabularyTerms.Split(meetingVocabulary)).Select(term => term.Trim()).Where(IsTerm).Reverse()
            .DistinctBy(term => term.ToLowerInvariant()).Reverse();
        return string.Join(", ", terms);
    }
    // Meetings once had their own vocabulary; its terms join the shared one after the terms already there, each once.
    public static string MergeIntoShared(string? sharedVocabulary, string? meetingVocabulary)
    {
        var known = VocabularyTerms.Split(sharedVocabulary).Select(term => term.ToLowerInvariant()).ToHashSet();
        var added = VocabularyTerms.Split(meetingVocabulary).Where(term => known.Add(term.ToLowerInvariant())).ToList();
        return added.Count == 0 ? (sharedVocabulary ?? "").Trim() : VocabularyTerms.Join(VocabularyTerms.Split(sharedVocabulary).Concat(added));
    }
    // A name or short term; snippets and sentences in the corrections library are not vocabulary.
    private static bool IsTerm(string term) => term.Length is > 0 and <= 40 && term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 && term.Any(char.IsLetter) && !term.Contains("[[");
}
