using System.Diagnostics;
using System.Text.RegularExpressions;
using static Harness;

// verify.ps1 -Changed (JobSelection.cs): every job can be selected, every path of the tables exists, every tracked file is covered,
// and the selection fails safe. In the sources and the tracked files only, so the Linux CI checks it too.
static class JobSelectionChecks
{
    public static void VerifyJobSelection()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src was not found above " + AppContext.BaseDirectory);
        var root = repository.FullName;
        var tracked = TrackedFiles(root);
        Check(tracked.Count > 100, $"the selection checks read every tracked path from Git ({tracked.Count})");
        var index = JobSelection.CodeIndex.Read(root);
        Check(index.Groups.Count >= 60 && index.Types.Count >= 100, $"the index finds every group of Program.cs and the types of the sources ({index.Groups.Count} groups, {index.Types.Count} types)");
        var pureJobs = index.Groups.Select(group => "pure/" + group.Name).ToList();
        JobSelection.Selection Pure(params string[] files) => JobSelection.Select(root, "pure", pureJobs, files, index: index);
        JobSelection.Selection PureLines(string file, int first, int last) =>
            JobSelection.Select(root, "pure", pureJobs, [file], new Dictionary<string, List<(int First, int Last)>> { [file] = [(first, last)] }, index);
        static HashSet<string> Names(JobSelection.Selection selection) => [.. selection.Selected.Select(choice => choice.Job)];

        // The tables name only paths that exist; a pattern that matches nothing fails here, so a renamed file cannot leave a rule behind.
        // The workflows are the one exception: a public snapshot carries none, and a change to one still selects everything where they exist.
        var patterns = JobSelection.Everything.Where(pattern => !pattern.StartsWith(".github/workflows/") || Directory.Exists(Path.Combine(root, ".github", "workflows")))
            .Select(pattern => ("everything", pattern))
            .Concat(JobSelection.Scans.SelectMany(scan => scan.Paths.Select(pattern => ("scan " + scan.Group, pattern))))
            .Concat(JobSelection.RulesOf(root).SelectMany(rule => rule.Paths.Select(pattern => ("rule " + rule.Name, pattern))));
        var unmatched = Unmatched(patterns, tracked);
        Check(unmatched.Count == 0, $"every path of the selection tables matches a tracked file (matching nothing: {string.Join(", ", unmatched.DefaultIfEmpty("none"))})");
        Check(Unmatched([("rule example", "scripts/no-such-script.ps1")], tracked).Count == 1, "a table path that matches no tracked file is reported");
        Check(JobSelection.Scans.All(scan => index.Groups.Any(group => group.Name == scan.Group)), "every scanning group of the table is a group of Program.cs");

        // Every tracked file is known to some rule; a new kind of file has to be placed in the tables.
        var uncovered = tracked.Where(file => Pure(file).Selected.Any(choice => choice.Reasons.Any(reason => reason.EndsWith("is covered by no rule")))).ToList();
        Check(uncovered.Count == 0, $"every tracked file is covered by a rule (not covered: {string.Join(", ", uncovered.DefaultIfEmpty("none"))})");

        // Every pure job is selected by some tracked file.
        var reached = new HashSet<string>();
        foreach (var file in tracked.Where(file => !JobSelection.Everything.Any(pattern => JobSelection.PathMatches(pattern, file)))) reached.UnionWith(Names(Pure(file)));
        var unreached = pureJobs.Where(job => !reached.Contains(job)).ToList();
        Check(unreached.Count == 0, $"every pure job is selected by a file of its own ({pureJobs.Count} jobs; never selected: {string.Join(", ", unreached.DefaultIfEmpty("none"))})");

        // Every engine and desktop job verify.ps1 can make is reached by a rule a tracked file triggers.
        // The jobs of verify.ps1 and of the desktop suites it runs (scripts/*.desktop-jobs.ps1).
        var templates = JobTemplates(string.Join("\n", Directory.EnumerateFiles(Path.Combine(root, "scripts"), "*.desktop-jobs.ps1").Prepend(Path.Combine(root, "scripts", "verify.ps1")).Select(File.ReadAllText)));
        var triggered = JobSelection.RulesOf(root).Where(rule => tracked.Any(file => rule.Paths.Any(pattern => JobSelection.PathMatches(pattern, file)))).SelectMany(rule => rule.Jobs).ToList();
        var orphaned = templates.Where(template => !template.StartsWith("pure/") && !triggered.Any(pattern => Overlap(pattern, template))).ToList();
        Check(templates.Count >= 12 && orphaned.Count == 0,
            $"every engine and desktop job of verify.ps1 is selected by some file ({string.Join(", ", templates.Where(template => !template.StartsWith("pure/")))}; never selected: {string.Join(", ", orphaned.DefaultIfEmpty("none"))})");

