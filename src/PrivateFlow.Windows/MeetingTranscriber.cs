using System.IO;
using NAudio.Wave;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

internal sealed record FinalPassRun(string Channel, double Start, double End, string Language, IReadOnlyDictionary<string, double> Probabilities, string Text);

// The step a long pass is in and how much of the whole pass is done, 0 to 1.
internal sealed record MeetingProgress(string Step, double Share);

// The model that produced a final pass, and why when it is the speech model standing in for the meeting model; empty otherwise.
internal sealed record FinalModel(string Name, string Fallback);

// Reports on the caller's thread, in order; Progress<T> without a synchronization context could deliver out of order.
internal sealed class ProgressCallback<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

// The final pass after a meeting: per channel, Silero finds the pauses, each stretch of speech gets the most likely of the
// meeting's languages, neighbouring stretches in one language are decoded together, and the result replaces the live preview.
internal static class MeetingTranscriber
{
    private const int Block = SpeechSegmentation.FrameSamples * 900;
    // whisper.cpp's limit of eight decoders.
    public const int BeamSize = 8;
    public const double Padding = 0.2;
    private const double MinimumSilence = 0.064, SwitchPenalty = 0.5;
    // Shares of a channel's progress for its three steps; decoding takes most of the time, detection is quicker per second of audio.
    private const double DetectionShare = 0.1, LanguageShare = 0.2;

