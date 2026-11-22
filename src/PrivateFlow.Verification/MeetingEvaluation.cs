using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.Wave;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// A scenario describes a meeting as clips placed on the mic and system channels, each with a
// reference text whose critical items are marked in square brackets.
internal sealed class EvaluationScenario
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public double Seconds { get; set; }
    // RMS in dBFS of the room noise added to the mic channel.
    public double NoiseFloor { get; set; } = -70;
    public List<EvaluationUtterance> Utterances { get; set; } = [];
    public List<EvaluationBackground> Backgrounds { get; set; } = [];
}
internal sealed class EvaluationUtterance
{
    public string Channel { get; set; } = "mic";
    public string Speaker { get; set; } = "";
    public string Language { get; set; } = "";
    public string Text { get; set; } = "";
    public string Clip { get; set; } = "";
    public double? At { get; set; }
    public double? Around { get; set; }
    // Seconds after the previous utterance on the same timeline ends; negative overlaps it.
    public double Gap { get; set; } = 0.6;
    // Utterances on one timeline follow each other whatever channel they are on, as people do when some of them come in over the
    // system channel; without one, each channel is its own timeline.
    public string? Timeline { get; set; }
    // Level in dB relative to speech at -20 dBFS active RMS.
    public double Gain { get; set; }
    public List<string> Tags { get; set; } = [];
}
internal sealed class EvaluationBackground
{
    public string Channel { get; set; } = "mic";
    public string? Clip { get; set; }
    public bool Music { get; set; }
    // Copies another channel's mix, as a microphone hears the loudspeakers.
    public string? Source { get; set; }
    public double Gain { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
}
internal sealed record PlacedUtterance(EvaluationUtterance Utterance, double Start, double End);
internal sealed record WindowReport(string Channel, int Index, double Start, double Seconds, string Detected, IReadOnlyList<string> Spoken, bool WrongLanguage, double ProcessingSeconds, string Text,
    IReadOnlyDictionary<string, double>? Probabilities = null);
internal sealed record ErrorReport(int ReferenceWords, int Substitutions, int Deletions, int Insertions, int FormattingOnly, double WordErrorRate, double WordRecall, double RecallIgnoringFormatting)
{
    public static ErrorReport From(WordErrors errors) => new(errors.ReferenceWords, errors.Substitutions, errors.Deletions, errors.Insertions, errors.FormattingOnly,
        Math.Round(errors.Rate, 4), Math.Round(errors.Recall, 4), Math.Round(errors.RecallIgnoringFormatting, 4));
}
internal sealed record GroupReport(string Name, ErrorReport Words, int CriticalItems, int CriticalRecalled, int CriticalFormattingOnly = 0);
internal sealed record MissedItem(string Channel, string Item, bool FormattingOnly, string Speaker, double Start, IReadOnlyList<string> Tags, string Utterance, string HeardForItem, string HeardForUtterance);
internal sealed record UtteranceReport(string Channel, string Speaker, string Language, double Start, double End, IReadOnlyList<string> Tags, double WordRecall, string Reference, string Heard,
    IReadOnlyList<string> Critical);
// Segments are the transcript rows as saved, with their times.
internal sealed record ChannelReport(string Channel, ErrorReport Words, int CriticalItems, int CriticalRecalled, string Transcript, IReadOnlyList<string> Segments);
// A time or number the reference marks as critical, searched in the saved meeting the way a user would type it. Produced: the
// recognizer wrote it, in any form. Written: in the reference's written form ("12:34", not "1234"). Found: every query returns a
// segment at its place and, for a time, the notes list it there.
internal sealed record FindabilityReport(string Channel, string Item, string Kind, string Speaker, double Start, IReadOnlyList<string> Tags, bool Produced,
    IReadOnlyList<string> Queries, IReadOnlyList<string> NotFoundBy, bool InNotes, bool Found, bool Written);
// An owner's recording: counts only, never text.
internal sealed record RecordingReport(string Name, string Scoring, int Chunks, string Status, string? Failure, double FinishSeconds, bool ProgressReached100, int AudioFilesLeft,
    IReadOnlyDictionary<string, int> Segments, IReadOnlyDictionary<string, int> PinnedSegments, ErrorReport? Words, int CriticalItems, int CriticalRecalled);
internal sealed record PreviewReport(ErrorReport Words, int CriticalItems, int CriticalRecalled, int WrongLanguageWindows, double ProcessingSeconds, double LongestWindowSeconds);
internal sealed record ScenarioReport(string Name, string Description, double Seconds, double ProcessingSeconds, double RealTimeFactor, int ClippedSamples,
    ErrorReport Words, int CriticalItems, int CriticalRecalled, int CriticalFormattingOnly, int WrongLanguageWindows,
    IReadOnlyList<ChannelReport> Channels, IReadOnlyList<GroupReport> Languages, IReadOnlyList<GroupReport> Tags, IReadOnlyList<MissedItem> Missed,
    IReadOnlyList<WindowReport> Windows, IReadOnlyList<UtteranceReport> Utterances)
{
    public string Mode { get; init; } = "final";
    public PreviewReport? Preview { get; init; }
    public IReadOnlyList<FindabilityReport> Findability { get; init; } = [];
}
// Mode "final" scores what the app keeps: the live preview, then the final pass. "oracle" transcribes every reference utterance
// alone, cut at its known start and end, in its known language: the ceiling of the recognizer for perfect segmentation and language choice.
internal sealed class EvaluationOptions
{
    public string Mode { get; set; } = "final";
    // The meeting's title, given to Whisper with the vocabulary.
    public string Title { get; set; } = MeetingVocabulary.DefaultTitle;
}

internal static class MeetingEvaluation
{
    private const int Rate = 16000;
    private const double SpeechLevel = 0.1;

