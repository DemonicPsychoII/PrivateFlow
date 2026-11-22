using System.Text.Json;
using System.Text.RegularExpressions;

// Which jobs of scripts/verify.ps1 a change affects (verify.ps1 -Changed). The pure jobs are derived from the code, so the map
// cannot fall behind it: a group is affected when its check file changed, when the code of its method (with the members of its
// file the method uses) names a type the change affects, when it names a changed file, or when it scans the folder of a changed
// file (Scans). A change affects the types whose lines it touched and, in turn, every type whose code names an affected one. The
// engine and desktop jobs follow the path rules below. Fail safe: a change to the entry point, the harness, this selection or the
// build, a deleted file and a file no rule knows select every job. Documentation selects only the leak scan and the checks that read
// it by name.
static class JobSelection
{
    public sealed record Choice(string Job, List<string> Reasons);
    public sealed record Selection(List<Choice> Selected, List<string> Omitted, string Summary);
    public sealed record Rule(string Name, string[] Paths, string[] Jobs);

    // Changes that select every job of every tier.
    public static readonly string[] Everything = ["scripts/verify.ps1", "tests/PrivateFlow.Tests/Program.cs", "tests/PrivateFlow.Tests/Harness.cs",
        "tests/PrivateFlow.Tests/JobSelection.cs", "**.csproj", "Directory.Build.props", "global.json", "scripts/build.ps1", "scripts/build.sh", ".github/workflows/*"];

    // Pure groups that read the files of a folder instead of naming them; the leak scan reads documentation too.
    public static readonly (string Group, string[] Paths)[] Scans =
    [
        ("publication-leaks", ["**"]),
        ("notice-keys-documented", ["src/**.cs"]),
        ("verification-outside-the-app", ["src/**.cs"]),
        ("theme-resources", ["src/PrivateFlow.Windows/*.xaml", "src/PrivateFlow.Windows/*.cs"]),
    ];

