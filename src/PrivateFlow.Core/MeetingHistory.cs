using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

// Notes a model wrote for one meeting when the user pressed the button for it, kept with the meeting for later visits and exports.
public sealed class MeetingWrittenNotes
{
    public string Text { get; set; } = "";
    // Which model wrote them and where it ran, as MeetingNotesResult.Model gives it, such as "gpt-6-luna through the API".
    public string Model { get; set; } = "";
    public DateTimeOffset Written { get; set; }
    // The fingerprint of the transcript they were written from; edits or a new transcript afterwards make them stale.
    public string Transcript { get; set; } = "";
}

// The History page's view of the archive: search hits per meeting, how long a meeting lasted and which notes it shows.
// A stretch of the notes on the Notes tab: plain text, or a citation that leads to the transcript row with SegmentId.
public sealed record NotesPiece(string Text, string? SegmentId);

public static partial class MeetingHistory
{
    // The transcript rows that match the search in each meeting, by meeting identifier and in time order; terms match exactly as in
    // MeetingArchive.Search. Only the given meeting is searched when one is named. Empty when the search has no terms.
    public static IReadOnlyDictionary<string, IReadOnlyList<MeetingSegment>> HitsByMeeting(IEnumerable<MeetingDocument> documents, string query, string? meetingId = null) =>
        MeetingArchive.Search(documents.Where(document => meetingId is null || document.Id == meetingId), query, int.MaxValue)
            .GroupBy(hit => hit.MeetingId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<MeetingSegment>)group.Select(hit => hit.Segment).OrderBy(segment => segment.Start).ToArray());
    // From start to end for a meeting that ended; otherwise as far as its saved audio or transcript reaches.
    public static TimeSpan Duration(MeetingDocument document) =>
        document.Ended is { } ended && ended > document.Started ? ended - document.Started
            : TimeSpan.FromSeconds(document.Chunks.Select(chunk => chunk.Offset + chunk.Duration).Concat(document.Segments.Select(segment => segment.End)).DefaultIfEmpty(0).Max());
    // "Today 10:33 · 25 min", "Fri 14:02 · 1 h 05 min" or "3 Nov 2026 09:00 · under 1 min" in local time.
    public static string Describe(MeetingDocument document, DateTimeOffset now)
    {
        var started = document.Started.ToLocalTime(); var days = (now.ToLocalTime().Date - started.Date).Days;
        var day = days switch { 0 => "Today", 1 => "Yesterday", > 1 and < 7 => started.ToString("ddd", CultureInfo.InvariantCulture), _ => started.ToString("d MMM yyyy", CultureInfo.InvariantCulture) };
        var minutes = (int)Duration(document).TotalMinutes;
        var length = minutes < 1 ? "under 1 min" : minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
        return $"{day} {started:HH:mm} · {length}";
    }
    public static string TranscriptFingerprint(MeetingDocument document) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ModelNotes.Transcript(document))));
    public static bool WrittenNotesMatchTranscript(MeetingDocument document) =>
        document.WrittenNotes is { } notes && notes.Transcript == TranscriptFingerprint(document);
    // The rule-based notes as the Notes tab shows them: without their header, whose caveat the notice above them carries, and with the
    // lines before it, such as a channel that recorded no sound.
    public static string RuleNotesOnPage(MeetingDocument document)
    {
        var notes = MeetingArchive.Notes(document);
        var header = notes.IndexOf(ExtractiveNotes.Header, StringComparison.Ordinal);
        return header < 0 ? notes : notes[..header] + notes[(header + ExtractiveNotes.Header.Length)..].TrimStart();
    }
    // Notes a model wrote as the Notes tab shows them: the row above them says which model wrote them and when, so the opening that names
    // the model goes. The stored and exported notes keep it.
    public static string WrittenNotesOnPage(MeetingWrittenNotes notes)
    {
        var opening = notes.Text.IndexOf(ModelNotes.WrittenBy(notes.Model), StringComparison.Ordinal);
        return opening < 0 ? notes.Text : notes.Text.Remove(opening, ModelNotes.WrittenBy(notes.Model).Length);
    }
    // The notes as the Notes tab shows them: each citation of a transcript row reads as its time and speaker, "[04:30 · You]", and
    // keeps the row's identifier so the page can lead to it. The stored and exported notes keep the full citation with the meeting title.
    public static IReadOnlyList<NotesPiece> ReadableNotes(string notes)
    {
        var pieces = new List<NotesPiece>(); var position = 0;
        foreach (Match citation in CitationPattern().Matches(notes))
        {
            if (citation.Index > position) pieces.Add(new(notes[position..citation.Index], null));
            var time = TimeSpan.ParseExact(citation.Groups["time"].Value, @"hh\:mm\:ss", CultureInfo.InvariantCulture);
            var clock = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : time.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
            pieces.Add(new($"[{clock} · {citation.Groups["speaker"].Value}]", citation.Groups["id"].Value));
            position = citation.Index + citation.Length;
        }
        if (position < notes.Length) pieces.Add(new(notes[position..], null));
        return pieces;
    }
    // EvidenceHit.Citation: "[Title · 00:04:30 · Speaker · 32 hexadecimal digits]"; the title may itself contain " · ".
    [GeneratedRegex(@"\[(?<title>[^\[\]\n]*?) · (?<time>\d{2}:\d{2}:\d{2}) · (?<speaker>[^\[\]\n·]*?) · (?<id>[0-9a-f]{32})\]", RegexOptions.CultureInvariant)]
    private static partial Regex CitationPattern();
    // The notes the meeting shows: those a model wrote for it, otherwise the extractive notes.
    public static string Notes(MeetingDocument document) => document.WrittenNotes?.Text ?? MeetingArchive.Notes(document);
    public const string StaleNotes = "The transcript changed after these notes were written, so they may cite rows that no longer say this.";
    // An exported file has no page around it to say that the transcript changed since a model wrote the notes.
    public static string ExportedNotes(MeetingDocument document) =>
        document.WrittenNotes is not null && !WrittenNotesMatchTranscript(document) ? StaleNotes + "\n\n" + Notes(document) : Notes(document);
}
