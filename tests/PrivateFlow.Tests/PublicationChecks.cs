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
            Directory.CreateDirectory(Path.Combine(root, "docs"));
            // The fixture's own internal paths, as public-snapshot/exclude.txt of a commit lists them, and a public counterpart.
            var internalDocument = "docs/INTERNAL_NOTES.md";
            File.WriteAllText(Path.Combine(root, "docs", "INTERNAL_NOTES.md"), "The job label is " + runnerLabel + ".");
            Directory.CreateDirectory(Path.Combine(root, "public-snapshot", "files"));
            File.WriteAllText(Path.Combine(root, "public-snapshot", "exclude.txt"), "# Internal paths\n" + internalDocument + "\ntests/Internal.Scenarios\nscripts/internal-only.ps1\nscripts/internal-tools\n");
            File.WriteAllText(Path.Combine(root, "RULES.md"), "Internal rules");
            File.WriteAllText(Path.Combine(root, "public-snapshot", "files", "RULES.md"), "Public rules");
            // Public release notes; internal notes are a file of their own, left out by path like the internal document.
            File.WriteAllText(Path.Combine(root, "docs", "RELEASE_NOTES.md"), "# Private Flow 0.5.9 (beta): fixture\n\n- Public line.\n");
            // The development repository is a remote of the checkout; the public repository may be named.
            const string developmentRepository = "example-owner/internal-app", publicRepository = "example-owner/public-app";
            // The bundled licence texts the scan may exempt from the email rule, pinned by SHA-256 as in the repository.
            var privateAddress = string.Join('.', 192, 168, 7, 8);
            var pinnedNotice = "Contact: author" + "@" + "example.org";
            var pinnedWithLabel = "Built on " + runnerLabel;
            string Sha(string content) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
            File.WriteAllText(Path.Combine(root, "THIRD_PARTY_NOTICES.sha256"), $"{Sha(pinnedNotice)}  licenses/runtime/THIRD-PARTY-NOTICES.TXT\n{Sha(pinnedWithLabel)}  licenses/runtime/LICENSE.TXT\n");
            var version = string.Join('.', 10, 0, 0, 0);
            // Indented, one member per line, as the .NET SDK writes deps.json.
            var dependencies = string.Join("\n", "{", "  \"targets\": {", "    \".NETCoreApp,Version=v10.0/win-x64\": {", "      \"runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.14\": {",
                "        \"runtime\": {", "          \"System.Runtime.dll\": {", "            \"assemblyVersion\": \"" + version + "\",", "            \"fileVersion\": \"" + version + "\"", "          }", "        },",
                "        \"native\": {", "          \"coreclr.dll\": {", "            \"fileVersion\": \"" + version + "\"", "          }", "        }", "      }", "    }", "  }", "}");
            // What runs only for the development repository, as the fixture's exclude.txt lists it.
            var internalOnly = new[] { "tests/Internal.Scenarios/Scenarios.cs", "scripts/internal-only.ps1", "scripts/internal-tools/payload.py", internalDocument };
            foreach (var file in internalOnly) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!); File.WriteAllText(Path.Combine(root, file), "internal"); }
            File.WriteAllText(Path.Combine(root, "scripts", "verify.ps1"), "public");
            // A person among the authors: the scan refuses the name, its parts and the address without being told them.
            File.WriteAllText(readme, "Archived content, from " + publicRepository); Git("add", "."); Git("commit", "--quiet", "-m", "fixture", "--author", "Mara Quint <mara.quint" + "@" + "example.org>");
            // A host and path in other letter cases are the same repository.
            Git("remote", "add", "origin", "https://GitHub.COM/" + developmentRepository.ToUpperInvariant() + ".git"); Git("remote", "add", "public", "git" + "@" + "github.com:" + publicRepository + ".git");
            var safeCommit = Git("rev-parse", "HEAD");
            var shells = new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" };
            foreach (var shell in shells)
            {
                var name = Path.GetFileName(shell);
                (int Exit, string Output) Export(string target, string commit = "HEAD") => Run(shell, "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "export-public.ps1"), "-Target", target, "-Commit", commit, "-PublicRepository", publicRepository);
                File.WriteAllText(readme, "Uncommitted content");
                var target = Path.Combine(root, "snapshot-" + Guid.NewGuid().ToString("N"));
                var exported = Export(target, safeCommit);
                Check(exported.Exit == 0 && File.ReadAllText(Path.Combine(target, "README.md")) == "Archived content, from " + publicRepository
                    && !Directory.Exists(Path.Combine(target, ".git")) && !Directory.Exists(Path.Combine(target, ".github", "workflows"))
                    && !File.Exists(Path.Combine(target, "docs", "INTERNAL_NOTES.md")),
                    $"{Path.GetFileName(shell)} exports the requested committed tree without history, workflows or the internal document that names the runner label, and names the public repository ({exported.Output.Trim()})");
                Check(exported.Exit == 0 && internalOnly.All(file => !File.Exists(Path.Combine(target, file))) && !Directory.Exists(Path.Combine(target, "tests", "Internal.Scenarios"))
                    && !Directory.Exists(Path.Combine(target, "scripts", "internal-tools")) && !Directory.Exists(Path.Combine(target, "public-snapshot")) && File.Exists(Path.Combine(target, "scripts", "verify.ps1")),
                    $"{name}: the snapshot holds none of the paths of public-snapshot/exclude.txt nor that folder, and keeps the rest");
                Check(exported.Exit == 0 && File.ReadAllText(Path.Combine(target, "RULES.md")) == "Public rules", $"{name}: a file of public-snapshot/files replaces its internal counterpart");
                Check(Export(target, safeCommit) is { Exit: not 0 } refused && refused.Output.Contains("empty folder"), "public export refuses a nonempty target");
                // Release notes and the extracted package are scanned with the same rules (scripts/publish-public.ps1).
                (int Exit, string Output) ExportAlso(string published, params string[] more)
                {
                    var result = Run(shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "export-public.ps1"),
                        "-Target", Path.Combine(root, "scanned-" + Guid.NewGuid().ToString("N")), "-Commit", safeCommit, "-PublicRepository", publicRepository, "-AlsoScan", published }.Concat(more).ToArray());
                    // PowerShell 7 colours its error view and wraps it at the console width; the checks read the plain message.
                    return (result.Exit, Regex.Replace(Regex.Replace(result.Output, @"\x1b\[[0-9;]*m", ""), @"\s*\r?\n\s*(?:\|\s*)?", " "));
                }
                string Package(Action<string>? change = null)
                {
                    var package = Path.Combine(root, "package-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(Path.Combine(package, "licenses", "runtime"));
                    File.WriteAllText(Path.Combine(package, "PrivateFlow.deps.json"), dependencies);
                    File.WriteAllText(Path.Combine(package, "licenses", "runtime", "THIRD-PARTY-NOTICES.TXT"), pinnedNotice);
                    File.WriteAllText(Path.Combine(package, "licenses", "runtime", "LICENSE.TXT"), "MIT licence of the runtime");
                    // Binary content is searched for the deny rules only, so an address-like byte string passes.
                    File.WriteAllBytes(Path.Combine(package, "address.bin"), new byte[] { 0, 255 }.Concat(Encoding.ASCII.GetBytes(privateAddress)).ToArray());
                    change?.Invoke(package);
                    return package;
                }
                var accepted = ExportAlso(Package());
                Check(accepted.Exit == 0, $"{name}: a package passes whose deps.json lists .NET versions where the build writes them, whose pinned runtime notice names its author and whose binary holds an address-like byte string ({accepted.Output.Trim()})");
                var notes = Path.Combine(root, "notes-" + Guid.NewGuid().ToString("N") + ".md");
                foreach (var (detail, reason) in new[] { (privateAddress, "the private address"), ("person" + "@" + "example.org", "the email address"), (runnerLabel, "the internal runner label"),
                    ("{\"serverVersion\":\"" + privateAddress + "\"}", "the private address"), ("Thanks, Mara.", "a part of the name of a Git identity"), ("Mara Quint", "the name of a Git identity"),
                    ("History (#53): kept", "the pull request reference #53"), ("see " + internalDocument, "a reference to " + internalDocument), ("compare in " + developmentRepository.ToUpperInvariant(), "the name of a development repository") })
                {
                    File.WriteAllText(notes, "- Fixed: " + detail);
                    var rejected = ExportAlso(notes);
                    Check(rejected.Exit != 0 && rejected.Output.Contains("Private detail in published file") && rejected.Output.Contains(reason),
                        $"{name}: release notes with {reason} are refused, naming the file and the pattern ({detail})");
                }
                File.WriteAllText(notes, "- A quintet ran a marathon.");
                Check(ExportAlso(notes) is { Exit: 0 }, $"{name}: an author's name inside longer words is no name");
                foreach (var (label, change, expected) in new (string, Action<string>, string)[] {
                    ("a text file with a private address", package => File.WriteAllText(Path.Combine(package, "private-capture.txt"), "Server " + privateAddress), "private-capture.txt contains the private address"),
                    ("a settings file with a version-shaped private address", package => File.WriteAllText(Path.Combine(package, "settings.json"), "{\"Version\":\"" + string.Join('.', 10, 1, 2, 3) + "\"}"), "settings.json contains the private address"),
                    ("deps.json with a private address outside the version fields", package => File.WriteAllText(Path.Combine(package, "PrivateFlow.deps.json"), "{\"serverVersion\":\"" + privateAddress + "\"," + dependencies[1..]), "PrivateFlow.deps.json contains the private address"),
                    ("another JSON file shaped like deps.json", package => File.WriteAllText(Path.Combine(package, "Other.deps.json"), dependencies), "Other.deps.json contains the private address"),
                    ("an email in another file under licenses", package => File.WriteAllText(Path.Combine(package, "licenses", "private-settings.json"), "{\"ownerEmail\":\"owner" + "@" + "example.org\"}"), "licenses/private-settings.json contains the email address"),
                    ("an address added to the pinned notice", package => File.AppendAllText(Path.Combine(package, "licenses", "runtime", "THIRD-PARTY-NOTICES.TXT"), "\nOwner: owner" + "@" + "example.org"), "THIRD-PARTY-NOTICES.TXT contains the email address"),
                    ("the runner label inside a pinned licence", package => File.WriteAllText(Path.Combine(package, "licenses", "runtime", "LICENSE.TXT"), pinnedWithLabel), "LICENSE.TXT contains the internal runner label"),
                    ("a private address and host in a file name", package => File.WriteAllText(Path.Combine(package, privateAddress + "-host." + "ts" + ".net.txt"), "safe"), "the name"),
                    ("the runner label as UTF-16 in a binary file", package => File.WriteAllBytes(Path.Combine(package, "symbols.pdb"), new byte[] { 0, 255 }.Concat(Encoding.Unicode.GetBytes(runnerLabel)).ToArray()), "symbols.pdb contains the internal runner label"),
                    ("the runner label written with \\u escapes in a JSON file", package => File.WriteAllText(Path.Combine(package, "settings.json"), "{\"runner\":\"" + runnerLabel.Replace("e", "\\u0065") + "\"}"), "settings.json contains the internal runner label"),
                    // ConvertFrom-Json keeps only the later of two duplicate members; the escapes are decoded on the raw text instead.
                    ("the runner label \\u-escaped in the earlier of two duplicate JSON members", package => File.WriteAllText(Path.Combine(package, "settings.json"), "{\"runner\":\"" + runnerLabel.Replace("e", "\\u0065") + "\",\"runner\":\"safe\"}"), "settings.json contains the internal runner label"),
                    ("the runner label with JSON escapes in a text file that is no JSON", package => File.WriteAllText(Path.Combine(package, "notes.txt"), "Label: " + runnerLabel.Replace("-", "\\u002d")), "notes.txt contains the internal runner label"),
                    // Escapes decoded in turn until nothing changes, and every file read as UTF-16LE too, with or without a byte-order mark.
                    ("a part of an author's name split across a JSON and a percent escape", package => File.WriteAllText(Path.Combine(package, "settings.json"), "{\"note\":\"\\u004d%61ra\"}"), "settings.json contains a part of the name of a Git identity"),
                    ("a JSON-escaped part of an author's name in UTF-16LE without a byte-order mark", package => File.WriteAllBytes(Path.Combine(package, "notes.json"), Encoding.Unicode.GetBytes("{\"note\":\"\\u004dara\"}")), "notes.json contains a part of the name of a Git identity"),
                    ("a part of an author's name in camel case in a text file", package => File.WriteAllText(Path.Combine(package, "credits.txt"), "Signature`nMara"), "credits.txt contains a part of the name of a Git identity"),
                    ("a part of an author's name as UTF-16 in a binary file", package => File.WriteAllBytes(Path.Combine(package, "symbols.pdb"), new byte[] { 0, 255 }.Concat(Encoding.Unicode.GetBytes("Signed by QUINT")).ToArray()), "symbols.pdb contains a part of the name of a Git identity"),
                    ("a percent-encoded runner label", package => File.WriteAllText(Path.Combine(package, "link.txt"), "https://example.org/" + runnerLabel.Replace("-", "%2D")), "link.txt contains the internal runner label"),
                    ("the runner label as ASCII in a binary file", package => File.WriteAllBytes(Path.Combine(package, "symbols.dll"), new byte[] { 0, 255 }.Concat(Encoding.ASCII.GetBytes(runnerLabel.ToUpperInvariant())).ToArray()), "symbols.dll contains the internal runner label"),
                    // The development repository a binary would compare builds in, as ASCII and as UTF-16LE, and a call of an excluded script.
                    ("the development repository as ASCII in a binary file", package => File.WriteAllBytes(Path.Combine(package, "PrivateFlow.dll"), new byte[] { 0, 255 }.Concat(Encoding.ASCII.GetBytes("repos/" + developmentRepository + "/compare")).ToArray()), "PrivateFlow.dll contains the name of a development repository"),
                    ("the development repository as UTF-16LE in a binary file", package => File.WriteAllBytes(Path.Combine(package, "PrivateFlow.dll"), new byte[] { 0, 255 }.Concat(Encoding.Unicode.GetBytes(developmentRepository)).ToArray()), "PrivateFlow.dll contains the name of a development repository"),
                    ("a call of an excluded script", package => File.WriteAllText(Path.Combine(package, "run.ps1"), "& .\\internal-only.ps1"), "run.ps1 contains a reference to scripts/internal-only.ps1"),
                    ("a pull request reference in published notes", package => File.WriteAllText(Path.Combine(package, "release-notes.md"), "- Fixed (#48)."), "release-notes.md contains the pull request reference #48"),
                    // The decoding of the contents applies to names and pull request references too.
                    ("a percent-encoded pull request reference in published notes", package => File.WriteAllText(Path.Combine(package, "release-notes.md"), "- Fixed (%2348)."), "release-notes.md contains the pull request reference #48"),
                    ("a percent-encoded name of an excluded script", package => File.WriteAllText(Path.Combine(package, "internal-only%2Eps1"), "x"), "the name internal-only%2Eps1 contains a reference to scripts/internal-only.ps1") })
                {
                    var refusedPackage = ExportAlso(Package(change));
                    Check(refusedPackage.Exit != 0 && refusedPackage.Output.Contains("Private detail in published file") && refusedPackage.Output.Contains(expected), $"{name}: a package with {label} is refused ({expected})");
                }
                // A -DenyText identifier with letters outside ASCII is found in any letter case, in text and in binary bytes.
                var deniedName = "J\u00f6rg";
                foreach (var (label, change) in new (string, Action<string>)[] {
                    ("a text file", package => File.WriteAllText(Path.Combine(package, "credits.txt"), "Built by " + deniedName)),
                    ("a binary file in UTF-8, upper case", package => File.WriteAllBytes(Path.Combine(package, "symbols.dll"), new byte[] { 0, 255 }.Concat(Encoding.UTF8.GetBytes(deniedName.ToUpperInvariant())).ToArray())),
                    ("a binary file in UTF-16LE", package => File.WriteAllBytes(Path.Combine(package, "symbols.pdb"), new byte[] { 0, 255 }.Concat(Encoding.Unicode.GetBytes(deniedName)).ToArray())) })
                {
                    var refusedName = ExportAlso(Package(change), "-DenyText", deniedName.ToLowerInvariant());
                    Check(refusedName.Exit != 0 && refusedName.Output.Contains("given with -DenyText"), $"{name}: a -DenyText name with an umlaut is refused in {label}");
                }
                var denied = ExportAlso(Package(), "-DenyText", "author" + "@" + "example.org");
                Check(denied.Exit != 0 && denied.Output.Contains("given with -DenyText"), $"{name}: a deny rule wins over the pinned-licence exception");
                foreach (var detail in new[] { "host." + "ts" + ".net", string.Join('.', 10, 1, 2, 3), string.Join('.', 172, 16, 0, 1),
                    string.Join('.', 192, 168, 0, 1), string.Join('.', 100, 64, 0, 1), "person" + "@" + "example.org", "C:" + @"\Users\Example Person\file.txt", runnerLabel,
                    "[notes](docs/INTERNAL_NOTES.md#setup)", @"scripts\internal-tools\run.py", "https://github.com/Example-Owner/Internal-App/pull/3", "Fixed in #12." })
                {
                    File.WriteAllText(readme, detail); Git("add", "README.md"); Git("commit", "--quiet", "-m", "fixture");
                    var rejected = Export(Path.Combine(root, "rejected-" + Guid.NewGuid().ToString("N")));
                    Check(rejected.Exit != 0 && rejected.Output.Contains("Private detail in public snapshot"),
                        $"{Path.GetFileName(shell)} rejects private detail, a runner label, a reference to an excluded path, the development repository or a pull request reference ({detail})");
                }
                // A commit that does not say which of its paths are internal, or whose public counterpart replaces nothing, is not exported.
                Git("rm", "--quiet", "public-snapshot/exclude.txt"); File.WriteAllText(readme, "Archived content"); Git("add", "README.md"); Git("commit", "--quiet", "-m", "fixture");
                var unlisted = Export(Path.Combine(root, "unlisted-" + Guid.NewGuid().ToString("N")));
                Check(unlisted.Exit != 0 && unlisted.Output.Contains("exclude.txt"), $"{name}: a commit without public-snapshot/exclude.txt is not exported");
                Git("checkout", "--quiet", safeCommit, "--", "public-snapshot/exclude.txt");
                File.WriteAllText(Path.Combine(root, "public-snapshot", "files", "MISSING.md"), "Public"); Git("add", "public-snapshot"); Git("commit", "--quiet", "-m", "fixture");
                var stray = Export(Path.Combine(root, "stray-" + Guid.NewGuid().ToString("N")));
                Check(stray.Exit != 0 && stray.Output.Contains("replaces no file"), $"{name}: a public counterpart that replaces no file is refused");
                Git("rm", "--quiet", "public-snapshot/files/MISSING.md"); Git("commit", "--quiet", "-m", "fixture");
            }
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    // scripts/publish-public.ps1 -DryRun against throwaway repositories: nothing is pushed and no release is touched.
    public static void VerifyPublishDryRun()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "PowerShell publication checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "publish-public.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-publish-check-" + Guid.NewGuid().ToString("N"));
        var emptyConfiguration = Path.Combine(root, "empty.gitconfig");
        var temporary = Path.Combine(root, "temp");
        var fakeCli = Path.Combine(root, "bin");
        Directory.CreateDirectory(temporary);
        File.WriteAllText(emptyConfiguration, "");
        try
        {
            (int Exit, string Output) Run(string folder, string command, params string[] arguments)
            {
                var start = new ProcessStartInfo(command) { WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);
                // The owner's global Git settings, such as tag signing, stay out of the throwaway repositories.
                start.Environment["GIT_CONFIG_GLOBAL"] = emptyConfiguration; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
                // The work folders a dry run keeps land inside this check's own folder.
                start.Environment["TEMP"] = start.Environment["TMP"] = temporary;
                // A stand-in GitHub CLI that is signed in as public-fixture, and an ambient Git identity the publisher must not take.
                start.Environment["PATH"] = fakeCli + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
                if (command != "git")
                    foreach (var variable in new[] { "GIT_AUTHOR", "GIT_COMMITTER" })
                    { start.Environment[variable + "_NAME"] = "Ambient Author"; start.Environment[variable + "_EMAIL"] = "1+ambient@users.noreply.github.com"; }
                if (Path.GetFileName(command) == "powershell.exe")
                    start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(120000)) { process.Kill(true); throw new Exception("Publication check timed out."); }
                return (process.ExitCode, output.Result + errors.Result);
            }
            string Git(string folder, params string[] arguments)
            {
                var result = Run(folder, "git", arguments);
                if (result.Exit != 0) throw new Exception(result.Output);
                return result.Output.Trim();
            }
            Directory.CreateDirectory(fakeCli);
            // The stand-in GitHub CLI: signed in as public-fixture; it lists the draft release of v0.5.9 from listing.json and reads it by
            // its ID from current.json, both as publish-public.ps1's jq filter leaves them, and records each change of the release.
            var releaseCalls = Path.Combine(fakeCli, "calls.txt");
            File.WriteAllText(Path.Combine(fakeCli, "gh.cmd"), "@echo off\r\nif \"%1 %2\"==\"api user\" (echo public-fixture& exit /b 0)\r\n"
                + "if \"%1 %2\"==\"api --paginate\" (type \"%~dp0listing.json\"& exit /b 0)\r\nif \"%1 %2\"==\"api --method\" (echo %*>>\"%~dp0calls.txt\"& exit /b 0)\r\n"
                + "if \"%1\"==\"api\" (type \"%~dp0current.json\"& exit /b 0)\r\nexit /b 1\r\n");
            const string publicName = "Public Fixture", publicEmail = "0+public-fixture@users.noreply.github.com";
            var development = Path.Combine(root, "development.git"); var publicRemote = Path.Combine(root, "public.git"); var checkout = Path.Combine(root, "checkout");
            Git(root, "init", "--quiet", "--bare", "-b", "main", development); Git(root, "init", "--quiet", "--bare", "-b", "main", publicRemote);
            Git(root, "clone", "--quiet", development, checkout);
            foreach (var setting in new[] { ("user.name", "Publication fixture"), ("user.email", "noreply@example.org") }) Git(checkout, "config", setting.Item1, setting.Item2);
            Directory.CreateDirectory(Path.Combine(checkout, "scripts")); Directory.CreateDirectory(Path.Combine(checkout, "docs"));
            foreach (var script in new[] { "publish-public.ps1", "export-public.ps1", "install.ps1" })
                File.Copy(Path.Combine(repository.FullName, "scripts", script), Path.Combine(checkout, "scripts", script));
            // A release key of this check: install.ps1 of the fixture pins its public key, and the stand-in release signs with it.
            using var releaseKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            using var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var point = releaseKey.ExportParameters(false).Q;
            var checkoutInstaller = Path.Combine(checkout, "scripts", "install.ps1");
            var pinned = Regex.Replace(File.ReadAllText(checkoutInstaller), @"(?m)^\$taskReleaseKey = '[^']*'", "$taskReleaseKey = '" + Convert.ToBase64String([.. point.X!, .. point.Y!]) + "'");
            Check(pinned.Contains(Convert.ToBase64String([.. point.X!, .. point.Y!])), "the fixture's install.ps1 pins the check's release key");
            File.WriteAllText(checkoutInstaller, pinned);
            Directory.CreateDirectory(Path.Combine(checkout, "public-snapshot"));
            File.WriteAllText(Path.Combine(checkout, "public-snapshot", "exclude.txt"), "docs/INTERNAL_NOTES.md\n");
            File.WriteAllText(Path.Combine(checkout, "docs", "INTERNAL_NOTES.md"), "Internal");
            File.WriteAllText(Path.Combine(checkout, "docs", "RELEASE_NOTES.md"), "# Private Flow 0.5.9 (beta): fixture\n\n- Fixed a fixture.\n\n# Private Flow 0.5.8\n\n- Older.\n");
            var project = Path.Combine(checkout, "src", "PrivateFlow.Windows", "PrivateFlow.Windows.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project><PropertyGroup><Version>0.5.9</Version></PropertyGroup></Project>\n");
            Git(checkout, "add", "."); Git(checkout, "commit", "--quiet", "-m", "fixture"); Git(checkout, "tag", "v0.5.9"); Git(checkout, "push", "--quiet", "origin", "main", "v0.5.9");
            var commit = Git(checkout, "rev-parse", "HEAD");
            // A later tag whose tree still waits for its number.
            File.WriteAllText(Path.Combine(checkout, "docs", "RELEASE_NOTES.md"), "# Private Flow next (beta): pending\n\n- Pending.\n\n# Private Flow 0.5.10 (beta): fixture\n\n- Ten.\n");
            File.WriteAllText(project, "<Project><PropertyGroup><Version>0.5.10</Version></PropertyGroup></Project>\n");
            Git(checkout, "add", "."); Git(checkout, "commit", "--quiet", "-m", "pending"); Git(checkout, "tag", "v0.5.10"); Git(checkout, "push", "--quiet", "origin", "main", "v0.5.10");
            var seed = Path.Combine(root, "seed"); Git(root, "clone", "--quiet", publicRemote, seed);
            foreach (var setting in new[] { ("user.name", "Publication fixture"), ("user.email", "noreply@example.org") }) Git(seed, "config", setting.Item1, setting.Item2);
            File.WriteAllText(Path.Combine(seed, "README.md"), "Private Flow 0.5.8"); Git(seed, "add", "."); Git(seed, "commit", "--quiet", "-m", "Private Flow 0.5.8"); Git(seed, "push", "--quiet", "origin", "HEAD:main");
            // Executables with a version resource: the release build of 0.5.9 and a development build of it.
            byte[] Executable(string identity)
            {
                var path = Path.Combine(root, "executable-" + Guid.NewGuid().ToString("N") + ".exe");
                var made = Run(root, Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile", "-Command",
                    $"Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyInformationalVersion(\"{identity}\")] public class ExecutableFixture {{ public static void Main() {{}} }}' -OutputAssembly '{path}' -OutputType ConsoleApplication");
                if (made.Exit != 0 || !File.Exists(path)) throw new Exception("The executable fixture was not built: " + made.Output);
                return File.ReadAllBytes(path);
            }
            var releaseExecutable = Executable("0.5.9+release"); var developmentExecutable = Executable("0.5.9+" + commit[..7]);

            string Assets(Action<Dictionary<string, object?>>? changeManifest = null, Action<System.IO.Compression.ZipArchive>? changePackage = null, bool withExecutable = true, bool tamperChecksums = false,
                Func<byte[], byte[]>? changeBytes = null, Func<string, string>? changeManifestText = null, byte[]? executable = null, System.Security.Cryptography.ECDsa? signer = null)
            {
                var folder = Path.Combine(root, "assets-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
                var package = Path.Combine(folder, "PrivateFlow-win-x64.zip");
                using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
                {
                    if (withExecutable) using (var stream = archive.CreateEntry("PrivateFlow.exe").Open()) stream.Write(executable ?? releaseExecutable);
                    using (var writer = new StreamWriter(archive.CreateEntry("scripts/models.json").Open())) writer.Write("{\"Models\":[]}");
                    changePackage?.Invoke(archive);
                }
                if (changeBytes is not null) File.WriteAllBytes(package, changeBytes(File.ReadAllBytes(package)));
                string Sha(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
                // The manifest of a release names no commit, build time or tree state (scripts/package.py).
                var manifest = new Dictionary<string, object?> { ["schema"] = 1, ["project"] = "private-flow", ["board"] = "win-x64",
                    ["files"] = new[] { new Dictionary<string, object> { ["name"] = "PrivateFlow-win-x64.zip", ["profile"] = "release", ["version"] = "0.5.9", ["size"] = new FileInfo(package).Length, ["sha256"] = Sha(package) } } };
                changeManifest?.Invoke(manifest);
                var manifestText = System.Text.Json.JsonSerializer.Serialize(manifest);
                File.WriteAllText(Path.Combine(folder, "manifest.json"), changeManifestText?.Invoke(manifestText) ?? manifestText);
                var packageHash = tamperChecksums ? new string('0', 64) : Sha(package);
                var checksums = Path.Combine(folder, "SHA256SUMS");
                File.WriteAllText(checksums, $"{packageHash}  PrivateFlow-win-x64.zip\n{Sha(Path.Combine(folder, "manifest.json"))}  manifest.json\n");
                // r and s in Base64, as scripts/sign-checksums.py writes them.
                File.WriteAllText(Path.Combine(folder, "SHA256SUMS.sig"), Convert.ToBase64String((signer ?? releaseKey).SignData(File.ReadAllBytes(checksums), System.Security.Cryptography.HashAlgorithmName.SHA256)) + "\n");
                return folder;
            }
            var shells = new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" };
            (int Exit, string Output) PublishWith(string shell, string tag, string account, params string[] more) => Run(checkout, shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                Path.Combine(checkout, "scripts", "publish-public.ps1"), "-Tag", tag, "-PublicRemote", publicRemote,
                "-AuthorName", publicName, "-AuthorEmail", publicEmail, "-UploadAccount", account, "-DryRun" }.Concat(more).ToArray());
            // The draft release as the public repository lists it: the digest of each file in the asset folder, unless digestOf says otherwise.
            string Listing(string assets, Func<string, string?>? digestOf = null) => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["id"] = 7, ["draft"] = true,
                ["assets"] = new[] { "PrivateFlow-win-x64.zip", "manifest.json", "SHA256SUMS", "SHA256SUMS.sig" }.Select(file => new Dictionary<string, string?>
                {
                    ["name"] = file,
                    ["digest"] = digestOf is null ? "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(assets, file)))).ToLowerInvariant() : digestOf(file),
                }).ToArray(),
            });
            (int Exit, string Output) FinishWith(string shell, string assets, string listed, string current, bool dryRun, params string[] more)
            {
                File.WriteAllText(Path.Combine(fakeCli, "listing.json"), listed); File.WriteAllText(Path.Combine(fakeCli, "current.json"), current);
                File.Delete(releaseCalls);
                var arguments = new[] { "-Tag", "v0.5.9", "-PublicRemote", publicRemote, "-AuthorName", publicName, "-AuthorEmail", publicEmail, "-UploadAccount", "public-fixture", "-Finish", "-AssetFolder", assets };
                return Run(checkout, shell, new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(checkout, "scripts", "publish-public.ps1") }
                    .Concat(arguments).Concat(dryRun ? ["-DryRun"] : Array.Empty<string>()).Concat(more).ToArray());
            }
            (int Exit, string Output) Finish(string shell, string assets, params string[] more) => FinishWith(shell, assets, Listing(assets), Listing(assets), true, more);
            // Step 1, the snapshot and its tag.
            var clone = "";
            foreach (var shell in shells)
            {
                var name = Path.GetFileName(shell);
                var prepared = PublishWith(shell, "v0.5.9", "public-fixture");
                clone = Regex.Match(prepared.Output, @"prepared in (\S+); nothing was pushed").Groups[1].Value;
                var expectedIdentity = $"{publicName} <{publicEmail}>";
                Check(prepared.Exit == 0 && clone.Length > 0 && Git(clone, "log", "-1", "--format=%an <%ae>|%cn <%ce>|%s") == $"{expectedIdentity}|{expectedIdentity}|Private Flow 0.5.9"
                    && Git(clone, "for-each-ref", "refs/tags/v0.5.9", "--format=%(taggername) %(taggeremail)") == expectedIdentity
                    && Git(clone, "tag", "-n1", "v0.5.9").Contains("Private Flow 0.5.9") && !File.Exists(Path.Combine(clone, "docs", "INTERNAL_NOTES.md")) && !Directory.Exists(Path.Combine(clone, "public-snapshot")),
                    $"{name}: a dry run prepares the public commit and annotated tag as author, committer and tagger given, never the inherited Git identity ({prepared.Output.Trim()})");
                Check(prepared.Output.Contains("signed in as public-fixture"), $"{name}: a dry run names the GitHub account that would publish");
                var notes = clone.Length == 0 ? [] : Directory.GetFiles(Path.GetDirectoryName(clone)!, "release-notes.md", SearchOption.AllDirectories);
                Check(notes.Length == 1 && File.ReadAllText(notes[0]) == "- Fixed a fixture.\n", $"{name}: the release notes come from the tagged commit's section of the version");
                var otherAccount = PublishWith(shell, "v0.5.9", "other-account");
                Check(otherAccount.Exit != 0 && otherAccount.Output.Contains("not as -UploadAccount other-account"), $"{name}: publication refuses when the GitHub CLI is signed in as another account than -UploadAccount");
                var pending = PublishWith(shell, "v0.5.10", "public-fixture");
                Check(pending.Exit != 0 && pending.Output.Contains("still has a \"# Private Flow next\" section"), $"{name}: publication refuses a tag whose tree still has an unnumbered next section");
                Check(Git(publicRemote, "tag").Length == 0 && Git(publicRemote, "log", "-1", "--format=%s", "main") == "Private Flow 0.5.8", $"{name}: a dry run pushes nothing");
            }
            // The push a real first step makes; its tag starts the public release workflow, whose draft the second step checks.
            Git(clone, "push", "--quiet", "origin", "HEAD:main", "refs/tags/v0.5.9");
            foreach (var shell in shells)
            {
                var name = Path.GetFileName(shell);
                var rerun = PublishWith(shell, "v0.5.9", "public-fixture");
                Check(rerun.Exit == 0 && rerun.Output.Contains("already has v0.5.9 with this snapshot; nothing to push"), $"{name}: a rerun of the first step finds the pushed snapshot and pushes nothing ({rerun.Output.Trim()})");
                // Step 2, the draft's files.
                var finished = Finish(shell, Assets());
                Check(finished.Exit == 0 && finished.Output.Contains("every check passed"), $"{name}: the second step accepts a draft whose files are signed, consistent, public and of this release ({finished.Output.Trim()})");
                foreach (var (label, assets, expected) in new[] {
                    ("a checksum file signed by another key", Assets(signer: otherKey), "not signed by the release key"),
                    ("an executable of a development build", Assets(executable: developmentExecutable), "not the release build 0.5.9+release"),
                    ("a manifest naming the tagged commit", Assets(manifest => manifest["commit"] = commit), "it describes the development build (commit)"),
                    ("a manifest with a build time", Assets(manifest => manifest["built_at"] = "2026-11-19T10:00:00Z"), "it describes the development build (built_at)"),
                    ("a manifest with the tree state", Assets(manifest => manifest["dirty"] = false), "it describes the development build (dirty)"),
                    ("a manifest with a merge commit field", Assets(manifest => manifest["merge_commit"] = null), "it describes the development build (merge_commit)"),
                    ("a package whose file names the tagged commit", Assets(changePackage: archive => { using var writer = new StreamWriter(archive.CreateEntry("build.txt").Open()); writer.Write("Build 0.5.9+" + commit); }), "build.txt contains the ID of a commit of the development repository"),
                    ("a package file named after the tagged commit", Assets(changePackage: archive => archive.CreateEntry("build-" + commit + ".txt")), "the name build-"),
                    ("a binary holding the tagged commit as UTF-16LE", Assets(changePackage: archive => { using var stream = archive.CreateEntry("data.bin").Open(); stream.Write([0xFF, 0x00, 0x01, .. Encoding.Unicode.GetBytes(commit), 0x00]); }), "data.bin contains the ID of a commit of the development repository"),
                    ("a manifest with a field the build does not write", Assets(manifest => manifest["source"] = "a private build folder"), "it has a field the build does not write"),
                    ("a package without PrivateFlow.exe", Assets(withExecutable: false), "has no PrivateFlow.exe"),
                    ("a tampered SHA256SUMS", Assets(tamperChecksums: true), "does not match SHA256SUMS"),
                    ("a package with a private address in a text file", Assets(changePackage: archive => { using var writer = new StreamWriter(archive.CreateEntry("private-capture.txt").Open()); writer.Write("Server " + string.Join('.', 192, 168, 0, 1)); }), "private-capture.txt contains the private address"),
                    // Nothing outside the scanned entries: no archive or entry comment, no bytes after the archive.
                    ("a ZIP with an archive comment", Assets(changePackage: archive => archive.Comment = "docs/INTERNAL_NOTES.md"), "does not end with its end record"),
                    ("a ZIP with an entry comment", Assets(changePackage: archive => archive.CreateEntry("note.txt").Comment = "docs/INTERNAL_NOTES.md"), "carries an extra field or a comment"),
                    ("a ZIP with bytes after it", Assets(changeBytes: bytes => [.. bytes, .. Encoding.ASCII.GetBytes("docs/INTERNAL_NOTES.md")]), "does not end with its end record"),
                    // The manifest strictly, as the app reads it: no comment, no repeated member.
                    ("a manifest with a repeated member", Assets(changeManifestText: text => text.Replace("\"schema\":1", "\"schema\":1,\"Schema\":1")), "it names a member twice"),
                    ("a manifest with a comment", Assets(changeManifestText: text => text.Replace("\"schema\":1", "/* docs/INTERNAL_NOTES.md */\"schema\":1")), "it holds a comment or an escape"),
                    ("a manifest with a trailing comma", Assets(changeManifestText: text => text.TrimEnd().TrimEnd('}') + ",}"), "it is not strict JSON") })
                {
                    var refused = Finish(shell, assets);
                    var refusedAsExpected = refused.Exit != 0 && refused.Output.Contains(expected);
                    Check(refusedAsExpected, $"{name}: publication refuses {label} ({expected}){(refusedAsExpected ? "" : $": exit {refused.Exit}, " + refused.Output.Trim())}");
                }
                var unsigned = Finish(shell, Assets(), "-Publisher", "CN=Example Publisher");
                Check(unsigned.Exit != 0 && unsigned.Output.Contains("no signed inventory"), $"{name}: with -Publisher, publication refuses a package without a signed inventory");
                // Without -Publisher, the publisher configured in the checkout's install.ps1 applies.
                File.WriteAllText(checkoutInstaller, pinned.Replace("$taskReleasePublisher = ''", "$taskReleasePublisher = 'CN=Example Publisher'"));
                var configured = Finish(shell, Assets());
                File.WriteAllText(checkoutInstaller, pinned);
                Check(configured.Exit != 0 && configured.Output.Contains("no signed inventory"), $"{name}: without -Publisher, publication uses the publisher configured in install.ps1 and refuses an unsigned package");
                // The files checked are the release's own: every digest is required and must match, also when they come from -AssetFolder.
                var checkedAssets = Assets(); var otherAssets = Assets();
                var withoutDigest = FinishWith(shell, checkedAssets, Listing(checkedAssets, _ => null), Listing(checkedAssets), true);
                Check(withoutDigest.Exit != 0 && withoutDigest.Output.Contains("gives no digest of"), $"{name}: publication refuses a release that gives no digest of a file");
                var otherDigest = FinishWith(shell, checkedAssets, Listing(otherAssets), Listing(otherAssets), true);
                Check(otherDigest.Exit != 0 && otherDigest.Output.Contains("is not the file the release of v0.5.9 holds"), $"{name}: publication refuses files that are not the ones the release holds");
                // Publication by the release's ID, only while it holds the files checked.
                var published = FinishWith(shell, checkedAssets, Listing(checkedAssets), Listing(checkedAssets), false);
                var calls = File.Exists(releaseCalls) ? File.ReadAllText(releaseCalls) : "";
                Check(published.Exit == 0 && published.Output.Contains("Published v0.5.9") && calls.Contains("releases/7") && calls.Contains("draft=false") && calls.Contains("make_latest=true") && !calls.Contains("draft=true"),
                    $"{name}: the checked draft is published by its ID as the latest release ({published.Output.Trim()} {calls.Trim()})");
                var replaced = FinishWith(shell, checkedAssets, Listing(checkedAssets), Listing(otherAssets), false);
                calls = File.Exists(releaseCalls) ? File.ReadAllText(releaseCalls) : "";
                Check(replaced.Exit != 0 && replaced.Output.Contains("changed during the checks") && !calls.Contains("draft=false"),
                    $"{name}: a draft whose files a rerun replaced during the checks is not published ({calls.Trim()})");
            }
            // A public tag with other content is never replaced, and a draft built from it is not published.
            Git(seed, "pull", "--quiet", "origin", "main"); File.WriteAllText(Path.Combine(seed, "README.md"), "Other content"); Git(seed, "add", "."); Git(seed, "commit", "--quiet", "-m", "other");
            Git(seed, "tag", "--force", "v0.5.9"); Git(seed, "push", "--quiet", "--force", "origin", "HEAD:main", "refs/tags/v0.5.9");
            var other = PublishWith("pwsh.exe", "v0.5.9", "public-fixture");
            Check(other.Exit != 0 && other.Output.Contains("already has v0.5.9 with other content"), "pwsh.exe: a public tag with other content is never replaced");
            var otherDraft = Finish("pwsh.exe", Assets());
            Check(otherDraft.Exit != 0 && otherDraft.Output.Contains("does not hold the snapshot of the development tag"), "pwsh.exe: a draft built from a public tag with other content is not published");
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    // The dependency exemption as a table: Find-DependencyDetail of scripts/export-public.ps1, a rule on the raw lines, in both PowerShell versions.
    public static void VerifyDependencyExemption()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "PowerShell dependency exemption checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "export-public.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        // The real deps.json of the built app, which the build writes before these checks run.
        var built = Directory.EnumerateFiles(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows", "bin"), "PrivateFlow.deps.json", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        var address = string.Join('.', 192, 168, 7, 8);
        var version = string.Join('.', 10, 0, 0, 0);
        string Lines(IEnumerable<string> lines) => string.Join("\n", lines);
        // The shape the .NET SDK writes: indented, one member per line. Each piece replaces the members of one file entry;
        // a changed level replaces one line of the path above it.
        string Deps(string[] members, int level = -1, string? levelLine = null)
        {
            var path = new[] { "{", "  \"targets\": {", "    \"framework\": {", "      \"package/1.0\": {", "        \"runtime\": {", "          \"file.dll\": {" };
            if (level >= 0) path[level] = levelLine!;
            return Lines(path.Concat(members.Select(member => "            " + member)).Concat(new[] { "          }", "        }", "      }", "    }", "  }", "}" }));
        }
        string Member(string name, string value, string end = "") => $"\"{name}\": \"{value}\"{end}";
        var sdkForm = new[] { Member("assemblyVersion", version, ","), Member("fileVersion", version) };
        var cases = new (string Label, string Json, bool Accepted)[]
        {
            ("version lines as the SDK writes them", Deps(sdkForm), true),
            ("version lines with CRLF line ends", Deps(sdkForm).Replace("\n", "\r\n"), true),
            ("an address-like scalar on a version line (accepted residue)", Deps(new[] { Member("assemblyVersion", address) }), true),
            ("an exact version line under another root or kind (any exact version line is exempt)", Deps(new[] { Member("fileVersion", address) }, 4, "        \"resources\": {"), true),
            ("round 1: an address in another member", "{\"serverVersion\":\"" + address + "\"}", false),
            ("round 2: a version-shaped private address in another member", "{\"Version\":\"" + string.Join('.', 10, 1, 2, 3) + "\"}", false),
            ("round 3: an address as a key inside assemblyVersion, minified", "{\"targets\":{\"net10.0/win-x64\":{\"package/1.0\":{\"runtime\":{\"file.dll\":{\"assemblyVersion\":{\"" + address + "\":\"safe\"}}}}}}}", false),
            ("round 3: an address as a key inside assemblyVersion, indented", Deps(new[] { "\"assemblyVersion\": {", "  \"" + address + "\": \"safe\"", "}" }), false),
            ("round 4: a dropped duplicate member and an escaped version", "{\"targets\":{\"framework\":{\"package\":{\"runtime\":{\"file.dll\":{\"assemblyVersion\":{\"" + address + "\":\"safe\"},\"assemblyVersion\":\"" + version.Replace(".", "\\u002e") + "\"}}}}}}", false),
            ("a duplicate member without escapes, indented", Deps(new[] { "\"assemblyVersion\": {", "  \"" + address + "\": \"safe\"", "},", Member("assemblyVersion", version) }), false),
            ("an address one level deeper than the version", Deps(new[] { "\"assemblyVersion\": {", "  " + Member("x", address), "}" }), false),
            ("an address as an array element", Deps(new[] { "\"assemblyVersion\": [", "  \"" + address + "\"", "]" }), false),
            ("a version with trailing whitespace inside the value", Deps(new[] { Member("assemblyVersion", address + " ") }), false),
            ("a version line with trailing content", Deps(new[] { Member("assemblyVersion", address, ", \"x\": \"y\"") }), false),
            ("a version line with a tab-separated second member", Deps(new[] { Member("assemblyVersion", version, ",") + "\t" + Member("fileVersion", address) }), false),
            ("an address as the framework key", Deps(sdkForm, 2, "    \"" + address + "\": {"), false),
            ("an address as the package key", Deps(sdkForm, 3, "      \"" + address + "\": {"), false),
            ("an address in place of runtime", Deps(sdkForm, 4, "        \"" + address + "\": {"), false),
            ("an address as the file key", Deps(sdkForm, 5, "          \"" + address + "\": {"), false),
            ("an address in place of assemblyVersion", Deps(new[] { Member(address, version) }), false),
            ("an address as a key at the root", Deps(sdkForm, 1, "  \"" + address + "\": {"), false),
            ("a backslash anywhere, which removes the exemption", Deps(sdkForm.Append(Member("path", "a\\\\b")).ToArray()), false),
            ("a \\u-escaped version, which removes the exemption from the exact version line beside it", Deps(new[] { Member("assemblyVersion", version.Replace("1", "\\u0031"), ","), Member("fileVersion", address) }), false),
            ("a version with an escaped newline", Deps(new[] { Member("assemblyVersion", address + "\\n") }), false),
            ("a minified single-line deps.json, where no line matches", "{\"targets\":{\"fw\":{\"p\":{\"runtime\":{\"a.dll\":{\"assemblyVersion\":\"" + version + "\"}}}}}}", false),
            ("a file that is no JSON", "{\"targets\": " + version, false),
        };
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-dependency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var table = Path.Combine(root, "cases.json");
            File.WriteAllText(table, System.Text.Json.JsonSerializer.Serialize(cases.Select(item => new { item.Label, item.Json, item.Accepted }).Append(new { Label = "the real deps.json of the built app", Json = built is null ? "" : File.ReadAllText(built), Accepted = true })));
            var runner = Path.Combine(root, "check.ps1");
            File.WriteAllText(runner, """
                param([string]$Exporter, [string]$Table)
                $ErrorActionPreference = 'Stop'
                $tree = [Management.Automation.Language.Parser]::ParseFile($Exporter, [ref]$null, [ref]$null)
                foreach ($definition in $tree.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([scriptblock]::Create($definition.Extent.Text)) }
                # Windows PowerShell 5.1 passes a JSON array on as one object; ForEach-Object unrolls it in both versions.
                foreach ($case in [IO.File]::ReadAllText($Table) | ConvertFrom-Json | ForEach-Object { $_ }) {
                    $detail = if ($case.Json) { Find-DependencyDetail $case.Json } else { 'no deps.json was built' }
                    $outcome = if ($detail) { 'REFUSED' } else { 'ACCEPTED' }
                    Write-Host "$outcome | $($case.Label) | $detail"
                }
                """);
            foreach (var shell in new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" })
            {
                var start = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileName(shell) == "powershell.exe") start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", runner, "-Exporter", Path.Combine(repository.FullName, "scripts", "export-public.ps1"), "-Table", table }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(true); throw new Exception("Dependency exemption check timed out."); }
                var lines = output.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Check(process.ExitCode == 0 && lines.Length == cases.Length + 1, $"{Path.GetFileName(shell)} runs the dependency exemption table ({errors.Result.Trim()})");
                foreach (var (item, line) in cases.Select(item => (Label: item.Label, Accepted: item.Accepted)).Append((Label: "the real deps.json of the built app", Accepted: true)).Zip(lines))
                    Check(line.StartsWith(item.Accepted ? "ACCEPTED" : "REFUSED"), $"{Path.GetFileName(shell)}: {(item.Accepted ? "accepts" : "refuses")} {item.Label} ({line})");
            }
        }
        finally { Directory.Delete(root, true); }
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
        var people = GitPeople(repository.FullName);
        Check(people is not null, $"the publication gate knows the authors: a complete Git history, or {SuppliedIdentities} where the checkout has none (a shallow or empty history fails)");
        Check(AuthorsKnown(false, "A\taddress-a", null) && AuthorsKnown(true, "A\taddress-a", "B\taddress-b") && AuthorsKnown(false, "", "B\taddress-b")
            && !AuthorsKnown(true, "A\taddress-a", null) && !AuthorsKnown(false, "", null) && !AuthorsKnown(false, null, " "),
            "a shallow or empty history without supplied identities leaves the authors unknown");
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
            // The names are read at run time and never written into a message, so neither this check nor its output names anyone.
            if (people is not null) Check(NamesPerson(text, people) is null, $"tracked text names no person of the Git identities: {file} ({NamesPerson(text, people)})");
            if (file.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) && text.Any(character => character > 127))
                Check(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), $"non-ASCII PowerShell script has a UTF-8 BOM for Windows PowerShell: {file}");
        }
        var dot = ".";
        string Ip(params int[] bytes) => string.Join(dot, bytes);
        Check(new[] { Ip(10, 1, 2, 3), Ip(172, 16, 0, 1), Ip(172, 31, 255, 254), Ip(192, 168, 0, 1), Ip(100, 64, 0, 1), Ip(100, 127, 255, 254),
            "host" + dot + "ts" + dot + "net", "person" + "@" + "example.org", "C:" + @"\Users\" + @"Example Person\file.txt" }.All(Leaks), "generic leak patterns reject private hosts, addresses, email and concrete profile paths");
        Check(new[] { Ip(127, 0, 0, 1), Ip(100, 63, 255, 255), Ip(100, 128, 0, 1), Ip(172, 15, 0, 1), Ip(172, 32, 0, 1),
            "123+noreply@example.org", "123+example@users.noreply.github.com", @"C:\Users\<user>\file.txt", @"%USERPROFILE%\file.txt" }.All(text => !Leaks(text)), "loopback, public addresses, noreply email and placeholder paths pass");
        // The name rule of scripts/export-public.ps1 on a made-up person: whole words, also in camel case, in any letter case.
        var inventedAddress = "mara.quint" + "@" + "example.org";
        var invented = People(new[] { "Mara Quint\t" + inventedAddress, "Agent Account\t1+agent@users.noreply.github.com", "Relay Account\tx" + "@" + "privaterelay.appleid.com" });
        Check(invented.Names.SequenceEqual(new[] { "Mara Quint" }) && invented.Words.SequenceEqual(new[] { "Mara", "Quint" }) && invented.Addresses.SequenceEqual(new[] { inventedAddress }),
            "a name only seen with a no-reply or relay address is an account, not a person; a person's whole name, its parts and address are refused");
        Check(new[] { "Thanks, Mara.", "MARA wrote it", "Signature`nQuint", "QuintReport", inventedAddress, "Mara Quint" }.All(text => NamesPerson(text, invented) is not null)
            && new[] { "A quintet ran a marathon.", "Unmarau", "Lo quintuple" }.All(text => NamesPerson(text, invented) is null),
            "a part of a person's name is found as a word of its own and in camel case, never inside a longer word");
    }

    private const string WordStart = @"(?-i:(?<![\p{L}\p{N}])|(?<=[\p{Ll}\p{N}])(?=\p{Lu}))", WordEnd = @"(?-i:(?![\p{L}\p{N}])|(?<=\p{Ll})(?=\p{Lu}))";
    internal sealed record Person(string[] Names, string[] Words, string[] Addresses);
    // The people behind "<name>\t<address>" identities as scripts/export-public.ps1 derives them: a name only seen with a no-reply or
    // relay address is an account, such as an agent's or a hosting service's; any other name is a person.
    internal static Person People(IEnumerable<string> identities)
    {
        var anonymous = new Regex(@"(?:^|[+._@-])no-?reply(?:$|[+._@-])|@privaterelay\.appleid\.com$", RegexOptions.IgnoreCase);
        var named = identities.Select(identity => identity.Split('\t')).Where(parts => parts.Length == 2 && parts[0].Trim().Length > 0 && parts[1].Trim().Length > 0 && !anonymous.IsMatch(parts[1].Trim())).ToArray();
        var names = named.Select(parts => parts[0].Trim()).Where(name => name.Length >= 4).Distinct().Order(StringComparer.Ordinal).ToArray();
        var words = names.SelectMany(name => Regex.Split(name, @"[^\p{L}]+")).Where(word => word.Length >= 3).Distinct().ToArray();
        return new(names, words, named.Select(parts => parts[1].Trim()).Where(address => address.Length >= 4).Distinct().ToArray());
    }
    private static Person? GitPeople(string repository)
    {
        string? Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : null;
        }
        var log = Git("log", "--all", "--format=%an%x09%ae");
        var supplied = Environment.GetEnvironmentVariable(SuppliedIdentities);
        if (!AuthorsKnown(Git("rev-parse", "--is-shallow-repository")?.Trim() == "true", log, supplied)) return null;
        var identities = (log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat((supplied ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        identities.Add($"{Git("config", "user.name")?.Trim()}\t{Git("config", "user.email")?.Trim()}");
        return People(identities);
    }
    // "<name>\t<address>" lines of the authors, given where the checkout has no history of its own, as in an archive a test machine builds
    // from (its tooling reads them from the repository it archives); never written into the repository.
    internal const string SuppliedIdentities = "PRIVATE_FLOW_GIT_IDENTITIES";
    // The authors are known from a complete history with commits, or from identities supplied at run time; a shallow or empty history alone
    // is not enough, and the gate then fails instead of passing every name.
    internal static bool AuthorsKnown(bool shallow, string? log, string? supplied) =>
        !string.IsNullOrWhiteSpace(supplied) || (!shallow && !string.IsNullOrWhiteSpace(log));
    // What of a person the text contains, without naming it, or null.
    internal static string? NamesPerson(string text, Person people) =>
        people.Names.Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase)) ? "the name of a Git identity"
        : people.Words.Any(word => Regex.IsMatch(text, WordStart + Regex.Escape(word) + WordEnd, RegexOptions.IgnoreCase)) ? "a part of the name of a Git identity"
        : people.Addresses.Any(address => text.Contains(address, StringComparison.OrdinalIgnoreCase)) ? "the email address of a Git identity" : null;
}