    public static async Task<IReadOnlyList<FinalPassRun>> FinalizeAsync(MeetingArchive archive, MeetingDocument document, Settings settings, IProgress<MeetingProgress>? progress,
        CancellationToken token)
    {
        var languages = SpeechLanguages.Parse(document.Languages) is { Count: > 0 } chosen ? chosen : SpeechLanguages.Parse(SpeechLanguages.Offered);
        var channels = document.Chunks.Where(chunk => File.Exists(archive.ChunkPath(document.Id, chunk.File))).GroupBy(chunk => chunk.Channel).ToArray();
        var total = channels.Sum(channel => channel.Max(chunk => chunk.Offset + chunk.Duration)); var done = 0.0;
        var segments = new Dictionary<string, List<TranscriptSegment>>(); var runs = new List<FinalPassRun>();
        var audio = channels.ToDictionary(channel => channel.Key, channel => new ChannelAudio(channel.Select(chunk => (archive.ChunkPath(document.Id, chunk.File), chunk.Offset, chunk.Duration))));
        var meeting = settings.Snapshot(); meeting.Vocabulary = Prompt(document, settings);
        var (_, model) = await WithFinalModelAsync(meeting, async (session, reporter) =>
        {
            segments.Clear(); runs.Clear(); done = 0;
            if (audio.Count > 0)
            {
                reporter?.Report(new($"Loading the model {SpeechSetup.ModelName(session.ModelPath)}", 0));
                await WhisperEngine.LoadAsync(session, token);
            }
            foreach (var (channel, recording) in audio)
            {
                var name = channel == "mic" ? "microphone" : "system audio";
                var (transcript, channelRuns) = await TranscribeAsync(recording, session, languages,
                    new ProgressCallback<MeetingProgress>(step => reporter?.Report(new($"{step.Step} · {name}", Math.Min(1, (done + step.Share * recording.Seconds) / Math.Max(1, total))))), token);
                segments[channel] = transcript.ToList(); runs.AddRange(channelRuns.Select(run => run with { Channel = channel })); done += recording.Seconds;
            }
            return true;
        }, token, progress);
        // A cancel that arrives after the last decode still keeps the preview and the audio.
        token.ThrowIfCancellationRequested();
        if (audio.Count > 0) { document.TranscriptModel = model.Name; document.ModelFallback = model.Fallback; }
        progress?.Report(new("Saving the transcript", 1));
        if (segments.TryGetValue("mic", out var microphone) && segments.TryGetValue("system", out var system))
        {
            var echoes = SpeechSegmentation.Echoes(microphone.Select(segment => (segment, SpeechSegmentation.Level(audio["mic"].Read(segment.Start, segment.End)))).ToArray(), system);
            segments["mic"] = microphone.Where((_, index) => !echoes.Contains(index)).ToList();
        }
        Replace(document, channels, segments);
        return runs;
    }
    public static string Prompt(MeetingDocument document, Settings settings) => MeetingVocabulary.Prompt(document.Vocabulary, document.Title, settings.Vocabulary, settings.Corrections);
    public static async Task<(IReadOnlyList<TranscriptSegment> Segments, IReadOnlyList<FinalPassRun> Runs)> TranscribeAsync(ChannelAudio audio, Settings settings,
        IReadOnlyList<string> languages, IProgress<MeetingProgress>? progress, CancellationToken token)
    {
        var transcript = new List<TranscriptSegment>(); var runs = new List<FinalPassRun>();
        var (pieces, gains, planned) = await PlanAsync(audio, settings, languages, progress, token);
        foreach (var run in planned)
        {
            var decode = settings.Snapshot(); decode.Language = run.Language; decode.SpeechDetection = false;
            var result = await WhisperEngine.TranscribeChunkAsync(Samples(audio, run, pieces, gains), decode, token, beamSize: BeamSize);
            var added = SegmentAssembly.JoinSentences(result.Segments.Select(segment => segment with { Start = Math.Min(run.End, run.Start + segment.Start), End = Math.Min(run.End, run.Start + segment.End) }));
            transcript.AddRange(added);
            IReadOnlyDictionary<string, double> evidence = pieces.Skip(run.FirstPiece).Take(run.PieceCount).SelectMany(piece => piece.Languages)
                .GroupBy(pair => pair.Key).ToDictionary(group => group.Key, group => Math.Round(group.Average(pair => pair.Value), 3));
            runs.Add(new("", Math.Round(run.Start, 2), Math.Round(run.End, 2), run.Language, evidence, string.Join(" ", added.Select(segment => segment.Text))));
            progress?.Report(new("Transcribing", DetectionShare + LanguageShare + (1 - DetectionShare - LanguageShare) * Math.Min(1, run.End / Math.Max(1, audio.Seconds))));
        }
        progress?.Report(new("Transcribing", 1));
        return (transcript, runs);
    }
    // Runs a final pass with the meeting model when setup installed one; ModelAttempts decides what follows a failure: one more try
    // with a fresh worker, a wait for GPU memory that other work can free, then the speech model. Each attempt's progress continues
    // from where the bar stands, and the returned model says which model produced the result and why.
    public static async Task<(T Result, FinalModel Model)> WithFinalModelAsync<T>(Settings settings, Func<Settings, IProgress<MeetingProgress>?, Task<T>> work, CancellationToken token,
        IProgress<MeetingProgress>? progress = null)
    {
        SpeechWorker.FinalPassLoads = true;
        // A warm Local polish server on the GPU would hold its memory against the final pass's model until it idles out.
        await LocalRewriter.FreeGpuAsync(token);
        var session = settings.Snapshot(); session.KeepModelWarm = true;
        var forward = new ForwardProgress();
        var reporter = progress is null ? null : new ProgressCallback<MeetingProgress>(step => progress.Report(step with { Share = forward.Map(step.Share) }));
        try
        {
            var outcome = await ModelAttempts.RunAsync(File.Exists(settings.MeetingModelPath) ? settings.MeetingModelPath : "", settings.ModelPath,
                model => { session.ModelPath = model; return work(session, reporter); },
                failure => (failure as GpuMemoryException)?.Memory,
                async (model, memory) => { var ended = await WaitForMemoryAsync(session, memory, reporter, token); forward.Restart(); return ended; },
                async (step, retry) =>
                {
                    forward.Restart(); reporter?.Report(new(step, 0));
                    // A fresh worker: the failed one may have lost its state, and its memory is released before the next load.
                    await WhisperEngine.UnloadAsync();
                    if (retry) await Task.Delay(RetryPause, token);
                },
                SpeechSetup.ModelName, token);
            return (outcome.Result, new(SpeechSetup.ModelName(outcome.Model), outcome.Fallback));
        }
        finally
        {
            // The meeting model's memory is freed at once. A kept-warm speech model loads in the background, so the meeting does not wait
            // for it; a dictation that starts first waits for the same load.
            if (!settings.KeepModelWarm) await WhisperEngine.UnloadAsync();
            else if (!SameModel(session.ModelPath, settings.ModelPath))
            {
                await WhisperEngine.UnloadAsync();
                if (!token.IsCancellationRequested) _ = WarmAsync(settings);
            }
        }
    }
    // Private Flow's own meeting notes model unloads when its notes are written, so while it runs the pass waits for it, however long
    // the notes take, and says so; the memory it holds is free again afterwards.
    private static Task<Exception?> WaitForMemoryAsync(Settings session, GpuMemory memory, IProgress<MeetingProgress>? reporter, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        return ModelAttempts.WaitForMemoryAsync(memory, MemoryWait, () => LocalRewriter.Running == LocalRewriter.NotesTask, () => WhisperEngine.LoadAsync(session, token),
            failure => (failure as GpuMemoryException)?.Memory, (forNotes, current) => reporter?.Report(new(SpeechRuntime.MemoryWaitStep(SpeechSetup.ModelName(session.ModelPath), current, forNotes), 0)),
            pause => Task.Delay(pause, token), () => clock.Elapsed, token);
    }
    // Long enough for a short spike of other GPU work to pass, short against a final pass. Notes written on the GPU take about half a minute.
    private static readonly TimeSpan RetryPause = TimeSpan.FromSeconds(2);
    private static readonly MemoryWaitTiming MemoryWait = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10));
    private static bool SameModel(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static async Task WarmAsync(Settings settings)
    {
        try { await WhisperEngine.LoadAsync(settings, CancellationToken.None); }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException) { }
    }

    private static async Task<(IReadOnlyList<SpeechPiece> Pieces, IReadOnlyList<float> Gains, IReadOnlyList<SpeechRun> Runs)> PlanAsync(ChannelAudio audio, Settings settings,
        IReadOnlyList<string> languages, IProgress<MeetingProgress>? progress, CancellationToken token)
    {
        var probabilities = new List<float>();
        progress?.Report(new("Detecting speech", 0));
        for (var start = 0; start < audio.Length; start += Block)
        {
            var length = Math.Min(Block, (audio.Length - start + SpeechSegmentation.FrameSamples - 1) / SpeechSegmentation.FrameSamples * SpeechSegmentation.FrameSamples);
            probabilities.AddRange(await WhisperEngine.SpeechProbabilitiesAsync(audio.Read(start, length), settings, start == 0, token));
            progress?.Report(new("Detecting speech", DetectionShare * Math.Min(1, (start + length) / (double)Math.Max(1, audio.Length))));
        }
        var regions = SpeechSegmentation.Regions(probabilities, audio.Seconds, minimumSilence: MinimumSilence, padding: Padding);
        var pieces = new List<SpeechPiece>(); var gains = new List<float>();
        foreach (var region in regions)
        {
            progress?.Report(new("Detecting languages", DetectionShare + LanguageShare * pieces.Count / regions.Count));
            var samples = audio.Read(region.Start, region.End);
            var gain = SpeechSegmentation.Gain(samples);
            gains.Add(gain);
            var detected = languages.Count == 1 || samples.Length < 1600 ? languages.ToDictionary(language => language, language => language == languages[0] ? 1.0 : 0.0)
                : await WhisperEngine.DetectLanguageAsync(Scale(samples, gain), settings, string.Join(",", languages), token);
            pieces.Add(new(region.Start, region.End, detected));
        }
        return (pieces, gains, SpeechSegmentation.Runs(pieces, SpeechSegmentation.ChooseLanguages(pieces, SwitchPenalty)));
    }
    // Each piece keeps its own level; the pauses between take the quieter neighbour's gain.
    private static float[] Samples(ChannelAudio audio, SpeechRun run, IReadOnlyList<SpeechPiece> pieces, IReadOnlyList<float> gains)
    {
        var samples = audio.Read(run.Start, run.End);
        for (var piece = run.FirstPiece; piece < run.FirstPiece + run.PieceCount; piece++)
        {
            var last = piece + 1 == run.FirstPiece + run.PieceCount;
            var from = SpeechSegmentation.Index(pieces[piece].Start - run.Start, samples.Length);
            var to = last ? samples.Length : SpeechSegmentation.Index(pieces[piece + 1].Start - run.Start, samples.Length);
            var end = SpeechSegmentation.Index(pieces[piece].End - run.Start, samples.Length);
            var pause = last ? gains[piece] : Math.Min(gains[piece], gains[piece + 1]);
            for (var sample = from; sample < to; sample++) samples[sample] *= sample < end ? gains[piece] : pause;
        }
        return samples;
    }
    private static float[] Scale(float[] samples, float gain) => gain == 1 ? samples : samples.Select(sample => sample * gain).ToArray();

    // Final segments replace the preview wherever the final pass had audio; preview text of audio deleted earlier stays.
    private static void Replace(MeetingDocument document, IEnumerable<IGrouping<string, MeetingChunk>> channels, Dictionary<string, List<TranscriptSegment>> segments)
    {
        foreach (var channel in channels)
        {
            var covered = channel.Select(chunk => (chunk.Offset, End: chunk.Offset + chunk.Duration)).ToArray();
            document.Segments.RemoveAll(segment => segment.Channel == channel.Key && covered.Any(range => segment.Start >= range.Offset - 0.5 && segment.Start < range.End));
            var speaker = channel.Key == "mic" ? "You" : "Remote speaker";
            document.Segments.AddRange(segments[channel.Key].Where(segment => !string.IsNullOrWhiteSpace(segment.Text))
                .Select(segment => new MeetingSegment { Start = segment.Start, End = Math.Max(segment.Start, segment.End), Text = segment.Text, Speaker = speaker, Channel = channel.Key }));
            foreach (var chunk in channel) chunk.Transcribed = true;
        }
        document.Segments = document.Segments.OrderBy(segment => segment.Start).ToList();
    }
}

