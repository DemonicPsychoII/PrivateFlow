using PrivateFlow.Core;
using static Harness;

static class ScriptChecks
{
    public static void VerifyEvaluationEncoding()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "Windows PowerShell evaluation encoding checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "eval-common.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        var runner = Path.Combine(Path.GetTempPath(), "PrivateFlow-evaluation-encoding-" + Guid.NewGuid().ToString("N") + ".ps1");
        try
        {
            File.WriteAllText(runner, """
                param([string]$Scripts)
                $ErrorActionPreference = 'Stop'
                $tokens = $null; $errors = $null
                $common = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Scripts 'eval-common.ps1'), [ref]$tokens, [ref]$errors)
                if ($errors) { throw 'Evaluation script syntax errors.' }
                $definition = $common.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-MarkedReference'}, $true)
                . ([scriptblock]::Create($definition.Extent.Text))
                $ningun = 'ning' + [char]0xfa + 'n'; $jamas = 'jam' + [char]0xe1 + 's'
                $marked = Get-MarkedReference "No hay $ningun error; $jamas." 'es'
                if (-not $marked.Contains("[$ningun]") -or -not $marked.Contains("[$jamas]")) { throw 'Accented Spanish negations were not marked.' }
                $joaquin = 'Joaqu' + [char]0xed + 'n'
                foreach ($script in 'meeting-eval.ps1','full-eval.ps1') {
                    $tree = [Management.Automation.Language.Parser]::ParseFile((Join-Path $Scripts $script), [ref]$tokens, [ref]$errors)
                    if ($errors) { throw "Syntax errors in $script" }
                    $literals = @($tree.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst]}, $true) | ForEach-Object Value)
                    if (-not ($literals | Where-Object { $_.Contains($joaquin) })) { throw "Accented literal corrupted in $script" }
                }
                Write-Host 'PASS Spanish negations and meeting/correction literals decode correctly'
                """);
            foreach (var shell in new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" })
            {
                var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoProfile", "-File", runner, "-Scripts", Path.Combine(repository.FullName, "scripts") }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(30000)) { process.Kill(true); throw new Exception("Evaluation encoding check timed out."); }
                Check(process.ExitCode == 0 && output.Result.Contains("PASS Spanish negations"), $"{Path.GetFileName(shell)} decodes accented Spanish negations and both evaluation scripts' accented literals ({errors.Result.Trim()})");
            }
        }
        finally { File.Delete(runner); }
    }