        var engineJobs = templates.Where(template => template.StartsWith("engine/")).Select(template => template.Replace("*", "sample")).ToList();
        JobSelection.Selection Engine(params string[] files) => JobSelection.Select(root, "engine", engineJobs, files, index: index);

        // Fail safe.
        // An existing file outside every rule: one of its own under artifacts, which Git ignores.
        var stray = $"artifacts/job-selection-{Guid.NewGuid():N}.bin";
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        File.WriteAllBytes(Path.Combine(root, stray), [1]);
        try
        {
            var unknown = Pure(stray);
            Check(unknown.Selected.Count == pureJobs.Count && unknown.Selected.All(choice => choice.Reasons.Any(reason => reason.Contains("covered by no rule"))),
                "a file no rule covers selects every pure job and says why");
            Check(Engine(stray).Selected.Count == engineJobs.Count, "a file no rule covers selects every engine job");
        }
        finally { File.Delete(Path.Combine(root, stray)); }
        foreach (var file in new[] { "scripts/verify.ps1", "tests/PrivateFlow.Tests/Harness.cs", "tests/PrivateFlow.Tests/JobSelection.cs", "src/PrivateFlow.Core/PrivateFlow.Core.csproj", "scripts/build.sh" })
            Check(Pure(file).Selected.Count == pureJobs.Count && Engine(file).Selected.Count == engineJobs.Count, $"{file} selects every job of every tier");
        var program = index.Texts[JobSelection.CodeIndex.Program].Split('\n');
        var themeLine = Array.FindIndex(program, line => line.StartsWith("Group(\"theme-resources\"")) + 1;
        Check(themeLine > 0 && Names(PureLines(JobSelection.CodeIndex.Program, themeLine, themeLine)).SetEquals(["pure/theme-resources", "pure/publication-leaks"]),
            "a change to one group's line in Program.cs selects that group and the leak scan of every tracked file only");
        Check(PureLines(JobSelection.CodeIndex.Program, 1, 1).Selected.Count == pureJobs.Count, "a change to any other line of Program.cs selects every job");
        foreach (var gone in new[] { "src/PrivateFlow.Core/RemovedFile.cs", "docs/REMOVED.md" })
        {
            var deleted = Pure(gone);
            Check(deleted.Selected.Count == pureJobs.Count && deleted.Selected[0].Reasons.Any(reason => reason.Contains("deleted")), $"a deleted file ({gone}) selects every job");
        }
        // The index is not read for a deletion, so a file the test project still links cannot stop the selection.
        var linkedGone = JobSelection.Select(root, "pure", pureJobs, ["src/PrivateFlow.Windows/ChatEndpoint.cs", "src/PrivateFlow.Windows/Gone.cs"]);
        Check(linkedGone.Selected.Count == pureJobs.Count, "a deleted file selects every job before the index is read");

        // Documentation selects only the leak scan, which reads every tracked text, and the checks that read the file by name.
        var guide = Pure("docs/GUIDE.md", "AGENTS.md", "docs/IMPLEMENTATION.md");
        Check(Names(guide).SetEquals(["pure/publication-leaks"]) && guide.Summary.StartsWith("documentation only"), $"documentation no check reads selects only the leak scan and says so ({guide.Summary})");
        Check(Engine("docs/GUIDE.md", "docs/VALIDATION.md").Selected.Count == 0, "documentation selects no engine job");
        Check(Names(Pure("docs/NOTICE_KEYS.md")).SetEquals(["pure/notice-keys-documented", "pure/publication-leaks"]), "docs/NOTICE_KEYS.md selects only the check that compares it with the code and the leak scan");
        Check(Names(Pure("docs/RELEASE_NOTES.md")).Contains("pure/release-version"), "docs/RELEASE_NOTES.md selects the version check that reads it");