// A channel's chunk files read as one recording; gaps between chunks, such as loopback silence, read as zeros.
internal sealed class ChannelAudio
{
    private readonly (string File, int Start, int Length)[] chunks;
    public int Length { get; }
    public double Seconds => Length / (double)SpeechSegmentation.SampleRate;
    // 16-bit mono 16 kHz WAV files with their place in the recording, in seconds.
    public ChannelAudio(IEnumerable<(string File, double Offset, double Duration)> files)
    {
        chunks = files.OrderBy(file => file.Offset).Select(file => (file.File, (int)Math.Round(file.Offset * SpeechSegmentation.SampleRate),
            (int)Math.Round(file.Duration * SpeechSegmentation.SampleRate))).ToArray();
        Length = chunks.Length == 0 ? 0 : chunks.Max(chunk => chunk.Start + chunk.Length);
    }
    public float[] Read(double start, double end) =>
        Read(SpeechSegmentation.Index(start, Length), Math.Max(0, SpeechSegmentation.Index(end, Length) - SpeechSegmentation.Index(start, Length)));
    public float[] Read(int start, int count)
    {
        var samples = new float[count];
        foreach (var (file, chunkStart, chunkLength) in chunks.Where(chunk => chunk.Start < start + count && chunk.Start + chunk.Length > start))
        {
            using var reader = new WaveFileReader(file);
            var skip = Math.Max(0, start - chunkStart);
            reader.Position = (long)skip * reader.WaveFormat.BlockAlign;
            var provider = reader.ToSampleProvider();
            var target = Math.Max(0, chunkStart - start);
            var wanted = Math.Min(count - target, chunkLength - skip);
            var buffer = new float[Math.Max(0, wanted)]; var read = 0; int got;
            while (read < buffer.Length && (got = provider.Read(buffer, read, buffer.Length - read)) > 0) read += got;
            Array.Copy(buffer, 0, samples, target, read);
        }
        return samples;
    }
}
