using System.IO;
using System.Text.Json;
using NAudio.Wave;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// Evaluation mode --build-audio-library <suite.json> <library folder>\library.json: turns the dictation suite of scripts/dictation-eval.ps1
// (Piper voices and FLEURS recordings) into an audio library for the test build, one folder per clip with 16 kHz mono audio and
// its manifest, plus quiet, noisy and code-switching versions. Writes library.json with the clip count when it is done.
internal static class AudioLibraryBuilder
{
    // One spoken-numbers case per language gets the harder versions; numbers and negations are what must survive them.
    private static readonly string[] VariantSources = ["en-numbers", "de-numbers", "es-numbers"];
    private static readonly (string First, string Second)[] CodeSwitching = [("en-numbers", "es-numbers"), ("de-technical", "en-names")];

    public static async Task RunAsync(string suiteFile, string report)
    {
        var libraryFolder = Path.GetDirectoryName(Path.GetFullPath(report))!;
        try
        {
            var suite = JsonSerializer.Deserialize<DictationSuite>(await File.ReadAllTextAsync(suiteFile)) ?? throw new InvalidDataException(suiteFile);
            Directory.CreateDirectory(libraryFolder);
            var written = new Dictionary<string, (DictationCase Case, float[] Samples)>();
            foreach (var entry in suite.Cases.Where(entry => !entry.Clip.StartsWith("noise:", StringComparison.Ordinal)))
            {
                var samples = LibraryAudio.Read(entry.Clip);
                var tags = entry.Tags.Select(tag => tag.ToLowerInvariant()).Where(tag => tag is not "multi-window" and not "noise")
                    .Concat(entry.Name.Contains("technical") ? ["technical"] : []).Concat(entry.Tags.Contains("multi-window") ? ["long"] : []);
                Write(libraryFolder, entry.Name, samples, entry.Language, entry.Reference, tags, entry.Source, entry.Edited is null ? null : OutputStyle.Clean, entry.Edited);
                written[entry.Name] = (entry, samples);
            }
            foreach (var name in VariantSources.Where(written.ContainsKey))
            {
                var (entry, samples) = written[name];
                Write(libraryFolder, name + "-quiet", LibraryVariants.Scaled(samples, -25), entry.Language, entry.Reference, ["quiet"], entry.Source);
                Write(libraryFolder, name + "-noisy", LibraryVariants.WithNoise(samples, -45, name.Length), entry.Language, entry.Reference, ["noisy"], entry.Source);
            }
            foreach (var (first, second) in CodeSwitching.Where(pair => written.ContainsKey(pair.First) && written.ContainsKey(pair.Second)))
            {
                var (firstCase, firstSamples) = written[first]; var (secondCase, secondSamples) = written[second];
                Write(libraryFolder, $"{first}-then-{second}", LibraryVariants.Joined(firstSamples, secondSamples, 0.8), firstCase.Language,
                    firstCase.Reference + " " + secondCase.Reference, ["code-switching"], firstCase.Source);
            }
            // Room noise without speech: nothing should be pasted.
            Write(libraryFolder, "room-noise", LibraryVariants.WithNoise(new float[5 * SpeechSegmentation.SampleRate], -60, 1), "en", "", ["noisy"], "synthetic");
            var clips = AudioLibrary.Load(libraryFolder);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = true, Clips = clips.Count }));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception error)
        {
            Directory.CreateDirectory(libraryFolder);
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = false, Error = error.Message }));
            System.Windows.Application.Current.Shutdown(1);
        }
    }

    private static void Write(string library, string id, float[] samples, string language, string reference, IEnumerable<string> tags, string source,
        OutputStyle? expectedStyle = null, string? expected = null)
    {
        var folder = Path.Combine(library, id);
        Directory.CreateDirectory(folder);
        using (var writer = new WaveFileWriter(Path.Combine(folder, "audio.wav"), new WaveFormat(SpeechSegmentation.SampleRate, 16, 1))) writer.WriteSamples(samples, 0, samples.Length);
        File.WriteAllText(Path.Combine(folder, AudioLibrary.ManifestName), AudioLibrary.ManifestJson("audio.wav", language, reference, tags.Distinct(), source, expectedStyle, expected));
    }
}
