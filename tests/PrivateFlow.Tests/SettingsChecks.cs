using PrivateFlow.Core;
using static Harness;

static class SettingsChecks
{
    public static void VerifySettingsPersistence()
    {
        var oldRoot = Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR");
        var testRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-settings-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", testRoot);
        try
        {
            Directory.CreateDirectory(testRoot);
            var file = Path.Combine(testRoot, "settings.json");
            Check(PrivateFlow.Windows.Settings.Load().AutoInsert, "fresh install enables insertion");
            Check(PrivateFlow.Windows.Settings.Load() is { RewriteEndpoint: "", MissingRewriteEndpoint: false }, "a fresh install has no API endpoint or migration notice");
            File.WriteAllText(file, "{\"AutoInsert\":false}");
            var settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { RewriteEndpoint: "", MissingRewriteEndpoint: true }, "a saved file that relied on the removed endpoint requests a visible configuration notice");
            settings.RewriteEndpoint = "https://example.invalid/v1";
            Check(settings.AutoInsert, "legacy default migrates to enabled insertion");
            Check(settings.ShortcutBehavior == ShortcutBehavior.Toggle, "a settings file without a shortcut behavior means toggle");
            settings.AutoInsert = false;
            settings.ShortcutModifiers = 6; settings.ShortcutKey = 0x41;
            settings.SaveAs(settings);
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { RewriteEndpoint: "https://example.invalid/v1", MissingRewriteEndpoint: false }, "a saved endpoint survives the update and removes the migration notice");
            Check(!settings.AutoInsert, "saved opt-out survives restart");
            Check(settings.ShortcutModifiers == 6 && settings.ShortcutKey == 0x41, "custom shortcut survives restart");
            settings.ShortcutBehavior = ShortcutBehavior.HoldToTalk; settings.SaveAs(settings);
            Check(File.ReadAllText(file).Contains("\"ShortcutBehavior\": \"HoldToTalk\"") && PrivateFlow.Windows.Settings.Load().ShortcutBehavior == ShortcutBehavior.HoldToTalk, "hold to talk is saved by name and survives restart");
            settings.ShortcutBehavior = ShortcutBehavior.Toggle;
            settings.Vocabulary = "Claude Code, Codex"; settings.SaveAs(settings);
            Check(PrivateFlow.Windows.Settings.Load().Vocabulary == "Claude Code, Codex", "vocabulary survives restart");
            File.WriteAllText(file, "{\"SettingsVersion\":2,\"AutoInsert\":false,\"ShortcutKey\":27}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(!settings.AutoInsert && settings.ShortcutKey == 0x20, "invalid shortcut falls back without resetting opt-out");
            File.WriteAllText(file, "{\"SettingsVersion\":2,\"AutoInsert\":false,\"LightCleanup\":true}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings.OutputStyle == OutputStyle.Clean && !settings.AutoInsert, "version 2 settings migrate to Clean output and keep the opt-out");
            settings.OutputStyle = OutputStyle.Exact; settings.SaveAs(settings);
            Check(PrivateFlow.Windows.Settings.Load().OutputStyle == OutputStyle.Exact, "chosen Exact style survives restart");
            Check(!File.ReadAllText(file).Contains("LightCleanup"), "obsolete setting is dropped on save");
            string Json(object value) => System.Text.Json.JsonSerializer.Serialize(value);
            File.WriteAllText(file, Json(new { SettingsVersion = 4, EnginePath = Path.Combine(testRoot, "runtime", "cuda12", "Release", "whisper-cli.exe"), ModelPath = Path.Combine(testRoot, "models", "ggml-medium.bin") }));
            settings = PrivateFlow.Windows.Settings.Load();
            Check(!settings.SpeechPathsCustom && settings.SpeechBackend == "auto" && settings.SpeechModel == "auto", "paths written by setup follow the recommended speech setup after the upgrade");
            File.WriteAllText(file, Json(new { SettingsVersion = 4, EnginePath = Path.Combine(testRoot, "runtime", "cpu", "whisper-cli.exe"), ModelPath = @"D:\Models\ggml-large-v3.bin" }));
            Check(PrivateFlow.Windows.Settings.Load().SpeechPathsCustom, "a model chosen outside the data folder stays as it is");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, EnginePath = @"D:\Engines\whisper-cli.exe", SpeechPathsCustom = false }));
            Check(!PrivateFlow.Windows.Settings.Load().SpeechPathsCustom, "the speech migration runs only once");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, Vocabulary = "Codex", AutoInsert = false }));
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { MeetingLanguages: SpeechLanguages.Offered, MeetingVocabulary: "", MeetingModelPath: "", Vocabulary: "Codex", AutoInsert: false }, "settings saved before the meeting fields load with their defaults and keep everything else");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"MeetingLanguages\":\"fr, ES,es\",\"MeetingVocabulary\":null,\"MeetingModelPath\":null}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { MeetingLanguages: "es", MeetingVocabulary: "", MeetingModelPath: "" }, "unknown meeting languages are dropped and null meeting fields recover");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, Vocabulary = "Claude Code, priya", MeetingVocabulary = "Priya; Okafor,okafor, Lindqvist" }));
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { Vocabulary: "Claude Code, priya, Okafor, Lindqvist", MeetingVocabulary: "" }, "an older meeting vocabulary joins the shared vocabulary once, without duplicates in any case");
            settings.SaveAs(settings);
            Check(PrivateFlow.Windows.Settings.Load() is { Vocabulary: "Claude Code, priya, Okafor, Lindqvist", MeetingVocabulary: "" } && File.ReadAllText(file).Contains("\"MeetingVocabulary\": \"\""), "the merged vocabulary survives restart and the old meeting vocabulary is not merged again");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, MeetingVocabulary = "Joaquín, Okafor" }));
            Check(PrivateFlow.Windows.Settings.Load().Vocabulary == "Joaquín, Okafor", "a meeting vocabulary becomes the shared vocabulary when there was none");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, Vocabulary = "Codex" }));
            Check(PrivateFlow.Windows.Settings.Load().PolishWaitSeconds == 120 && new PrivateFlow.Windows.Settings().PolishWaitSeconds == 120, "settings from before the polish wait mean two minutes");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, PolishWaitSeconds = 3 }));
            var tooShort = PrivateFlow.Windows.Settings.Load().PolishWaitSeconds;
            File.WriteAllText(file, Json(new { SettingsVersion = 5, PolishWaitSeconds = 100_000 }));
            var tooLong = PrivateFlow.Windows.Settings.Load().PolishWaitSeconds;
            File.WriteAllText(file, Json(new { SettingsVersion = 5, PolishWaitSeconds = 45 }));
            Check(tooShort == 10 && tooLong == 1800 && PrivateFlow.Windows.Settings.Load().PolishWaitSeconds == 45, "the polish wait stays between 10 seconds and 30 minutes and keeps a value set by hand");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"Vocabulary\":null,\"MeetingVocabulary\":\" , ;\"}");
            Check(PrivateFlow.Windows.Settings.Load().Vocabulary == "", "an empty or missing vocabulary stays empty");
            File.WriteAllText(file, Json(new { SettingsVersion = 5, Vocabulary = "Claude Code,Codex ; GPT, Zoë" }));
            Check(PrivateFlow.Windows.Settings.Load().Vocabulary is var typedVocabulary && typedVocabulary == "Claude Code,Codex ; GPT, Zoë" && VocabularyTerms.Split(typedVocabulary).SequenceEqual(["Claude Code", "Codex", "GPT", "Zoë"]),
                "a vocabulary typed into the former single field loads as it was stored and lists its terms in order");
            Check(settings is { MeetingShortcutKey: 0, MeetingShortcutModifiers: 0, SystemAudioColor: SystemAudioColors.Default }, "the meeting shortcut is not set by default and the system audio colour is the accent blue");
            settings.MeetingShortcutModifiers = 2; settings.MeetingShortcutKey = 0x4D; settings.SystemAudioColor = "#3fb950"; settings.SaveAs(settings);
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { MeetingShortcutModifiers: 2, MeetingShortcutKey: 0x4D, SystemAudioColor: "#3FB950" }, "the meeting shortcut and the chosen system audio colour survive restart");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"ShortcutModifiers\":3,\"ShortcutKey\":32,\"MeetingShortcutModifiers\":3,\"MeetingShortcutKey\":32,\"SystemAudioColor\":\"#123456\"}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { MeetingShortcutKey: 0, ShortcutKey: 0x20, SystemAudioColor: SystemAudioColors.Default }, "a meeting shortcut equal to the dictation shortcut is dropped and an unknown colour falls back to the default");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"MeetingShortcutModifiers\":0,\"MeetingShortcutKey\":65,\"SystemAudioColor\":null}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { MeetingShortcutKey: 0, SystemAudioColor: SystemAudioColors.Default }, "an invalid meeting shortcut is dropped and a missing colour recovers");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"ShortcutModifiers\":3,\"ShortcutKey\":77,\"MeetingShortcutModifiers\":1,\"MeetingShortcutKey\":115}");
            settings = PrivateFlow.Windows.Settings.Load();
            Check(settings is { ShortcutModifiers: 3, ShortcutKey: 0x4d, MeetingShortcutKey: 0 } && settings.DroppedShortcuts.SequenceEqual([("the meeting shortcut", new ActivationShortcut(1, 0x73))]),
                "a Ctrl + Alt + M dictation shortcut survives restart; an Alt + F4 meeting shortcut is dropped and named once");
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"ShortcutModifiers\":8,\"ShortcutKey\":76,\"Profiles\":[{\"Name\":\"Code\",\"Language\":\"en\",\"ShortcutModifiers\":1,\"ShortcutKey\":115},{\"Name\":\"Mails\",\"Language\":\"de\",\"ShortcutModifiers\":3,\"ShortcutKey\":80}]}");
            settings = PrivateFlow.Windows.Settings.Load();
            var reloaded = settings.Snapshot(); reloaded.ShortcutBehavior = ShortcutBehavior.HoldToTalk;
            Check(settings.Profiles is [{ ShortcutKey: 0, ShortcutModifiers: 0 }, { ShortcutModifiers: 3, ShortcutKey: 0x50 }] && settings is { ShortcutModifiers: 3, ShortcutKey: 0x20 }
                && settings.DroppedShortcuts.SequenceEqual([("the dictation shortcut", new ActivationShortcut(8, 0x4c)), ("the profile “Code”", new ActivationShortcut(1, 0x73))])
                && PrivateFlow.Windows.PreferenceSections.Problem(reloaded, settings) is null,
                "a stored profile shortcut this version refuses (Alt + F4) is cleared on load like the dictation shortcut (Win + L), each named with its owner, and no longer blocks saving another change");
            settings.SaveAs(settings);
            Check(PrivateFlow.Windows.Settings.Load() is { DroppedShortcuts.Count: 0 } cleaned && cleaned.Profiles[0].ShortcutKey == 0, "once the cleared shortcuts are stored, the next start names none again");
            {
                // The shared settings take over new values only once settings.json is written; a failed write leaves them as stored.
                // Saved once, so the profiles above keep the identifiers they were given.
                var shared = PrivateFlow.Windows.Settings.Load(); shared.SaveAs(shared);
                var next = shared.Snapshot(); next.MeetingRetentionDays = 1; next.MeetingNotesStyle = MeetingNotesStyle.Api; next.Profiles[0].Name = "Renamed"; next.KeepModelWarm = !shared.KeepModelWarm;
                // A folder in place of settings.json makes the write fail; the stored file waits beside it.
                File.Move(file, file + ".aside"); Directory.CreateDirectory(file);
                var failed = false;
                try { shared.SaveAs(next); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failed = true; }
                var ownLeft = Directory.EnumerateFiles(testRoot).Count(PrivateFlow.Windows.Settings.IsOwnPartial);
                Directory.Delete(file); File.Move(file + ".aside", file);
                var unchanged = PrivateFlow.Windows.Settings.Differences(shared, PrivateFlow.Windows.Settings.Load());
                Check(failed && ownLeft == 0 && unchanged.Count == 0 && shared.MeetingRetentionDays == 30 && shared.MeetingNotesStyle == MeetingNotesStyle.Extractive && shared.Profiles[0].Name == "Code",
                    $"a failed write leaves the shared settings exactly as stored, field by field, and removes its temporary file ({string.Join(", ", unchanged)})");
                // A setup script's own write in progress, under the name it uses when run by hand, and a leftover of a save a crash interrupted.
                var scriptWrite = Path.Combine(testRoot, "settings.json.setup-" + Guid.NewGuid().ToString("N") + ".partial");
                File.WriteAllText(scriptWrite, "{\"SettingsVersion\":5,\"Language\":\"es\"}");
                var crashed = Path.Combine(testRoot, "settings.json." + Guid.NewGuid().ToString("N") + ".partial"); File.WriteAllText(crashed, "{");
                var raised = 0; shared.Saved += () => raised++;
                shared.SaveAs(next);
                var scriptsWriteKept = File.ReadAllText(scriptWrite) == "{\"SettingsVersion\":5,\"Language\":\"es\"}";
                Check(raised == 1 && PrivateFlow.Windows.Settings.Differences(shared, next).Count == 0 && PrivateFlow.Windows.Settings.Differences(shared, PrivateFlow.Windows.Settings.Load()).Count == 0
                    && !ReferenceEquals(shared.Profiles, next.Profiles) && scriptsWriteKept && File.Exists(crashed),
                    "a successful write is taken over completely, as its own copy, and announced once; it writes under its own name, so a setup script's temporary file and an older leftover are untouched and Load ignores them");
                PrivateFlow.Windows.Settings.RemoveLeftoverWrites();
                Check(!File.Exists(crashed) && !File.Exists(scriptWrite) && !PrivateFlow.Windows.Settings.IsOwnPartial(scriptWrite),
                    "at start, under the settings lock, a save's leftover temporary file and a setup script's are removed");
                File.Delete(scriptWrite);
            }
            {
                // One owner of settings.json: a setup run by the app hands back what it installed in its result file, and the app stores exactly
                // those settings through SaveAs. Preferences saved while the setup ran stay, in the shared settings, the file and a fresh load.
                var shared = PrivateFlow.Windows.Settings.Load();
                var whileSetupRuns = shared.Snapshot(); whileSetupRuns.AutoInsert = false; whileSetupRuns.MeetingRetentionDays = 7; whileSetupRuns.MeetingNotesStyle = MeetingNotesStyle.Api;
                shared.SaveAs(whileSetupRuns);
                var resultFile = Path.Combine(testRoot, "setup-result-test.json");
                (IReadOnlyList<string> Changed, string? Refused) Install(string content)
                {
                    File.WriteAllText(resultFile, content);
                    var (values, refused) = PrivateFlow.Windows.SpeechSetup.ReadResult(resultFile);
                    return refused is null ? (PrivateFlow.Windows.SpeechSetup.TakeInstalled(shared, values), null) : ([], refused);
                }
                var engine = Path.Combine(testRoot, "runtime", "cuda12", "whisper-cli.exe"); var model = Path.Combine(testRoot, "models", "ggml-large-v3-turbo-q8_0.bin");
                var raised = 0; shared.Saved += () => raised++;
                var (changed, _) = Install(Json(new { EnginePath = engine, ModelPath = model, MeetingModelPath = "", SpeechBackend = "auto", SpeechModel = "auto", SpeechPathsCustom = false, SpeechSetupVersion = "0.5.2+test" }));
                var fresh = PrivateFlow.Windows.Settings.Load();
                Check(new[] { shared, fresh }.All(each => each is { AutoInsert: false, MeetingRetentionDays: 7, MeetingNotesStyle: MeetingNotesStyle.Api, SpeechSetupVersion: "0.5.2+test" } && each.EnginePath == engine && each.ModelPath == model)
                    && PrivateFlow.Windows.Settings.Differences(shared, fresh).Count == 0 && raised == 1 && changed.Contains("EnginePath") && changed.Contains("SpeechSetupVersion"),
                    $"a setup's result is stored over the preferences saved while it ran: the shared settings, the file and a fresh load hold the saved paste option, retention and notes style and the installed paths ({string.Join(", ", changed)})");
                var unchanged = Install(Json(new { EnginePath = engine }));
                var missing = PrivateFlow.Windows.SpeechSetup.ReadResult(Path.Combine(testRoot, "no-result.json"));
                Check(unchanged is { Changed.Count: 0, Refused: null } && missing is { Values.Count: 0, Refused: null } && raised == 1, "a result that changes nothing, or no result, as from a download of one model, stores nothing");
                // A file that cannot be read meanwhile is not read back after a setup, so the shared settings keep what they hold.
                File.WriteAllText(file, "{broken");
                var otherEngine = Path.Combine(testRoot, "runtime", "elsewhere", "llama-server.exe");
                Install(Json(new { LocalRewriteEnginePath = otherEngine }));
                Check(PrivateFlow.Windows.Settings.Load() is { AutoInsert: false, MeetingRetentionDays: 7, Unread: null } reread && reread.LocalRewriteEnginePath == otherEngine && shared.EnginePath == engine,
                    "after a setup the app does not read settings.json again, so an unreadable file there never turns the settings in use into defaults");
                // A result is input from another process and read without ever throwing: a setting a setup does not install, a path outside this
                // data folder's models and runtime folders (also through .. or a junction out of runtime), a path with NUL or of 2,000 characters,
                // a file of 5 MB, an unknown backend, a version that is none, typed paths, a value of another type or no JSON refuse all of it.
                var storedBeforeRefusals = PrivateFlow.Windows.Settings.Load();
                var hostile = new List<string>
                {
                    Json(new { EnginePath = engine, AutoInsert = true }), Json(new { EnginePath = Path.Combine(Path.GetTempPath(), "elsewhere", "whisper-cli.exe") }),
                    Json(new { ModelPath = Path.Combine(testRoot, "models", "..", "outside.bin") }), Json(new { EnginePath = Path.Combine(testRoot, "runtime", "a\0b", "whisper-cli.exe") }),
                    Json(new { EnginePath = Path.Combine(testRoot, "runtime", new string('x', 2000)) }), Json(new { EnginePath = Path.Combine(testRoot, "runtime", new string('x', 5_000_000)) }),
                    Json(new { SpeechBackend = "rocm" }), Json(new { SpeechSetupVersion = "anything goes" }), Json(new { SpeechPathsCustom = true }), Json(new { EnginePath = 42 }),
                    Json(new { EnginePath = (string?)null }), "not json", "[1, 2]",
                };
                var outside = Path.Combine(Path.GetTempPath(), "PrivateFlow-outside-" + Guid.NewGuid().ToString("N"));
                var junction = Path.Combine(testRoot, "runtime", "junction");
                if (OperatingSystem.IsWindows())
                {
                    Directory.CreateDirectory(outside); Directory.CreateDirectory(Path.Combine(testRoot, "runtime"));
                    using var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", junction, outside }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                    link.StandardOutput.ReadToEnd(); link.WaitForExit();
                    hostile.Add(Json(new { EnginePath = Path.Combine(junction, "outside.exe") }));
                }
                var refusals = new List<string>(); var thrown = "";
                foreach (var content in hostile)
                    try { refusals.Add(Install(content).Refused ?? "accepted"); } catch (Exception ex) { thrown += ex.GetType().Name + " "; }
                if (Directory.Exists(junction)) Directory.Delete(junction);
                if (Directory.Exists(outside)) Directory.Delete(outside, true);
                Check(thrown.Length == 0 && refusals.Count == hostile.Count && !refusals.Contains("accepted") && PrivateFlow.Windows.Settings.Differences(shared, storedBeforeRefusals).Count == 0
                    && PrivateFlow.Windows.Settings.Differences(PrivateFlow.Windows.Settings.Load(), storedBeforeRefusals).Count == 0 && (!OperatingSystem.IsWindows() || hostile.Count == 14),
                    $"{hostile.Count} hostile results (another setting, a path outside, through .. or a junction out of runtime, with NUL, of 2,000 characters, a 5 MB file, an unknown backend, no version, typed paths, wrong types, null, no JSON) are refused without an exception and store nothing ({thrown}{string.Join(" | ", refusals)})");
                File.Delete(resultFile);
                // SaveAs is all or nothing: while another program holds the settings lock past the wait, it changes neither memory nor disk;
                // once the lock is free, memory and disk hold the same values.
                var beforeLocked = PrivateFlow.Windows.Settings.Load(); var attemptsBefore = PrivateFlow.Windows.Settings.LockAttempts;
                var lockedOut = shared.Snapshot(); lockedOut.MeetingRetentionDays = 9;
                string? lockRefusal = null;
                PrivateFlow.Windows.Settings.LockAttempts = 3;
                try
                {
                    using (new FileStream(Path.Combine(testRoot, "settings.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                        try { shared.SaveAs(lockedOut); } catch (PrivateFlow.Windows.SettingsUnavailableException ex) { lockRefusal = ex.Message; }
                }
                finally { PrivateFlow.Windows.Settings.LockAttempts = attemptsBefore; }
                var memoryKept = PrivateFlow.Windows.Settings.Differences(shared, beforeLocked).Count == 0 && PrivateFlow.Windows.Settings.Differences(PrivateFlow.Windows.Settings.Load(), beforeLocked).Count == 0;
                shared.SaveAs(lockedOut);
                Check(lockRefusal is not null && memoryKept && shared.MeetingRetentionDays == 9 && PrivateFlow.Windows.Settings.Differences(shared, PrivateFlow.Windows.Settings.Load()).Count == 0,
                    $"a save that cannot get the settings lock changes neither the shared settings nor the file; one that can leaves both equal ({lockRefusal})");
                // The leftovers a start removes, by exact name: temporary files of saves and of scripts run outside the app, results of setups.
                // The lock files and files of other names stay.
                string Hex() => Guid.NewGuid().ToString("N");
                var removedAtStart = new[] { $"settings.json.setup-{Hex()}.partial", $"setup-result-{Hex()}.json" }.Select(name => Path.Combine(testRoot, name)).ToArray();
                var keptAtStart = new[] { "setup-result-test.json", "settings.json.setup-x.partial", $"setup-result-{Hex()}.json.bak" }.Select(name => Path.Combine(testRoot, name)).ToArray();
                foreach (var each in removedAtStart.Concat(keptAtStart)) File.WriteAllText(each, "{}");
                PrivateFlow.Windows.Settings.RemoveLeftoverWrites();
                Check(removedAtStart.All(each => !File.Exists(each)) && keptAtStart.All(File.Exists) && File.Exists(Path.Combine(testRoot, "settings.lock")),
                    "at start the setups' leftovers go by exact name; the lock file and other files stay");
                foreach (var each in keptAtStart) File.Delete(each);
                // A settings file another program holds open, sharing deletion or nothing, is read again for about a second; then the load fails,
                // so nothing starts with the defaults and nothing is renamed, and the file stays as it was.
                if (OperatingSystem.IsWindows())
                {
                    var intact = PrivateFlow.Windows.Settings.Load(); var keptBefore = Directory.EnumerateFiles(testRoot, "settings.json.unreadable-*").Count();
                    foreach (var share in new[] { FileShare.Delete, FileShare.None })
                    {
                        string? failure = null;
                        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, share))
                            try { PrivateFlow.Windows.Settings.Load(); } catch (PrivateFlow.Windows.SettingsUnavailableException ex) { failure = ex.Message; }
                        Check(failure?.StartsWith("settings.json is in use by another program or not accessible") == true && File.Exists(file)
                            && Directory.EnumerateFiles(testRoot, "settings.json.unreadable-*").Count() == keptBefore && PrivateFlow.Windows.Settings.Differences(PrivateFlow.Windows.Settings.Load(), intact).Count == 0,
                            $"a settings file another program holds open with FileShare.{share} fails the load after retries, is not set aside and stays intact ({failure})");
                    }
                }
            }
            {
                // What counts as unsaved: each preference belongs to the section that shows it, and only a differing value makes it unsaved.
                var stored = new PrivateFlow.Windows.Settings { Profiles = [new() { Name = "Code", ShortcutModifiers = 3, ShortcutKey = 0x50 }] };
                var sections = new (string Section, Action<PrivateFlow.Windows.Settings> Change)[]
                {
                    (PrivateFlow.Windows.PreferenceSections.Audio, shown => shown.SystemAudioColor = "#3FB950"),
                    (PrivateFlow.Windows.PreferenceSections.Audio, shown => shown.MicrophoneId = "usb"),
                    (PrivateFlow.Windows.PreferenceSections.Shortcuts, shown => shown.ShortcutBehavior = ShortcutBehavior.HoldToTalk),
                    (PrivateFlow.Windows.PreferenceSections.Shortcuts, shown => shown.MeetingShortcutKey = 0x4d),
                    (PrivateFlow.Windows.PreferenceSections.LanguageAndPasting, shown => shown.Language = "de"),
                    (PrivateFlow.Windows.PreferenceSections.TextStyle, shown => shown.PolishWaitSeconds = 30),
                    (PrivateFlow.Windows.PreferenceSections.Profiles, shown => shown.Profiles[0].Literal = true),
                    (PrivateFlow.Windows.PreferenceSections.Models, shown => shown.MeetingNotesStyle = MeetingNotesStyle.Api),
                    (PrivateFlow.Windows.PreferenceSections.Models, shown => shown.LocalPolishModel = LocalPolishModel.MeetingNotesModel),
                    (PrivateFlow.Windows.PreferenceSections.MeetingDefaults, shown => shown.MeetingRetentionDays = 45),
                    (PrivateFlow.Windows.PreferenceSections.SpeechRecognition, shown => shown.KeepModelWarm = false),
                    (PrivateFlow.Windows.PreferenceSections.Vocabulary, shown => shown.Vocabulary = "Qwen"),
                };
                var each = sections.Select(entry => { var shown = stored.Snapshot(); entry.Change(shown); return (entry.Section, Changed: PrivateFlow.Windows.PreferenceSections.Changed(shown, stored)); }).ToList();
                Check(PrivateFlow.Windows.PreferenceSections.Changed(stored.Snapshot(), stored).Count == 0 && each.All(entry => entry.Changed.SequenceEqual([entry.Section])),
                    "a copy of the stored preferences is not unsaved, and a changed value in each section marks exactly that section");
                var notShown = stored.Snapshot(); notShown.SpeechSetupVersion = "other"; notShown.Corrections = [new() { Original = "a", Replacement = "b" }]; notShown.SelectedProfileId = "x";
                Check(PrivateFlow.Windows.PreferenceSections.Changed(notShown, stored).Count == 0, "values no field shows, such as the setup version, corrections and the selected profile, never count as unsaved");
                // Each stored setting is described once in the table, with its section or among the commented settings without a control.
                var storedNames = typeof(PrivateFlow.Windows.Settings).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public).Where(property => property.CanWrite).Select(property => property.Name).ToList();
                var described = PrivateFlow.Windows.PreferenceSections.Table.Select(setting => setting.Name).ToList();
                Check(described.Order().SequenceEqual(storedNames.Order()),
                    $"every setting settings.json stores is described exactly once in the table of stored settings (missing: {string.Join(", ", storedNames.Except(described))}; twice: {string.Join(", ", described.GroupBy(name => name).Where(group => group.Count() > 1).Select(group => group.Key))})");
                var withSection = PrivateFlow.Windows.PreferenceSections.Table.Where(setting => setting.Section is not null).Select(setting => setting.Name).ToList();
                Check(PrivateFlow.Windows.PreferenceSections.ControlProblem(withSection) is null
                    && PrivateFlow.Windows.PreferenceSections.ControlProblem(withSection.Skip(1)) == $"{withSection[0]} has 0 controls"
                    && PrivateFlow.Windows.PreferenceSections.ControlProblem([.. withSection, withSection[0]]) == $"{withSection[0]} has 2 controls"
                    && PrivateFlow.Windows.PreferenceSections.ControlProblem([.. withSection, nameof(PrivateFlow.Windows.Settings.SpeechSetupVersion)]) == "SpeechSetupVersion has a control but no section",
                    "the window's controls must cover every setting of a section exactly once and no setting without a section");
                var twoSections = stored.Snapshot(); twoSections.Language = "de"; twoSections.MeetingRetentionDays = 7; twoSections.SystemAudioColor = "#3FB950";
                Check(PrivateFlow.Windows.PreferenceSections.Describe(PrivateFlow.Windows.PreferenceSections.Changed(twoSections, stored)) == "Your changes under Microphone and speakers, Language and pasting and Meeting defaults are not saved yet."
                    && PrivateFlow.Windows.PreferenceSections.Describe([PrivateFlow.Windows.PreferenceSections.Shortcuts]) == "Your changes under Shortcuts are not saved yet.",
                    "the question before unsaved preferences are lost names the sections in the order of the settings popup");
                var badDays = stored.Snapshot(); badDays.MeetingRetentionDays = 0;
                var clash = stored.Snapshot(); clash.ShortcutModifiers = 3; clash.ShortcutKey = 0x50;
                var unnamed = stored.Snapshot(); unnamed.Profiles[0].Name = " ";
                var badStored = stored.Snapshot(); badStored.MeetingRetentionDays = 0; var otherChange = badStored.Snapshot(); otherChange.Language = "de";
                Check(PrivateFlow.Windows.PreferenceSections.Problem(badDays, stored) == (PrivateFlow.Windows.PreferenceSections.MeetingDefaults, "Keep completed meetings for: choose 1 to 3650 days.")
                    && PrivateFlow.Windows.PreferenceSections.Problem(clash, stored) == (PrivateFlow.Windows.PreferenceSections.Profiles, "The profile “Code” has the dictation shortcut. Choose another one for it.")
                    && PrivateFlow.Windows.PreferenceSections.Problem(unnamed, stored)?.Message == "Profile names must be unique, and every profile needs a language."
                    && PrivateFlow.Windows.PreferenceSections.Problem(otherChange, badStored) is null && PrivateFlow.Windows.PreferenceSections.Problem(stored.Snapshot(), stored) is null,
                    "Save preferences refuses invalid retention days, an unnamed profile and a profile shortcut equal to the new dictation shortcut, naming the section; an unchanged section is not checked");
                var reserved = stored.Snapshot(); reserved.Profiles[0].ShortcutModifiers = 1; reserved.Profiles[0].ShortcutKey = 0x73;
                var twins = stored.Snapshot(); twins.Profiles.Add(new() { Name = "Mails", ShortcutModifiers = 3, ShortcutKey = 0x50 });
                Check(PrivateFlow.Windows.PreferenceSections.Problem(reserved, stored)?.Message == "The shortcut of the profile “Code” cannot be used: Alt + F4 closes the window in front; it cannot be a shortcut."
                    && PrivateFlow.Windows.PreferenceSections.Problem(twins, stored)?.Message == "The profiles “Code” and “Mails” have the same shortcut. Choose another one for one of them.",
                    "a Save blocked by a profile's shortcut names the profile and why");
                var sameNames = stored.Snapshot(); sameNames.Profiles.Add(new() { Name = " code " });
                var noLanguage = stored.Snapshot(); noLanguage.Profiles[0].Language = "fr";
                Check(PrivateFlow.Windows.PreferenceSections.Problem(sameNames, stored) == (PrivateFlow.Windows.PreferenceSections.Profiles, "Profile names must be unique, and every profile needs a language.")
                    && PrivateFlow.Windows.PreferenceSections.Problem(noLanguage, stored)?.Message == "Profile names must be unique, and every profile needs a language.",
                    "Save preferences refuses two profiles named alike in any case and spacing, and a profile language other than auto/en/de/es");
            }
            PrivateFlow.Windows.Settings Migrated(string backendFolder, string model, bool hasNvidiaGpu)
            {
                var engine = Path.Combine(testRoot, "runtime", backendFolder, "whisper-cli.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(engine)!); File.WriteAllText(engine, "");
                var migrated = new PrivateFlow.Windows.Settings { EnginePath = engine, ModelPath = Path.Combine(testRoot, "models", $"ggml-{model}.bin") };
                PrivateFlow.Windows.Settings.MigrateSpeechChoice(migrated, hasNvidiaGpu);
                return migrated;
            }
            var cpuFallback = Migrated("cpu", "base", true);
            Check(cpuFallback.SpeechBackend == "cpu" && cpuFallback.SpeechModel == "auto" && !cpuFallback.SpeechPathsCustom, "a CPU engine kept on an NVIDIA PC stays an explicit CPU choice");
            var gpuMedium = Migrated("cuda12", "medium", true);
            Check(gpuMedium.SpeechBackend == "auto" && gpuMedium.SpeechModel == "auto", "the former GPU recommendation medium follows the new recommendation");
            var cpuMedium = Migrated("cpu", "medium", false);
            Check(cpuMedium.SpeechBackend == "auto" && cpuMedium.SpeechModel == "medium", "medium on the CPU was a deliberate choice and stays explicit");
            Check(Migrated("cuda12", "small", true).SpeechPathsCustom, "a model setup never installs counts as hand-typed");
            var automatic = new PrivateFlow.Windows.Settings { EnginePath = Path.Combine(testRoot, "runtime", "cuda12", "Release", "whisper-cli.exe"), ModelPath = Path.Combine(testRoot, "models", "ggml-large-v3-turbo-q8_0.bin"),
                MeetingModelPath = Path.Combine(testRoot, "models", "ggml-large-v3.bin") };
            automatic.UsePathsEnteredByHand(automatic.EnginePath, automatic.ModelPath);
            Check(!automatic.SpeechPathsCustom && automatic.MeetingModelPath.EndsWith("ggml-large-v3.bin"), "saving unchanged engine paths keeps the setup and its meeting model");
            var typedEngine = Path.Combine(testRoot, "runtime", "cpu", "whisper-cli.exe");
            automatic.UsePathsEnteredByHand(typedEngine, automatic.ModelPath);
            Check(automatic is { SpeechPathsCustom: true, MeetingModelPath: "" } && automatic.EnginePath == typedEngine, "a hand-typed engine path also takes the meeting final pass off the setup's large-v3");
            var ownData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateFlow");
            bool IsolatedWith(string root) { Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", root); try { return PrivateFlow.Windows.Settings.IsolatedRoot; } finally { Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", testRoot); } }
            Check(!IsolatedWith(ownData) && !IsolatedWith(ownData + Path.DirectorySeparatorChar) && !IsolatedWith(ownData.ToUpperInvariant()) && !IsolatedWith(Path.Combine(ownData, "evaluation")) && !IsolatedWith("relative-data") && !IsolatedWith("")
                && IsolatedWith(testRoot) && IsolatedWith(ownData + "Evaluation"), "evaluation and diagnostic modes refuse the app's own data folder and anything inside it");
            // A file that cannot be read is set aside under its own name, never overwritten, and the app starts with defaults and says why.
            List<string> Kept() => Directory.EnumerateFiles(testRoot, "settings.json.unreadable-*").ToList();
            foreach (var kept in Kept()) File.Delete(kept);
            File.WriteAllText(file, "{\"SettingsVersion\":5,\"AutoInsert\":\"no\",\"Vocabulary\":\"Joaquín\"}");
            var mistyped = PrivateFlow.Windows.Settings.Load();
            var keptFiles = Kept();
            var loadedAgain = PrivateFlow.Windows.Settings.Load();
            Check(mistyped is { AutoInsert: true, Vocabulary: "", Unread: { KeptAs: { } keptAs } unread } && unread.Reason.Contains("AutoInsert") && keptFiles.SequenceEqual([keptAs]) && !File.Exists(file)
                && File.ReadAllText(keptAs).Contains("Joaquín") && loadedAgain.Unread is null && Kept().Count == 1,
                $"a settings file with a mistyped value loads as the defaults, is kept unchanged as settings.json.unreadable-<time>, exactly once, and names the value ({mistyped.Unread?.Reason})");
            mistyped.SaveAs(mistyped.Snapshot());
            Check(File.Exists(file) && Kept().SequenceEqual(keptFiles) && File.ReadAllText(keptFiles[0]).Contains("Joaquín") && PrivateFlow.Windows.Settings.Load().Unread is null,
                "the next save writes a new settings.json and leaves the kept copy as it was");
            // Kept names differ by the millisecond; the waits keep these three loads apart.
            Thread.Sleep(20); File.WriteAllText(file, "{\"SettingsVersion\":5,\"Vocabul");
            var truncated = PrivateFlow.Windows.Settings.Load();
            Thread.Sleep(20); File.WriteAllText(file, "null");
            var empty = PrivateFlow.Windows.Settings.Load();
            File.Delete(file);
            var firstStart = PrivateFlow.Windows.Settings.Load();
            Check(truncated is { AutoInsert: true, Unread.KeptAs: not null } && empty is { AutoInsert: true, Unread.KeptAs: not null } && Kept().Count == 3 && firstStart is { AutoInsert: true, Unread: null },
                "a file cut short and one holding null are kept aside too, each under its own name; a missing file is the normal first start");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", oldRoot);
            Directory.Delete(testRoot, true);
        }
    }

    public static void VerifySettingsDataFolder()
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", "relative-data");
            try { _ = PrivateFlow.Windows.Settings.Root; Check(false, "relative data override is rejected"); }
            catch (InvalidOperationException) { Check(true, "relative data override is rejected before settings access"); }
        }
        finally { Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", previousDataRoot); }
    }

    public static void VerifyProfilesApply()
    {
        var aliases = new[] { new PhraseRule { Original = "code x", Replacement = "Codex" } };
        var snapshotSource = new PrivateFlow.Windows.Settings { Corrections = aliases.ToList() };
        var snapshot = snapshotSource.Snapshot(); snapshotSource.Corrections[0].Replacement = "changed";
        Same(snapshot.Corrections[0].Replacement, "Codex", "session snapshots freeze mutable dictionary entries");
        var apiProfile = new DictationProfile { OutputStyle = OutputStyle.Concise, Formatting = true };
        snapshot.ApplyProfile(apiProfile, false); Check(snapshot.OutputStyle == OutputStyle.Clean && snapshot.SpokenFormatting, "automatic profile matching cannot opt into API Concise");
        snapshot.ApplyProfile(apiProfile, true); Check(snapshot.OutputStyle == OutputStyle.Concise, "explicit profile selection may select API Concise");
        var insertionSettings = new PrivateFlow.Windows.Settings { AutoInsert = false };
        var insertionProfile = new DictationProfile();
        insertionSettings.ApplyProfile(insertionProfile, false);
        Check(!insertionSettings.AutoInsert, "automatic profile preserves global insertion opt-out even with a new profile");
        insertionSettings.AutoInsert = true; insertionProfile.AutoInsert = false;
        insertionSettings.ApplyProfile(insertionProfile, false);
        Check(!insertionSettings.AutoInsert, "automatic profile may opt out of insertion");
        insertionSettings.AutoInsert = true; insertionProfile.AutoInsert = true;
        insertionSettings.ApplyProfile(insertionProfile, false);
        Check(insertionSettings.AutoInsert, "automatic profile keeps insertion enabled when both settings allow it");
        insertionSettings.AutoInsert = false;
        insertionSettings.ApplyProfile(insertionProfile, true);
        Check(insertionSettings.AutoInsert, "explicit profile selection may enable insertion");
        insertionProfile.AutoInsert = false;
        insertionSettings.ApplyProfile(insertionProfile, true);
        Check(!insertionSettings.AutoInsert, "explicit profile selection may disable insertion");
    }

    public static void VerifySettingsBounds()
    {
        var previousDataRoot = Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR");
        // Settings, profiles, setup names and the archive helpers that the UI calls.
        var boundsRoot = Path.Combine(Path.GetTempPath(), "PrivateFlow-bounds-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", boundsRoot);
        try
        {
            Directory.CreateDirectory(boundsRoot);
            var boundsFile = Path.Combine(boundsRoot, "settings.json");
            File.WriteAllText(boundsFile, "{\"SettingsVersion\":5,\"MeetingRetentionDays\":0,\"OutputStyle\":7,\"Profiles\":null,\"Corrections\":null}");
            var bounded = PrivateFlow.Windows.Settings.Load();
            Check(bounded.MeetingRetentionDays == 1 && bounded.OutputStyle == OutputStyle.Clean && bounded.Profiles.Count == 0 && bounded.Corrections.Count == 0, "out-of-range retention, unknown style and missing lists load as safe values");
            File.WriteAllText(boundsFile, "{\"SettingsVersion\":5,\"MeetingRetentionDays\":99999}");
            Check(PrivateFlow.Windows.Settings.Load().MeetingRetentionDays == 3650, "retention is capped at ten years");
            Check(PrivateFlow.Windows.Settings.Load().MeetingNotesStyle == MeetingNotesStyle.Extractive, "a settings file without the notes style means extractive notes");
            File.WriteAllText(boundsFile, "{\"SettingsVersion\":5,\"MeetingNotesStyle\":7}");
            Check(PrivateFlow.Windows.Settings.Load().MeetingNotesStyle == MeetingNotesStyle.Extractive, "an unknown notes style loads as extractive notes");
            File.WriteAllText(boundsFile, "{\"SettingsVersion\":5,\"AutoInsert\":false,\"MeetingNotesStyle\":\"FutureStyle\",\"ShortcutBehavior\":\"FutureBehavior\",\"OutputStyle\":\"FutureOutput\"}");
            var unknownNames = PrivateFlow.Windows.Settings.Load();
            Check(!unknownNames.AutoInsert && unknownNames.MeetingNotesStyle == MeetingNotesStyle.Extractive && unknownNames.ShortcutBehavior == ShortcutBehavior.Toggle && unknownNames.OutputStyle == OutputStyle.Clean,
                "choices with a name this version does not know load as their defaults and keep the rest of the file, such as the insertion opt-out");
            foreach (var style in new[] { MeetingNotesStyle.LocalModel, MeetingNotesStyle.Api })
            {
                var notesChoice = PrivateFlow.Windows.Settings.Load(); notesChoice.MeetingNotesStyle = style; notesChoice.SaveAs(notesChoice);
                Check(PrivateFlow.Windows.Settings.Load().MeetingNotesStyle == style && File.ReadAllText(boundsFile).Contains($"\"MeetingNotesStyle\": \"{style}\""), $"the notes style {style} survives restart by name");
            }
            File.WriteAllText(boundsFile, "{\"SettingsVersion\":5,\"MeetingLanguages\":\"cy,xx\",\"MeetingVocabulary\":null}");
            Check(PrivateFlow.Windows.Settings.Load() is { MeetingLanguages: "en,de,es", MeetingVocabulary: "", MeetingModelPath: "" }, "unknown meeting languages fall back to English, German and Spanish; no meeting model is set by default");
            var meetingChoices = PrivateFlow.Windows.Settings.Load(); meetingChoices.MeetingLanguages = "es,en"; meetingChoices.SaveAs(meetingChoices);
            Check(PrivateFlow.Windows.Settings.Load() is { MeetingLanguages: "es,en" }, "the last meeting's languages survive restart");
            var engineChoices = PrivateFlow.Windows.Settings.Load();
            engineChoices.SpeechDetection = false; engineChoices.KeepModelWarm = false; engineChoices.RetainMeetingAudio = true; engineChoices.Language = "es"; engineChoices.OutputStyle = OutputStyle.LocalPolish; engineChoices.SaveAs(engineChoices);
            var reloaded = PrivateFlow.Windows.Settings.Load();
            Check(!reloaded.SpeechDetection && !reloaded.KeepModelWarm && reloaded.RetainMeetingAudio && reloaded.Language == "es" && reloaded.OutputStyle == OutputStyle.LocalPolish, "detection, warm model, audio retention, Spanish and Local polish survive restart");
        }
        finally { Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", previousDataRoot); Directory.Delete(boundsRoot, true); }
    }

    public static void VerifyProfilesAndSetupNames()
    {
        var profiled = new PrivateFlow.Windows.Settings();
        var spanishProfile = new DictationProfile { Language = "es", Vocabulary = "Joaquín", OutputStyle = OutputStyle.Exact, Technical = true, Literal = true, Formatting = true };
        profiled.ApplyProfile(spanishProfile, false);
        Check(profiled is { Language: "es", Vocabulary: "Joaquín", OutputStyle: OutputStyle.Exact, TechnicalMode: true, LiteralMode: true, SpokenFormatting: true } && profiled.SelectedProfileId == spanishProfile.Id, "a profile sets language, vocabulary, style and text modes");
        Check(SpeechLanguages.IsChoice("es") && SpeechLanguages.IsChoice("auto") && !SpeechLanguages.IsChoice("fr") && SpeechLanguages.IsSpoken("es") && !SpeechLanguages.IsSpoken("auto"), "Spanish is an offered and a spoken language, auto is offered only");
        Check(PrivateFlow.Windows.SpeechSetup.ModelName(Path.Combine("Data", "models", "ggml-large-v3-turbo-q8_0.bin")) == "large-v3-turbo-q8_0" && PrivateFlow.Windows.SpeechSetup.ModelName(Path.Combine("x", "custom.bin")) == "custom.bin"
            && PrivateFlow.Windows.SpeechSetup.Processor(@"C:\Data\runtime\cuda12\Release\whisper-cli.exe") == "GPU" && PrivateFlow.Windows.SpeechSetup.Processor(@"C:\Data\runtime\cpu\whisper-cli.exe") == "CPU", "the Speech recognition section names model and processor from the paths");
        Check(SpeechRuntime.BlockedMessage("ggml.dll").Contains("Smart App Control") && SpeechRuntime.BlockedMessage("ggml.dll").Contains("ggml.dll"), "a blocked runtime names Smart App Control and the file");
        Check(SpeechRuntime.ProcessingFailedMessage(-7).Contains("decoder memory could not be allocated") && SpeechRuntime.ProcessingFailedMessage(-6) == "Local speech processing failed (whisper code -6).",
            "a failed decode says when the decoder memory was missing and gives Whisper's code otherwise");
        Check(SpeechRuntime.GpuMemoryNeeded(3_095_033_483) is > 4_520_000_000 and < 4_700_000_000 && SpeechRuntime.GpuMemoryNeeded(874_188_075) is > 1_210_000_000 and < 1_400_000_000,
            "the GPU memory a model needs covers what large-v3 with its beam cache and large-v3-turbo-q8_0 took when measured");
        Check(SpeechRuntime.Gigabytes(500_000_000) is "0.5" or "0,5" && SpeechRuntime.Gigabytes(8_589_000_000) is "8.6" or "8,6", "GPU memory is shown in gigabytes with one decimal");
    }

    public static void VerifyProfileMatching()
    {
        var codeProfile = new DictationProfile { Name = "Code", Applications = " Code.exe , ,devenv.exe" };
        var mailProfile = new DictationProfile { Name = "Mails", Applications = "OUTLOOK.EXE,code.exe" };
        var unmatchedProfile = new DictationProfile { Name = "Notes", Applications = "" };
        DictationProfile[] matchable = [unmatchedProfile, codeProfile, mailProfile];
        Check(DictationProfiles.Match(matchable, "Code.exe") == codeProfile && DictationProfiles.Match(matchable, "DEVENV.exe") == codeProfile && DictationProfiles.Match(matchable, "outlook.exe") == mailProfile,
            "profile matching finds an application in a comma-separated list, trimmed and in any case");
        Check(DictationProfiles.Match(matchable, "code.exe") == codeProfile && DictationProfiles.Match([mailProfile, codeProfile], "Code.exe") == mailProfile, "when two profiles name the application, the first one wins");
        Check(DictationProfiles.Match(matchable, "notepad.exe") is null && DictationProfiles.Match(matchable, "Code") is null && DictationProfiles.Match(matchable, "") is null && DictationProfiles.Match([], "Code.exe") is null,
            "an application no profile names, a name without .exe, an empty name and no profiles match nothing; empty entries never match");
    }
}
