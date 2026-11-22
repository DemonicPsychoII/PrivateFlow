# Checks scripts\uninstall.ps1 against fake default installations in a throwaway folder under %TEMP%. Windows only, not in
# CI; run it with Windows PowerShell and with PowerShell 7 (the uninstaller runs in the same shell). It never touches this
# PC's real Private Flow: Invoke-Uninstaller always points LOCALAPPDATA, USERPROFILE, TEMP and TMP into the throwaway folder (another
# LOCALAPPDATA only with -WhatIf), passes a throwaway Start menu shortcut and registry key, and running programs are
# stand-ins built here. The folder and key are removed after every run, also after a failure.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$uninstaller = Join-Path $PSScriptRoot 'uninstall.ps1'
$shell = (Get-Process -Id $PID).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlowUninstallTest-' + [Guid]::NewGuid().ToString('N'))
$testKey = 'HKCU:\Software\PrivateFlowUninstallTest-' + [Guid]::NewGuid().ToString('N')
$failures = New-Object System.Collections.Generic.List[string]
$passed = 0
$started = New-Object System.Collections.Generic.List[int]
function Check([bool]$Condition, [string]$Name, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "PASS $Name" } else { $failures.Add($Name); Write-Host "FAIL $Name`n$Detail" -ForegroundColor Red }
}
function Test-InTestRoot([string]$Path) { $Path -match '^[A-Za-z]:\\' -and ([IO.Path]::GetFullPath($Path) + '\').StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase) }

# A console program that sleeps. --hold <file> keeps the file open without sharing; --spawn <program> also starts a
# PowerShell child and the program, as Private Flow starts its helpers; --launch <file> runs the program and arguments in
# the file's first two lines on its own console, as an in-app button would, and writes exit code and output to the third.
$standInSource = @'
using System.Diagnostics; using System.IO; using System.Threading;
public static class StandIn {
    public static void Main(string[] arguments) {
        if (arguments.Length == 2 && arguments[0] == "--hold") { using (File.Open(arguments[1], FileMode.Open, FileAccess.Read, FileShare.None)) Thread.Sleep(180000); return; }
        if (arguments.Length == 2 && arguments[0] == "--launch") {
            var lines = File.ReadAllLines(arguments[1]);
            var child = Process.Start(new ProcessStartInfo(lines[0], lines[1]) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true });
            child.StandardInput.Close(); var errors = child.StandardError.ReadToEndAsync(); var output = child.StandardOutput.ReadToEnd(); child.WaitForExit();
            File.WriteAllText(lines[2], "EXIT " + child.ExitCode + "\n" + output + errors.Result); return;
        }
        if (arguments.Length == 2 && arguments[0] == "--spawn") {
            Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -Command Start-Sleep 180") { UseShellExecute = false, CreateNoWindow = true });
            Process.Start(new ProcessStartInfo(arguments[1]) { UseShellExecute = false, CreateNoWindow = true });
        }
        Thread.Sleep(180000);
    }
}
'@
function New-StandIn([string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    Set-Content -LiteralPath "$testRoot\StandIn.cs" -Value $standInSource
    $output = & "$env:windir\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:exe "/out:$Path" "$testRoot\StandIn.cs" 2>&1
    if ($LASTEXITCODE -ne 0) { throw "The stand-in did not compile: $output" }
}
function Start-StandIn([string]$Path, [string[]]$Arguments = @('--wait')) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -WindowStyle Hidden -PassThru; $null = $process.Handle; $started.Add($process.Id); $process
}
function Test-Running([int]$ProcessId) { [bool](Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) }

# A default installation as install.ps1 and the app leave it, with things around it that must stay.
function New-Fixture([string]$Name) {
    $base = Join-Path $testRoot $Name
    $fake = [pscustomobject]@{ Base = $base; Local = "$base\Local"; Install = "$base\Local\Programs\PrivateFlow"; Data = "$base\Local\PrivateFlow"
        Temp = "$base\Temp"; Shortcut = "$base\StartMenu\Private Flow.lnk"; Key = "$testKey\$Name"; Outside = "$base\Outside"
        Evaluation = "$base\PrivateFlowEvaluation"; Clips = "$base\PrivateFlowEvaluation\dictation-clips" }
    foreach ($folder in "$($fake.Install)\scripts", "$($fake.Install).old-0123abcd", "$($fake.Local)\Programs\SomeOtherApp", "$($fake.Data)\models", "$($fake.Data)\runtime\rewrite",
        "$($fake.Data)\meetings\$('a' * 32)", "$($fake.Data)\meetings\$('b' * 32)", $fake.Temp, "$($fake.Temp)\PrivateFlow-install-$('c' * 32)", "$($fake.Outside)\target", (Split-Path $fake.Shortcut),
        "$($fake.Clips)\own-20261119-100000-000", "$($fake.Clips)\own-20261119-100500-000", "$($fake.Evaluation)\recordings") {
        New-Item -ItemType Directory -Force -Path $folder | Out-Null
    }
    foreach ($file in "$($fake.Install)\PrivateFlow.exe", "$($fake.Install)\PrivateFlow.dll", "$($fake.Install).old-0123abcd\PrivateFlow.exe", "$($fake.Data)\settings.json",
        "$($fake.Data)\models\ggml-base.bin", "$($fake.Data)\meetings\$('a' * 32)\mic-000000.wav", "$($fake.Data)\put-there-by-hand.txt", "$($fake.Temp)\PrivateFlow-update-$('d' * 32).ps1",
        "$($fake.Temp)\unrelated.txt", "$($fake.Local)\Programs\SomeOtherApp\keep.txt", "$($fake.Outside)\target\keep.txt",
        "$($fake.Clips)\own-20261119-100000-000\manifest.json", "$($fake.Clips)\own-20261119-100000-000\audio.wav", "$($fake.Clips)\own-20261119-100500-000\manifest.json",
        "$($fake.Clips)\own-20261119-100500-000\audio.wav", "$($fake.Evaluation)\recordings\keep.txt") { Set-Content -LiteralPath $file -Value 'x' }
    Copy-Item -LiteralPath $uninstaller -Destination "$($fake.Install)\scripts\uninstall.ps1"
    New-Item -ItemType Junction -Path "$($fake.Data)\models\linked" -Target "$($fake.Outside)\target" | Out-Null
    Set-Shortcut $fake.Shortcut "$($fake.Install)\PrivateFlow.exe"
    New-Item -Path $fake.Key -Force | Out-Null
    Set-ItemProperty -LiteralPath $fake.Key -Name PrivateFlow -Value "`"$($fake.Install)\PrivateFlow.exe`""
    Set-ItemProperty -LiteralPath $fake.Key -Name SomeOtherApp -Value 'C:\Other\Other.exe'
    $fake
}
function Set-Shortcut([string]$Path, [string]$Target) { $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Path); $link.TargetPath = $Target; $link.Save() }
# Every entry below a folder and every value of the key; links are listed, not entered.
function Get-Snapshot($Fake) {
    $entries = New-Object System.Collections.Generic.List[string]; $pending = New-Object System.Collections.Generic.Stack[string]; $pending.Push($Fake.Base)
    while ($pending.Count) { foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force) { $entries.Add($child.FullName); if ($child.PSIsContainer -and -not ($child.Attributes -band 'ReparsePoint')) { $pending.Push($child.FullName) } } }
    @($entries) + @((Get-Item -LiteralPath $Fake.Key).Property | ForEach-Object { "registry:$_" })
}
function Test-NothingRemoved($Before, $Fake) { $after = Get-Snapshot $Fake; @($Before | Where-Object { $after -notcontains $_ }).Count -eq 0 }

# Runs the uninstaller in a child of this shell with the fixture's shortcut and key, LOCALAPPDATA, TEMP and TMP inside
# the fixture and the answer on standard input; -AtPrompt runs once the confirmation is asked for. A LOCALAPPDATA outside
# the test folder, or none, is refused unless the run is -WhatIf.
function Invoke-Uninstaller($Fake, [string[]]$Flags = @(), [string]$Answer = '', [string]$Script = $uninstaller, [hashtable]$Environment = @{}, [scriptblock]$AtPrompt) {
    # USERPROFILE too: the kept test clips are under it, and no run may reach the real ones.
    $variables = @{ LOCALAPPDATA = $Fake.Local; USERPROFILE = $Fake.Base; TEMP = $Fake.Temp; TMP = $Fake.Temp }
    foreach ($name in $Environment.Keys) { $variables[$name] = $Environment[$name] }
    if (-not ((Test-InTestRoot $variables.LOCALAPPDATA) -and (Test-InTestRoot $variables.USERPROFILE) -and (Test-InTestRoot $Fake.Temp) -and (Test-InTestRoot $Fake.Shortcut) -and $Fake.Key.StartsWith("$testKey\")) -and $Flags -notcontains '-WhatIf') { throw 'Test bug: a run that may delete must stay inside the test folder.' }
    $start = New-Object Diagnostics.ProcessStartInfo $shell
    $start.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$Script`" -StartMenuShortcut `"$($Fake.Shortcut)`" -AutostartRegistryKey `"$($Fake.Key)`" $($Flags -join ' ')"
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($name in $variables.Keys) { if ($null -eq $variables[$name]) { $start.EnvironmentVariables.Remove($name) } else { $start.EnvironmentVariables[$name] = $variables[$name] } }
    foreach ($name in 'PSModulePath', 'PRIVATE_FLOW_DATA_DIR', 'PRIVATE_FLOW_DICTATION_CLIPS') { $start.EnvironmentVariables.Remove($name) }
    # On a UTF-8 console Windows PowerShell's input writer would start with a byte order mark, read as part of the answer.
    $inputEncoding = [Console]::InputEncoding
    if ($inputEncoding.CodePage -eq 65001) { [Console]::InputEncoding = New-Object Text.UTF8Encoding $false }
    try { $process = [Diagnostics.Process]::Start($start) } finally { [Console]::InputEncoding = $inputEncoding }
    $errors = $process.StandardError.ReadToEndAsync()
    $before = ''
    if ($AtPrompt) { while ($null -ne ($line = $process.StandardOutput.ReadLine())) { $before += "$line`n"; if ($line -match '^Type UNINSTALL') { & $AtPrompt; break } } }
    $output = $process.StandardOutput.ReadToEndAsync()
    $process.StandardInput.WriteLine($Answer)
    $process.StandardInput.Close()
    if (-not $process.WaitForExit(180000)) { $process.Kill(); throw 'The uninstaller did not finish within three minutes.' }
    # PowerShell 7 colours its error view and wraps it behind "     | ".
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $before + $output.Result + (($errors.Result -replace '\x1b\[[0-9;]*m', '') -replace '\s*\r?\n\s*\|\s*', ' ') }
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Write-Host "Shell: $shell ($($PSVersionTable.PSVersion))"

    # Refused before anything is listed: LOCALAPPDATA that is not a plain local path (only with -WhatIf), and default
    # folders that are links, have a link on their path, or a program folder without PrivateFlow.exe.
    $fake = New-Fixture 'refusals'
    foreach ($case in @(@('LOCALAPPDATA unset', $null, 'LOCALAPPDATA is not set'), @('LOCALAPPDATA blank', ' ', 'LOCALAPPDATA is not set'), @('LOCALAPPDATA relative', 'Local', 'not a full path on a local drive'),
            @('LOCALAPPDATA as a UNC path', "\\localhost\C$\$($fake.Local.Substring(3))", 'not a full path on a local drive'), @('LOCALAPPDATA as a device path', "\\?\$($fake.Local)", 'not a full path on a local drive'))) {
        $run = Invoke-Uninstaller $fake @('-WhatIf') -Environment @{ LOCALAPPDATA = $case[1] }
        Check ($run.ExitCode -eq 1 -and $run.Output -match $case[2] -and $run.Output -match 'Nothing was removed' -and $run.Output -notmatch 'Removes:') "refuses $($case[0]) ($($case[2]))" $run.Output
    }
    $fake = New-Fixture 'program-link'; Rename-Item -LiteralPath $fake.Install -NewName 'Real'; New-Item -ItemType Junction -Path $fake.Install -Target "$($fake.Local)\Programs\Real" | Out-Null
    $fake2 = New-Fixture 'data-link'; Rename-Item -LiteralPath $fake2.Data -NewName 'Real'; New-Item -ItemType Junction -Path $fake2.Data -Target "$($fake2.Local)\Real" | Out-Null
    $fake3 = New-Fixture 'link-in-path'; New-Item -ItemType Junction -Path "$testRoot\Alias" -Target $fake3.Base | Out-Null
    $fake4 = New-Fixture 'no-program'; Remove-Item -LiteralPath "$($fake4.Install)\PrivateFlow.exe"
    $fake5 = New-Fixture 'clips-link'; Rename-Item -LiteralPath $fake5.Clips -NewName 'Real'; New-Item -ItemType Junction -Path $fake5.Clips -Target "$($fake5.Evaluation)\Real" | Out-Null
    $fake6 = New-Fixture 'evaluation-link'; Rename-Item -LiteralPath $fake6.Evaluation -NewName 'RealEvaluation'; New-Item -ItemType Junction -Path $fake6.Evaluation -Target "$($fake6.Base)\RealEvaluation" | Out-Null
    foreach ($case in @(@($fake5, @{}, 'the test clips folder is a junction', 'dictation-clips is a junction or symbolic link'), @($fake6, @{}, 'a link above the test clips below the profile', 'PrivateFlowEvaluation is a junction or symbolic link'),
            @($fake, @{}, 'the program folder is a junction', 'is a junction or symbolic link'), @($fake2, @{}, 'the data folder is a junction', 'is a junction or symbolic link'),
            @($fake3, @{ LOCALAPPDATA = "$testRoot\Alias\Local" }, 'a link on the path below the profile', 'Alias is a junction or symbolic link'), @($fake4, @{}, 'a program folder without PrivateFlow.exe', 'holds no PrivateFlow\.exe'))) {
        $before = Get-Snapshot $case[0]
        $run = Invoke-Uninstaller $case[0] @('-Force') -Environment $case[1]
        Check ($run.ExitCode -eq 1 -and $run.Output -match $case[3] -and $run.Output -match 'relocated or custom installation' -and $run.Output -notmatch 'Removes:' -and (Test-NothingRemoved $before $case[0])) "refuses $($case[2]) and removes nothing" $run.Output
    }

    # -WhatIf lists everything and deletes nothing; a wrong answer cancels.
    $fake = New-Fixture 'listing'
    $before = Get-Snapshot $fake
    $run = Invoke-Uninstaller $fake @('-WhatIf')
    $listed = $fake.Install, "$($fake.Install).old-0123abcd", $fake.Data, "PrivateFlow-install-$('c' * 32)", "PrivateFlow-update-$('d' * 32)", 'Saved meetings: 2', 'Model: ggml-base.bin', 'also files you put there yourself', 'Start menu shortcut', 'Autostart value PrivateFlow', $fake.Clips, 'Kept test clips: 2'
    Check ($run.ExitCode -eq 0 -and @($listed | Where-Object { $run.Output -notmatch [regex]::Escape($_) }).Count -eq 0 -and $run.Output -match 'What if: Performing the operation "Remove"' -and $run.Output -notmatch 'Set Alias') '-WhatIf lists every location, the meetings and models' $run.Output
    Check (Test-NothingRemoved $before $fake) '-WhatIf deletes nothing'
    foreach ($answer in '', 'y', 'yes', 'UNINSTAL') {
        $run = Invoke-Uninstaller $fake -Answer $answer
        Check ($run.ExitCode -eq 1 -and $run.Output -match 'Cancelled\. Nothing was removed\.' -and (Test-NothingRemoved $before $fake)) "answer '$answer' cancels and deletes nothing" $run.Output
    }

    # Confirmed from the installed copy's own scripts folder: it continues from a copy in TEMP and removes everything, a
    # link inside the data folder as a link only; what lies around the installation stays.
    $fake = New-Fixture 'confirmed'
    $run = Invoke-Uninstaller $fake -Answer 'UNINSTALL' -Script "$($fake.Install)\scripts\uninstall.ps1"
    $left = @($fake.Install, "$($fake.Install).old-0123abcd", $fake.Data, $fake.Clips, $fake.Shortcut, "$($fake.Temp)\PrivateFlow-install-$('c' * 32)", "$($fake.Temp)\PrivateFlow-update-$('d' * 32).ps1") | Where-Object { Test-Path -LiteralPath $_ }
    Check ($run.ExitCode -eq 0 -and $run.Output -match 'Private Flow is uninstalled' -and @($left).Count -eq 0 -and $null -eq (Get-ItemProperty -LiteralPath $fake.Key -Name PrivateFlow -ErrorAction SilentlyContinue)) 'a confirmed run removes everything listed' "$($run.Output)`nleft: $left"
    $kept = @("$($fake.Outside)\target\keep.txt", "$($fake.Local)\Programs\SomeOtherApp\keep.txt", "$($fake.Temp)\unrelated.txt", "$($fake.Evaluation)\recordings\keep.txt") | Where-Object { Test-Path -LiteralPath $_ }
    Check (@($kept).Count -eq 4 -and $null -ne (Get-ItemProperty -LiteralPath $fake.Key -Name SomeOtherApp -ErrorAction SilentlyContinue) -and @(Get-ChildItem -LiteralPath $fake.Temp -Filter 'PrivateFlow-*').Count -eq 0) 'the junction target, another app, other TEMP files, other evaluation files and another Run value stay; the TEMP copy and the kept test clips are gone'

    # Changed while the confirmation waits: the data folder replaced by a junction, the shortcut or the Run value pointed elsewhere.
    foreach ($case in @(@('the data folder replaced by a junction', { Rename-Item -LiteralPath $fake.Data -NewName 'Aside'; New-Item -ItemType Junction -Path $fake.Data -Target $fake.Outside | Out-Null }, 'Stopped: .*junction or symbolic link'),
            @('the shortcut pointed elsewhere', { Set-Shortcut $fake.Shortcut "$($fake.Outside)\Other.exe" }, 'Stopped: the Start menu shortcut changed'),
            @('the Run value pointed elsewhere', { Set-ItemProperty -LiteralPath $fake.Key -Name PrivateFlow -Value "`"$($fake.Outside)\Other.exe`"" }, 'Stopped: the autostart value changed'))) {
        $fake = New-Fixture ('changed-' + [Guid]::NewGuid().ToString('N').Substring(0, 6))
        $run = Invoke-Uninstaller $fake -Answer 'UNINSTALL' -AtPrompt $case[1]
        Check ($run.ExitCode -eq 1 -and $run.Output -match $case[2] -and (Test-Path -LiteralPath "$($fake.Outside)\target\keep.txt") -and (Test-Path -LiteralPath "$($fake.Local)\Programs\PrivateFlow\PrivateFlow.exe")) "$($case[0]) while the confirmation waits stops the run" $run.Output
    }

    # A locked file is reported with exit code 1 and a rerun finishes; an editor outside holding settings.json keeps running.
    $fake = New-Fixture 'locked'
    $lock = [IO.File]::Open("$($fake.Data)\models\ggml-base.bin", 'Open', 'Read', 'None')
    try { $run = Invoke-Uninstaller $fake @('-Force') } finally { $lock.Dispose() }
    Check ($run.ExitCode -eq 1 -and $run.Output -match 'ggml-base\.bin: .*used by another process' -and -not (Test-Path -LiteralPath $fake.Install)) 'a locked file is reported, exit code 1, the rest goes' $run.Output
    $run = Invoke-Uninstaller $fake @('-Force')
    Check ($run.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $fake.Data)) 'a rerun finishes the job' $run.Output
    # Another console whose current folder is the program folder: PrivateFlow.exe goes, the emptied folder stays, the data
    # folder still goes, and a rerun once that console has ended removes the empty folder.
    $fake = New-Fixture 'program-folder-in-use'
    New-StandIn "$($fake.Outside)\Console.exe"
    $console = Start-Process -FilePath "$($fake.Outside)\Console.exe" -WorkingDirectory $fake.Install -WindowStyle Hidden -PassThru; $null = $console.Handle; $started.Add($console.Id)
    Start-Sleep -Milliseconds 500
    $run = Invoke-Uninstaller $fake @('-Force')
    $emptied = (Test-Path -LiteralPath $fake.Install) -and -not (Get-ChildItem -LiteralPath $fake.Install -Force)
    Check ($run.ExitCode -eq 1 -and $emptied -and -not (Test-Path -LiteralPath $fake.Data) -and $run.Output -match 'PrivateFlow: ') 'a program folder another console is in is emptied and reported, the data folder still goes' $run.Output
    Stop-Process -Id $console.Id -Force; $console.WaitForExit(5000) | Out-Null
    $run = Invoke-Uninstaller $fake @('-Force')
    Check ($run.ExitCode -eq 0 -and -not (Test-Path -LiteralPath $fake.Install) -and $run.Output -match 'Private Flow is uninstalled') 'a rerun removes the emptied program folder' $run.Output
    $fake = New-Fixture 'editor'
    New-StandIn "$($fake.Outside)\Editor.exe"
    $editor = Start-StandIn "$($fake.Outside)\Editor.exe" @('--hold', "`"$($fake.Data)\settings.json`"")
    Start-Sleep -Milliseconds 500
    $run = Invoke-Uninstaller $fake @('-Force')
    Check ((Test-Running $editor.Id) -and $run.ExitCode -eq 1 -and $run.Output -match 'settings\.json: ' -and $run.Output -notmatch 'Editor\.exe') 'an editor outside holding settings.json keeps running, its lock is reported' $run.Output

    # Processes: the app in the program folder, llama-server in the data folder and the app's PowerShell child are ended only
    # after confirmation; another program the app started and a PrivateFlow.exe elsewhere keep running.
    $fake = New-Fixture 'running'
    foreach ($path in "$($fake.Install)\PrivateFlow.exe", "$($fake.Data)\runtime\rewrite\llama-server.exe", "$($fake.Outside)\Browser.exe", "$($fake.Outside)\PrivateFlow.exe") { New-StandIn $path }
    $app = Start-StandIn "$($fake.Install)\PrivateFlow.exe" @('--spawn', "`"$($fake.Outside)\Browser.exe`"")
    $helper = Start-StandIn "$($fake.Data)\runtime\rewrite\llama-server.exe"
    $elsewhere = Start-StandIn "$($fake.Outside)\PrivateFlow.exe"
    for ($wait = 0; $wait -lt 50 -and ($children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($app.Id)" | Where-Object { $_.Name -in 'powershell.exe', 'Browser.exe' })).Count -lt 2; $wait++) { Start-Sleep -Milliseconds 200 }
    foreach ($child in $children) { $started.Add([int]$child.ProcessId) }
    $powerShellChild = [int](@($children | Where-Object Name -eq 'powershell.exe')[0]).ProcessId
    $browser = [int](@($children | Where-Object Name -eq 'Browser.exe')[0]).ProcessId
    $run = Invoke-Uninstaller $fake -Answer 'no'
    Check ($run.Output -match "PrivateFlow\.exe \(process $($app.Id)\)" -and $run.Output -match "llama-server\.exe \(process $($helper.Id)\)" -and $run.Output -match "powershell\.exe \(process $powerShellChild\)" -and
        $run.Output -notmatch "\(process ($browser|$($elsewhere.Id))\)" -and (Test-Running $app.Id)) 'the app, its helper and its PowerShell child are listed, nothing else, and keep running without confirmation' $run.Output
    $run = Invoke-Uninstaller $fake @('-Force')
    $states = "app $(Test-Running $app.Id), helper $(Test-Running $helper.Id), PowerShell child $(Test-Running $powerShellChild), browser $(Test-Running $browser), elsewhere $(Test-Running $elsewhere.Id)"
    Check ($run.ExitCode -eq 0 -and $states -eq 'app False, helper False, PowerShell child False, browser True, elsewhere True' -and -not (Test-Path -LiteralPath $fake.Data)) '-Force ends them, the others keep running, everything is removed' "$states`n$($run.Output)"

    # A Run value of another program naming PrivateFlow.exe in an argument and a shortcut to another program stay.
    $fake = New-Fixture 'other-program'
    Set-ItemProperty -LiteralPath $fake.Key -Name PrivateFlow -Value "`"$($fake.Outside)\Other.exe`" --open `"$($fake.Install)\PrivateFlow.exe`""
    Set-Shortcut $fake.Shortcut "$($fake.Outside)\Other.exe"
    $run = Invoke-Uninstaller $fake @('-Force')
    Check ($run.ExitCode -eq 0 -and (Test-Path -LiteralPath $fake.Shortcut) -and $null -ne (Get-ItemProperty -LiteralPath $fake.Key -Name PrivateFlow -ErrorAction SilentlyContinue) -and $run.Output -match 'Left in place: .*not this installation') 'a Run value and shortcut of another program stay' $run.Output

    # Started by the app on its own console: the app and its console host are not ended and the final report is written.
    $fake = New-Fixture 'started-by-app'
    New-StandIn "$($fake.Install)\PrivateFlow.exe"
    Set-Content -LiteralPath "$($fake.Base)\launch.txt" -Value @($shell, "-NoProfile -ExecutionPolicy Bypass -File `"$uninstaller`" -StartMenuShortcut `"$($fake.Shortcut)`" -AutostartRegistryKey `"$($fake.Key)`" -Force", "$($fake.Base)\launch.log")
    $saved = @{}; foreach ($name in 'LOCALAPPDATA', 'USERPROFILE', 'TEMP', 'TMP') { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
    try { $env:LOCALAPPDATA = $fake.Local; $env:USERPROFILE = $fake.Base; $env:TEMP = $env:TMP = $fake.Temp; $launcher = Start-StandIn "$($fake.Install)\PrivateFlow.exe" @('--launch', "`"$($fake.Base)\launch.txt`"") }
    finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    $log = $(if ($launcher.WaitForExit(180000) -and (Test-Path -LiteralPath "$($fake.Base)\launch.log")) { Get-Content -LiteralPath "$($fake.Base)\launch.log" -Raw } else { '' })
    Check ($launcher.ExitCode -eq 0 -and $log -match '^EXIT 1' -and $log -notmatch '(conhost|PrivateFlow)\.exe \(process' -and $log -match 'PrivateFlow\.exe: ' -and -not (Test-Path -LiteralPath $fake.Data)) 'the app and its console host survive, the report names its locked exe, exit code 1' $log
} finally {
    Remove-Item -LiteralPath $testKey -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($processId in $started) { Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Milliseconds 500
    # Junctions go first, found without entering any and removed as links, so the cleanup never reaches through one.
    for ($attempt = 0; $attempt -lt 5 -and (Test-Path -LiteralPath $testRoot); $attempt++) {
        try {
            $pending = New-Object System.Collections.Generic.Stack[string]; $pending.Push($testRoot)
            while ($pending.Count) { foreach ($child in Get-ChildItem -LiteralPath $pending.Pop() -Force -Directory) { if ($child.Attributes -band 'ReparsePoint') { [IO.Directory]::Delete($child.FullName, $false) } else { $pending.Push($child.FullName) } } }
            Remove-Item -LiteralPath $testRoot -Recurse -Force
        } catch { Start-Sleep -Seconds 2 }
    }
    if ((Test-Path -LiteralPath $testRoot) -or (Test-Path -LiteralPath $testKey)) { $failures.Add('cleanup'); Write-Host "FAIL cleanup: $testRoot or $testKey is left" -ForegroundColor Red }
}
if ($failures.Count) { Write-Host "$($failures.Count) uninstall checks failed, $passed passed." -ForegroundColor Red; exit 1 }
Write-Host "$passed uninstall checks passed." -ForegroundColor Green
