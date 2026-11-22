using PrivateFlow.Core;
using static Harness;

static class ReleaseChecks
{
    public static void VerifyReleaseVersion()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src and docs was not found above " + AppContext.BaseDirectory);
        // Only a change to the shipped app takes a number, assigned when it is merged (scripts/release.py assign-version):
        // until then its notes stand in the one "# Private Flow next" section on top, and the project version stays the newest number.
        var projectVersion = System.Xml.Linq.XDocument.Load(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows", "PrivateFlow.Windows.csproj"))
            .Descendants("Version").Single().Value;
        var notesProblem = NotesVersionProblem(File.ReadLines(Path.Combine(repository.FullName, "docs", "RELEASE_NOTES.md")), projectVersion);
        Check(notesProblem is null, $"docs/RELEASE_NOTES.md: at most one “Private Flow next” section, on top, and the topmost “Private Flow x.y.z” heading is the project version {projectVersion} ({notesProblem ?? "so"})");
        string[] Notes(params string[] headings) => headings;
        Check(NotesVersionProblem(Notes("# Private Flow 0.5.20 (beta): a", "# Private Flow 0.5.19"), "0.5.20") is null
            && NotesVersionProblem(Notes("# Private Flow next (beta): a", "- x", "# Private Flow 0.5.19 (beta): b"), "0.5.19") is null
            && NotesVersionProblem(Notes("# Private Flow next", "# Private Flow 0.5.19"), "0.5.20") is not null
            && NotesVersionProblem(Notes("# Private Flow 0.5.20", "# Private Flow 0.5.19"), "0.5.19") is not null
            && NotesVersionProblem(Notes("# Private Flow next", "# Private Flow next", "# Private Flow 0.5.19"), "0.5.19") is not null
            && NotesVersionProblem(Notes("# Private Flow 0.5.19", "# Private Flow next"), "0.5.19") is not null
            && NotesVersionProblem(Notes("# Private Flow next"), "0.5.19") is not null,
            "the version check accepts an unnumbered next section on top of the project version and refuses a stale or second one");
        // A version in a document's opening goes stale with the next run: results, with their counts, stand in the results table of
        // VALIDATION.md, where each row names the commit it was measured on.
        // The public snapshot holds only the counterparts, under the documents' own paths.
        foreach (var document in new[] { "docs/VALIDATION.md", "docs/TEST_COVERAGE.md", "public-snapshot/files/docs/VALIDATION.md", "public-snapshot/files/docs/TEST_COVERAGE.md" }
                     .Where(document => File.Exists(Path.Combine(repository.FullName, document))))
        {
            var problems = ValidationTextProblems(File.ReadAllLines(Path.Combine(repository.FullName, document)));
            Check(problems.Count == 0, $"{document} names no version in its opening ({string.Join("; ", problems.DefaultIfEmpty("none"))})");
        }
        var resultRows = File.ReadAllLines(Path.Combine(repository.FullName, "docs", "VALIDATION.md"))
            .SkipWhile(line => line != "## What passed where").TakeWhile(line => line == "## What passed where" || !line.StartsWith("## "))
            .Where(line => line.StartsWith("| ") && !line.StartsWith("| Date") && !line.StartsWith("| ---")).ToList();
        // The public counterpart has no results table.
        Check(resultRows.All(row => System.Text.RegularExpressions.Regex.IsMatch(row.Split('|')[2], @"\b[0-9a-f]{7,40}\b|not recorded")),
            $"every row of “What passed where” in docs/VALIDATION.md names the commit it ran on ({resultRows.Count} rows)");
        Check(ValidationTextProblems(["# Validation", "", "The state of verification for Private Flow 0.5.19 (2026-11-19)."]).Count == 1
            && ValidationTextProblems(["Intro.", "", "## Levels", "Version 0.5.5 adds pure checks."]).Count == 0,
            "the validation text check refuses a version in the opening, not in a later section");
        // Internal notes live in a file of their own, which public snapshots leave out; the public notes hold no section for them.
        var notesLines = File.ReadAllLines(Path.Combine(repository.FullName, "docs", "RELEASE_NOTES.md"));
        Check(!HasInternalHeading(notesLines), "docs/RELEASE_NOTES.md has no “Internal” heading; internal notes go to their own file");
        Check(new[] { "### Internal", "  ## internal ##", "#### INTERNAL   ", "#Internal" }.All(heading => HasInternalHeading(["x", heading]))
            && HasInternalHeading(["Internal", "--------"]) && HasInternalHeading([" internal ", "==="])
            && !HasInternalHeading(["### Internals of the pill", "- Internal timing.", "Internal", "- x"]),
            "an “Internal” heading is found at any level, in any letter case, with closing hashes or as a setext heading, and nothing else is");
        // The setup action names the size of the model the script downloads, which it reads from scripts/models.json.
        var polishScript = File.ReadAllText(Path.Combine(repository.FullName, "scripts", "setup-rewrite.ps1"));
        var polishSize = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "models.json"))).RootElement.GetProperty("Models").EnumerateArray()
            .Single(model => model.GetProperty("Key").GetString() == "qwen3-4b").GetProperty("Bytes").GetInt64().ToString();
        Check(polishScript.Contains("Get-Pinned 'qwen3-4b'") && polishSize == PrivateFlow.Windows.LocalRewriter.PolishModelBytes.ToString() && PrivateFlow.Windows.LocalRewriter.PolishSetupLabel == "Set up Local polish (2,497 MB)",
            $"the Local polish setup action names the size of the model setup-rewrite.ps1 downloads ({polishSize} bytes; “{PrivateFlow.Windows.LocalRewriter.PolishSetupLabel}”)");
    }

    internal static bool HasInternalHeading(string[] lines) => lines.Select((line, index) => (line, index)).Any(item =>
        System.Text.RegularExpressions.Regex.IsMatch(item.line, @"^ {0,3}#{1,6}[ \t]*internal[ \t]*#*[ \t]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        || (System.Text.RegularExpressions.Regex.IsMatch(item.line, @"^ {0,3}internal[ \t]*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && item.index + 1 < lines.Length && System.Text.RegularExpressions.Regex.IsMatch(lines[item.index + 1], @"^ {0,3}(?:=+|-+)[ \t]*$")));

    // The version a validation document states in its opening, before its first section, if any.
    internal static List<string> ValidationTextProblems(string[] lines)
    {
        var firstSection = Array.FindIndex(lines, line => line.StartsWith("## "));
        var opening = string.Join(" ", lines.Take(firstSection < 0 ? lines.Length : firstSection));
        return System.Text.RegularExpressions.Regex.Match(opening, @"\b\d+\.\d+\.\d+\b") is { Success: true } version ? [$"version {version.Value} in the opening"] : [];
    }

    // Why the top-level headings of release notes do not fit the project version, or null.
    internal static string? NotesVersionProblem(IEnumerable<string> lines, string projectVersion)
    {
        var headings = lines.Where(line => line.StartsWith("# ")).ToArray();
        static bool IsNext(string heading) => System.Text.RegularExpressions.Regex.IsMatch(heading, @"^# Private Flow next(?![\w.])");
        var next = headings.Count(IsNext);
        if (next > 1 || (next == 1 && !IsNext(headings[0]))) return "the next section must be the one topmost section";
        var notesVersion = headings.Select(heading => System.Text.RegularExpressions.Regex.Match(heading, @"^# Private Flow v?(\d+\.\d+\.\d+)(?![\d.])")).FirstOrDefault(match => match.Success)?.Groups[1].Value;
        return notesVersion == projectVersion ? null : $"the topmost numbered heading is {notesVersion ?? "missing"}";
    }
}
