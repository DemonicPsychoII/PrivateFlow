# One entry point for every automated check. Each check runs as a job in its own process with its own temporary and output
# folders, so jobs never read each other's output and run in parallel. Jobs declare what they occupy: processor jobs run as
# wide as -Parallel allows, GPU jobs only as many as fit in GPU memory, desktop jobs (foreground and clipboard) one per Windows
# session. Writes report.json and prints one RESULT line. Tiers, cheapest first:
#   pure     the grouped checks of tests/PrivateFlow.Tests, one process per group (also on Linux with pwsh)
#   engine   real speech engine in isolated data folders: smoke test, workspace check, dictation and meeting accuracy per
#            configuration, language and scenario; -Full adds every backend and model in isolated setups
#   desktop  checks that take the foreground and the clipboard: overlay, paste fixture, UI Automation
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('pure', 'engine', 'desktop')][string]$Tier,
    [ValidateRange(1, 256)][int]$Parallel = [Environment]::ProcessorCount,
    # GPU jobs at once; 0 counts how many speech models fit into the free GPU memory.
    [ValidateRange(0, 16)][int]$GpuSlots = 0,
    # Desktop jobs at once. All of them run in this Windows session, which takes one at a time, so a value above one is treated as one:
    # a second job would only wait for the session lock while its time limit runs.
    [ValidateRange(1, 16)][int]$DesktopSlots = 1,
    # Job name patterns (* as a wildcard); a pattern that matches no job fails the run.
    [string[]]$Filter = @(),
    [string]$Output = '',
    [string]$App = '',
    # Seeds every job that draws at random; printed so a failure replays exactly. -1 picks one.
    [int]$Seed = -1,
    [switch]$Full,
    [switch]$SkipBuild,
    [switch]$List,
    # Runs the jobs one at a time and then in parallel with the same seed and fails unless both give the same results.
    [switch]$CheckDeterminism,
    # Runs only the jobs the files changed since the merge base with -Base affect, committed or not (tests/PrivateFlow.Tests/JobSelection.cs).
    [switch]$Changed,
    [string]$Base = 'origin/main',
    # Internal: runs one job in this process (the scheduler starts every job this way).
    [string]$RunJob = '',
    [string]$JobFolder = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($PSVersionTable.PSVersion.Major -ge 7) { $PSStyle.OutputRendering = 'PlainText' }
$VerifyRepository = Split-Path $PSScriptRoot -Parent
$VerifyOnWindows = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$VerifyHost = (Get-Process -Id $PID).Path
$VerifyStarted = Get-Date
foreach ($pattern in $Filter) { if (-not $pattern) { throw 'An empty -Filter pattern matches nothing.' } }
$Filter = @($Filter | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Seed -lt 0) { $Seed = (New-Object Random).Next(0, [int]::MaxValue) }
$VerifyArtifacts = Join-Path (Join-Path $VerifyRepository 'artifacts') 'verify'
if (-not $Output) { $Output = Join-Path $VerifyArtifacts ("$Tier-" + $VerifyStarted.ToString('yyyyMMdd-HHmmss')) }
$Output = [IO.Path]::GetFullPath($Output)
$VerifyTests = Join-Path $VerifyRepository 'tests/PrivateFlow.Tests/bin/Release/net10.0/PrivateFlow.Tests.dll'

function New-Job([string]$Name, [scriptblock]$Action, [string]$Resource = 'processor', [int]$Cores = 1, [string[]]$DependsOn = @(), [int]$TimeoutMinutes = 60, [hashtable]$Data = @{}) {
    [pscustomobject]@{ Name = $Name; Action = $Action; Resource = $Resource; Cores = $Cores; DependsOn = $DependsOn; TimeoutMinutes = $TimeoutMinutes; Data = $Data }
}
function Get-SafeName([string]$Name) { [regex]::Replace($Name, '[^A-Za-z0-9.+-]', '-') }
function Test-Selected([string]$Name) { $Filter.Count -eq 0 -or @($Filter | Where-Object { $Name -like $_ }).Count -gt 0 }
function Invoke-Native([string]$File, [string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$([IO.Path]::GetFileName($File)) exited with $LASTEXITCODE." }
}
# A PowerShell script of this repository in a child host, with the job's environment.
function Invoke-RepositoryScript([string]$Script, [string[]]$Arguments) {
    $hostArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot $Script)) + $Arguments
    & $VerifyHost @hostArguments
    if ($LASTEXITCODE -ne 0) { throw "$Script exited with $LASTEXITCODE." }
}

# ---- Jobs per tier -------------------------------------------------------------------------------------------------
function Get-PureJobs {
    if (-not (Test-Path -LiteralPath $VerifyTests)) { throw "No test build at $VerifyTests; run without -SkipBuild." }
    $groups = @(& dotnet $VerifyTests --list)
    if ($LASTEXITCODE -ne 0 -or $groups.Count -eq 0) { throw 'The pure checks did not list their groups.' }
    foreach ($group in $groups) {
        New-Job "pure/$group" {
            param($job, $folder)
            Invoke-Native 'dotnet' @($VerifyTests, '--filter', $job.Data.Group, '--result', (Join-Path $folder 'result.json'))
        } -Data @{ Group = $group } -TimeoutMinutes 10
    }
}

