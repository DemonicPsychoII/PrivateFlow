using System.Text.RegularExpressions;
using static Harness;

// The shipped app carries no verification code: the verification, evaluation and diagnostic modes live in the verification host
// (src/PrivateFlow.Verification), which the package does not contain. In the sources, so the Linux CI checks it too; --check-package
// of the host checks the built package itself.
static class VerificationHostChecks
{
    public static void VerifyVerificationOutsideTheApp()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src was not found above " + AppContext.BaseDirectory);
        var source = Path.Combine(repository.FullName, "src");
        var hostFolder = Path.Combine(source, "PrivateFlow.Verification");
        IEnumerable<string> Sources(string project)
        {
            var folder = Path.Combine(source, project);
            return Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).Where(file => Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar)[0] is not ("obj" or "bin"));
        }
        // Top-level declarations start at the first column; nested types are indented.
        static IEnumerable<string> Declared(string file) => Regex.Matches(File.ReadAllText(file),
            @"^(?:(?:internal|public|static|sealed|abstract|partial|file|readonly)\s+)*(?:class|record|struct|interface|enum)\s+(\w+)", RegexOptions.Multiline).Select(match => match.Groups[1].Value);
        var hostTypes = Sources("PrivateFlow.Verification").SelectMany(Declared).ToHashSet();
        Check(hostTypes.Contains("WorkspaceVerification") && hostTypes.Contains("MeetingEvaluation") && hostTypes.Contains("PackageCheck"),
            $"the verification host declares the verification and evaluation types ({hostTypes.Count} types)");
        foreach (var project in new[] { "PrivateFlow.Windows", "PrivateFlow.Core" })
        {
            var shared = Sources(project).SelectMany(file => Declared(file).Where(hostTypes.Contains).Select(name => $"{Path.GetFileName(file)}: {name}")).ToArray();
            var inHostNamespace = Sources(project).Where(file => File.ReadAllText(file).Contains("namespace PrivateFlow.Verification")).Select(Path.GetFileName).ToArray();
            Check(shared.Length == 0 && inHostNamespace.Length == 0, $"{project} declares no type of the verification host ({string.Join(", ", shared.Concat(inHostNamespace).DefaultIfEmpty("none"))})");
        }
        var appProject = File.ReadAllText(Path.Combine(source, "PrivateFlow.Windows", "PrivateFlow.Windows.csproj"));
        Check(!appProject.Contains("PrivateFlow.Verification.csproj", StringComparison.OrdinalIgnoreCase), "the app project does not reference the verification host");
        // The app refuses exactly the modes the host took over, and the host runs each of them.
        var moved = Regex.Matches(File.ReadAllText(Path.Combine(hostFolder, "PackageCheck.cs")), @"\[""(--[a-z-]+)""\] = \d").Select(match => match.Groups[1].Value).Order().ToArray();
        var refusedList = Regex.Match(File.ReadAllText(Path.Combine(source, "PrivateFlow.Windows", "App.xaml.cs")), @"VerificationSwitches = \[(.*?)\];", RegexOptions.Singleline).Groups[1].Value;
        var refused = Regex.Matches(refusedList, @"""(--[a-z-]+)""").Select(match => match.Groups[1].Value).Order().ToArray();
        Check(moved.Length >= 10 && moved.SequenceEqual(refused), $"the app refuses every mode the verification host took over ({string.Join(" ", refused)})");
        var program = File.ReadAllText(Path.Combine(hostFolder, "Program.cs"));
        var notServed = moved.Where(mode => !program.Contains($"case [\"{mode}\"", StringComparison.Ordinal)).ToArray();
        Check(notServed.Length == 0, $"the verification host runs every mode the app refuses ({string.Join(" ", notServed.DefaultIfEmpty("none missing"))})");
        // The modes that send keys or write the clipboard run only in the disposable test VM, by the guard the desktop scenarios use.
        var inputModes = Regex.Match(program, @"InputModes = \[(.*?)\];").Groups[1].Value;
        Check(inputModes.Contains("\"--verify-insert\"") && inputModes.Contains("\"--insert-fixture\"") && program.Contains("InputModes.Contains(mode) && !DisposableTestVm.IsThisComputer"),
            "the verification host runs --verify-insert and --insert-fixture only in the disposable test VM");
        static bool Is(string? marker, string computer, string account) => PrivateFlow.Core.DisposableTestVm.Is(marker, computer, account);
        Check(Is(@"MACHINE-A\runner" + "\r\n", "MACHINE-A", "runner") && !Is(null, "MACHINE-A", "runner") && !Is("", "MACHINE-A", "runner") && !Is(@"MACHINE-A\runner", "MACHINE-A", "other")
            && !Is(@"MACHINE-A\runner", "MACHINE-B", "runner") && !Is(@"machine-a\runner", "MACHINE-A", "runner") && !Is(@"\", "", ""),
            "the test machine is only the computer and account its marker names, exactly; no marker is no test machine");
    }
}
