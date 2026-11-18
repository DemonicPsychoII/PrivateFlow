using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PrivateFlow.Windows;

// Runs the bundled setup scripts. setup-engine.ps1 decides what "auto" means, so the recommendation
// lives in one place and an update that changes it reaches every PC that keeps Automatic.
internal static class SpeechSetup
{
    private static readonly SemaphoreSlim Gate = new(1);
    // The SDK appends the source commit, so every build has its own identity.
    public static string BuildIdentity => typeof(SpeechSetup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    public static bool HasNvidiaGpu => (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(folder => File.Exists(Path.Combine(folder, "nvidia-smi.exe")));
    public static bool IsRunning => Gate.CurrentCount == 0;

    // Returns null on success, otherwise a message for the user with the first error the script wrote. progress hears each line the
    // script prints, such as "Local model download: 42%".
    public static async Task<string?> RunAsync(string script, IEnumerable<string> arguments, CancellationToken cancellation, IProgress<string>? progress = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "scripts", script);
        if (!File.Exists(path)) return "Setup script is missing from this build. Reinstall the complete package.";
        if (!await Gate.WaitAsync(0, cancellation)) return "Speech setup is already running. Try again when it finishes.";
        try
        {
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path, "-DataRoot", Settings.Root }.Concat(arguments)) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            // PowerShell and the curl downloads it starts end with Private Flow, also when it quits or crashes during a download.
            try { HelperProcessJob.Attach(process); }
            catch (InvalidOperationException ex) { return ex.Message; }
            var output = ReportLinesAsync(process.StandardOutput, progress); var errors = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(cancellation); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            await Task.WhenAll(output, errors);
            return process.ExitCode == 0 ? null : FailureMessage(await errors);
        }
        finally { Gate.Release(); }
    }
    // A full path names the user's folders on screen; its file name says enough.
    internal static string FailureMessage(string errors) =>
        "Setup failed" + (errors.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() is { } line
            && WithoutFolders(line) is var reason
            ? $": {(reason.Length > 200 ? reason[..200] + "…" : reason).TrimEnd('.')}." : ".") + " Check network, available disk space and pinned download availability.";
    private static string WithoutFolders(string text) =>
        System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(text, @"'[A-Za-z]:\\[^']*'", path => $"'{WindowsFileName(path.Value.Trim('\''))}'"),
            @"(?<!')[A-Za-z]:\\[^\s']*", path => WindowsFileName(path.Value));
    // By the Windows separator itself: Path.GetFileName follows the system the code runs on, and the checks also run on Linux.
    private static string WindowsFileName(string path) => path[(path.LastIndexOf('\\') + 1)..];
    private static async Task ReportLinesAsync(StreamReader reader, IProgress<string>? progress)
    {
        while (await reader.ReadLineAsync() is { } line)
            if (line.Trim().Length > 0) progress?.Report(line.Trim());
    }

    // settings.json has one owner, the app. A setup script it runs writes what it installed to the result file it is given, not to
    // settings.json, and the app stores exactly those settings through Settings.SaveAs on the window's thread, like every other save:
    // preferences saved while a setup runs stay saved, and the window's unsaved ones stay unsaved.
    // Runs a setup script and stores what it installed. Returns null or a message for the user, and the names of the settings that changed.
    // run replaces RunAsync for the verification's stand-ins.
    public static async Task<(string? Error, IReadOnlyList<string> Changed)> InstallAsync(Settings settings, string script, IEnumerable<string> arguments, CancellationToken cancellation,
        IProgress<string>? progress = null, Func<string, IEnumerable<string>, CancellationToken, IProgress<string>?, Task<string?>>? run = null)
    {
        var resultFile = Path.Combine(Settings.Root, $"setup-result-{Guid.NewGuid():N}.json");
        try
        {
            if (await (run ?? RunAsync)(script, [.. arguments, "-ResultFile", resultFile], cancellation, progress) is { } error) return (error, []);
            var (installed, refused) = ReadResult(resultFile);
            if (refused is not null) return ($"Setup finished, but its result was refused: {refused}.", []);
            try { return (null, TakeInstalled(settings, installed)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ($"Setup finished, but what it installed could not be stored: {ex.Message}", []); }
        }
        finally { try { File.Delete(resultFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }
    internal static IReadOnlyList<string> TakeInstalled(Settings settings, IReadOnlyDictionary<string, object?> installed)
    {
        var next = settings.Snapshot();
        var changed = new List<string>();
        foreach (var (name, value) in installed)
        {
            var property = typeof(Settings).GetProperty(name)!;
            if (Equals(property.GetValue(next), value)) continue;
            property.SetValue(next, value); changed.Add(name);
        }
        if (changed.Count > 0) settings.SaveAs(next);
        return changed;
    }
    // A result is input from another process, read here without ever throwing: at most 64 KiB; only the settings a setup installs; each
    // path valid and, with every link on it resolved, inside this data folder's own models or runtime folder (the final-pass model may be
    // empty); the backend and model among those offered; the setup version in its form; paths not marked as typed. Anything else refuses
    // the whole result, with the reason. No result file, as after a download of one model, installs nothing.
    internal static (Dictionary<string, object?> Values, string? Refused) ReadResult(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return ([], null);
            if (info.Length > 65536) return ([], "it is larger than a setup result can be");
            var values = new Dictionary<string, object?>();
            foreach (var (name, element) in System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(File.ReadAllText(file)) ?? [])
            {
                if (!PreferenceSections.Installed.Contains(name)) return ([], $"it names {name}, which a setup does not install");
                var value = System.Text.Json.JsonSerializer.Deserialize(element, typeof(Settings).GetProperty(name)!.PropertyType);
                if (ResultProblem(name, value) is { } problem) return ([], problem);
                values[name] = value;
            }
            return (values, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or NotSupportedException)
        { return ([], $"it cannot be read as a setup result ({ex.GetType().Name})"); }
    }
    private static string? ResultProblem(string name, object? value) => value switch
    {
        string path when name.EndsWith("Path", StringComparison.Ordinal) =>
            path.Length == 0 && name == nameof(Settings.MeetingModelPath) || InsideSetupFolders(path) ? null : $"its {name} is not a path inside this data folder's models or runtime folder",
        string backend when name == nameof(Settings.SpeechBackend) => backend is "auto" or "cpu" or "cuda12" ? null : $"its {name} is not one offered",
        string model when name == nameof(Settings.SpeechModel) =>
            model is "auto" or PrivateFlow.Core.ModelCatalogue.SpeechTurbo or PrivateFlow.Core.ModelCatalogue.SpeechMedium or PrivateFlow.Core.ModelCatalogue.SpeechBase ? null : $"its {name} is not one offered",
        string version when name == nameof(Settings.SpeechSetupVersion) =>
            System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d+\.\d+\.\d+(?:[+.-][0-9A-Za-z.+-]{1,80})?$") ? null : $"its {name} is not a version",
        bool custom when name == nameof(Settings.SpeechPathsCustom) => custom ? "it marks the paths as entered by hand" : null,
        _ => $"its {name} has no value",
    };
    private static bool InsideSetupFolders(string path)
    {
        if (path.Length > 1024 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || !Path.IsPathFullyQualified(path)) return false;
        var real = ModelFiles.RealPath(path); var root = ModelFiles.RealPath(Settings.Root);
        return new[] { "models", "runtime" }.Any(folder => real.StartsWith(Path.Combine(root, folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    // Once per installed build, Automatic choices are brought to the current recommendation. The first
    // setup stays an explicit action, and paths typed in by hand are left alone.
    public static async Task<(string? Notice, string? Error, IReadOnlyList<string> Changed)> FollowRecommendationAsync(Settings settings)
    {
        if (settings.SpeechPathsCustom || settings.SpeechSetupVersion == BuildIdentity || !File.Exists(settings.EnginePath)) return (null, null, []);
        if (settings.SpeechBackend != "auto" && settings.SpeechModel != "auto") return (null, null, []);
        var (previousEngine, previousModel) = (settings.EnginePath, settings.ModelPath);
        var (error, changed) = await InstallAsync(settings, "setup-engine.ps1", ["-Backend", settings.SpeechBackend, "-Model", settings.SpeechModel, "-SetupVersion", BuildIdentity], CancellationToken.None);
        if (error is not null) return (null, "Could not bring the speech setup up to date; the current one stays in use. Private Flow tries again at the next start.", []);
        return string.Equals(previousEngine, settings.EnginePath, StringComparison.OrdinalIgnoreCase) && string.Equals(previousModel, settings.ModelPath, StringComparison.OrdinalIgnoreCase)
            ? (null, null, changed) : ($"Speech recognition switched to the recommended setup: {Processor(settings.EnginePath)}, {ModelName(settings.ModelPath)}.", null, changed);
    }
    public static string Processor(string enginePath) => enginePath.Contains("cuda", StringComparison.OrdinalIgnoreCase) ? "GPU" : "CPU";
    public static string ModelName(string modelPath) => Path.GetFileNameWithoutExtension(modelPath) is var name && name.StartsWith("ggml-") ? name[5..] : Path.GetFileName(modelPath);
}
