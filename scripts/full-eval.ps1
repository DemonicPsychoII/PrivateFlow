# Runs every automated check of Private Flow and prints one pass/fail line with the accuracy metrics the next
# improvement round is judged on. Local only: real engines, synthetic and real speech, isolated data folders.
# Default (installed engine): build with pure checks, smoke test, workspace check, dictation and meeting accuracy.
# -Full adds every speech backend and model setup-engine.ps1 offers, in an isolated data root under artifacts\engine-matrix.
[CmdletBinding()]
param(
    [switch]$Full,
    [switch]$SkipBuild,
    # Checks that take the foreground and the clipboard (overlay, paste fixture, UI automation); run them when the PC is not in use.
    [switch]$DesktopChecks,
    # The backends -Full sets up; cpu runs take several hours on a laptop.
    [string[]]$Backends = @('cuda12', 'cpu'),
    [string]$App = '',
    # Your own recordings (docs\TEST_COVERAGE.md), finished with the recommended setup and scored with counts only.
    [string]$Recordings = $env:PRIVATE_FLOW_EVALUATION_RECORDINGS
)
. (Join-Path $PSScriptRoot 'eval-common.ps1')
$taskRoot = Join-Path $EvalArtifacts 'full-eval'
$taskStarted = Get-Date
if (Test-Path -LiteralPath $taskRoot) { Remove-Item -LiteralPath $taskRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskSteps = New-Object System.Collections.Generic.List[object]
function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "== $name" -ForegroundColor Cyan
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try { $detail = & $action; $taskSteps.Add([pscustomobject]@{ Name = $name; Passed = $true; Detail = "$detail"; Minutes = [math]::Round($watch.Elapsed.TotalMinutes, 1) }) }
    catch { Write-Host "FAIL $name : $_" -ForegroundColor Red; $taskSteps.Add([pscustomobject]@{ Name = $name; Passed = $false; Detail = "$_"; Minutes = [math]::Round($watch.Elapsed.TotalMinutes, 1) }) }
}
function Invoke-Script([string]$script, [string[]]$arguments) {
    $log = Join-Path $taskRoot ([IO.Path]::GetFileNameWithoutExtension($script) + "-$([guid]::NewGuid().ToString('N').Substring(0, 6)).log")
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments *>&1 | Tee-Object -FilePath $log | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$script failed; see $log" }
    Get-Content -LiteralPath $log -Raw
}

if (-not $SkipBuild) {
    Invoke-Step 'Build, pure checks and portable package' {
        $output = Invoke-Script 'build.ps1' @('-Portable')
        if ($output -notmatch '(\d+) safety and preference checks passed') { throw 'The pure checks did not report.' }
        "$($Matches[1]) pure checks passed"
    }
}
Invoke-Step 'Uninstall script (fake installations in TEMP)' {
    $output = Invoke-Script 'uninstall-test.ps1' @()
    if ($output -notmatch '(\d+) uninstall checks passed') { throw 'The uninstall checks did not report.' }
    "$($Matches[1]) checks passed"
}
$App = Get-EvalApp $App
$taskInstalled = Get-InstalledEngine
Invoke-Step 'Smoke test (installed engine, synthetic speech)' { $null = Invoke-Script 'smoke-test.ps1' @('-App', $App); 'negation and number kept' }
Invoke-Step 'Workspace verification (--verify-workspace)' {
    $data = Join-Path $taskRoot 'workspace-data'; $output = Join-Path $taskRoot 'workspace'
    Write-EvalSettings $data $taskInstalled 'auto' $true
    $previous = $env:PRIVATE_FLOW_DATA_DIR
    try { $env:PRIVATE_FLOW_DATA_DIR = $data; $process = Start-Process -FilePath $App -ArgumentList @('--verify-workspace', "`"$output`"") -WindowStyle Hidden -PassThru } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
    if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'Workspace verification timed out.' }
    $result = Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $result.Passed) { throw "Workspace verification failed after $(@($result.Checks).Count) checks: $($result.Error)" }
    "$(@($result.Checks).Count) checks passed"
}
if ($DesktopChecks) {
    foreach ($taskMode in '--verify-overlay', '--verify-insert') {
        Invoke-Step "Desktop check $taskMode" {
            $output = Join-Path $taskRoot $taskMode.TrimStart('-')
            # The app runs these modes only with an isolated data folder; the insertion fixture it starts inherits it.
            $previous = $env:PRIVATE_FLOW_DATA_DIR
            try { $env:PRIVATE_FLOW_DATA_DIR = Join-Path $taskRoot 'desktop-data'; $process = Start-Process -FilePath $App -ArgumentList @($taskMode, "`"$output`"") -PassThru } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
            if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$taskMode timed out." }
            $file = Get-ChildItem -LiteralPath $output -Filter '*result.txt' | Select-Object -First 1
            $text = Get-Content -LiteralPath $file.FullName -Raw
            if ($process.ExitCode -ne 0 -or $text -match 'FAIL') { throw $text.Trim() }
            ($text -split "`n" | Where-Object { $_ -match 'PASS' }).Count.ToString() + ' passed'
        }
    }
    Invoke-Step 'UI smoke test (UI Automation, isolated data)' { $null = Invoke-Script 'ui-smoke-test.ps1' @('-App', $App); 'passed' }
}