    // The engine and desktop jobs by path; a file may match several rules.
    public static readonly Rule[] Rules =
    [
        new("recognition, audio and engine setup", [
            "src/PrivateFlow.Windows/WhisperEngine.cs", "src/PrivateFlow.Windows/NativeSpeech.cs", "src/PrivateFlow.Windows/SpeechWorker.cs",
            "src/PrivateFlow.Windows/SpeechSetup.cs", "src/PrivateFlow.Windows/HelperProcesses.cs", "src/PrivateFlow.Windows/ModelList.cs",
            "src/PrivateFlow.Windows/Settings.cs", "src/PrivateFlow.Windows/PreferenceSections.cs", "src/PrivateFlow.Windows/AudioRecorder.cs",
            "src/PrivateFlow.Windows/LibraryAudio.cs", "src/PrivateFlow.Windows/MicrophoneDevices.cs",
            "src/PrivateFlow.Core/SpeechRuntime.cs", "src/PrivateFlow.Core/ModelCatalogue.cs", "src/PrivateFlow.Core/WorkspaceModels.cs",
            "src/PrivateFlow.Core/ModelAttempts.cs", "src/PrivateFlow.Core/AudioWindows.cs", "src/PrivateFlow.Core/AudioLibrary.cs",
            "src/PrivateFlow.Core/SpeechSegmentation.cs", "src/PrivateFlow.Core/InputFeedback.cs",
            "src/PrivateFlow.Verification/AudioLibraryBuilder.cs", "scripts/setup-engine.ps1", "scripts/settings-file.ps1", "scripts/models.json", "scripts/eval-common.ps1"],
            ["engine/*"]),
        new("the verification host's entry and package check", ["src/PrivateFlow.Verification/Program.cs", "src/PrivateFlow.Verification/PackageCheck.cs"],
            ["engine/*", "desktop/*"]),
        new("dictation pipeline", [
            "src/PrivateFlow.Windows/DictationPipeline.cs", "src/PrivateFlow.Windows/DictationController.cs", "src/PrivateFlow.Windows/LocalRewriter.cs",
            "src/PrivateFlow.Windows/ConciseRewriter.cs", "src/PrivateFlow.Windows/ChatEndpoint.cs",
            "src/PrivateFlow.Core/DictationPolicy.cs", "src/PrivateFlow.Core/DictationState.cs", "src/PrivateFlow.Core/SpokenCorrections.cs",
            "src/PrivateFlow.Core/TechnicalSpans.cs", "src/PrivateFlow.Core/NumberWords.cs", "src/PrivateFlow.Core/TextLibrary.cs",
            "src/PrivateFlow.Core/TextLibraryFile.cs", "src/PrivateFlow.Core/TextLibraryValidation.cs", "src/PrivateFlow.Core/RewriteIntegrity.cs",
            "src/PrivateFlow.Core/VocabularyTerms.cs", "src/PrivateFlow.Core/StartingCapture.cs"],
            ["engine/smoke-test", "engine/*/dictation/*"]),
        new("meeting pipeline", [
            "src/PrivateFlow.Windows/MeetingCapture.cs", "src/PrivateFlow.Windows/MeetingTranscriber.cs", "src/PrivateFlow.Windows/MeetingNotesWriter.cs",
            "src/PrivateFlow.Windows/LocalRewriter.cs", "src/PrivateFlow.Core/MeetingArchive.cs", "src/PrivateFlow.Core/MeetingVocabulary.cs",
            "src/PrivateFlow.Core/ExtractiveNotes.cs", "src/PrivateFlow.Core/ModelNotes.cs", "src/PrivateFlow.Core/VocabularyTerms.cs",
            "src/PrivateFlow.Core/StartingCapture.cs"],
            ["engine/*/meeting/*", "engine/*/own-recordings"]),
        new("dictation evaluation", ["src/PrivateFlow.Verification/DictationEvaluation.cs", "scripts/dictation-eval.ps1"], ["engine/prepare-clips", "engine/*/dictation/*"]),
        new("meeting evaluation", ["src/PrivateFlow.Verification/MeetingEvaluation.cs", "scripts/meeting-eval.ps1"],
            ["engine/prepare-clips", "engine/*/meeting/*", "engine/*/own-recordings"]),
        new("transcript scoring", ["src/PrivateFlow.Core/TranscriptScoring.cs"], ["engine/*/dictation/*", "engine/*/meeting/*", "engine/*/own-recordings"]),
        // The app's own --transcribe-file mode, which the smoke test runs.
        new("smoke test", ["scripts/smoke-test.ps1", "src/PrivateFlow.Windows/App.xaml.cs"], ["engine/smoke-test"]),
        new("speech worker faults", ["src/PrivateFlow.Verification/WorkerFaults.cs", "scripts/verify-worker-faults.ps1"], ["engine/worker-faults"]),
        new("compressed imports", ["src/PrivateFlow.Verification/ImportVerification.cs", "scripts/verify-imports.ps1"], ["engine/imports"]),
        new("workspace verification", ["src/PrivateFlow.Verification/WorkspaceVerification.cs", "scripts/verify-workspace.ps1"], ["engine/workspace-verification"]),
        new("the app the workspace verification drives", ["src/PrivateFlow.Windows/**", "src/PrivateFlow.Core/**"], ["engine/workspace-verification"]),
        new("overlay", ["src/PrivateFlow.Verification/OverlayVerification.cs", "src/PrivateFlow.Windows/RecordingHud.cs", "src/PrivateFlow.Windows/MeetingWave.cs",
            "src/PrivateFlow.Windows/Native.cs"], ["desktop/verify-overlay"]),
        new("insertion", ["src/PrivateFlow.Verification/InsertionVerification.cs", "src/PrivateFlow.Windows/Native.cs", "src/PrivateFlow.Windows/DictationController.cs",
            "src/PrivateFlow.Core/DictationPolicy.cs"], ["desktop/verify-insert"]),
        new("UI smoke test", ["scripts/ui-smoke-test.ps1", "src/PrivateFlow.Windows/**"], ["desktop/ui-smoke-test"]),
        // Checked by the pure checks and CI's build.sh, or run by full-eval.ps1 on demand; no engine or desktop job runs them.
        new("no engine or desktop job", ["scripts/full-eval.ps1", "scripts/package.py", "scripts/release.py", "scripts/release_test.py", "scripts/sign-checksums.py",
            "scripts/install.ps1", "scripts/uninstall.ps1", "scripts/uninstall-test.ps1", "scripts/setup-rewrite.ps1",
            "scripts/export-public.ps1", "scripts/publish-public.ps1", ".gitignore", ".gitattributes", "THIRD_PARTY_NOTICES.sha256", "assets/**",
            "tests/PrivateFlow.Tests/**"], []),
    ];

