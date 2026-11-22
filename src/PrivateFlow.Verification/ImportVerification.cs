using System.IO;
using System.Text.Json;
using NAudio.MediaFoundation;
using NAudio.Wave;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// --verify-imports <speech.wav> <report.json> (isolated data folder only): the speech encoded by Windows' Media Foundation into the
// compressed formats Transcribe file offers, each transcribed as a picked file would be. FLAC goes through the FLAC encoder of
// Media Foundation; raw .aac (ADTS) has no Media Foundation writer and is not checked.
internal static class ImportVerification
{
    private static readonly Guid FlacSubtype = new("0000f1ac-0000-0010-8000-00aa00389b71");

    public static async Task RunAsync(string speech, string report)
    {
        var results = new List<object>();
        var passed = true;
        var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "imports");
        try
        {
            Directory.CreateDirectory(folder);
            MediaFoundationApi.Startup();
            var settings = Settings.Load();
            foreach (var (extension, encode) in new (string, Action<string>)[]
            {
                (".mp3", file => { using var reader = new WaveFileReader(speech); MediaFoundationEncoder.EncodeToMp3(reader, file, 128000); }),
                (".m4a", file => { using var reader = new WaveFileReader(speech); MediaFoundationEncoder.EncodeToAac(reader, file, 128000); }),
                (".wma", file => { using var reader = new WaveFileReader(speech); MediaFoundationEncoder.EncodeToWma(reader, file, 128000); }),
                (".flac", file => { using var reader = new WaveFileReader(speech); using var encoder = new MediaFoundationEncoder(Flac(reader.WaveFormat)); encoder.Encode(file, reader); }),
            })
            {
                var file = Path.Combine(folder, "speech" + extension);
                string? text = null, error = null;
                try { encode(file); text = await WhisperEngine.TranscribeFileAsync(file, settings, CancellationToken.None); }
                catch (Exception caught) { error = $"{caught.GetType().Name}: {caught.Message}"; }
                // The generated sentence says "private" and "forty two"; either survives any of these encodings.
                var ok = text is not null && (text.Contains("private", StringComparison.OrdinalIgnoreCase) || text.Contains("42"));
                passed &= ok;
                results.Add(new { format = extension, passed = ok, bytes = File.Exists(file) ? new FileInfo(file).Length : 0, text, error });
            }
        }
        catch (Exception error) { passed = false; results.Add(new { format = "setup", passed = false, error = error.Message }); }
        finally { foreach (var file in Directory.Exists(folder) ? Directory.GetFiles(folder) : []) File.Delete(file); }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = passed, Formats = results }, new JsonSerializerOptions { WriteIndented = true }));
        WhisperEngine.Shutdown();
        System.Windows.Application.Current.Shutdown(passed ? 0 : 1);
    }

    private static MediaType Flac(WaveFormat format)
    {
        var type = new MediaType();
        type.MajorType = MediaTypes.MFMediaType_Audio;
        type.SubType = FlacSubtype;
        type.SampleRate = format.SampleRate;
        type.ChannelCount = format.Channels;
        type.BitsPerSample = format.BitsPerSample;
        return type;
    }
}