# The engines to measure: the installed one (read only) and, with -Full, every pinned backend and model in an isolated root.
$taskEngines = @($taskInstalled)
if ($Full) {
    $taskMatrix = Join-Path $EvalArtifacts 'engine-matrix'
    $taskOwner = Join-Path $env:LOCALAPPDATA 'PrivateFlow'
    foreach ($taskBackend in @($Backends | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
        if ($taskBackend -eq 'cuda12' -and -not (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) { continue }
        # Automatic on the GPU is large-v3-turbo with large-v3 for the final pass.
        foreach ($taskModel in @($(if ($taskBackend -eq 'cuda12') { 'auto' }), 'large-v3-turbo-q8_0', 'medium', 'base') | Where-Object { $_ }) {
            if ("$taskBackend-$taskModel" -eq $taskInstalled.Label) { continue }
            Invoke-Step "Engine setup $taskBackend $taskModel (isolated)" {
                # Files the installed copy already verified are copied, not downloaded again; setup-engine.ps1 checks their hashes.
                foreach ($taskSeed in @(@('downloads', $(if ($taskBackend -eq 'cuda12') { 'whisper-cublas-12.4.0-bin-x64.zip' } else { 'whisper-bin-x64.zip' })), @('models', "ggml-$taskModel.bin"), @('models', 'ggml-large-v3.bin'), @('models', 'ggml-silero-v6.2.0.bin'))) {
                    $from = Join-Path $taskOwner "$($taskSeed[0])\$($taskSeed[1])"; $to = Join-Path $taskMatrix "$($taskSeed[0])\$($taskSeed[1])"
                    if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) { New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null; Copy-Item -LiteralPath $from -Destination $to }
                }
                $null = Invoke-Script 'setup-engine.ps1' @('-Backend', $taskBackend, '-Model', $taskModel, '-DataRoot', $taskMatrix)
                $written = Get-Content -LiteralPath (Join-Path $taskMatrix 'settings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
                $meeting = "$($written.MeetingModelPath)"
                $script:taskEngines += [pscustomobject]@{ Label = "$taskBackend-$([IO.Path]::GetFileNameWithoutExtension($written.ModelPath).Replace('ggml-', ''))" + $(if ($meeting) { '+' + [IO.Path]::GetFileNameWithoutExtension($meeting).Replace('ggml-', '') }); EnginePath = $written.EnginePath; ModelPath = $written.ModelPath
                    MeetingModelPath = $meeting; VadModelPath = $written.VadModelPath }
                "$taskBackend $taskModel ready"
            }
        }
    }
}

$taskDictation = $(if ($Full) { @('auto', 'auto-no-detection', 'fixed', 'fixed-no-detection') } else { @('auto', 'auto-no-detection', 'fixed') })
$taskRealSpeech = $(if ($Full) { 40 } else { 25 })
foreach ($taskEngine in $taskEngines) {
    $taskEngineArguments = @('-App', $App, '-EnginePath', $taskEngine.EnginePath, '-ModelPath', $taskEngine.ModelPath, '-VadModelPath', $taskEngine.VadModelPath, '-EngineLabel', $taskEngine.Label) +
        $(if ($taskEngine.MeetingModelPath) { @('-MeetingModelPath', $taskEngine.MeetingModelPath) } else { @() })
    # On the CPU, medium and large-v3-turbo take about 1.8 s per second of audio on this laptop; they get the shipped
    # configuration with fewer real utterances and the meeting scenarios closest to the mixed-channel scenarios.
    $taskSlow = $taskEngine.Label -match '^cpu-(medium|large)'
    Invoke-Step "Dictation accuracy $($taskEngine.Label)" {
        $configurations = $(if ($taskSlow) { @('auto') } else { $taskDictation })
        $null = Invoke-Script 'dictation-eval.ps1' ($taskEngineArguments + @('-Configuration', ($configurations -join ','), '-RealSpeech', $(if ($taskSlow) { 10 } else { $taskRealSpeech }))); "$($configurations.Count) configurations"
    }
    $taskMeeting = $(if ($taskSlow) { @('auto') } elseif ($taskEngine -eq $taskInstalled -and $Full) { @('auto', 'auto-no-detection', 'en', 'es') } else { @('auto', 'auto-no-detection') })
    $taskScenarios = $(if ($taskSlow) { 'three-speakers,mixed-channel-en-es,code-switching-en-es,real-speech-en-es' } else { '*' })
    Invoke-Step "Meeting accuracy $($taskEngine.Label)" {
        $null = Invoke-Script 'meeting-eval.ps1' ($taskEngineArguments + @('-Configuration', ($taskMeeting -join ','), '-Scenario', $taskScenarios, '-RealSpeech', $(if ($taskSlow) { 6 } else { 12 }))); "$($taskMeeting.Count) configurations"
    }
    # A user who never types a meeting vocabulary: some colleagues' names in the dictation vocabulary next to unrelated terms,
    # two names taught as corrections, a meeting title; Keiko and Joachim are missing, as they would be.
    if ($Full -and $taskEngine.MeetingModelPath) {
        # Goal 1 compares the final pass with this oracle: every utterance alone, cut at its known times, in its known language, same model.
        Invoke-Step "Meeting oracle $($taskEngine.Label)" {
            $null = Invoke-Script 'meeting-eval.ps1' ($taskEngineArguments + @('-Configuration', 'auto', '-Mode', 'oracle')); 'auto'
        }
        if ($Recordings -and (Test-Path -LiteralPath $Recordings -PathType Container)) {
            Invoke-Step "Own recordings $($taskEngine.Label)" { $null = Invoke-Script 'meeting-eval.ps1' ($taskEngineArguments + @('-Scenario', 'none', '-Recordings', $Recordings)); 'counts only' }
        }
        Invoke-Step "Meeting accuracy $($taskEngine.Label), vocabulary the user already gave the app" {
            $null = Invoke-Script 'meeting-eval.ps1' ($taskEngineArguments + @('-Configuration', 'auto', '-Tag', 'vocabulary', '-Title', 'Design review', '-DictationVocabulary', 'Claude Code, Codex, GPT, Zephyr, Priya, Okafor, Lindqvist',
                '-Corrections', 'Mariano=Mariana;Joaquin=Joaquín;Tobi=Tobias')); 'auto'
        }
    }
}

# Headline numbers per configuration: word recall and critical-item recall, strict, with formatting-only differences apart.
function New-Tally { [pscustomobject]@{ Words = 0; Matched = 0; Formatting = 0; Insertions = 0; Items = 0; Recalled = 0; ItemFormatting = 0 } }
function Add-Tally($tally, $words, [int]$items, [int]$recalled, [int]$itemFormatting) {
    $tally.Words += $words.ReferenceWords; $tally.Matched += $words.ReferenceWords - $words.Substitutions - $words.Deletions; $tally.Formatting += $words.FormattingOnly; $tally.Insertions += $words.Insertions
    $tally.Items += $items; $tally.Recalled += $recalled; $tally.ItemFormatting += $itemFormatting
}
# Lenient counts words and items that were heard but written differently ("1234" for "12:34") as recalled; Ins counts words the reference does not have.
function Format-Tally($tally) {
    if ($tally.Words -eq 0) { return '-' }
    '{0,6:0.0} % {1,10} {2,6:0.0} % {3,4} {4,5}   lenient {5,6:0.0} % {6,6:0.0} %' -f (100 * $tally.Matched / $tally.Words), "$($tally.Recalled)/$($tally.Items)", $(if ($tally.Items) { 100 * $tally.Recalled / $tally.Items } else { 100 }), $tally.ItemFormatting, $tally.Insertions,
        (100 * ($tally.Matched + $tally.Formatting) / $tally.Words), $(if ($tally.Items) { 100 * ($tally.Recalled + $tally.ItemFormatting) / $tally.Items } else { 100 })
}
$taskRows = New-Object System.Collections.Generic.List[object]
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $EvalArtifacts 'meeting-eval\reports') -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $taskStarted) {
    $report = Get-Content -LiteralPath $taskFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $sets = [ordered]@{ synthetic = (New-Tally); 'mixed-channel' = (New-Tally); real = (New-Tally) }
    $languages = @{}
    foreach ($scenario in $report.Scenarios) {
        $set = Get-MeetingSet $scenario
        # Each real recording counts once; the "-show" variant repeats the same speech.
        if ($scenario.Name -notlike 'real-*-show') { Add-Tally $sets[$set] $scenario.Words $scenario.CriticalItems $scenario.CriticalRecalled $scenario.CriticalFormattingOnly }
        if ($scenario.Name -like 'mixed-channel-*') { Add-Tally $sets['mixed-channel'] $scenario.Words $scenario.CriticalItems $scenario.CriticalRecalled $scenario.CriticalFormattingOnly }
        foreach ($group in @($scenario.Languages | Where-Object { $scenario.Name -notlike 'real-*-show' })) {
            $key = "$set $($group.Name)"; if (-not $languages[$key]) { $languages[$key] = New-Tally }
            Add-Tally $languages[$key] $group.Words $group.CriticalItems $group.CriticalRecalled $group.CriticalFormattingOnly
        }
    }
    $taskRows.Add([pscustomobject]@{ Kind = 'meeting'; Configuration = "$($taskFile.BaseName) ($(@($report.Scenarios).Count))"; Sets = $sets; Languages = $languages; Checks = $null })
}
foreach ($taskFile in Get-ChildItem -LiteralPath (Join-Path $EvalArtifacts 'dictation-eval\reports') -Filter '*.json' -ErrorAction SilentlyContinue | Where-Object LastWriteTime -ge $taskStarted) {
    $report = Get-Content -LiteralPath $taskFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
    $sets = [ordered]@{ synthetic = (New-Tally); 'synthetic Clean' = (New-Tally); real = (New-Tally) }
    $languages = @{}
    foreach ($group in $report.Groups | Where-Object { $_.Language }) {
        if ($group.Style -eq 'Exact') {
            Add-Tally $sets[$group.Source] $group.Words $group.CriticalItems $group.CriticalRecalled $group.CriticalFormattingOnly
            $key = "$($group.Source) $($group.Language)"; if (-not $languages[$key]) { $languages[$key] = New-Tally }
            Add-Tally $languages[$key] $group.Words $group.CriticalItems $group.CriticalRecalled $group.CriticalFormattingOnly
        }
        if ($group.Style -eq 'Clean' -and $group.Source -eq 'synthetic') { Add-Tally $sets['synthetic Clean'] $group.Words $group.CriticalItems $group.CriticalRecalled $group.CriticalFormattingOnly }
    }
    $endToEnd = @($report.Cases | ForEach-Object { $_.Checks })
    $taskRows.Add([pscustomobject]@{ Kind = 'dictation'; Configuration = "$($taskFile.BaseName) ($(@($report.Cases).Count))"; Sets = $sets; Languages = $languages
        Checks = "$(@($endToEnd | Where-Object Passed).Count)/$($endToEnd.Count)"; Functional = @($report.Functional) })
}

