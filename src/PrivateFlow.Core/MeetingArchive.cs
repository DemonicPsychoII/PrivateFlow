using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateFlow.Core;

public sealed class MeetingDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = MeetingVocabulary.DefaultTitle;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? Ended { get; set; }
    public string Status { get; set; } = "Recording";
    public bool RetainAudio { get; set; }
    // The languages the final pass chooses among for each stretch of speech, comma-separated; one fixes the language.
    public string Languages { get; set; } = SpeechLanguages.Offered;
    // Names and terms said in the meeting, given to Whisper as its prompt like the dictation vocabulary.
    public string Vocabulary { get; set; } = "";
    // The Windows names of the capture device and of the playback device whose output was recorded; empty when not recorded.
    public string MicrophoneDevice { get; set; } = "";
    public string PlaybackDevice { get; set; } = "";
    // Why the meeting ended unfinished, one of the MeetingArchive reasons; empty once it is complete and for meetings saved before it was kept.
    public string UnfinishedReason { get; set; } = "";
    // The model that produced the final transcript, and why it is not the meeting model when the pass fell back to the speech model.
    // Empty before the transcript is final and in meetings from earlier versions.
    public string TranscriptModel { get; set; } = "";
    public string ModelFallback { get; set; } = "";
    // Null until a model writes notes for this meeting, and in meetings saved before such notes were kept.
    public MeetingWrittenNotes? WrittenNotes { get; set; }
    public List<MeetingChunk> Chunks { get; set; } = [];
    public List<MeetingSegment> Segments { get; set; } = [];
    public override string ToString() => $"{Started.LocalDateTime:yyyy-MM-dd HH:mm} — {Title} ({Status})";
}
public sealed class MeetingChunk
{
    public string File { get; set; } = "";
    public string Channel { get; set; } = "";
    public double Offset { get; set; }
    public double Duration { get; set; }
    public bool Transcribed { get; set; }
    // The largest absolute sample, 0 to 1; null for chunks saved before it was recorded.
    public double? Peak { get; set; }
}
public sealed class MeetingSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public double Start { get; set; }
    public double End { get; set; }
    public string Speaker { get; set; } = "Unknown";
    public string Channel { get; set; } = "";
    public string Text { get; set; } = "";
}
public sealed record EvidenceHit(string MeetingId, string MeetingTitle, MeetingSegment Segment, int Score)
{
    public string Citation => $"[{MeetingTitle} · {TimeSpan.FromSeconds(Segment.Start):hh\\:mm\\:ss} · {Segment.Speaker} · {Segment.Id}]";
}