    public static void VerifySettingsFileScript()
    {
        // The settings step setup-engine.ps1, setup-rewrite.ps1 and install.ps1 share when no Private Flow started them (scripts/settings-file.ps1),
        // against the app's own lock files, in Windows PowerShell and, where installed, PowerShell 7. No download runs: the downloads the hand-run
        // scripts would start are replaced by functions that fail the test.
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !Directory.Exists(Path.Combine(repository.FullName, "src", "PrivateFlow.Windows"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout with src and scripts was not found above " + AppContext.BaseDirectory);
        string ScriptPath(string name) => Path.Combine(repository.FullName, "scripts", name);
        string Script(string name) => File.ReadAllText(ScriptPath(name));
        var install = Script("install.ps1");
        Check(new[] { "setup-engine.ps1", "setup-rewrite.ps1", "install.ps1" }.All(helper => Script(helper) is var text && text.Contains("settings-file.ps1") && text.Contains("Save-InstalledSettings ") && !text.Contains("ConvertTo-Json -Depth 20"))
            && install.Contains(@"Join-Path $Source 'scripts\settings-file.ps1'") && install.IndexOf("-ResultFile $taskResult", StringComparison.Ordinal) < install.IndexOf("Get-Process PrivateFlow", StringComparison.Ordinal)
            && install.IndexOf("Get-Process PrivateFlow", StringComparison.Ordinal) < install.IndexOf("Save-InstalledSettings $taskDataRoot", StringComparison.Ordinal)
            && install.IndexOf("Save-InstalledSettings $taskDataRoot", StringComparison.Ordinal) < install.IndexOf("if (-not $NoLaunch)", StringComparison.Ordinal)
            && Script("build.ps1").Contains("scripts/settings-file.ps1") && Script("build.sh").Contains("'settings-file.ps1'"),
            "the three helpers write settings only through the shared step; install.ps1 downloads into a result file before it stops the app, stores the paths after stopping it and before starting the new one, and loads the step from the package it installs; both package builds ship it");
        var settingNames = PrivateFlow.Windows.PreferenceSections.Table.Select(setting => setting.Name).ToHashSet();
        var scriptInstalled = new[] { ("setup-engine.ps1", @"(?:\$taskInstalled\.|[{;]\s*|^\s+)(\w+) = "), ("setup-rewrite.ps1", @"'(\w+)'"), ("install.ps1", @"\$taskInstalled = \[ordered\]@\{ (\w+) = ") }
            .SelectMany(each => System.Text.RegularExpressions.Regex.Matches(Script(each.Item1), each.Item2, System.Text.RegularExpressions.RegexOptions.Multiline).Select(match => match.Groups[1].Value))
            .Where(settingNames.Contains).ToHashSet();
        Check(scriptInstalled.SetEquals(PrivateFlow.Windows.PreferenceSections.Installed),
            $"the settings setup-engine.ps1, setup-rewrite.ps1 and install.ps1 install are exactly those the table of stored settings marks as installed, the only ones a setup result may name ({string.Join(", ", scriptInstalled.Order())})");
        if (!OperatingSystem.IsWindows()) { Check(true, "the shared settings step runs in Windows PowerShell and PowerShell 7 only on Windows; not run here"); return; }
        var shells = new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(folder => Path.Combine(folder.Trim(), "pwsh.exe")).Where(File.Exists).Take(1))
            .Where(File.Exists).ToList();
        var oldRoot = Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR");
        foreach (var shell in shells)
        {
            var shellName = Path.GetFileNameWithoutExtension(shell);
            var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-settings-script-" + Guid.NewGuid().ToString("N"));
            var alias = root + "-alias";
            Directory.CreateDirectory(root);
            Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", root);
            try
            {
                // Modes: save, the helpers' last step with stand-in paths (-Pause holds it after its read, at its ConvertTo-Json, until released);
                // hold, a stand-in Private Flow holding instance.lock, or settings.lock with -Lock settings; engine and rewrite, the hand-run
                // setup-engine.ps1 and setup-rewrite.ps1, whose downloads fail the test if reached.
                var runner = Path.Combine(root, "runner.ps1");
                File.WriteAllText(runner, """
                    param([string]$Scripts, [string]$DataRoot, [string]$Mode, [string]$Backend = 'cpu', [string]$Pause, [string]$Lock = 'instance')
                    $ErrorActionPreference = 'Stop'
                    function Invoke-WebRequest { throw 'A download was attempted.' }
                    function curl.exe { throw 'A download was attempted.' }
                    switch ($Mode) {
                        'save' {
                            . (Join-Path $Scripts 'settings-file.ps1')
                            if ($Pause) { function ConvertTo-Json { Set-Content -LiteralPath "$Pause.paused" -Value 'x'; while (-not (Test-Path -LiteralPath "$Pause.resume")) { Start-Sleep -Milliseconds 20 }; $input | Microsoft.PowerShell.Utility\ConvertTo-Json @args } }
                            Save-InstalledSettings $DataRoot ([ordered]@{ EnginePath = (Join-Path $DataRoot "runtime\$Backend\whisper-cli.exe"); ModelPath = (Join-Path $DataRoot 'models\ggml-base.bin') })
                        }
                        'hold' { $held = [IO.File]::Open((Join-Path $DataRoot "$Lock.lock"), 'OpenOrCreate', 'ReadWrite', 'None'); Set-Content -LiteralPath "$Pause.paused" -Value 'x'; Start-Sleep -Seconds 120 }
                        'engine' { & (Join-Path $Scripts 'setup-engine.ps1') -Backend cpu -Model base -DataRoot $DataRoot }
                        'rewrite' { & (Join-Path $Scripts 'setup-rewrite.ps1') -DataRoot $DataRoot }
                    }
                    """);
                System.Diagnostics.Process Run(string mode, string backend = "cpu", string pause = "", string dataRoot = "", string lockName = "instance")
                {
                    var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", runner, "-Scripts", Path.Combine(repository.FullName, "scripts"), "-DataRoot", dataRoot.Length > 0 ? dataRoot : root,
                        "-Mode", mode, "-Backend", backend, "-Lock", lockName }.Concat(pause.Length > 0 ? ["-Pause", pause] : Array.Empty<string>())) start.ArgumentList.Add(argument);
                    return System.Diagnostics.Process.Start(start)!;
                }
                (bool Done, int Exit, string Errors) Finish(System.Diagnostics.Process process)
                {
                    var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
                    var done = process.WaitForExit(60000);
                    // PowerShell 7 colours its error view and wraps it behind "     | ".
                    var text = System.Text.RegularExpressions.Regex.Replace(System.Text.RegularExpressions.Regex.Replace(errors.Result + output.Result, @"\x1b\[[0-9;]*m", ""), @"\s*\r?\n\s*\|?\s*", " ");
                    return (done, done ? process.ExitCode : -1, text);
                }
                bool WaitFor(string marker) { for (var wait = 0; wait < 500 && !File.Exists(marker); wait++) Thread.Sleep(20); return File.Exists(marker); }
                string Engine(string backend) => Path.Combine(root, "runtime", backend, "whisper-cli.exe");
                var settingsFile = Path.Combine(root, "settings.json");
                PrivateFlow.Windows.Settings Saved(bool autoInsert, int days, MeetingNotesStyle notes)
                {
                    var app = PrivateFlow.Windows.Settings.Load();
                    var next = app.Snapshot(); next.AutoInsert = autoInsert; next.MeetingRetentionDays = days; next.MeetingNotesStyle = notes; app.SaveAs(next); return app;
                }
                bool Holds(PrivateFlow.Windows.Settings settings, bool autoInsert, int days, MeetingNotesStyle notes, string backend) =>
                    settings.AutoInsert == autoInsert && settings.MeetingRetentionDays == days && settings.MeetingNotesStyle == notes && settings.EnginePath == Engine(backend);
                // No Private Flow on this folder: the step writes its paths into settings.json inside the lock and keeps the saved preferences.
                Saved(false, 71, MeetingNotesStyle.LocalModel);
                var alone = Finish(Run("save", "cpu"));
                Check(alone.Exit == 0 && Holds(PrivateFlow.Windows.Settings.Load(), false, 71, MeetingNotesStyle.LocalModel, "cpu"),
                    $"[{shellName}] without a running Private Flow the settings step writes its paths into settings.json and keeps the saved preferences ({alone.Errors.Trim()})");
                // Private Flow running on the folder: the step and the hand-run setup scripts change nothing; the scripts stop before any download.
                using (var running = PrivateFlow.Windows.Settings.ClaimRunningInstance())
                {
                    var app = Saved(true, 72, MeetingNotesStyle.Api);
                    var before = File.ReadAllText(settingsFile);
                    var refusedStep = Finish(Run("save", "cuda12-a"));
                    var refusedEngine = Finish(Run("engine")); var refusedRewrite = Finish(Run("rewrite"));
                    var unchanged = File.ReadAllText(settingsFile) == before && !Directory.Exists(Path.Combine(root, "downloads")) && !Directory.Exists(Path.Combine(root, "models"));
                    var later = app.Snapshot(); later.MeetingRetentionDays = 73; app.SaveAs(later);
                    Check(new[] { refusedStep, refusedEngine, refusedRewrite }.All(run => run.Exit != 0 && run.Errors.Contains("Private Flow is running on this data folder") && !run.Errors.Contains("download was attempted"))
                        && refusedEngine.Errors.Contains("Nothing was downloaded or changed") && unchanged && Holds(PrivateFlow.Windows.Settings.Load(), true, 73, MeetingNotesStyle.Api, "cpu"),
                        $"[{shellName}] while Private Flow runs on the folder, the settings step and the hand-run setup-engine.ps1 and setup-rewrite.ps1 are refused before any download and change nothing; the app's own saves go on ({refusedEngine.Errors.Trim()})");
                }
                var notRunning = Finish(Run("engine"));
                Check(notRunning.Errors.Contains("download was attempted"), $"[{shellName}] with no Private Flow running, the hand-run setup-engine.ps1 is not refused and goes on to its download (stopped here by the test)");
                // The step waits while the app holds the settings lock, and finishes once it is released.
                System.Diagnostics.Process waiting; bool waited;
                using (PrivateFlow.Windows.Settings.Lock()) { waiting = Run("save", "cuda12-b"); waited = !waiting.WaitForExit(1500); }
                var afterWait = Finish(waiting);
                Check(waited && afterWait.Exit == 0 && PrivateFlow.Windows.Settings.Load().EnginePath == Engine("cuda12-b"), $"[{shellName}] the settings step waits while the app holds the settings lock, and finishes once it is released");
                // An instance started while the step holds the lock, after it found none: the instance claims instance.lock, its first load waits
                // for the lock, and it then loads what the step wrote together with the saved preferences.
                var pause = Path.Combine(root, "pause");
                var paused = Run("save", "cuda12-c", pause);
                var heldAtRead = WaitFor(pause + ".paused");
                PrivateFlow.Windows.Settings? loaded = null;
                using (var started = PrivateFlow.Windows.Settings.ClaimRunningInstance())
                {
                    var loading = Task.Run(() => loaded = PrivateFlow.Windows.Settings.Load());
                    var loadWaited = !loading.Wait(700);
                    File.WriteAllText(pause + ".resume", "x");
                    var stepDone = Finish(paused).Exit == 0;
                    loading.Wait(30000);
                    Check(heldAtRead && loadWaited && stepDone && loaded is not null && Holds(loaded, true, 73, MeetingNotesStyle.Api, "cuda12-c"),
                        $"[{shellName}] a Private Flow started while the step holds the lock waits for it and then loads the step's paths with the saved preferences");
                }
                File.Delete(pause + ".paused"); File.Delete(pause + ".resume");
                // A killed script or app releases its lock: a stand-in holding settings.lock, then one holding instance.lock, each killed.
                var lockHolder = Run("hold", pause: pause, lockName: "settings");
                var lockHeld = WaitFor(pause + ".paused");
                var attemptsBefore = PrivateFlow.Windows.Settings.LockAttempts; PrivateFlow.Windows.Settings.LockAttempts = 3;
                bool lockedWhileHeld;
                try { try { PrivateFlow.Windows.Settings.Load(); lockedWhileHeld = false; } catch (PrivateFlow.Windows.SettingsUnavailableException) { lockedWhileHeld = true; } }
                finally { PrivateFlow.Windows.Settings.LockAttempts = attemptsBefore; }
                lockHolder.Kill(); lockHolder.WaitForExit();
                var loadsAfterKill = PrivateFlow.Windows.Settings.Load().EnginePath == Engine("cuda12-c");
                File.Delete(pause + ".paused");
                var instanceHolder = Run("hold", pause: pause, lockName: "instance");
                var instanceHeld = WaitFor(pause + ".paused");
                var refusedWhileHeld = Finish(Run("save", "cuda12-d")).Exit != 0;
                instanceHolder.Kill(); instanceHolder.WaitForExit();
                var storedAfterKill = Finish(Run("save", "cuda12-d")).Exit == 0 && PrivateFlow.Windows.Settings.Load().EnginePath == Engine("cuda12-d");
                File.Delete(pause + ".paused");
                Check(lockHeld && lockedWhileHeld && loadsAfterKill && instanceHeld && refusedWhileHeld && storedAfterKill,
                    $"[{shellName}] a killed holder of settings.lock or instance.lock releases it: the app loads again, the step stores again");
                // Another spelling of the folder and a junction to it meet the same lock files.
                using var link = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", alias, root }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                link.StandardOutput.ReadToEnd(); link.WaitForExit();
                using (PrivateFlow.Windows.Settings.ClaimRunningInstance())
                {
                    var throughJunction = Finish(Run("save", "cuda12-e", dataRoot: alias));
                    var otherSpelling = Finish(Run("save", "cuda12-e", dataRoot: root.ToUpperInvariant() + "\\.\\"));
                    Check(Directory.Exists(alias) && throughJunction.Exit != 0 && otherSpelling.Exit != 0 && throughJunction.Errors.Contains("Private Flow is running on this data folder")
                        && PrivateFlow.Windows.Settings.Load().EnginePath == Engine("cuda12-d"),
                        $"[{shellName}] through a junction to the data folder and through another spelling of its path, the step meets the running Private Flow's lock and changes nothing");
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR", oldRoot);
                if (Directory.Exists(alias)) Directory.Delete(alias);
                for (var attempt = 0; attempt < 10 && Directory.Exists(root); attempt++) { try { Directory.Delete(root, true); } catch (IOException) { Thread.Sleep(200); } }
            }
        }
        Check(shells.Count >= 1, $"the settings step ran in {string.Join(" and ", shells.Select(Path.GetFileNameWithoutExtension))}");
    }
}