# The engines to measure: the installed one and, with -Full, every pinned backend and model in its own isolated root.
function Get-EngineSetups {
    . (Join-Path $PSScriptRoot 'eval-common.ps1')
    $installed = Get-InstalledEngine
    $setups = @([pscustomobject]@{ Label = $installed.Label; Setup = $null; Gpu = $installed.EnginePath -match 'cuda'; MeetingModel = [bool]$installed.MeetingModelPath })
    if ($Full) {
        $gpu = [bool](Get-Command nvidia-smi -ErrorAction SilentlyContinue)
        foreach ($backend in @($(if ($gpu) { 'cuda12' }), 'cpu') | Where-Object { $_ }) {
            foreach ($model in @($(if ($backend -eq 'cuda12') { 'auto' }), 'large-v3-turbo-q8_0', 'medium', 'base') | Where-Object { $_ }) {
                $label = "$backend-$model"
                # As full-eval.ps1: only the setup with exactly the installed label is the installed one.
                if ($installed.Label -eq $label) { continue }
                # Automatic on the GPU is the only setup that adds a meeting model (large-v3), as setup-engine.ps1 does.
                $setups += [pscustomobject]@{ Label = $label; Setup = @{ Backend = $backend; Model = $model }; Gpu = $backend -eq 'cuda12'; MeetingModel = $backend -eq 'cuda12' -and $model -eq 'auto' }
            }
        }
    }
    $setups
}
# The engine a job measures, read when the job runs: the installed one, or what its setup job wrote into its isolated root.
function Get-JobEngine($job) {
    . (Join-Path $PSScriptRoot 'eval-common.ps1')
    if (-not $job.Data.SetupRoot) { return Get-InstalledEngine }
    $written = Get-Content -LiteralPath (Join-Path $job.Data.SetupRoot 'settings.json') -Raw | ConvertFrom-Json
    $meeting = "$($written.MeetingModelPath)"
    [pscustomobject]@{ Label = "$($job.Data.Label)"; EnginePath = $written.EnginePath; ModelPath = $written.ModelPath; MeetingModelPath = $meeting; VadModelPath = $written.VadModelPath }
}
function Get-EngineArguments($engine) {
    @('-App', $App, '-EnginePath', $engine.EnginePath, '-ModelPath', $engine.ModelPath, '-VadModelPath', $engine.VadModelPath, '-EngineLabel', $engine.Label) +
        $(if ($engine.MeetingModelPath) { @('-MeetingModelPath', $engine.MeetingModelPath) } else { @() })
}
function Get-EngineJobs {
    $jobs = @()
    $jobs += New-Job 'engine/prepare-clips' {
        param($job, $folder)
        # Downloads and verifies the test assets and synthesizes every clip once, so the jobs after it only read them.
        Invoke-RepositoryScript 'dictation-eval.ps1' @('-App', $App, '-RealSpeech', '40', '-PrepareOnly')
        Invoke-RepositoryScript 'meeting-eval.ps1' @('-App', $App, '-RealSpeech', '12', '-PrepareOnly')
    } -TimeoutMinutes 120
    $jobs += New-Job 'engine/smoke-test' { param($job, $folder) Invoke-RepositoryScript 'smoke-test.ps1' @('-App', $App) } -TimeoutMinutes 10
    # A speech worker that fails on cue, and the idle unload; then compressed files made by Windows' encoders.
    $jobs += New-Job 'engine/worker-faults' { param($job, $folder) Invoke-RepositoryScript 'verify-worker-faults.ps1' @('-App', $App) } -TimeoutMinutes 15
    $jobs += New-Job 'engine/imports' { param($job, $folder) Invoke-RepositoryScript 'verify-imports.ps1' @('-App', $App) } -TimeoutMinutes 15
    $jobs += New-Job 'engine/workspace-verification' {
        param($job, $folder)
        . (Join-Path $PSScriptRoot 'eval-common.ps1')
        $data = Join-Path $folder 'data'; $result = Join-Path $folder 'workspace'
        Write-EvalSettings $data (Get-InstalledEngine) 'auto' $true
        $env:PRIVATE_FLOW_DATA_DIR = $data
        # The verification host built beside the package runs the workspace verification; the package does not have it.
        $process = Start-Process -FilePath (Get-VerificationHost $App) -ArgumentList @('--verify-workspace', "`"$result`"") -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'Workspace verification timed out.' }
        $report = Get-Content -LiteralPath (Join-Path $result 'result.json') -Raw | ConvertFrom-Json
        Copy-Item -LiteralPath (Join-Path $result 'result.json') -Destination (Join-Path $folder 'result.json')
        if (-not $report.Passed) { throw "Workspace verification failed after $(@($report.Checks).Count) checks: $($report.Error)" }
    # It times the interface thread (never busy over 0.25 s), so it runs alone: it takes every core.
    } -Cores $Parallel -TimeoutMinutes 15
    $meetingScenarios = @([regex]::Matches((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'meeting-eval.ps1') -Raw), "Add-Scenario '([a-z0-9-]+)'") | ForEach-Object { $_.Groups[1].Value })
    foreach ($setup in Get-EngineSetups) {
        $setupRoot = ''
        $dependencies = @('engine/prepare-clips')
        if ($setup.Setup) {
            $setupRoot = Join-Path $Output "engines\$($setup.Label)"
            $jobs += New-Job "engine/setup/$($setup.Label)" {
                param($job, $folder)
                # Files the installed copy already verified are copied, not downloaded again; setup-engine.ps1 checks their hashes.
                $owner = Join-Path $env:LOCALAPPDATA 'PrivateFlow'
                $archive = $(if ($job.Data.Backend -eq 'cuda12') { 'whisper-cublas-12.4.0-bin-x64.zip' } else { 'whisper-bin-x64.zip' })
                foreach ($seed in @(@('downloads', $archive), @('models', "ggml-$($job.Data.Model).bin"), @('models', 'ggml-large-v3.bin'), @('models', 'ggml-silero-v6.2.0.bin'))) {
                    $from = Join-Path $owner "$($seed[0])\$($seed[1])"; $to = Join-Path $job.Data.SetupRoot "$($seed[0])\$($seed[1])"
                    if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) { New-Item -ItemType Directory -Force -Path (Split-Path $to -Parent) | Out-Null; Copy-Item -LiteralPath $from -Destination $to }
                }
                Invoke-RepositoryScript 'setup-engine.ps1' @('-Backend', $job.Data.Backend, '-Model', $job.Data.Model, '-DataRoot', $job.Data.SetupRoot)
            } -Data @{ Backend = $setup.Setup.Backend; Model = $setup.Setup.Model; SetupRoot = $setupRoot } -TimeoutMinutes 120
            $dependencies += "engine/setup/$($setup.Label)"
        }
        $resource = $(if ($setup.Gpu) { 'gpu' } else { 'processor' })
        # Whisper uses up to 8 threads on the processor; four leaves room for a second job on eight cores.
        $cores = $(if ($setup.Gpu) { 1 } else { [math]::Min(4, $Parallel) })
        # On the processor, medium and large-v3-turbo take about 1.8 s per second of audio; they get the shipped configuration only.
        $slow = $setup.Label -match '^cpu-(medium|large)'
        $dictation = $(if ($slow) { @('auto') } elseif ($Full) { @('auto', 'auto-no-detection', 'fixed', 'fixed-no-detection') } else { @('auto', 'auto-no-detection', 'fixed') })
        $realSpeech = $(if ($slow) { 10 } elseif ($Full) { 40 } else { 25 })
        foreach ($configuration in $dictation) {
            foreach ($language in 'en', 'de', 'es') {
                $jobs += New-Job "engine/$($setup.Label)/dictation/$configuration/$language" {
                    param($job, $folder)
                    $engine = Get-JobEngine $job
                    Invoke-RepositoryScript 'dictation-eval.ps1' ((Get-EngineArguments $engine) + @('-Configuration', $job.Data.Configuration, '-Language', $job.Data.Language, '-RealSpeech', "$($job.Data.RealSpeech)"))
                } -Resource $resource -Cores $cores -DependsOn $dependencies -TimeoutMinutes 180 -Data @{ Label = $setup.Label; SetupRoot = $setupRoot; Configuration = $configuration; Language = $language; RealSpeech = $realSpeech; Kind = 'dictation' }
            }
        }
        $meetingConfigurations = $(if ($slow) { @('auto') } elseif (-not $setup.Setup -and $Full) { @('auto', 'auto-no-detection', 'en', 'es') } else { @('auto', 'auto-no-detection') })
        # The scenarios full-eval.ps1 keeps for slow processor models, those meeting-eval.ps1 still has.
        $scenarios = $(if ($slow) { @('three-speakers', 'mixed-channel-en-es', 'code-switching-en-es', 'real-speech-en-es') | Where-Object { $meetingScenarios -contains $_ } } else { $meetingScenarios })
        $variants = @(foreach ($configuration in $meetingConfigurations) { @{ Configuration = $configuration; Mode = 'final'; Tag = ''; Extra = @() } })
        if ($Full -and $setup.MeetingModel) {
            $variants += @{ Configuration = 'auto'; Mode = 'oracle'; Tag = ''; Extra = @() }
            # A user who never types a meeting vocabulary: some names in the dictation vocabulary, two taught as corrections, a title.
            $variants += @{ Configuration = 'auto'; Mode = 'final'; Tag = 'vocabulary'; Extra = @('-Title', 'Design review', '-DictationVocabulary', 'Claude Code, Codex, GPT, Zephyr, Priya, Okafor, Lindqvist', '-Corrections', 'Mariano=Mariana;Joaquin=Joaquín;Tobi=Tobias') }
        }
        if ($Full -and $setup.MeetingModel -and $env:PRIVATE_FLOW_EVALUATION_RECORDINGS -and (Test-Path -LiteralPath $env:PRIVATE_FLOW_EVALUATION_RECORDINGS -PathType Container)) {
            $jobs += New-Job "engine/$($setup.Label)/own-recordings" {
                param($job, $folder)
                Invoke-RepositoryScript 'meeting-eval.ps1' ((Get-EngineArguments (Get-JobEngine $job)) + @('-Scenario', 'none', '-Recordings', $job.Data.Recordings))
            } -Resource $resource -Cores $cores -DependsOn $dependencies -TimeoutMinutes 240 -Data @{ Label = $setup.Label; SetupRoot = $setupRoot; Recordings = $env:PRIVATE_FLOW_EVALUATION_RECORDINGS; Kind = 'recordings' }
        }
        foreach ($variant in $variants) {
            foreach ($scenario in $scenarios) {
                $variantName = $variant.Configuration + $(if ($variant.Mode -ne 'final') { "-$($variant.Mode)" }) + $(if ($variant.Tag) { "-$($variant.Tag)" })
                $jobs += New-Job "engine/$($setup.Label)/meeting/$variantName/$scenario" {
                    param($job, $folder)
                    $engine = Get-JobEngine $job
                    $arguments = (Get-EngineArguments $engine) + @('-Configuration', $job.Data.Configuration, '-Scenario', $job.Data.Scenario, '-Mode', $job.Data.Mode, '-RealSpeech', "$($job.Data.RealSpeech)")
                    if ($job.Data.Tag) { $arguments += @('-Tag', $job.Data.Tag) }
                    # The owner's own recordings run only as their own job.
                    $env:PRIVATE_FLOW_EVALUATION_RECORDINGS = $null
                    Invoke-RepositoryScript 'meeting-eval.ps1' ($arguments + $job.Data.Extra)
                } -Resource $resource -Cores $cores -DependsOn $dependencies -TimeoutMinutes 180 -Data @{ Label = $setup.Label; SetupRoot = $setupRoot; Configuration = $variant.Configuration; Mode = $variant.Mode; Tag = $variant.Tag
                    Extra = $variant.Extra; Scenario = $scenario; RealSpeech = $(if ($slow) { 6 } else { 12 }); Kind = 'meeting'; Variant = $variantName }
            }
        }
    }
    $jobs
}

# The disposable test machine, as DisposableTestVm in src/PrivateFlow.Core names it: simulated keys, pastes into real windows and writes
# to the system clipboard run only there (AGENTS.md).
$VerifyTestMachineMarker = "$env:ProgramData\PrivateFlow\disposable-test-machine.txt"
$VerifyInTestVm = [bool]$env:ProgramData -and [bool]$env:COMPUTERNAME -and [bool]$env:USERNAME -and (Test-Path -LiteralPath $VerifyTestMachineMarker -PathType Leaf) -and
    "$(Get-Content -LiteralPath $VerifyTestMachineMarker -Raw)".Trim() -ceq "$env:COMPUTERNAME\$env:USERNAME"
function Get-DesktopJobs {
    # --verify-insert writes the clipboard and sends Ctrl+V: only in the test VM, where the verification host also allows it.
    foreach ($mode in @('--verify-overlay') + @($(if ($VerifyInTestVm) { '--verify-insert' }))) {
        New-Job "desktop/$($mode.TrimStart('-'))" {
            param($job, $folder)
            $result = Join-Path $folder 'output'
            # Both checks and their fixture run only with an isolated data folder, in the verification host built beside the package.
            . (Join-Path $PSScriptRoot 'eval-common.ps1')
            $env:PRIVATE_FLOW_DATA_DIR = Join-Path $folder 'data'
            $process = Start-Process -FilePath (Get-VerificationHost $App) -ArgumentList @($job.Data.Mode, "`"$result`"") -PassThru
            if (-not $process.WaitForExit(120000)) { $process.Kill(); throw "$($job.Data.Mode) timed out." }
            $file = Get-ChildItem -LiteralPath $result -Filter '*result.txt' | Select-Object -First 1
            if (-not $file) { throw "$($job.Data.Mode) wrote no result (exit code $($process.ExitCode))." }
            $text = Get-Content -LiteralPath $file.FullName -Raw
            Write-Host $text
            if ($process.ExitCode -ne 0 -or $text -match 'FAIL') { throw "$($job.Data.Mode) failed." }
        } -Resource 'desktop' -Data @{ Mode = $mode } -TimeoutMinutes 5
    }
    New-Job 'desktop/ui-smoke-test' { param($job, $folder) Invoke-RepositoryScript 'ui-smoke-test.ps1' @('-App', $App) } -Resource 'desktop' -TimeoutMinutes 10
    # More desktop jobs of suites this checkout may hold: each scripts/*.desktop-jobs.ps1 runs here and adds its jobs.
    foreach ($suite in @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.desktop-jobs.ps1' -File | Sort-Object Name)) { . $suite.FullName }
}

function Get-TierJobs {
    switch ($Tier) {
        'pure' { @(Get-PureJobs) }
        'engine' { @(Get-EngineJobs) }
        'desktop' { @(Get-DesktopJobs) }
    }
}

# ---- One job in this process -----------------------------------------------------------------------------------------
if ($RunJob) {
    if (-not $App -and $Tier -ne 'pure') { $App = Join-Path $VerifyRepository 'artifacts\app\PrivateFlow.exe' }
    $job = @(Get-TierJobs | Where-Object Name -eq $RunJob) | Select-Object -First 1
    if (-not $job) { throw "No job named $RunJob." }
    # One desktop run per Windows session, with a second verify run or another desktop suite beside it: the session lock of
    # eval-common.ps1, which every desktop suite takes as well, and under which a job's own children run.
    if ($job.Resource -eq 'desktop') {
        . (Join-Path $PSScriptRoot 'eval-common.ps1')
        Invoke-DesktopSession { & $job.Action $job $JobFolder } ([TimeSpan]::FromMinutes($job.TimeoutMinutes))
    } else { & $job.Action $job $JobFolder }
    exit 0
}

# ---- Prepare ----------------------------------------------------------------------------------------------------------
if (($Tier -ne 'pure') -and -not $VerifyOnWindows) { throw "The $Tier tier runs on Windows only (the app, its speech worker and its windows)." }
# The pure checks and a build need the .NET SDK; the other tiers only the built package.
if (($Tier -eq 'pure' -or (-not $SkipBuild -and -not $App)) -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is not on PATH.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $SkipBuild -and -not $List) {
    Write-Host '== Build'
    if ($Tier -eq 'pure') { Invoke-Native 'dotnet' @('build', (Join-Path $VerifyRepository 'tests/PrivateFlow.Tests'), '-c', 'Release', '--nologo', '-v', 'quiet') }
    elseif (-not $App) { Invoke-RepositoryScript 'build.ps1' @('-Portable') }
}
if ($Tier -ne 'pure') {
    if (-not $App) { $App = Join-Path $VerifyRepository 'artifacts\app\PrivateFlow.exe' }
    if (-not (Test-Path -LiteralPath $App -PathType Leaf)) { throw "No Private Flow package at $App. Run without -SkipBuild or give -App." }
    $App = (Resolve-Path -LiteralPath $App).Path
}
$jobs = @(Get-TierJobs)
if ($Tier -eq 'desktop' -and -not $VerifyInTestVm) { Write-Host 'desktop/verify-insert sends keys and writes the clipboard, so it runs only in the disposable test machine (AGENTS.md); this run leaves it out.' }
$unmatched = @($Filter | Where-Object { $pattern = $_; @($jobs | Where-Object { $_.Name -like $pattern }).Count -eq 0 })
$selected = @($jobs | Where-Object { Test-Selected $_.Name })
$changeSelection = $null
if ($Changed) {
    if (-not (Test-Path -LiteralPath $VerifyTests)) { throw "-Changed reads the selection from the test build at $VerifyTests; build it first." }
    $mergeBase = "$(& git -C $VerifyRepository merge-base $Base HEAD)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $mergeBase) { throw "No merge base of $Base and HEAD; fetch $Base or give -Base." }
    # The working tree against the merge base: the branch's commits and what is not committed yet, plus new untracked files.
    # A Git command that fails would leave files out of the selection, so it ends the run.
    function Get-GitLines([string[]]$Arguments) {
        $lines = @(& git -C $VerifyRepository @Arguments)
        if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') exited with $LASTEXITCODE; -Changed cannot tell what changed." }
        $lines
    }
    $changedFiles = @(@(Get-GitLines @('diff', '--name-only', '--no-renames', $mergeBase)) + @(Get-GitLines @('ls-files', '--others', '--exclude-standard')) |
        Where-Object { $_ } | Select-Object -Unique)
    $changedLines = @{}; $diffFile = $null
    foreach ($line in @(Get-GitLines @('diff', '-U0', '--no-renames', '--no-color', $mergeBase))) {
        if ($line -match '^\+\+\+ (?:b/(.+)|/dev/null)$') { $diffFile = $Matches[1]; if ($diffFile) { $changedLines[$diffFile] = @() }; continue }
        if ($diffFile -and $line -match '^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@') {
            $first = [int]$Matches[1]; $count = $(if ($null -ne $Matches[2]) { [int]$Matches[2] } else { 1 })
            # A deletion touches the lines on both sides of where it was.
            $changedLines[$diffFile] += , @($first, $(if ($count -eq 0) { $first + 1 } else { $first + $count - 1 }))
        }
    }
    $selectionFolder = Join-Path $Output 'selection'
    New-Item -ItemType Directory -Force -Path $selectionFolder | Out-Null
    $request = [ordered]@{ repository = $VerifyRepository; tier = $Tier; jobs = @($jobs | ForEach-Object Name); changed = $changedFiles; lines = $changedLines }
    ConvertTo-Json -InputObject $request -Depth 6 | Set-Content -LiteralPath (Join-Path $selectionFolder 'request.json') -Encoding UTF8
    Invoke-Native 'dotnet' @($VerifyTests, '--select-jobs', (Join-Path $selectionFolder 'request.json'), (Join-Path $selectionFolder 'selection.json'))
    $answer = Get-Content -LiteralPath (Join-Path $selectionFolder 'selection.json') -Raw | ConvertFrom-Json
    $chosen = @($answer.selected | ForEach-Object job)
    $selected = @($selected | Where-Object { $chosen -contains $_.Name })
}
# A selected job brings the jobs it depends on.
do {
    $names = @($selected | ForEach-Object Name)
    $missing = @($selected | ForEach-Object { $_.DependsOn } | Where-Object { $names -notcontains $_ } | Select-Object -Unique)
    $selected += @($jobs | Where-Object { $missing -contains $_.Name })
} while ($missing.Count -gt 0)
$selected = @($jobs | Where-Object { @($selected | ForEach-Object Name) -contains $_.Name })
if ($Changed) {
    # What runs after -Filter and the dependencies: a job the selection left out runs when a selected one needs it.
    $scheduled = @($selected | ForEach-Object Name)
    $reasonsOf = @{}; foreach ($choice in @($answer.selected)) { $reasonsOf[$choice.job] = @($choice.reasons) }
    $running = @(foreach ($job in $selected) {
        $reasons = $(if ($reasonsOf.ContainsKey($job.Name)) { $reasonsOf[$job.Name] } else {
            @("needed by $(@($selected | Where-Object { $_.DependsOn -contains $job.Name } | ForEach-Object Name) -join ', ')") })
        [pscustomobject]@{ job = $job.Name; reasons = $reasons } })
    $changeSelection = [ordered]@{ base = $Base; mergeBase = $mergeBase; files = $changedFiles; summary = $answer.summary; selected = $running
        omitted = @($jobs | ForEach-Object Name | Where-Object { $scheduled -notcontains $_ })
        filteredOut = @($chosen | Where-Object { $scheduled -notcontains $_ }) }
    Write-Host "Changed since $Base ($($mergeBase.Substring(0, 12))): $($changedFiles.Count) files; $($answer.summary)"
    # Every job for the same reasons is said once, in the summary above; report.json has the reasons of each job.
    if ($changeSelection.omitted.Count -gt 0 -or -not "$($answer.summary)".StartsWith('every job')) {
        foreach ($choice in $running) { Write-Host "  run  $($choice.job): $(@($choice.reasons | Select-Object -First 2) -join '; ')$(if (@($choice.reasons).Count -gt 2) { " (+$(@($choice.reasons).Count - 2) more)" })" }
    }
    if ($changeSelection.filteredOut.Count) { Write-Host "  selected but left out by -Filter ($($changeSelection.filteredOut.Count)): $($changeSelection.filteredOut -join ', ')" }
    if ($changeSelection.omitted.Count) { Write-Host "  left out ($($changeSelection.omitted.Count)): $($changeSelection.omitted -join ', ')" }
}
if ($List) { $selected | ForEach-Object { '{0,-70} {1,-9} cores {2}{3}' -f $_.Name, $_.Resource, $_.Cores, $(if ($_.DependsOn.Count) { '  after ' + ($_.DependsOn -join ', ') }) }; exit 0 }

if ($GpuSlots -eq 0) {
    $GpuSlots = 1
    if ($VerifyOnWindows -and (Get-Command nvidia-smi -ErrorAction SilentlyContinue)) {
        $free = @(& nvidia-smi --query-gpu=memory.free --format=csv,noheader,nounits) | Select-Object -First 1
        # A large-v3 final pass with its beam cache took about 4.6 GB (SpeechRuntime.GpuMemoryNeeded).
        if ("$free" -match '^\s*(\d+)') { $GpuSlots = [math]::Max(1, [math]::Floor([int]$Matches[1] / 4700)) }
    }
}

# ---- Scheduler --------------------------------------------------------------------------------------------------------
function Invoke-Jobs($jobList, [int]$width, [string]$root) {
    $pending = New-Object System.Collections.Generic.List[object]; $jobList | ForEach-Object { $pending.Add($_) }
    $running = New-Object System.Collections.Generic.List[object]
    $results = @{}
    $limits = @{ processor = [int]::MaxValue; gpu = $GpuSlots; desktop = 1 }
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($entry in $running.ToArray()) {
            $timedOut = ((Get-Date) - $entry.Started).TotalMinutes -gt $entry.Job.TimeoutMinutes
            if (-not $entry.Process.HasExited -and -not $timedOut) { continue }
            if (-not $entry.Process.HasExited) { Stop-ProcessTree $entry.Process.Id }
            $entry.Process.WaitForExit()
            Remove-Item -LiteralPath $entry.Temporary -Recurse -Force -ErrorAction SilentlyContinue
            $seconds = [math]::Round(((Get-Date) - $entry.Started).TotalSeconds, 1)
            $passed = -not $timedOut -and $entry.Process.ExitCode -eq 0
            $detail = $(if ($timedOut) { "timed out after $($entry.Job.TimeoutMinutes) min" } elseif (-not $passed) { Get-FailureDetail $entry.Folder } else { '' })
            $results[$entry.Job.Name] = [pscustomobject]@{ step = $entry.Job.Name; result = $(if ($passed) { 'pass' } else { 'fail' }); started = $entry.Started.ToString('o'); durationSeconds = $seconds; resource = $entry.Job.Resource
                log = (Join-Path $entry.Folder 'log.txt'); errorLog = (Join-Path $entry.Folder 'error.txt'); resultFile = $(if (Test-Path -LiteralPath (Join-Path $entry.Folder 'result.json')) { Join-Path $entry.Folder 'result.json' })
                screenshots = @(Get-ChildItem -LiteralPath $entry.Folder -Recurse -Include '*.png' -ErrorAction SilentlyContinue | ForEach-Object FullName); detail = $detail }
            Write-Host ('{0} {1} ({2} s){3}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }), $entry.Job.Name, $seconds, $(if ($detail) { ": $detail" }))
            $null = $running.Remove($entry)
        }
        $usedCores = ($running | ForEach-Object { $_.Job.Cores } | Measure-Object -Sum).Sum
        foreach ($job in $pending.ToArray()) {
            $failedDependency = @($job.DependsOn | Where-Object { $results[$_] -and $results[$_].result -ne 'pass' }) | Select-Object -First 1
            if ($failedDependency) {
                $results[$job.Name] = [pscustomobject]@{ step = $job.Name; result = 'skipped'; started = $null; durationSeconds = 0; resource = $job.Resource; log = $null; errorLog = $null; resultFile = $null; screenshots = @(); detail = "$failedDependency failed" }
                Write-Host "SKIP $($job.Name): $failedDependency failed"
                $null = $pending.Remove($job); continue
            }
            if (@($job.DependsOn | Where-Object { -not $results[$_] }).Count -gt 0) { continue }
            $cores = [math]::Min($job.Cores, $width)
            if ($usedCores + $cores -gt $width) { continue }
            if (@($running | Where-Object { $_.Job.Resource -eq $job.Resource }).Count -ge $limits[$job.Resource]) { continue }
            $running.Add((Start-VerifyJob $job $root)); $usedCores += $cores
            $null = $pending.Remove($job)
        }
        if ($running.Count -gt 0) { Start-Sleep -Milliseconds 200 }
    }
    $jobList | ForEach-Object { $results[$_.Name] }
}
function Start-VerifyJob($job, [string]$root) {
    $folder = Join-Path (Join-Path $root 'jobs') (Get-SafeName $job.Name)
    if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
    # The job's temporary folder sits under the system TEMP with a short name, not under the job folder: checks that nest temporary Git
    # clones there would otherwise pass Windows' 260-character path limit when the checkout path is long. It goes when the job ends.
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('pfv-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
    New-Item -ItemType Directory -Force -Path $folder, $temporary | Out-Null
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Tier', $Tier, '-RunJob', "`"$($job.Name)`"", '-JobFolder', "`"$folder`"",
        '-Seed', $Seed, '-Parallel', $Parallel, '-Output', "`"$($root.TrimEnd('\', '/'))`"")
    # The run's root, so a job rebuilds the same job list: -Full setup jobs and the evaluations after them agree on SetupRoot.
    if ($App) { $arguments += @('-App', "`"$App`"") }
    if ($Full) { $arguments += '-Full' }
    # Every job gets its own temporary folder and evaluation output; the app and the speech worker inherit them, and every
    # job gives the app its own data folder.
    $saved = @{}
    $environment = @{ TEMP = $temporary; TMP = $temporary; TMPDIR = $temporary; PRIVATE_FLOW_EVAL_OUTPUT = $folder; PRIVATE_FLOW_TEST_SEED = "$Seed" }
    foreach ($name in $environment.Keys) { $saved[$name] = [Environment]::GetEnvironmentVariable($name); [Environment]::SetEnvironmentVariable($name, $environment[$name]) }
    try {
        $process = Start-Process -FilePath $VerifyHost -ArgumentList $arguments -NoNewWindow -PassThru -WorkingDirectory $VerifyRepository `
            -RedirectStandardOutput (Join-Path $folder 'log.txt') -RedirectStandardError (Join-Path $folder 'error.txt')
    } finally { foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) } }
    # Windows PowerShell reports no exit code unless the handle was taken while the process ran.
    $null = $process.Handle
    [pscustomobject]@{ Job = $job; Process = $process; Folder = $folder; Temporary = $temporary; Started = Get-Date }
}
function Stop-ProcessTree([int]$Id) {
    if ($VerifyOnWindows) { & taskkill.exe /T /F /PID $Id 2>&1 | Out-Null } else { & pkill -KILL -P $Id 2>&1 | Out-Null; Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue }
}
function Get-FailureDetail([string]$folder) {
    $read = { param($name) $file = Join-Path $folder $name; if (Test-Path -LiteralPath $file) { @(Get-Content -LiteralPath $file | ForEach-Object { ($_ -replace '\x1b\[[0-9;]*m', '').Trim() } | Where-Object { $_ }) } else { @() } }
    $log = @(& $read 'log.txt'); $errors = @(& $read 'error.txt')
    # The checks' own failure lines first, then what the job threw, then the last thing it printed.
    $failure = @($log | Where-Object { $_ -match '^FAILED\b' }) + @($errors) + @($log | Where-Object { $_ -match '^FAIL\b' }) + @($log | Select-Object -Last 1) | Select-Object -First 1
    # Git and .NET name the path limit obscurely; say what to change.
    $tooLong = @($log + $errors | Where-Object { $_ -match '(?i)filename too long|PathTooLongException|path is too long' }).Count -gt 0
    $(if ($failure) { "$failure" } else { 'no output' }) + $(if ($tooLong) { " (a path passed Windows' 260-character limit: choose a shorter -Output, or enable long paths in Windows and Git)" } else { '' })
}
# What a job's result says apart from timing and paths, for the comparison of a sequential and a parallel run.
function Get-Fingerprint($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [string] -or $value -is [ValueType]) { return $value }
    if ($value -is [Collections.IEnumerable] -and $value -isnot [Collections.IDictionary]) { return @($value | ForEach-Object { Get-Fingerprint $_ }) }
    $copy = [ordered]@{}
    foreach ($property in $value.PSObject.Properties) {
        if ($property.Name -match '(?i)^(duration\w*|\w*seconds|\w*milliseconds|started|finished|date|\w*path|\w*folder|log|errorLog|resultFile|screenshots)$') { continue }
        $copy[$property.Name] = Get-Fingerprint $property.Value
    }
    $copy
}
function Get-ResultFingerprints($results) {
    $prints = @{}
    foreach ($result in $results) {
        $content = $(if ($result.resultFile) { Get-Fingerprint (Get-Content -LiteralPath $result.resultFile -Raw -Encoding UTF8 | ConvertFrom-Json) })
        $prints[$result.step] = ConvertTo-Json -Compress -Depth 30 -InputObject ([ordered]@{ result = $result.result; content = $content })
    }
    $prints
}

Write-Host "Private Flow verify: tier $Tier, $($selected.Count) jobs, up to $Parallel cores, $GpuSlots GPU and $DesktopSlots desktop slot(s), seed $Seed"
Write-Host "Replay: scripts/verify.ps1 -Tier $Tier -Seed $Seed$(if ($Filter.Count) { " -Filter '$($Filter -join ',')'" })$(if ($Full) { ' -Full' })$(if ($Changed) { " -Changed -Base $Base" })"
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$watch = [Diagnostics.Stopwatch]::StartNew()
$results = @(Invoke-Jobs $selected $Parallel $(if ($CheckDeterminism) { Join-Path $Output 'parallel' } else { $Output }))
$wallSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
$determinism = $null
if ($CheckDeterminism) {
    Write-Host "== The same jobs one at a time, seed $Seed"
    $watch.Restart()
    $sequential = @(Invoke-Jobs $selected 1 (Join-Path $Output 'sequential'))
    $sequentialSeconds = [math]::Round($watch.Elapsed.TotalSeconds, 1)
    $parallelPrints = Get-ResultFingerprints $results; $sequentialPrints = Get-ResultFingerprints $sequential
    $different = @($selected | ForEach-Object Name | Where-Object { $parallelPrints[$_] -ne $sequentialPrints[$_] })
    $determinism = [ordered]@{ result = $(if ($different.Count -eq 0) { 'pass' } else { 'fail' }); sequentialSeconds = $sequentialSeconds; parallelSeconds = $wallSeconds; different = $different }
    foreach ($name in $different) { Write-Host "FAIL $name gave different results in sequence and in parallel" }
}

# ---- Report -----------------------------------------------------------------------------------------------------------
$timingsFile = Join-Path $VerifyArtifacts 'timings.json'
$timings = $(if (Test-Path -LiteralPath $timingsFile) { Get-Content -LiteralPath $timingsFile -Raw | ConvertFrom-Json } else { [pscustomobject]@{} })
$key = "$Tier$(if ($Full) { '-full' })$(if ($Filter.Count) { ' ' + ($Filter -join ',') })$(if ($Changed) { ' changed' })"
$previous = $timings.PSObject.Properties[$key]
$jobSeconds = [math]::Round(($results | Measure-Object durationSeconds -Sum).Sum, 1)
$failed = @($results | Where-Object result -ne 'pass')
$passed = $failed.Count -eq 0 -and $unmatched.Count -eq 0 -and ($selected.Count -gt 0 -or $Changed) -and (-not $determinism -or $determinism.result -eq 'pass')
$report = [ordered]@{
    tier = $Tier; full = [bool]$Full; seed = $Seed; parallel = $Parallel; gpuSlots = $GpuSlots; desktopSlots = $DesktopSlots; filter = $Filter; unmatchedFilters = $unmatched; changed = $changeSelection
    started = $VerifyStarted.ToString('o'); wallClockSeconds = $wallSeconds; jobSeconds = $jobSeconds
    previousSequentialSeconds = $(if ($previous) { $previous.Value.seconds }); determinism = $determinism
    result = $(if ($passed) { 'pass' } else { 'fail' }); steps = $results
}
ConvertTo-Json -InputObject $report -Depth 8 | Set-Content -LiteralPath (Join-Path $Output 'report.json') -Encoding UTF8
# A complete sequential run is the baseline later parallel runs are compared with.
$measuredSequential = $(if ($determinism) { $determinism.sequentialSeconds } elseif ($Parallel -eq 1) { $wallSeconds })
if ($passed -and $measuredSequential) {
    $timings | Add-Member -NotePropertyName $key -NotePropertyValue ([pscustomobject]@{ seconds = $measuredSequential; date = $VerifyStarted.ToString('yyyy-MM-dd') }) -Force
    New-Item -ItemType Directory -Force -Path $VerifyArtifacts | Out-Null
    ConvertTo-Json -InputObject $timings | Set-Content -LiteralPath $timingsFile -Encoding UTF8
}
foreach ($result in $failed) { Write-Host "FAILED $($result.step): $($result.detail)$(if ($result.log) { " (log $($result.log))" })" }
foreach ($pattern in $unmatched) { Write-Host "FAILED no job matches $pattern" }
$sequentialNote = $(if ($previous) { ", sequential $($previous.Value.seconds) s on $($previous.Value.date)" } elseif ($determinism) { ", sequential $($determinism.sequentialSeconds) s" } else { '' })
Write-Host ('RESULT: {0} ({1}/{2} jobs, {3} s wall clock, {4} s of job time{5}, seed {6}) report {7}' -f $(if ($passed) { 'PASS' } else { 'FAIL' }),
    @($results | Where-Object result -eq 'pass').Count, $selected.Count, $wallSeconds, $jobSeconds, $sequentialNote, $Seed, (Join-Path $Output 'report.json'))
if (-not $passed) { exit 1 }
