using System.IO;
using System.Text.Json;
using NAudio.Wave;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// The test build's microphone (--library-audio <folder> --seed <n>, only with an isolated PRIVATE_FLOW_DATA_DIR): every dictation
// plays the next clip of the library, drawn with the seed. Each draw is logged to library-audio.jsonl in the data folder with the
// clip's folder name only, never its text, so a test can score the result and a failure replays with the same seed.
// "Keep as test clip" on Dictate, while Keep dictations as test clips is on: the dictation's audio and the text as the user corrected
// it become a clip of an audio library in the user's evaluation folder, outside the app's folder and any repository. It stays on
// this PC; test reports name its folder and count its words, never its text.
internal static class DictationClips
{
    public static string Root => Environment.GetEnvironmentVariable("PRIVATE_FLOW_DICTATION_CLIPS") is { Length: > 0 } folder ? Path.GetFullPath(folder)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PrivateFlowEvaluation", "dictation-clips");

    public static string Keep(float[] samples, string language, string reference)
    {
        var id = "own-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var folder = Path.Combine(Root, id);
        Directory.CreateDirectory(folder);
        using (var writer = new WaveFileWriter(Path.Combine(folder, "audio.wav"), new WaveFormat(SpeechSegmentation.SampleRate, 16, 1))) writer.WriteSamples(samples, 0, samples.Length);
        File.WriteAllText(Path.Combine(folder, AudioLibrary.ManifestName), AudioLibrary.ManifestJson("audio.wav", language, reference.Trim(), [], "own"));
        return id;
    }
}

internal sealed class LibraryAudio
{
    private readonly ClipPicker picker;
    private readonly string log;
    public int Seed => picker.Seed;

    private LibraryAudio(IReadOnlyList<LibraryClip> clips, int seed, string folder)
    {
        picker = new(clips, seed);
        log = Path.Combine(Settings.Root, "library-audio.jsonl");
        Directory.CreateDirectory(Settings.Root);
        Append(new { library = folder, clips = clips.Count, seed });
    }

    public static LibraryAudio Load(string folder, int seed) => new(AudioLibrary.Load(folder), seed, Path.GetFullPath(folder));

    // A dictation asks for its audio: the microphone it names is not used.
    public IAudioSource Open(string microphoneId)
    {
        var clip = picker.Next();
        var samples = Read(clip.AudioPath);
        Append(new { dictation = picker.Drawn, clip = clip.Id, seconds = Math.Round(samples.Length / 16000.0, 3), started = DateTime.UtcNow.ToString("o") });
        return new LibraryAudioSource(samples);
    }

    private void Append(object entry)
    {
        lock (picker) File.AppendAllText(log, JsonSerializer.Serialize(entry) + "\n");
    }

    internal static float[] Read(string path)
    {
        using var reader = new AudioFileReader(path);
        var source = WhisperEngine.Mono16Khz(reader);
        var samples = new List<float>();
        var buffer = new float[SpeechSegmentation.SampleRate];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
        return [.. samples];
    }
}
