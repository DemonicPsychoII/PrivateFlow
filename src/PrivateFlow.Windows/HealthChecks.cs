using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PrivateFlow.Windows;

internal static class HealthChecks
{
    private static double? cold, warm;
    // An optional model that was never set up has an empty path, which FileInfo rejects.
    private static long Size(string path) { try { return new FileInfo(path).Length; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; } }
    public static object Metadata(Settings settings) => new
    {
        AppVersion = typeof(App).Assembly.GetName().Version?.ToString(), SettingsVersion = Settings.CurrentVersion,
        Runtime = Environment.Version.ToString(), OsVersion = Environment.OSVersion.Version.ToString(), Architecture = "x64",
        Backend = settings.EnginePath.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "CUDA" : "CPU",
        EnginePresent = File.Exists(settings.EnginePath), SpeechModelBytes = Size(settings.ModelPath), MeetingModelBytes = Size(settings.MeetingModelPath), VadModelBytes = Size(settings.VadModelPath),
        LocalRewriteModelBytes = Size(settings.LocalRewriteModelPath), MeetingNotesModelBytes = Size(settings.MeetingNotesModelPath), settings.SpeechDetection, settings.KeepModelWarm,
        MicrophonePinned = settings.MicrophoneId.Length > 0, ProfileCount = settings.Profiles.Count,
        CorrectionCount = settings.Corrections.Count, SnippetCount = settings.Snippets.Count,
        ColdSpeechSeconds = cold, WarmSpeechSeconds = warm, AudioNetworkTransfer = false
    };
    public static string Describe(Settings settings) =>
        $"Speech runtime: {(File.Exists(settings.EnginePath) ? "ready" : "missing")}\nSpeech model: {Size(settings.ModelPath) / 1024 / 1024} MB\n" +
        $"Meeting final pass: {(File.Exists(settings.MeetingModelPath) ? SpeechSetup.ModelName(settings.MeetingModelPath) : "the speech model")}\n" +
        $"Silero detector: {(File.Exists(settings.VadModelPath) ? "ready" : "missing")}\nLocal polish: {(ModelList.IsInstalled(settings, ModelList.PolishModel(settings)) ? $"ready with {ModelList.PolishModel(settings).Pinned.Name}" : $"optional, {ModelList.PolishModel(settings).Pinned.Name} not installed")}\n" +
        $"Meeting notes model: {(MeetingNotesModel.IsInstalled(settings) ? SpeechSetup.ModelName(settings.MeetingNotesModelPath) : "optional, not installed")}\n" +
        $"Speech worker: {(WhisperEngine.IsWarm ? "warm" : "unloaded")}\n" +
        "Audio and speech recognition stay on this PC. Text goes to the API only as the dictated text with the Concise style, or as one meeting's title, languages, vocabulary and transcript text when you press its notes button with the API notes style.";
    public static async Task<string> SyntheticAsync(Settings settings, CancellationToken token)
    {
        var directory = Path.Combine(Settings.Root, "checks", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var wav = Path.Combine(directory, "speech.wav"); var script = Path.Combine(directory, "generate.ps1");
        try
        {
            await File.WriteAllTextAsync(script, "param([string]$Output)\nAdd-Type -AssemblyName System.Speech\n$taskSynth = New-Object System.Speech.Synthesis.SpeechSynthesizer\n$taskSynth.SetOutputToWaveFile($Output)\n$taskSynth.Speak('This is a private voice test. Please do not delete the file. The number is forty two.')\n$taskSynth.Dispose()", token);
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var arg in new[] { "-NoProfile", "-File", script, "-Output", wav }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            await Task.WhenAll(stdout, stderr);
            if (process.ExitCode != 0) throw new InvalidOperationException("Windows speech synthesis unavailable.");
            WhisperEngine.Shutdown(); var watch = Stopwatch.StartNew();
            var result = (await WhisperEngine.TranscribeRecordingAsync(wav, settings, token)).Text; cold = watch.Elapsed.TotalSeconds;
            watch.Restart(); var second = (await WhisperEngine.TranscribeRecordingAsync(wav, settings, token)).Text; warm = watch.Elapsed.TotalSeconds;
            if (!result.Contains("private", StringComparison.OrdinalIgnoreCase) || !result.Contains("not delete", StringComparison.OrdinalIgnoreCase) || !second.Contains("not delete", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Synthetic speech did not preserve the expected phrase.");
            return Describe(settings) + $"\nSynthetic speech passed. Cold {cold:0.00}s; warm {warm:0.00}s.\nThese results measure generated speech on the selected model, not microphone accuracy.";
        }
        finally { File.Delete(wav); File.Delete(script); Directory.Delete(directory); }
    }
}
