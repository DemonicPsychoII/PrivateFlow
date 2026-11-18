using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static Harness;

static class PublicationChecks
{
    private static readonly Regex PrivateHost = new(@"\.ts[.]net\b", RegexOptions.IgnoreCase);
    private static readonly Regex Address = new(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])");
    private static readonly Regex Email = new(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase);
    private static readonly Regex UserPath = new(@"\b[A-Z]:[\\/]+Users[\\/]+([^\\/\r\n""']+)", RegexOptions.IgnoreCase);

    internal static bool Leaks(string text) => PrivateHost.IsMatch(text)
        || Address.Matches(text).Any(match => IPAddress.TryParse(match.Value, out var address) && address.GetAddressBytes() is var bytes
            && (bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and <= 127)))
        || Email.Matches(text).Any(match => !Regex.IsMatch(match.Value, @"(?:^|[+._@-])no-?reply(?:$|[+._@-])", RegexOptions.IgnoreCase))
        || UserPath.Matches(text).Any(match => !Regex.IsMatch(match.Groups[1].Value.Trim(), @"^(?:<[^>]+>|%[^%]+%|\$[^\s]+|Public|Default)(?:\s|$)", RegexOptions.IgnoreCase));

    public static void VerifyExport()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "PowerShell public export checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "export-public.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            (int Exit, string Output) Run(string command, params string[] arguments)
            {
                var start = new ProcessStartInfo(command) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                if (Path.GetFileName(command) == "powershell.exe")
                    start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Public export check timed out."); }
                return (process.ExitCode, output.Result + errors.Result);
            }
            string Git(params string[] arguments)
            {
                var result = Run("git", arguments);
                if (result.Exit != 0) throw new Exception(result.Output);
                return result.Output.Trim();
            }
            Git("init", "--quiet");
            Git("config", "user.name", "Publication fixture"); Git("config", "user.email", "noreply@example.org");
            Git("config", "commit.gpgsign", "false");
            Directory.CreateDirectory(Path.Combine(root, "scripts"));
            File.Copy(Path.Combine(repository.FullName, "scripts", "export-public.ps1"), Path.Combine(root, "scripts", "export-public.ps1"));
            Directory.CreateDirectory(Path.Combine(root, ".github", "workflows"));
            var runnerLabel = "example-runner-pool";
            File.WriteAllText(Path.Combine(root, ".github", "workflows", "build.yml"), "runs-on: " + runnerLabel + "-${{ github.run_id }}");
            var readme = Path.Combine(root, "README.md");
            File.WriteAllText(readme, "Archived content"); Git("add", "."); Git("commit", "--quiet", "-m", "fixture");
            var safeCommit = Git("rev-parse", "HEAD");
            var shells = new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" };
            foreach (var shell in shells)
            {
                (int Exit, string Output) Export(string target, string commit = "HEAD") => Run(shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "export-public.ps1"), "-Target", target, "-Commit", commit);
                File.WriteAllText(readme, "Uncommitted content");
                var target = Path.Combine(root, "snapshot-" + Guid.NewGuid().ToString("N"));
                var exported = Export(target, safeCommit);
                Check(exported.Exit == 0 && File.ReadAllText(Path.Combine(target, "README.md")) == "Archived content"
                    && !Directory.Exists(Path.Combine(target, ".git")) && !Directory.Exists(Path.Combine(target, ".github", "workflows")),
                    $"{Path.GetFileName(shell)} exports the requested committed tree without history or workflows ({exported.Output.Trim()})");
                Check(Export(target, safeCommit) is { Exit: not 0 } refused && refused.Output.Contains("empty folder"), "public export refuses a nonempty target");
                foreach (var detail in new[] { "host." + "ts" + ".net", string.Join('.', 10, 1, 2, 3), string.Join('.', 172, 16, 0, 1),
                    string.Join('.', 192, 168, 0, 1), string.Join('.', 100, 64, 0, 1), "person" + "@" + "example.org", "C:" + @"\Users\Example Person\file.txt", runnerLabel })
                {
                    File.WriteAllText(readme, detail); Git("add", "README.md"); Git("commit", "--quiet", "-m", "fixture");
                    var rejected = Export(Path.Combine(root, "rejected-" + Guid.NewGuid().ToString("N")));
                    Check(rejected.Exit != 0 && rejected.Output.Contains("Private detail in public snapshot"),
                        $"{Path.GetFileName(shell)} rejects private detail or a runner label outside the excluded workflows");
                }
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    public static void VerifyTrackedFiles()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        var start = new ProcessStartInfo("git") { WorkingDirectory = repository.FullName, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("ls-files"); start.ArgumentList.Add("-z");
        using var process = Process.Start(start)!;
        var tracked = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        process.WaitForExit();
        Check(process.ExitCode == 0 && tracked.Length > 0, "the publication gate reads every tracked path from Git");
        foreach (var file in tracked)
        {
            var path = Path.Combine(repository.FullName, file);
            if (!File.Exists(path)) continue;
            string text;
            try
            {
                using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
                text = reader.ReadToEnd();
            }
            catch (DecoderFallbackException) { continue; }
            if (text.Contains('\0')) continue;
            Check(!Leaks(text), $"tracked text is safe to publish: {file}");
            if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && text.Any(character => character > 127))
                Check(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), $"non-ASCII PowerShell script has a UTF-8 BOM for Windows PowerShell: {file}");
        }
        var dot = ".";
        string Ip(params int[] bytes) => string.Join(dot, bytes);
        Check(new[] { Ip(10, 1, 2, 3), Ip(172, 16, 0, 1), Ip(172, 31, 255, 254), Ip(192, 168, 0, 1), Ip(100, 64, 0, 1), Ip(100, 127, 255, 254),
            "host" + dot + "ts" + dot + "net", "person" + "@" + "example.org", "C:" + @"\Users\" + @"Example Person\file.txt" }.All(Leaks), "generic leak patterns reject private hosts, addresses, email and concrete profile paths");
        Check(new[] { Ip(127, 0, 0, 1), Ip(100, 63, 255, 255), Ip(100, 128, 0, 1), Ip(172, 15, 0, 1), Ip(172, 32, 0, 1),
            "123+noreply@example.org", "123+example@users.noreply.github.com", @"C:\Users\<user>\file.txt", @"%USERPROFILE%\file.txt" }.All(text => !Leaks(text)), "loopback, public addresses, noreply email and placeholder paths pass");
    }
}
