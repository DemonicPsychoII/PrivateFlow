using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

public static class WhisperEngine
{
    private static readonly SpeechWorker Worker = new();
    public static bool IsWarm => Worker.IsWarm;
    // The GPU memory when the loaded model did not fit the free memory and loaded anyway; null otherwise.
    internal static GpuMemory? LowMemory => Worker.LowMemory;
    public static void UnloadIfIdle() => Worker.UnloadIfIdle();
    internal static TimeProvider Clock { get => Worker.Clock; set => Worker.Clock = value; }
    public static Task UnloadAsync() => Worker.UnloadAsync();
    public static Task LoadAsync(Settings settings, CancellationToken token, Action<GpuMemory>? loadingWithLowMemory = null) => Worker.LoadAsync(settings, token, loadingWithLowMemory);
    public static void Shutdown() => Worker.Dispose();
    public static Task<IDisposable> ReserveAsync(CancellationToken token) => Worker.ReserveAsync(token);
    public static Task<SpeechResult> TranscribeChunkAsync(float[] samples, Settings settings, CancellationToken token, string languages = "", int beamSize = 0) =>
        Worker.TranscribeAsync(samples, settings, token, languages, beamSize);
    public static async Task<IReadOnlyDictionary<string, double>> DetectLanguageAsync(float[] samples, Settings settings, string languages, CancellationToken token) =>
        (await Worker.RequestAsync(samples, settings, new("auto", "", settings.VadModelPath, false, languages, Operation: SpeechOperation.DetectLanguage), token)).LanguageProbabilities
        ?? throw new InvalidOperationException("Speech worker failed.");
    public static async Task<float[]> SpeechProbabilitiesAsync(float[] samples, Settings settings, bool reset, CancellationToken token) =>
        (await Worker.RequestAsync(samples, settings, new("auto", "", settings.VadModelPath, true, Operation: SpeechOperation.SpeechProbabilities, ResetSpeechState: reset), token)).SpeechProbabilities
        ?? throw new InvalidOperationException("Speech worker failed.");
    // "auto" on its own lets Whisper choose among all its languages; Private Flow only offers these.
    internal static string AllowedLanguages(Settings settings) => settings.Language == "auto" ? SpeechLanguages.Offered : "";
    public static async Task<string> TranscribeFileAsync(string input, Settings settings, CancellationToken token) =>
        (await TranscribeSegmentsAsync(input, settings, token)).Result.Text;
    // A file gets the meeting final pass: cuts at pauses and a language per stretch of speech. Progress is the share done, 0 to 1.
    internal static async Task<(SpeechResult Result, FinalModel Model)> TranscribeSegmentsAsync(string input, Settings settings, CancellationToken token, IProgress<double>? progress = null)
    {
        var directory = Path.Combine(Settings.Root, "temp"); Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            double seconds;
            using (var source = new AudioFileReader(input))
            using (var writer = new WaveFileWriter(file, new WaveFormat(SpeechSegmentation.SampleRate, 16, 1)))
            {
                var mono = Mono16Khz(source); var buffer = new float[SpeechSegmentation.SampleRate]; int read; long total = 0;
                while ((read = mono.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); writer.WriteSamples(buffer, 0, read); total += read; }
                seconds = total / (double)SpeechSegmentation.SampleRate;
            }
            var audio = new ChannelAudio([(file, 0, seconds)]);
            var languages = SpeechLanguages.Parse(settings.Language == "auto" ? SpeechLanguages.Offered : settings.Language);
            var ((segments, runs), model) = await MeetingTranscriber.WithFinalModelAsync(settings, (session, reporter) => MeetingTranscriber.TranscribeAsync(audio, session, languages, reporter, token),
                token, progress is null ? null : new ProgressCallback<MeetingProgress>(step => progress.Report(step.Share)));
            var language = runs.GroupBy(run => run.Language).OrderByDescending(group => group.Sum(run => run.End - run.Start)).Select(group => group.Key).FirstOrDefault() ?? languages[0];
            return (new(segments, seconds, language), model);
        }
        finally { File.Delete(file); }
    }
    // A recording in the windows live capture cuts, as a dictation hears it.
    public static async Task<SpeechResult> TranscribeRecordingAsync(string input, Settings settings, CancellationToken token, string languages = "")
    {
        using var reader = new AudioFileReader(input);
        var source = Mono16Khz(reader); var samples = new List<float>(); var buffer = new float[SpeechSegmentation.SampleRate]; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
        var all = samples.ToArray();
        var segments = new List<TranscriptSegment>(); var detected = new Dictionary<string, double>();
        foreach (var (start, length) in SpeechSegmentation.RecordingWindows(all, AudioRecorder.ChunkSamples, AudioRecorder.CutSearchSamples))
        {
            var result = await TranscribeChunkAsync(all[start..(start + length)], settings, token, languages.Length > 0 ? languages : AllowedLanguages(settings));
            SegmentAssembly.Append(segments, result.Segments, start / (double)SpeechSegmentation.SampleRate);
            detected[result.Language] = detected.GetValueOrDefault(result.Language) + result.Seconds;
        }
        return new(segments, all.Length / (double)SpeechSegmentation.SampleRate, detected.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault() ?? settings.Language);
    }
    internal static ISampleProvider Mono16Khz(ISampleProvider source)
    {
        if (source.WaveFormat.Channels != 1) source = new DownmixProvider(source);
        return source.WaveFormat.SampleRate == 16000 ? source : new WdlResamplingSampleProvider(source, 16000);
    }
    public static void ClearStaleTemporaryFiles()
    {
        var dir = Path.Combine(Settings.Root, "temp");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.EnumerateFiles(dir))
            if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) && Path.GetExtension(file) is ".wav" or ".txt")
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class DownmixProvider(ISampleProvider source) : ISampleProvider
{
    private float[] buffer = [];
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
    public int Read(float[] output, int offset, int count)
    {
        var channels = source.WaveFormat.Channels;
        if (buffer.Length < count * channels) buffer = new float[count * channels];
        var read = source.Read(buffer, 0, count * channels) / channels;
        for (var i = 0; i < read; i++)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++) sum += buffer[i * channels + channel];
            output[offset + i] = sum / channels;
        }
        return read;
    }
}