    // The rules above and those of the suites the checkout holds besides them: each scripts/*.selection.json is an array of
    // {"Name", "Paths", "Jobs"}. Public snapshots leave the internal suites out with their rules, so the table above names none of them.
    public static Rule[] RulesOf(string repository) => [.. Rules, .. Directory.EnumerateFiles(Path.Combine(repository, "scripts"), "*.selection.json")
        .Order(StringComparer.Ordinal).SelectMany(file => JsonSerializer.Deserialize<Rule[]>(File.ReadAllText(file)) ?? [])];

    public static bool IsDocumentation(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path == "LICENSE";

    // * stands for any characters but /, ** also for /.
    public static bool PathMatches(string pattern, string path) => Regex.IsMatch(path,
        "^" + Regex.Escape(pattern).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace("\u0001", ".*") + "$");
    // Job names use * for any characters, as verify.ps1 -Filter does.
    public static bool JobMatches(string pattern, string job) => Regex.IsMatch(job, "^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$");

    // verify.ps1 -Changed: reads {repository, tier, jobs, changed, lines} and writes the selection as JSON. lines gives, per file,
    // the [first, last] line ranges the change touched in the new file; a file it does not list counts as changed throughout.
    public static int Run(string input, string output)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(input));
        var root = document.RootElement;
        static string[] Strings(JsonElement element) => element.ValueKind == JsonValueKind.Array ? [.. element.EnumerateArray().Select(item => item.GetString() ?? "")] : [];
        var lines = new Dictionary<string, List<(int First, int Last)>>(StringComparer.Ordinal);
        if (root.TryGetProperty("lines", out var ranges) && ranges.ValueKind == JsonValueKind.Object)
            foreach (var file in ranges.EnumerateObject())
                lines[file.Name] = [.. file.Value.EnumerateArray().Select(range => (range[0].GetInt32(), range[1].GetInt32()))];
        var selection = Select(root.GetProperty("repository").GetString()!, root.GetProperty("tier").GetString()!, Strings(root.GetProperty("jobs")),
            Strings(root.GetProperty("changed")), lines);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            selected = selection.Selected.Select(choice => new { job = choice.Job, reasons = choice.Reasons }),
            omitted = selection.Omitted,
            summary = selection.Summary,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static Selection Select(string repository, string tier, IReadOnlyList<string> jobs, IReadOnlyList<string> changedFiles,
        IReadOnlyDictionary<string, List<(int First, int Last)>>? changedLines = null, CodeIndex? index = null)
    {
        var changed = changedFiles.Select(file => file.Replace('\\', '/').Trim()).Where(file => file.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
        var reasons = jobs.ToDictionary(job => job, _ => new List<string>());
        void Add(string job, string reason) { if (reasons.TryGetValue(job, out var list) && !list.Contains(reason)) list.Add(reason); }
        Selection Finish(string summary) => new(
            [.. jobs.Where(job => reasons[job].Count > 0).Select(job => new Choice(job, reasons[job]))], [.. jobs.Where(job => reasons[job].Count == 0)], summary);

        // A deleted or renamed file's types are gone from the index, so nothing would find the code that used them; and the index
        // cannot be read while the test project still links a deleted file.
        var deleted = changed.Where(file => !File.Exists(Path.Combine(repository, file))).ToList();
        foreach (var file in deleted) foreach (var job in jobs) Add(job, $"{file} was deleted or renamed");
        if (deleted.Count > 0) return Finish("every job: " + string.Join(", ", deleted));

        var code = index ?? CodeIndex.Read(repository);
        var rules = RulesOf(repository);
        // A change to Program.cs that touched only lines of the group list selects those groups; any other line selects everything.
        var listedGroups = new List<string>();
        if (changed.Contains(CodeIndex.Program) && changedLines is not null && changedLines.TryGetValue(CodeIndex.Program, out var programRanges) && programRanges.Count > 0)
        {
            var programLines = code.Texts[CodeIndex.Program].Split('\n');
            var touched = programRanges.SelectMany(range => Enumerable.Range(range.First, Math.Max(1, range.Last - range.First + 1)))
                .Select(number => number >= 1 && number <= programLines.Length ? Regex.Match(programLines[number - 1], @"^(?:await )?Group\(""([^""]+)""") : Match.Empty).ToList();
            if (touched.All(match => match.Success)) listedGroups = [.. touched.Select(match => match.Groups[1].Value).Distinct()];
        }
        var everything = changed.Where(file => Everything.Any(pattern => PathMatches(pattern, file)) && !(file == CodeIndex.Program && listedGroups.Count > 0)).ToList();
        var unknown = changed.Where(file => !IsDocumentation(file) && !everything.Contains(file) && !code.Declares(file) && !code.Groups.Any(group => group.NamesFile(file))
            && !rules.Any(rule => rule.Paths.Any(pattern => PathMatches(pattern, file)))).ToList();
        foreach (var file in everything) foreach (var job in jobs) Add(job, $"{file} is the entry point, the harness, this selection or the build");
        foreach (var file in unknown) foreach (var job in jobs) Add(job, $"{file} is covered by no rule");
        if (everything.Count + unknown.Count > 0) return Finish("every job: " + string.Join(", ", everything.Concat(unknown).Distinct()));

        if (tier == "pure")
        {
            var affected = code.AffectedTypes(changed, changedLines ?? new Dictionary<string, List<(int First, int Last)>>());
            foreach (var group in code.Groups)
            {
                var job = "pure/" + group.Name;
                if (listedGroups.Contains(group.Name)) Add(job, $"its line in {CodeIndex.Program} changed");
                foreach (var file in changed)
                {
                    if (file == group.File) Add(job, $"its check file {file} changed");
                    else if (group.NamesFile(file)) Add(job, $"it reads {file}");
                    else if (Scans.Any(scan => scan.Group == group.Name && scan.Paths.Any(pattern => PathMatches(pattern, file))))
                        Add(job, $"it scans the folder of {file}");
                }
                foreach (var type in group.Names.Where(affected.ContainsKey).Order(StringComparer.Ordinal)) Add(job, $"it uses {type}, {affected[type]}");
            }
        }
        else
        {
            foreach (var file in changed)
                foreach (var rule in rules.Where(rule => rule.Paths.Any(pattern => PathMatches(pattern, file))))
                    foreach (var job in jobs.Where(job => rule.Jobs.Any(pattern => JobMatches(pattern, job)))) Add(job, $"{file}: {rule.Name}");
        }
        var count = jobs.Count(job => reasons[job].Count > 0);
        var documentation = changed.Count > 0 && changed.All(IsDocumentation);
        return Finish(count > 0 ? (documentation ? $"documentation only: {count} of {jobs.Count} jobs read it" : $"{count} of {jobs.Count} jobs")
            : changed.Count == 0 ? "no changed files"
            : documentation ? "documentation only: no job reads the changed files"
            : $"no {tier} job covers the changed files");
    }

    // The C# files the pure checks are compiled from: the top-level types each declares with their lines and the names each type's
    // code uses, and the names the code of each group of the checks uses.
    public sealed class CodeIndex
    {
        public const string TestProject = "tests/PrivateFlow.Tests/PrivateFlow.Tests.csproj";
        public const string Program = "tests/PrivateFlow.Tests/Program.cs";

        // The checks of this selection name files only as examples; they run when their own file or this one changes.
        public const string SelectionChecks = "tests/PrivateFlow.Tests/JobSelectionChecks.cs";

        // SharedNames are the file names several tracked files have, such as Program.cs: a group reads one of them only when its
        // code also names the file's folder.
        public sealed record Group(string Name, string File, HashSet<string> Names, string Text, IReadOnlySet<string> SharedNames)
        {
            public bool NamesFile(string path)
            {
                if (File == SelectionChecks) return false;
                var parts = path.Split('/');
                static bool Mentions(string text, string word) => Regex.IsMatch(text, @"(?<![\w.-])" + Regex.Escape(word) + @"(?![\w-])");
                return Mentions(Text, parts[^1]) && (!SharedNames.Contains(parts[^1]) || parts.Length > 1 && Mentions(Text, parts[^2]));
            }
        }
        // A top-level type runs from the line after the previous type of its file to its closing brace, so its attributes and
        // comment belong to it; nested types are part of it. Lines are numbered from 1.
        public sealed record TypeSpan(string Name, string File, int First, int Last, HashSet<string> Names);

        public required List<Group> Groups { get; init; }
        public required List<TypeSpan> Types { get; init; }
        public required Dictionary<string, string> Texts { get; init; }

        public bool Declares(string file) => Types.Any(type => type.File == file);

        // The types a change affects, with how each was reached: those whose lines it touched (every type of the file for a change
        // above its first type or without line ranges), those of a file that reads a changed data file by name, then every type whose
        // code names an affected one.
        public Dictionary<string, string> AffectedTypes(IEnumerable<string> changed, IReadOnlyDictionary<string, List<(int First, int Last)>> lines)
        {
            var affected = new Dictionary<string, string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            void Seed(string type, string reason) { if (affected.TryAdd(type, reason)) queue.Enqueue(type); }
            foreach (var file in changed)
            {
                var declared = Types.Where(type => type.File == file).ToList();
                if (declared.Count > 0)
                {
                    var ranges = lines.TryGetValue(file, out var touched) && touched.Count > 0 ? touched : null;
                    var whole = ranges is null || ranges.Any(range => range.First < declared[0].First);
                    foreach (var type in declared.Where(type => whole || ranges!.Any(range => range.First <= type.Last && range.Last >= type.First)))
                        Seed(type.Name, $"changed in {file}");
                }
                if (file.EndsWith(".cs", StringComparison.Ordinal) || IsDocumentation(file)) continue;
                // A data file the code reads by name, such as scripts/models.json, which the core project embeds.
                var name = file[(file.LastIndexOf('/') + 1)..];
                foreach (var reader in Texts.Where(pair => pair.Value.Contains('"' + name + '"', StringComparison.Ordinal)).Select(pair => pair.Key))
                    foreach (var type in Types.Where(type => type.File == reader)) Seed(type.Name, $"{reader} reads {name}");
            }
            while (queue.Count > 0)
            {
                var name = queue.Dequeue();
                foreach (var type in Types.Where(type => type.Names.Contains(name))) Seed(type.Name, $"{type.File} uses {name}, {affected[name]}");
            }
            return affected;
        }

        static readonly Regex Identifier = new(@"[A-Za-z_]\w*", RegexOptions.Compiled);
        static readonly Regex TopLevelType = new(@"^(?:(?:internal|public|static|sealed|abstract|partial|file|readonly)\s+)*(?:class|record(?:\s+struct)?|struct|interface|enum)\s+(\w+)",
            RegexOptions.Compiled);

        // The identifiers of some code, except members: a name after a value (settings.SpokenFormatting) or after a type of the
        // index (Settings.SpokenFormatting). A type is named on its own or after a namespace.
        static HashSet<string> Names(string code, Regex notMember) => notMember.Matches(code).Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        static Regex NotMember(IEnumerable<string> types) => new(@"(?<!\w)(?<!(?:\b[a-z_]\w*|[)\]]|\b(?:" + string.Join('|', types.Distinct().Select(Regex.Escape))
            + @"))\s*\??\.\s*)[A-Za-z_]\w*", RegexOptions.Compiled);

        // The names a type's code uses. A property named like a type, such as Settings.SpokenFormatting, counts as that type
        // only where the code puts it in a type's place: before a member access, a generic, a declared name, or after new, is or as.
        static HashSet<string> TypeNames(string span, Regex notMember)
        {
            var names = Names(span, notMember);
            foreach (var property in Regex.Matches(span, @"\b(\w+)\s*\{\s*(?:get|set|init)\b").Select(match => match.Groups[1].Value).Distinct().ToList())
            {
                var name = Regex.Escape(property);
                if (!Regex.IsMatch(span, $@"(?<![\w.]){name}(?:\s*\.|\s*<|\??\[\]|\??\s+@?[A-Za-z_]\w*\s*[;=,)(\{{])|\b(?:new|is|as|typeof\(|nameof\()\s*{name}\b|<{name}[,>]"))
                    names.Remove(property);
            }
            return names;
        }

        // The tracked files; none when Git cannot list them, so every file name counts as unique and selects more, not less.
        static List<string> TrackedFiles(string repository)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                start.ArgumentList.Add("ls-files"); start.ArgumentList.Add("-z");
                using var process = System.Diagnostics.Process.Start(start)!;
                var files = process.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
                process.WaitForExit();
                return process.ExitCode == 0 ? files : [];
            }
            catch (System.ComponentModel.Win32Exception) { return []; }
        }