public sealed partial class MeetingArchive
{
    public string Root { get; }
    public MeetingArchive(string root) { Root = Path.GetFullPath(root); Directory.CreateDirectory(Root); EnsurePlain(Root); }
    public string DirectoryFor(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidOperationException("Invalid meeting identifier.");
        EnsurePlain(Root);
        var path = Path.Combine(Root, id);
        if (Directory.Exists(path)) EnsurePlain(path);
        return path;
    }
    public string ChunkPath(string id, string name)
    {
        if (!Regex.IsMatch(name, @"^(mic|system)-[0-9]{6}\.wav$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) throw new InvalidOperationException("Invalid audio chunk name.");
        var path = Path.Combine(DirectoryFor(id), name);
        if (File.Exists(path)) EnsurePlain(path);
        return path;
    }
    public void Save(MeetingDocument document)
    {
        var directory = DirectoryFor(document.Id); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "meeting.json");
        if (File.Exists(file)) EnsurePlain(file);
        var partial = file + ".partial"; if (File.Exists(partial)) EnsurePlain(partial);
        using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, document, new JsonSerializerOptions { WriteIndented = true }); stream.Flush(true); }
        File.Move(partial, file, true);
    }
    public MeetingDocument Load(string id)
    {
        var file = Path.Combine(DirectoryFor(id), "meeting.json"); EnsurePlain(file);
        if (new FileInfo(file).Length > 64 * 1024 * 1024) throw new InvalidOperationException("Meeting manifest is too large.");
        var document = JsonSerializer.Deserialize<MeetingDocument>(File.ReadAllText(file)) ?? throw new InvalidOperationException("Invalid meeting manifest.");
        if (document.Id != id || document.Chunks is null || document.Segments is null) throw new InvalidOperationException("Invalid meeting manifest.");
        foreach (var chunk in document.Chunks)
            if (chunk is null || chunk.File is null) throw new InvalidOperationException("Invalid chunk.");
            else if (!double.IsFinite(chunk.Offset) || !double.IsFinite(chunk.Duration) || chunk.Offset < 0 || chunk.Duration < 0) throw new InvalidOperationException("Invalid chunk timing.");
            else ChunkPath(id, chunk.File);
        foreach (var segment in document.Segments)
            if (segment is null || !double.IsFinite(segment.Start) || !double.IsFinite(segment.End) || segment.Start < 0 || segment.End < segment.Start || segment.Text is null || segment.Speaker is null || !Guid.TryParseExact(segment.Id, "N", out _)) throw new InvalidOperationException("Invalid segment.");
        // Damaged written notes fall back to the extractive notes instead of hiding the whole meeting.
        if (document.WrittenNotes is { } notes && (notes.Text is null || notes.Model is null || notes.Transcript is null)) document.WrittenNotes = null;
        return document;
    }
    public List<MeetingDocument> List()
    {
        var documents = new List<MeetingDocument>();
        foreach (var directory in Directory.EnumerateDirectories(Root))
        {
            var id = Path.GetFileName(directory);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try { documents.Add(Load(id)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        return documents.OrderByDescending(d => d.Started).ToList();
    }
    public void Delete(string id)
    {
        var directory = DirectoryFor(id); if (!Directory.Exists(directory)) return;
        EnsurePlain(directory);
        // Inspect the entire app-owned tree before recursive deletion. Never follow a junction.
        VerifyTree(directory); Directory.Delete(directory, true);
    }
    // Meetings whose transcript is not final and whose audio is still on disk, so they can be finished; except the one recording now.
    public IReadOnlyList<(MeetingDocument Document, long AudioBytes)> Unfinished(string? recordingId = null) =>
        List().Where(document => document.Status != "Complete" && document.Id != recordingId).Select(document => (Document: document, AudioBytes: AudioBytes(document.Id)))
            .Where(found => found.AudioBytes > 0).ToArray();
    public const string StoppedToFinishLater = "Stopped by you with Stop; finish later.";
    public const string FinishingCancelled = "Finishing was cancelled by you.";
    public const string QuitBeforeFinished = "Private Flow was quit before the transcript was finished.";
    public static string FinalTranscriptFailed(string message) => "The final transcript failed: " + message;
    public static string CaptureStopped(string error) => $"The capture stopped on its own ({error}).";
    // A meeting still Recording or Finalizing when it is found unfinished was ended by a crash or by Windows, not by Private Flow.
    public static string DescribeUnfinished(MeetingDocument document) => document + (UnfinishedReasonText(document) is { Length: > 0 } reason ? " · " + reason : "");
    public static string UnfinishedReasonText(MeetingDocument document) => document.Status switch
    {
        "Recording" => "Private Flow ended unexpectedly while recording it, such as in a crash.",
        "Finalizing" => "Private Flow ended unexpectedly while finishing its transcript, such as in a crash.",
        _ => document.UnfinishedReason ?? ""
    };
    public long AudioBytes(string id) => AudioFiles(id).Sum(file => new FileInfo(file).Length);
    // Every audio file of the meeting: listed and unlisted chunks and chunk writes a crash left unfinished.
    public void DeleteAudio(string id) { foreach (var file in AudioFiles(id)) File.Delete(file); }
    private IEnumerable<string> AudioFiles(string id)
    {
        var directory = DirectoryFor(id);
        if (!Directory.Exists(directory)) return [];
        var files = Directory.EnumerateFiles(directory).Where(file => AudioName.IsMatch(Path.GetFileName(file))).ToArray();
        foreach (var file in files) EnsurePlain(file);
        return files;
    }
    private static readonly Regex AudioName = new(@"^(mic|system)-[0-9]{6}\.wav(\.partial)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // Audio an earlier run left in meetings whose transcript is final and that do not retain audio, such as after a crash between
    // saving the transcript and deleting the audio. Unfinished meetings keep theirs until they are finished or the user deletes it.
    public int DeleteLeftoverAudio()
    {
        var deleted = 0;
        foreach (var document in List().Where(document => document.Status == "Complete" && !document.RetainAudio))
        {
            var files = AudioFiles(document.Id).ToArray();
            foreach (var file in files) File.Delete(file);
            deleted += files.Length;
        }
        return deleted;
    }
    // A channel that delivered no sound for the whole meeting: every saved chunk below the silence level, or no chunk at all from a
    // device that was recorded (system audio sends nothing while nothing plays). Unknown for meetings saved before levels were kept.
    public static IReadOnlyList<string> SilentChannels(MeetingDocument document, double threshold = SilenceWatch.Threshold) =>
        new[] { (Channel: "mic", Device: document.MicrophoneDevice), (Channel: "system", Device: document.PlaybackDevice) }
            .Where(recorded => recorded.Device.Length > 0 && document.Chunks.Where(chunk => chunk.Channel == recorded.Channel).All(chunk => chunk.Peak is { } peak && peak < threshold))
            .Select(recorded => recorded.Channel).ToArray();
    public static string DescribeSilence(MeetingDocument document, string channel) => channel == "mic"
        ? $"The microphone “{document.MicrophoneDevice}” delivered no sound during the whole meeting, so your side is not in the transcript."
        : $"The system audio of “{document.PlaybackDevice}” delivered no sound during the whole meeting, so the other side is not in the transcript.";
    public void RecoverUnlistedChunks(MeetingDocument document)
    {
        var directory = DirectoryFor(document.Id);
        // A chunk whose write completed but whose rename a crash prevented; its header then counts the whole file.
        foreach (var partial in Directory.EnumerateFiles(directory, "*.wav.partial"))
        {
            var file = partial[..^".partial".Length];
            if (!AudioName.IsMatch(Path.GetFileName(partial)) || File.Exists(file)) continue;
            EnsurePlain(partial);
            if (CompleteWave(partial)) File.Move(partial, file);
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.wav").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (document.Chunks.Any(c => c.File == name)) continue;
            var match = Regex.Match(name, @"^(mic|system)-([0-9]{6})\.wav$");
            if (!match.Success) continue;
            ChunkPath(document.Id, name);
            var channel = match.Groups[1].Value;
            var number = int.Parse(match.Groups[2].Value);
            static int Number(MeetingChunk chunk) => int.Parse(chunk.File.AsSpan(chunk.Channel.Length + 1, 6));
            var previous = document.Chunks.Where(c => c.Channel == channel && Number(c) < number).MaxBy(Number);
            var length = new FileInfo(file).Length;
            if (length < 44) continue;
            // Capture writes mono 16-bit WAV chunks of at most 20 s; exact timestamps remain in the manifest
            // when available. This closes the crash window between rename and manifest save.
            var offset = previous is null ? number * 20.0 : previous.Offset + previous.Duration + (number - Number(previous) - 1) * 20.0;
            document.Chunks.Add(new() { File = name, Channel = channel, Offset = offset, Duration = (length - 44) / 32000.0 });
        }
        Save(document);
    }
    private static bool CompleteWave(string file)
    {
        using var stream = File.OpenRead(file);
        var header = new byte[12];
        if (stream.Read(header, 0, header.Length) != header.Length) return false;
        return Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && Encoding.ASCII.GetString(header, 8, 4) == "WAVE" && BitConverter.ToUInt32(header, 4) == stream.Length - 8;
    }
    private static void VerifyTree(string directory)
    { foreach (var path in Directory.EnumerateFileSystemEntries(directory)) { EnsurePlain(path); if (Directory.Exists(path)) VerifyTree(path); } }
    private static void EnsurePlain(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Meeting archive cannot follow links or junctions."); }
    public IReadOnlyList<MeetingDocument> Expired(int days, DateTimeOffset now) =>
        List().Where(m => m.Ended is { } ended && ended < now.AddDays(-Math.Clamp(days, 1, 3650)) && m.Status is "Complete").ToArray();
    public int DeleteExpired(int days, DateTimeOffset now)
    {
        var expired = Expired(days, now);
        foreach (var document in expired) Delete(document.Id);
        return expired.Count;
    }
    public void Export(string id, string destination)
    {
        var target = Path.GetFullPath(destination);
        if (target.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Export backups outside the live meeting archive.");
        var directory = DirectoryFor(id); VerifyTree(directory);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            ZipFile.CreateFromDirectory(directory, temporary, CompressionLevel.Optimal, false);
            File.Move(temporary, target, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static string ExportText(MeetingDocument document) => string.Join("\n", document.Segments.OrderBy(s => s.Start).Select(s => $"[{TimeSpan.FromSeconds(s.Start):hh\\:mm\\:ss}] {s.Speaker}: {s.Text}"));
    public static string ExportSrt(IEnumerable<TranscriptSegment> segments)
    {
        var builder = new StringBuilder(); var index = 1;
        foreach (var segment in segments)
        {
            static string Time(double value) => TimeSpan.FromSeconds(Math.Max(0, value)).ToString(@"hh\:mm\:ss\,fff");
            builder.AppendLine((index++).ToString()).AppendLine($"{Time(segment.Start)} --> {Time(Math.Max(segment.Start, segment.End))}").AppendLine(segment.Text).AppendLine();
        }
        return builder.ToString();
    }
    public static IReadOnlyList<EvidenceHit> Search(IEnumerable<MeetingDocument> documents, string question, int maximum = 12)
    {
        // Words compare as the accuracy scoring compares them, so "twelve thirty-four" finds "12:34" and "1234"; adjacent numbers
        // of the question form one term.
        var terms = new List<string>(); var afterNumber = false;
        var searchWords = TranscriptScoring.SearchWords(question);
        var asked = searchWords.Select(word => word.Text).ToArray();
        bool Number(int index) => index >= 0 && index < asked.Length && asked[index].All(char.IsAsciiDigit);
        // Only a pair of numbers is a time; "ocho siete tres" stays 873.
        for (var index = 1; index < asked.Length; index++)
            if (searchWords[index].Minutes && !Number(index - 2) && !Number(index + 1)) asked[index] = "0" + asked[index];
        foreach (var word in asked)
        {
            var number = word.All(char.IsAsciiDigit);
            if (number && afterNumber) terms[^1] += word;
            else if (!StopWords.Contains(word) && (number || word.Length >= 2)) terms.Add(word);
            afterNumber = number;
        }
        if (terms.Count == 0) return [];
        return documents.SelectMany(d => d.Segments.Select(s => (Document: d, Segment: s, Words: TranscriptScoring.SearchWords(s.Text))))
            .Select(found => new EvidenceHit(found.Document.Id, found.Document.Title, found.Segment, terms.Distinct().Count(term => Says(found.Words, term))))
            .Where(hit => hit.Score > 0).OrderByDescending(hit => hit.Score).ThenBy(hit => hit.Segment.Start).Take(maximum).ToArray();
    }
    // Words in a row spell the term: "9am" in "9 a.m.", "1234" in "12:34" (several words only with a number among them, so
    // "together" is not in "to get her"); or the term is the number a word starts with: "40" in "40mm".
    // Minutes below ten are written without their zero by the search words ("12:05" is "12", "5"), so minutes after an hour
    // read as "05": "12:05", "1205", "twelve oh five" and "doce y cinco" then spell the same term.
    private static bool Says(IReadOnlyList<(string Text, bool Minutes)> words, string term)
    {
        var number = term.All(char.IsAsciiDigit);
        for (var first = 0; first < words.Count; first++)
        {
            if (number && words[first].Text.Length > term.Length && words[first].Text.StartsWith(term, StringComparison.Ordinal) && char.IsLetter(words[first].Text[term.Length])) return true;
            var spelled = ""; var hasNumber = false;
            for (var last = first; last < words.Count; last++)
            {
                var word = last == first + 1 && words[last].Minutes ? "0" + words[last].Text : words[last].Text;
                if (!term.StartsWith(spelled + word, StringComparison.Ordinal) && !term.StartsWith(spelled + (word = words[last].Text), StringComparison.Ordinal)) break;
                spelled += word; hasNumber |= word.All(char.IsAsciiDigit);
                if (spelled.Length == term.Length && (last == first || hasNumber)) return true;
            }
        }
        return false;
    }
    // Shown in yellow with extractive notes; the notes text carries the same caveat (ExtractiveNotes.Header), so exports keep it.
    public const string ExtractiveNoticeText = "Notes built by rules on this PC, without a model: listed action items and times are usually right, but some are missing; decisions and open questions are only candidates and most are missing. Useful notes need an API connection set up under Settings, or the meeting notes model installed on this PC.";
    public static string? ExtractiveNotice(MeetingDocument document) => document.Segments.Count == 0 ? null : ExtractiveNoticeText;
    public static string Notes(MeetingDocument document) => ExtractiveNotes.Write(document);
    // What a reader must know before the notes: that the speech model stood in for the meeting model, and channels that stayed silent.
    public static string Caveats(MeetingDocument document) => (string.IsNullOrEmpty(document.ModelFallback) ? "" : document.ModelFallback + "\n\n") +
        string.Concat(SilentChannels(document).Select(channel => DescribeSilence(document, channel) + "\n\n"));
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal) { "what", "when", "where", "who", "why", "how", "the", "was", "were", "is", "are", "did", "do", "we", "in", "on", "of", "to", "about", "meeting", "meetings", "and", "or", "a", "an", "der", "die", "das", "was", "wer", "wie", "wann", "wo", "ist", "sind", "haben", "wir", "und", "zu", "den", "dem", "ein", "eine", "über" };
}