$taskSummary = New-Object System.Collections.Generic.List[string]
function Out-Summary([string]$text) { Write-Host $text; $taskSummary.Add($text) }
Out-Summary ''
Out-Summary "Private Flow full evaluation, $(Get-Date -Format 'yyyy-MM-dd HH:mm'), $(if ($Full) { 'full' } else { 'default' }) run"
foreach ($taskStep in $taskSteps) { Out-Summary ('{0} {1,-58} {2,5} min  {3}' -f $(if ($taskStep.Passed) { 'PASS' } else { 'FAIL' }), $taskStep.Name, $taskStep.Minutes, $taskStep.Detail.Trim().Split("`n")[-1].Trim()) }
$taskFunctional = @($taskRows | Where-Object Kind -eq 'dictation' | ForEach-Object { $_.Functional } | Where-Object { $_ })
Out-Summary "Functional checks inside the dictation runs: $(@($taskFunctional | Where-Object Passed).Count)/$($taskFunctional.Count) passed"
@($taskFunctional | Where-Object { -not $_.Passed } | Select-Object -ExpandProperty Description -Unique) | ForEach-Object { Out-Summary "  FAIL $_" }
Out-Summary ''
Out-Summary 'Headline: strict word recall and critical-item recall (formatting-only critical misses counted as misses, listed in Fmt); lenient counts them as recalled.'
Out-Summary ('{0,-10} {1,-50} {2,-16} {3,30}' -f 'Kind', 'Engine-voices-configuration', 'Set', 'Words      Critical  Crit%  Fmt   Ins   lenient words, critical')
foreach ($taskRow in $taskRows) {
    foreach ($taskSet in $taskRow.Sets.Keys) {
        if ($taskRow.Sets[$taskSet].Words -eq 0) { continue }
        Out-Summary ('{0,-10} {1,-50} {2,-16} {3}' -f $taskRow.Kind, $taskRow.Configuration, $taskSet, (Format-Tally $taskRow.Sets[$taskSet]))
    }
}
Out-Summary ''
Out-Summary 'Per language (dictation: Exact style, the recognized words):'
foreach ($taskRow in $taskRows) {
    foreach ($taskKey in $taskRow.Languages.Keys | Sort-Object) { Out-Summary ('{0,-10} {1,-50} {2,-16} {3}' -f $taskRow.Kind, $taskRow.Configuration, $taskKey, (Format-Tally $taskRow.Languages[$taskKey])) }
}
Out-Summary ''
foreach ($taskRow in $taskRows | Where-Object Kind -eq 'dictation') { Out-Summary "End-to-end feature checks $($taskRow.Configuration): $($taskRow.Checks) (depend on the recognized text; details in the dictation summary)" }
# The meeting goals of docs\VALIDATION.md for the installed engine and the recommended GPU setup.
$taskReportFolder = Join-Path $EvalArtifacts 'meeting-eval\reports'
function Read-Report([string]$name) { $file = Join-Path $taskReportFolder $name; if ((Test-Path -LiteralPath $file) -and (Get-Item -LiteralPath $file).LastWriteTime -ge $taskStarted) { Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json } }
foreach ($taskTarget in @(@('installed engine', $taskInstalled.Label), @('recommended GPU setup', 'cuda12-large-v3-turbo-q8_0+large-v3'))) {
    $taskFinal = Read-Report "$($taskTarget[1])-piper-auto.json"
    if (-not $taskFinal) { continue }
    Out-Summary ''
    Out-Summary "Meeting goals, $($taskTarget[0]) $($taskTarget[1]), auto:"
    Get-MeetingGoalLines $taskFinal (Read-Report "$($taskTarget[1])-piper-auto-oracle.json") (Read-Report "$($taskTarget[1])-piper-auto-vocabulary.json") | ForEach-Object { Out-Summary $_ }
}
$taskRecordingReport = Read-Report 'cuda12-large-v3-turbo-q8_0+large-v3-recordings.json'
if ($taskRecordingReport) { Out-Summary ''; Get-RecordingLines $taskRecordingReport | ForEach-Object { Out-Summary $_ } }
$taskPassed = @($taskSteps | Where-Object { -not $_.Passed }).Count -eq 0 -and @($taskFunctional | Where-Object { -not $_.Passed }).Count -eq 0
Out-Summary "RESULT: $(if ($taskPassed) { 'PASS' } else { 'FAIL' }) ($(@($taskSteps | Where-Object Passed).Count)/$($taskSteps.Count) steps; accuracy targets are reported above, not part of pass/fail)"
$taskSummary | Set-Content -LiteralPath (Join-Path $taskRoot 'summary.txt') -Encoding UTF8
if (-not $taskPassed) { exit 1 }