        // The files the pure checks are compiled from, as their project file names them: its own folder, the folders of the
        // projects it references and the files it links. No other source changes what a pure check runs.
        public static List<string> CompiledFiles(string repository)
        {
            static string Relative(string repository, string path) => Path.GetRelativePath(repository, path).Replace('\\', '/');
            static IEnumerable<string> Sources(string repository, string folder) => Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Select(file => Relative(repository, file)).Where(file => !file.Split('/').Any(part => part is "bin" or "obj"));
            var projectFolder = Path.GetDirectoryName(Path.Combine(repository, TestProject))!;
            var project = File.ReadAllText(Path.Combine(repository, TestProject));
            var files = Sources(repository, projectFolder).ToList();
            foreach (Match reference in Regex.Matches(project, @"<ProjectReference Include=""([^""]+)"""))
                files.AddRange(Sources(repository, Path.GetDirectoryName(Path.GetFullPath(Path.Combine(projectFolder, reference.Groups[1].Value)))!));
            foreach (Match linked in Regex.Matches(project, @"<Compile Include=""([^""]+)"""))
                files.Add(Relative(repository, Path.GetFullPath(Path.Combine(projectFolder, linked.Groups[1].Value))));
            return [.. files.Distinct().Order(StringComparer.Ordinal)];
        }