        // Concrete cases.
        var textCleanup = index.Types.Single(type => type.Name == "TextCleanup");
        var cleanup = Names(PureLines(textCleanup.File, textCleanup.First + 2, textCleanup.First + 2));
        // Meeting groups that run text code stay in: meeting-archive checks PreparedDictation, the notes groups reach LocalRewriter.
        Check(cleanup.IsSupersetOf(["pure/text-clean", "pure/prepared-dictation", "pure/dictation-pipeline"])
            && !cleanup.Overlaps(["pure/extractive-notes", "pure/archive-helpers", "pure/archive-audio", "pure/archive-links", "pure/hold-to-talk", "pure/echo-removal", "pure/speech-segmentation"]),
            $"a change inside TextCleanup ({textCleanup.File}) selects the text checks and not the archive, extractive notes, shortcut or audio checks ({string.Join(", ", cleanup)})");
        var extractive = Names(Pure("src/PrivateFlow.Core/ExtractiveNotes.cs"));
        Check(extractive.Contains("pure/extractive-notes") && !extractive.Overlaps(["pure/text-clean", "pure/hold-to-talk", "pure/echo-removal"]),
            "src/PrivateFlow.Core/ExtractiveNotes.cs selects the notes checks and not the text, shortcut or audio checks");
        var checkFile = Names(Pure("tests/PrivateFlow.Tests/ShortcutChecks.cs"));
        Check(checkFile.IsSupersetOf(["pure/shortcut-validation", "pure/hold-to-talk", "pure/meeting-shortcut"]) && !checkFile.Contains("pure/text-clean"),
            "a check file selects its own groups");
        var models = Names(Pure("scripts/models.json"));
        Check(models.Contains("pure/models"), "scripts/models.json, which the core project embeds, selects the model checks");
        var export = Names(Pure("scripts/export-public.ps1"));
        Check(export.Contains("pure/public-export") && !export.Contains("pure/text-clean"), "scripts/export-public.ps1 selects the export check that runs it");
        var meetingEngine = Names(Engine("src/PrivateFlow.Core/MeetingArchive.cs"));
        Check(meetingEngine.Any(job => job.Contains("/meeting/")) && !meetingEngine.Any(job => job.Contains("/dictation/")),
            "a meeting archive change selects the meeting accuracy jobs and not the dictation ones");
        var dictationEngine = Names(Engine("scripts/dictation-eval.ps1"));
        Check(dictationEngine.Contains("engine/prepare-clips") && dictationEngine.Any(job => job.Contains("/dictation/")) && !dictationEngine.Any(job => job.Contains("/meeting/")),
            "the dictation evaluation script selects the clips and the dictation jobs and not the meeting ones");
        Check(Names(Engine("scripts/settings-file.ps1")).Contains("engine/setup/sample"), "the settings step setup-engine.ps1 loads selects the engine setup jobs");
        Check(Names(Engine("src/PrivateFlow.Windows/App.xaml.cs")).Contains("engine/smoke-test"), "App.xaml.cs, which runs --transcribe-file, selects the smoke test");
        Check(Engine("src/PrivateFlow.Core/ExtractiveNotes.cs").Selected.All(choice => !choice.Job.Contains("/dictation/")), "a notes change selects no dictation accuracy job");
        Check(Engine("scripts/export-public.ps1", "tests/PrivateFlow.Tests/OutputStyleChecks.cs").Selected.Count == 0, "a publication script or a pure check file selects no engine job");
        var choice = Pure("src/PrivateFlow.Core/ExtractiveNotes.cs").Selected.First(each => each.Job == "pure/extractive-notes");
        Check(choice.Reasons.Count > 0 && choice.Reasons.All(reason => reason.Length > 0), $"each selected job says why ({choice.Reasons[0]})");
    }

    static List<string> TrackedFiles(string root)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("ls-files"); start.ArgumentList.Add("-z");
        using var process = Process.Start(start)!;
        var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(file => File.Exists(Path.Combine(root, file))).ToList();
        process.WaitForExit();
        return process.ExitCode == 0 ? files : [];
    }

    static List<string> Unmatched(IEnumerable<(string Owner, string Pattern)> patterns, List<string> tracked) =>
        [.. patterns.Where(each => !tracked.Any(file => JobSelection.PathMatches(each.Pattern, file))).Select(each => $"{each.Pattern} ({each.Owner})")];

    // The job names of verify.ps1, each part it builds from a variable as *.
    static List<string> JobTemplates(string script) =>
        [.. Regex.Matches(script, @"New-Job (?:""([^""]+)""|'([^']+)')").Select(match => match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value)
            .Select(name => Regex.Replace(name, @"\$\((?:[^()]|\([^()]*\))*\)|\$\w+", "*")).Distinct()];

    // Two job patterns can name the same job: either matches the other with its wildcards read as text.
    static bool Overlap(string pattern, string template) =>
        JobSelection.JobMatches(pattern, template.Replace("*", "sample")) || JobSelection.JobMatches(template, pattern.Replace("*", "sample"));
}