    public static async Task RunAsync(string scenariosFolder, string reportFile, string? optionsFile = null)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            var evaluation = optionsFile is null ? new EvaluationOptions() : JsonSerializer.Deserialize<EvaluationOptions>(await File.ReadAllTextAsync(optionsFile)) ?? new EvaluationOptions();
            var settings = Settings.Load().Snapshot(); settings.OutputStyle = OutputStyle.Clean;
            var loading = Stopwatch.StartNew();
            await WhisperEngine.TranscribeChunkAsync(new float[Rate], settings, CancellationToken.None);
            var modelLoadSeconds = loading.Elapsed.TotalSeconds;
            var archive = new MeetingArchive(Path.Combine(Settings.Root, "evaluation-meetings"));
            var reports = new List<ScenarioReport>();
            foreach (var file in Directory.EnumerateFiles(scenariosFolder, "scenario.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var scenario = JsonSerializer.Deserialize<EvaluationScenario>(await File.ReadAllTextAsync(file)) ?? throw new InvalidDataException(file);
                reports.Add(await EvaluateAsync(scenario, Path.GetDirectoryName(file)!, archive, settings, evaluation));
            }
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new
            {
                Passed = true, Created = DateTimeOffset.Now, Model = Path.GetFileName(settings.ModelPath), MeetingModel = Path.GetFileName(settings.MeetingModelPath),
                Backend = settings.EnginePath.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "CUDA" : "CPU",
                settings.Language, settings.SpeechDetection, ModelLoadSeconds = Math.Round(modelLoadSeconds, 2), evaluation.Mode, evaluation.Title, DictationVocabulary = settings.Vocabulary,
                Corrections = settings.Corrections.Select(rule => $"{rule.Original}={rule.Replacement}").ToArray(),
                Prompt = MeetingVocabulary.Prompt("", evaluation.Title, settings.Vocabulary, settings.Corrections), Scenarios = reports
            }, options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { Passed = false, Error = ex.ToString() }, options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    // The owner's own recordings, each a folder with "meeting" (a meeting folder as the app left it: meeting.json and its chunks) and
    // either "reference.txt" (one utterance per line, critical items in square brackets) or "final-pass-*.meeting.json" (a transcript
    // an earlier build produced). A temporary copy is finished as Finish transcripts does. The report holds counts only: these are
    // private conversations, so no transcript text leaves the copy, which is deleted afterwards.
    public static async Task RecordingsAsync(string recordingsRoot, string reportFile)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            var settings = Settings.Load().Snapshot(); settings.OutputStyle = OutputStyle.Clean;
            var archive = new MeetingArchive(Path.Combine(Settings.Root, "evaluation-recordings"));
            var reports = new List<RecordingReport>();
            foreach (var folder in Directory.EnumerateDirectories(recordingsRoot).Order(StringComparer.Ordinal))
            {
                var manifest = Path.Combine(folder, "meeting", "meeting.json");
                if (!File.Exists(manifest)) continue;
                var id = JsonSerializer.Deserialize<MeetingDocument>(await File.ReadAllTextAsync(manifest))?.Id ?? throw new InvalidDataException("Recording without a meeting id.");
                var copy = archive.DirectoryFor(id);
                if (Directory.Exists(copy)) archive.Delete(id);
                Directory.CreateDirectory(copy);
                foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "meeting")))
                    if (Path.GetFileName(file) == "meeting.json" || Path.GetExtension(file) == ".wav") File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
                try
                {
                    var document = archive.Load(id); document.RetainAudio = false; archive.Save(document);
                    var chunks = document.Chunks.Count; var highest = 0.0;
                    var progress = new ProgressCallback<MeetingProgress>(step => { lock (reports) highest = Math.Max(highest, step.Share); });
                    var watch = Stopwatch.StartNew(); string? failure = null;
                    try { await Task.Run(() => MeetingCapture.RetryAsync(archive, document, settings, CancellationToken.None, progress)); }
                    catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                    watch.Stop();
                    var finished = archive.Load(id);
                    var audioLeft = Directory.EnumerateFiles(copy).Count(file => file.EndsWith(".wav", StringComparison.Ordinal) || file.EndsWith(".partial", StringComparison.Ordinal));
                    var reference = Path.Combine(folder, "reference.txt");
                    var pinned = Directory.EnumerateFiles(folder, "final-pass-*.meeting.json").OrderBy(File.GetLastWriteTimeUtc).LastOrDefault();
                    var segments = finished.Segments.OrderBy(segment => segment.Start).Select(segment => new TranscriptSegment(segment.Start, segment.End, segment.Text)).ToArray();
                    TranscriptScore? score = null; var kind = "none"; var pinnedSegments = new Dictionary<string, int>();
                    if (File.Exists(reference))
                    {
                        kind = "accuracy against reference.txt";
                        score = TranscriptScoring.Score((await File.ReadAllLinesAsync(reference)).Where(line => line.Trim().Length > 0).ToArray(), segments);
                    }
                    else if (pinned is not null)
                    {
                        kind = $"agreement with pinned transcript {Path.GetFileName(pinned)} (not accuracy)";
                        var earlier = JsonSerializer.Deserialize<MeetingDocument>(await File.ReadAllTextAsync(pinned)) ?? throw new InvalidDataException("Unreadable pinned transcript.");
                        score = TranscriptScoring.Score(earlier.Segments.OrderBy(segment => segment.Start).Select(segment => segment.Text).ToArray(), segments);
                        pinnedSegments = earlier.Segments.GroupBy(segment => segment.Channel).ToDictionary(group => group.Key, group => group.Count());
                    }
                    reports.Add(new(Path.GetFileName(folder), kind, chunks, finished.Status, failure, Math.Round(watch.Elapsed.TotalSeconds, 1), highest >= 1, audioLeft,
                        finished.Segments.GroupBy(segment => segment.Channel).ToDictionary(group => group.Key, group => group.Count()), pinnedSegments,
                        score is null ? null : ErrorReport.From(score.Errors()), score?.CriticalItems.Count ?? 0, score?.CriticalItems.Count(item => item.Recalled) ?? 0));
                }
                finally { archive.Delete(id); }
            }
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { Passed = true, Created = DateTimeOffset.Now, Model = Path.GetFileName(settings.ModelPath),
                MeetingModel = Path.GetFileName(settings.MeetingModelPath), Recordings = reports }, options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(reportFile, JsonSerializer.Serialize(new { Passed = false, Error = ex.GetType().Name + ": " + ex.Message }, options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private static async Task<ScenarioReport> EvaluateAsync(EvaluationScenario scenario, string folder, MeetingArchive archive, Settings settings, EvaluationOptions evaluation)
    {
        var (channels, placed, clipped) = Compose(scenario, folder);
        var document = new MeetingDocument { Title = evaluation.Title, Status = "Recording", Languages = settings.Language != "auto" ? settings.Language : SpeechLanguages.Offered };
        archive.Save(document);
        var windows = new List<WindowReport>(); var processing = 0.0;
        try
        {
            // The oracle uses the model the final pass would use, so the two compare on the same recognizer.
            if (evaluation.Mode == "oracle")
                return (await MeetingTranscriber.WithFinalModelAsync(settings, (session, _) => OracleAsync(scenario, placed, channels, document, session, clipped), CancellationToken.None)).Result;
            // The same cuts, chunk files, transcription call and Append as a live meeting.
            var chunks = new List<(MeetingChunk Chunk, int Index)>();
            foreach (var (channel, samples) in channels)
            {
                var cuts = SpeechSegmentation.RecordingWindows(samples, AudioRecorder.ChunkSamples, AudioRecorder.CutSearchSamples);
                for (var index = 0; index < cuts.Count; index++)
                {
                    var chunk = new MeetingChunk { File = $"{channel}-{index:D6}.wav", Channel = channel, Offset = cuts[index].Start / (double)Rate, Duration = cuts[index].Length / (double)Rate };
                    using (var writer = new WaveFileWriter(archive.ChunkPath(document.Id, chunk.File), new WaveFormat(Rate, 16, 1))) writer.WriteSamples(samples, cuts[index].Start, cuts[index].Length);
                    document.Chunks.Add(chunk); chunks.Add((chunk, index));
                }
            }
            archive.Save(document);
            foreach (var (chunk, index) in chunks.OrderBy(entry => entry.Chunk.Offset).ThenBy(entry => entry.Chunk.Channel == "mic" ? 0 : 1))
            {
                var watch = Stopwatch.StartNew();
                var result = await MeetingCapture.TranscribeChunkAsync(archive, document, chunk, settings, CancellationToken.None);
                watch.Stop(); processing += watch.Elapsed.TotalSeconds;
                var before = document.Segments.Select(segment => segment.Id).ToHashSet();
                MeetingCapture.Append(document, chunk, result); archive.Save(document);
                var added = document.Segments.Where(segment => !before.Contains(segment.Id)).Select(segment => segment.Text);
                windows.Add(Window(placed, chunk.Channel, index, chunk.Offset, chunk.Duration, result.Language, watch.Elapsed.TotalSeconds, string.Join(" ", added)));
            }
            var previewScore = Score(scenario, placed, channels, document, windows, processing, clipped);
            var preview = new PreviewReport(previewScore.Words, previewScore.CriticalItems, previewScore.CriticalRecalled, previewScore.WrongLanguageWindows, Math.Round(processing, 2),
                windows.Select(window => window.ProcessingSeconds).DefaultIfEmpty(0).Max());
            var final = Stopwatch.StartNew();
            var runs = await MeetingTranscriber.FinalizeAsync(archive, document, settings, null, CancellationToken.None);
            final.Stop();
            var runWindows = runs.Select((run, index) => Window(placed, run.Channel, index, run.Start, run.End - run.Start, run.Language, 0, run.Text) with { Probabilities = run.Probabilities }).ToList();
            // Search and notes read the meeting as the archive stores it.
            archive.Save(document); var saved = archive.Load(document.Id);
            return Score(scenario, placed, channels, saved, runWindows, final.Elapsed.TotalSeconds, clipped, findability: true) with { Preview = preview };
        }
        finally { archive.Delete(document.Id); }
    }
    private static WindowReport Window(IReadOnlyList<PlacedUtterance> placed, string channel, int index, double start, double seconds, string detected, double processing, string text)
    {
        var spoken = TranscriptScoring.SpokenLanguages(placed.Where(utterance => utterance.Utterance.Channel == channel)
            .Select(utterance => (utterance.Start, utterance.End, utterance.Utterance.Language)), start, start + seconds);
        return new(channel, index, Math.Round(start, 2), Math.Round(seconds, 2), detected, spoken, spoken.Count > 0 && !spoken.Contains(detected), Math.Round(processing, 3), text);
    }
    private static async Task<ScenarioReport> OracleAsync(EvaluationScenario scenario, IReadOnlyList<PlacedUtterance> placed, IReadOnlyDictionary<string, float[]> channels,
        MeetingDocument document, Settings settings, int clipped)
    {
        var watch = Stopwatch.StartNew(); var windows = new List<WindowReport>();
        foreach (var (utterance, index) in placed.Select((utterance, index) => (utterance, index)))
        {
            var samples = channels[utterance.Utterance.Channel];
            var from = SpeechSegmentation.Index(utterance.Start - MeetingTranscriber.Padding, samples.Length);
            var to = SpeechSegmentation.Index(utterance.End + MeetingTranscriber.Padding, samples.Length);
            var clip = samples[from..to];
            var gain = SpeechSegmentation.Gain(clip); for (var sample = 0; sample < clip.Length; sample++) clip[sample] *= gain;
            var session = settings.Snapshot(); session.SpeechDetection = false; session.Vocabulary = MeetingTranscriber.Prompt(document, settings);
            session.Language = SpeechLanguages.IsSpoken(utterance.Utterance.Language) ? utterance.Utterance.Language : "auto";
            var offset = from / (double)Rate; var text = new List<string>(); var language = session.Language;
            // Some FLEURS sentences run longer than Whisper's 30 s window; the final pass cuts its runs at 25 s the same way.
            foreach (var (start, length) in SpeechSegmentation.RecordingWindows(clip, 25 * Rate, 6 * Rate))
            {
                var result = await WhisperEngine.TranscribeChunkAsync(clip[start..(start + length)], session, CancellationToken.None, beamSize: MeetingTranscriber.BeamSize);
                foreach (var segment in result.Segments)
                    document.Segments.Add(new() { Start = offset + start / (double)Rate + segment.Start, End = offset + start / (double)Rate + segment.End, Text = segment.Text, Channel = utterance.Utterance.Channel, Speaker = utterance.Utterance.Speaker });
                text.Add(result.Text); language = result.Language;
            }
            windows.Add(Window(placed, utterance.Utterance.Channel, index, offset, clip.Length / (double)Rate, language, 0, string.Join(" ", text)));
        }
        document.Segments = document.Segments.OrderBy(segment => segment.Start).ToList();
        return Score(scenario, placed, channels, document, windows, watch.Elapsed.TotalSeconds, clipped) with { Mode = "oracle" };
    }

    private static ScenarioReport Score(EvaluationScenario scenario, IReadOnlyList<PlacedUtterance> placed, IReadOnlyDictionary<string, float[]> channels, MeetingDocument document,
        List<WindowReport> windows, double processing, int clipped, bool findability = false)
    {
        var channelReports = new List<ChannelReport>(); var missed = new List<MissedItem>(); var utteranceReports = new List<UtteranceReport>();
        var scoredItems = new List<(PlacedUtterance Reference, CriticalItem Item)>();
        var groups = new Dictionary<string, (WordErrors Words, int Items, int Recalled, int Formatting)>();
        void AddGroup(string name, WordErrors words, CriticalItem[] items)
        {
            var known = groups.GetValueOrDefault(name, (Words: WordErrors.None, Items: 0, Recalled: 0, Formatting: 0));
            groups[name] = (known.Words + words, known.Items + items.Length, known.Recalled + items.Count(item => item.Recalled), known.Formatting + items.Count(item => item.FormattingOnly));
        }
        var total = WordErrors.None;
        foreach (var channel in placed.Select(utterance => utterance.Utterance.Channel).Distinct())
        {
            // In time order: utterances of one channel can come from different timelines (people and a show on the system channel).
            var references = placed.Where(utterance => utterance.Utterance.Channel == channel).OrderBy(utterance => utterance.Start).ToArray();
            var segments = document.Segments.Where(segment => segment.Channel == channel).OrderBy(segment => segment.Start).ToArray();
            var score = TranscriptScoring.Score(references.Select(utterance => utterance.Utterance.Text).ToArray(), segments.Select(segment => new TranscriptSegment(segment.Start, segment.End, segment.Text)).ToArray());
            var errors = score.Errors(); total += errors;
            channelReports.Add(new(channel, ErrorReport.From(errors), score.CriticalItems.Count, score.CriticalItems.Count(item => item.Recalled), string.Join(" ", segments.Select(segment => segment.Text)),
                segments.Select(segment => FormattableString.Invariant($"{segment.Start:0.00}-{segment.End:0.00} {segment.Text}")).ToArray()));
            for (var position = 0; position < references.Length; position++)
            {
                var reference = references[position]; var local = position; var tags = reference.Utterance.Tags;
                var words = score.Errors(utterance => utterance == local);
                var items = score.CriticalItems.Where(item => item.Utterance == local).ToArray();
                utteranceReports.Add(new(channel, reference.Utterance.Speaker, reference.Utterance.Language, Math.Round(reference.Start, 2), Math.Round(reference.End, 2), tags,
                    Math.Round(words.Recall, 3), TranscriptScoring.Unmarked(reference.Utterance.Text), score.Heard(local), items.Select(item => item.Text).ToArray()));
                if (reference.Utterance.Language.Length > 0) AddGroup("language:" + reference.Utterance.Language, words, items);
                // "people" is everything but the show, so a clean show channel cannot lift the figure for what people said.
                foreach (var tag in tags.Append("all").Append(tags.Contains("show") ? "" : "people").Where(tag => tag.Length > 0)) AddGroup(tag, words, items);
                scoredItems.AddRange(items.Select(item => (reference, item)));
                foreach (var item in items.Where(item => !item.Recalled))
                    missed.Add(new(channel, item.Text, item.FormattingOnly, reference.Utterance.Speaker, Math.Round(reference.Start, 2), tags, TranscriptScoring.Unmarked(reference.Utterance.Text), item.Heard, score.Heard(local)));
            }
        }
        var seconds = channels.Values.Max(samples => samples.Length) / (double)Rate;
        GroupReport Group(KeyValuePair<string, (WordErrors Words, int Items, int Recalled, int Formatting)> pair) => new(pair.Key, ErrorReport.From(pair.Value.Words), pair.Value.Items, pair.Value.Recalled, pair.Value.Formatting);
        return new(scenario.Name, scenario.Description, Math.Round(seconds, 1), Math.Round(processing, 2), Math.Round(processing / seconds, 4), clipped,
            ErrorReport.From(total), channelReports.Sum(channel => channel.CriticalItems), channelReports.Sum(channel => channel.CriticalRecalled), missed.Count(item => item.FormattingOnly),
            windows.Count(window => window.WrongLanguage), channelReports,
            groups.Where(pair => pair.Key.StartsWith("language:")).Select(pair => Group(pair) with { Name = pair.Key["language:".Length..] }).OrderBy(group => group.Name).ToArray(),
            groups.Where(pair => !pair.Key.StartsWith("language:")).Select(Group).OrderBy(group => group.Name).ToArray(),
            missed, windows, utteranceReports) { Findability = findability ? Findable(document, scoredItems) : [] };
    }

    // Every critical time and number, searched through the archive's search and notes as a user would look for it.
    private static IReadOnlyList<FindabilityReport> Findable(MeetingDocument document, IReadOnlyList<(PlacedUtterance Reference, CriticalItem Item)> items)
    {
        var notes = MeetingArchive.Notes(document);
        var timeLines = notes[(notes.IndexOf(TimesHeading, StringComparison.Ordinal) is var heading and >= 0 ? heading + TimesHeading.Length : notes.Length)..].Split('\n')
            .Where(line => line.StartsWith("- ") && line.Contains(" · ")).Select(line => (Times: line[2..line.IndexOf(" · ", StringComparison.Ordinal)].Split(", "), Line: line)).ToArray();
        var reports = new List<FindabilityReport>();
        foreach (var (reference, item) in items)
        {
            if (Classify(item.Text, reference.Utterance.Text) is not { } found) continue;
            var (kind, queries, listed) = found;
            bool AtPlace(MeetingSegment segment) => segment.Channel == reference.Utterance.Channel && segment.End >= reference.Start - 1 && segment.Start <= reference.End + 1;
            var notFound = queries.Where(query => !MeetingArchive.Search([document], query).Any(hit => AtPlace(hit.Segment))).ToArray();
            var places = document.Segments.Where(AtPlace).Select(segment => $" · {segment.Id}]").ToArray();
            var inNotes = listed is null || timeLines.Any(line => line.Times.Contains(listed) && places.Any(line.Line.EndsWith));
            reports.Add(new(reference.Utterance.Channel, item.Text, kind, reference.Utterance.Speaker, Math.Round(reference.Start, 2), reference.Utterance.Tags, item.Recalled || item.FormattingOnly,
                queries, notFound, inNotes, notFound.Length == 0 && inNotes, item.Recalled));
        }
        return reports;
    }
    private const string TimesHeading = "\n\nTimes mentioned\n";
    // A clock time (two numbers, hour and minutes), a minute mark ("minute 42", also "el minuto [cuarenta y uno]") or a number; null for
    // anything else. Queries are the forms a user would type; Listed is the entry the notes show for a time.
    private static (string Kind, IReadOnlyList<string> Queries, string? Listed)? Classify(string item, string markedUtterance)
    {
        var words = TranscriptScoring.Words(item);
        static bool IsNumber(string word) => word.Length > 0 && word.All(char.IsAsciiDigit);
        if (words.Count == 0) return null;
        var minuteBefore = System.Text.RegularExpressions.Regex.IsMatch(markedUtterance, @"(?i)\b(?:minute|minuto)\s*\[" + System.Text.RegularExpressions.Regex.Escape(item) + @"\]");
        if (words.Count == 2 && words[0] is "minute" or "minuto" && IsNumber(words[1]) || minuteBefore && words.Count == 1 && IsNumber(words[0]))
        {
            var minute = int.Parse(words[^1]);
            return ("minute", [$"minute {minute}", "minute " + NumberWords.Say(minute, "en"), "minuto " + NumberWords.Say(minute, "es"), "Minute " + NumberWords.Say(minute, "de")], $"minute {minute}");
        }
        if (!words.All(IsNumber) || words.Any(word => word.Length > 6)) return null;
        if (words.Count == 2 && int.Parse(words[0]) <= 23 && int.Parse(words[1]) <= 59)
        {
            int hour = int.Parse(words[0]), minute = int.Parse(words[1]);
            return ("time", [$"{hour}:{minute:D2}", $"{hour}{minute:D2}", NumberWords.SayTime(hour, minute, "en"), NumberWords.SayTime(hour, minute, "es"), NumberWords.SayTime(hour, minute, "de")], $"{hour}:{minute:D2}");
        }
        // A digit sequence such as "ocho siete tres" is a code: its digits and the words as spoken.
        if (words.Count > 1) return ("number", [string.Concat(words), item], null);
        var value = int.Parse(words[0]);
        return ("number", [words[0], NumberWords.Say(value, "en"), NumberWords.Say(value, "es"), NumberWords.Say(value, "de")], null);
    }

    private static (Dictionary<string, float[]> Channels, List<PlacedUtterance> Placed, int Clipped) Compose(EvaluationScenario scenario, string folder)
    {
        string Resolve(string path) => Path.IsPathFullyQualified(path) ? path : Path.Combine(folder, path);
        var placements = new List<(string Channel, double Start, float[] Samples)>();
        var placed = new List<PlacedUtterance>(); var channelEnds = new Dictionary<string, double>();
        foreach (var utterance in scenario.Utterances)
        {
            var samples = Level(Trim(Read(Resolve(utterance.Clip))), utterance.Gain);
            var duration = samples.Length / (double)Rate;
            var timeline = utterance.Timeline ?? utterance.Channel;
            var start = Math.Max(0, utterance.At ?? (utterance.Around is { } centre ? centre - duration / 2 : channelEnds.GetValueOrDefault(timeline) + utterance.Gap));
            channelEnds[timeline] = Math.Max(channelEnds.GetValueOrDefault(timeline), start + duration);
            placements.Add((utterance.Channel, start, samples));
            placed.Add(new(utterance, start, start + duration));
        }
        var length = (int)(Math.Max(scenario.Seconds, channelEnds.Values.DefaultIfEmpty(0).Max() + 2) * Rate);
        var channelNames = placed.Select(utterance => utterance.Utterance.Channel).Concat(scenario.Backgrounds.Select(background => background.Channel)).Append("mic").Distinct().ToArray();
        var channels = channelNames.ToDictionary(name => name, _ => new float[length]);
        foreach (var (channel, start, samples) in placements) Mix(channels[channel], samples, (int)(start * Rate));
        // A copied channel hears the others' final mix, so copies are added after all other sources.
        foreach (var background in scenario.Backgrounds.OrderBy(background => background.Source is null ? 0 : 1))
        {
            var target = channels[background.Channel];
            var from = (int)(background.Start * Rate); var to = background.End > 0 ? Math.Min(length, (int)(background.End * Rate)) : length;
            if (background.Source is { } source)
            {
                // About 10 ms of loudspeaker-to-microphone delay.
                var copy = channels[source]; var factor = (float)Math.Pow(10, background.Gain / 20);
                for (var sample = Math.Max(from, 160); sample < to; sample++) target[sample] += copy[sample - 160] * factor;
                continue;
            }
            var bed = Level(background.Music ? MusicBed(20 * Rate) : Trim(Read(Resolve(background.Clip!))), background.Gain);
            for (var sample = from; sample < to; sample++) target[sample] += bed[(sample - from) % bed.Length];
        }
        var random = new Random(1); var amplitude = (float)(Math.Pow(10, scenario.NoiseFloor / 20) * Math.Sqrt(3));
        var microphone = channels["mic"];
        for (var sample = 0; sample < microphone.Length; sample++) microphone[sample] += (float)(random.NextDouble() * 2 - 1) * amplitude;
        var clipped = 0;
        foreach (var samples in channels.Values)
            for (var sample = 0; sample < samples.Length; sample++)
                if (Math.Abs(samples[sample]) > 1) { samples[sample] = Math.Sign(samples[sample]); clipped++; }
        return (channels, placed, clipped);
    }
    private static void Mix(float[] target, float[] source, int start)
    { for (var sample = 0; sample < source.Length && start + sample < target.Length; sample++) target[start + sample] += source[sample]; }
    private static float[] Read(string file)
    {
        using var reader = new AudioFileReader(file);
        var source = WhisperEngine.Mono16Khz(reader); var samples = new List<float>(); var buffer = new float[Rate]; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
        return [.. samples];
    }
    private static float[] Trim(float[] samples)
    {
        int first = Array.FindIndex(samples, sample => Math.Abs(sample) > 0.001), last = Array.FindLastIndex(samples, sample => Math.Abs(sample) > 0.001);
        return first < 0 ? [] : samples[first..(last + 1)];
    }
    // Scales to speech at -20 dBFS active RMS (the mean over 20 ms frames that carry sound), then applies the gain.
    private static float[] Level(float[] samples, double gain)
    {
        var frames = Enumerable.Range(0, samples.Length / 320).Select(frame => samples.AsSpan(frame * 320, 320).ToArray().Average(sample => sample * sample)).Where(power => power > 1e-6).ToArray();
        if (frames.Length == 0) return samples;
        var factor = (float)(SpeechLevel / Math.Sqrt(frames.Average()) * Math.Pow(10, gain / 20));
        return samples.Select(sample => sample * factor).ToArray();
    }
    // A deterministic stand-in for a show's music: a four-chord progression with a two-beat pulse.
    private static float[] MusicBed(int length)
    {
        double[][] chords = [[220, 261.63, 329.63], [174.61, 220, 261.63], [196, 246.94, 293.66], [164.81, 207.65, 246.94]];
        var samples = new float[length];
        for (var sample = 0; sample < length; sample++)
        {
            var time = sample / (double)Rate; var chord = chords[(int)(time / 2.5) % chords.Length];
            var pulse = 0.55 + 0.45 * Math.Pow(Math.Cos(Math.PI * time * 2), 2);
            samples[sample] = (float)(pulse * chord.Sum(frequency => Math.Sin(2 * Math.PI * frequency * time) + 0.3 * Math.Sin(4 * Math.PI * frequency * time)) / 4);
        }
        return samples;
    }
}