        public static CodeIndex Read(string repository)
        {
            var files = CompiledFiles(repository).ToDictionary(file => file, file => File.ReadAllText(Path.Combine(repository, file)));
            var spans = new List<(string Name, string File, int First, int Last, string Text)>();
            foreach (var (file, text) in files)
            {
                var lines = text.Split('\n');
                var first = 0;
                for (var line = 0; line < lines.Length; line++)
                {
                    var declaration = TopLevelType.Match(lines[line]);
                    if (!declaration.Success) continue;
                    var last = line;
                    // A declaration ends on its own line when it ends with ; or closes every brace it opens there.
                    var opened = lines[line].Count(character => character == '{');
                    var endsHere = lines[line].TrimEnd().EndsWith(';') || (opened > 0 && opened == lines[line].Count(character => character == '}'));
                    if (!endsHere) while (last + 1 < lines.Length && !lines[last].StartsWith('}')) last++;
                    var span = string.Join('\n', lines[line..(last + 1)]);
                    // The first type of a file starts at its declaration; the lines above it (usings, namespace) touch every type.
                    spans.Add((declaration.Groups[1].Value, file, (first == 0 ? line : first) + 1, last + 1, span));
                    first = last + 1;
                    line = last;
                }
            }
            var notMember = NotMember(spans.Select(span => span.Name));
            var sharedNames = TrackedFiles(repository).GroupBy(path => path[(path.LastIndexOf('/') + 1)..]).Where(each => each.Count() > 1).Select(each => each.Key).ToHashSet(StringComparer.Ordinal);
            var types = spans.Select(span => new TypeSpan(span.Name, span.File, span.First, span.Last, TypeNames(span.Text, notMember))).ToList();
            var groups = new List<Group>();
            foreach (Match match in Regex.Matches(files[Program], @"Group\(""([^""]+)"",\s*(\w+)\.(\w+)\)"))
            {
                var (name, type, method) = (match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
                var file = types.FirstOrDefault(each => each.Name == type && each.File.StartsWith("tests/", StringComparison.Ordinal))?.File
                    ?? throw new InvalidOperationException($"Group {name}: no check file declares {type}.");
                var text = MethodText(files[file], method) ?? throw new InvalidOperationException($"Group {name}: {file} has no method {method}.");
                // A comment names a file without reading it; a // after a colon is part of a URL.
                text = Regex.Replace(text, @"(?<![:""])//.*$", "", RegexOptions.Multiline);
                var names = Names(text, notMember);
                // using static brings a type's members in without its name.
                foreach (Match imported in Regex.Matches(files[file], @"^using static ([\w.]+);", RegexOptions.Multiline)) names.Add(imported.Groups[1].Value.Split('.')[^1]);
                groups.Add(new(name, file, names, text, sharedNames));
            }
            return new CodeIndex { Groups = groups, Types = types, Texts = files };
        }

        // A method of a check class with the members of the same file it uses, by their names: the members of a top-level class
        // start at four spaces, so each one runs to the next line at that indentation. Other groups' methods are left out.
        static string? MethodText(string source, string method)
        {
            var members = new List<(string Name, string Text)>();
            var current = new List<string>();
            void Close()
            {
                if (current.Count == 0) return;
                var head = Regex.Match(current[0], @"(\w+)\s*(?:<[^>]*>)?\s*(?:\(|=|\{|;|$)");
                members.Add((head.Success ? head.Groups[1].Value : "", string.Join('\n', current)));
                current = [];
            }
            foreach (var line in source.Split('\n'))
            {
                if (Regex.IsMatch(line, @"^    [^\s{}/\[]")) Close();
                current.Add(line);
            }
            Close();
            var start = members.FindIndex(member => member.Name == method);
            if (start < 0) return null;
            var included = new HashSet<int> { start };
            var queue = new Queue<int>([start]);
            while (queue.Count > 0)
            {
                var names = Identifier.Matches(members[queue.Dequeue()].Text).Select(each => each.Value).ToHashSet(StringComparer.Ordinal);
                for (var member = 0; member < members.Count; member++)
                    if (members[member].Name.Length > 0 && !members[member].Name.StartsWith("Verify", StringComparison.Ordinal)
                        && names.Contains(members[member].Name) && included.Add(member)) queue.Enqueue(member);
            }
            return string.Join('\n', included.Order().Select(member => members[member].Text));
        }
    }
}
