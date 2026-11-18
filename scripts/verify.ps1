# One entry point for every automated check. Each check runs as a job in its own process with its own temporary and output
# folders, so jobs never read each other's output and run in parallel. Jobs declare what they occupy: processor jobs run as
# wide as -Parallel allows, GPU jobs only as many as fit in GPU memory, desktop jobs (foreground and clipboard) one per Windows
# session. Writes report.json and prints one RESULT line. Tiers:
#   pure     the grouped checks of tests/PrivateFlow.Tests, one process per group (also on Linux with pwsh)
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('pure')][string]$Tier,
    [ValidateRange(1, 256)][int]$Parallel = [Environment]::ProcessorCount,
    # GPU jobs at once.
    [ValidateRange(1, 16)][int]$GpuSlots = 1,
    # Windows sessions for desktop jobs; one per test VM or session.
    [ValidateRange(1, 16)][int]$DesktopSlots = 1,
    # Job name patterns (* as a wildcard); a pattern that matches no job fails the run.
    [string[]]$Filter = @(),
    [string]$Output = '',
    # Seeds every job that draws at random; printed so a failure replays exactly. -1 picks one.
    [int]$Seed = -1,
    [switch]$SkipBuild,
    [switch]$List,
    # Runs the jobs one at a time and then in parallel with the same seed and fails unless both give the same results.
    [switch]$CheckDeterminism,
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

function Get-TierJobs {
    switch ($Tier) {
        'pure' { @(Get-PureJobs) }
    }
}

# ---- One job in this process -----------------------------------------------------------------------------------------
if ($RunJob) {
    $job = @(Get-TierJobs | Where-Object Name -eq $RunJob) | Select-Object -First 1
    if (-not $job) { throw "No job named $RunJob." }
    & $job.Action $job $JobFolder
    exit 0
}

# ---- Prepare ----------------------------------------------------------------------------------------------------------
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'dotnet is not on PATH.' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
if (-not $SkipBuild -and -not $List) {
    Write-Host '== Build'
    Invoke-Native 'dotnet' @('build', (Join-Path $VerifyRepository 'tests/PrivateFlow.Tests'), '-c', 'Release', '--nologo', '-v', 'quiet')
}
$jobs = @(Get-TierJobs)
$unmatched = @($Filter | Where-Object { $pattern = $_; @($jobs | Where-Object { $_.Name -like $pattern }).Count -eq 0 })
$selected = @($jobs | Where-Object { Test-Selected $_.Name })
# A selected job brings the jobs it depends on.
do {
    $names = @($selected | ForEach-Object Name)
    $missing = @($selected | ForEach-Object { $_.DependsOn } | Where-Object { $names -notcontains $_ } | Select-Object -Unique)
    $selected += @($jobs | Where-Object { $missing -contains $_.Name })
} while ($missing.Count -gt 0)
$selected = @($jobs | Where-Object { @($selected | ForEach-Object Name) -contains $_.Name })
if ($List) { $selected | ForEach-Object { '{0,-70} {1,-9} cores {2}{3}' -f $_.Name, $_.Resource, $_.Cores, $(if ($_.DependsOn.Count) { '  after ' + ($_.DependsOn -join ', ') }) }; exit 0 }

# ---- Scheduler --------------------------------------------------------------------------------------------------------
function Invoke-Jobs($jobList, [int]$width, [string]$root) {
    $pending = New-Object System.Collections.Generic.List[object]; $jobList | ForEach-Object { $pending.Add($_) }
    $running = New-Object System.Collections.Generic.List[object]
    $results = @{}
    $limits = @{ processor = [int]::MaxValue; gpu = $GpuSlots; desktop = $DesktopSlots }
    while ($pending.Count -gt 0 -or $running.Count -gt 0) {
        foreach ($entry in $running.ToArray()) {
            $timedOut = ((Get-Date) - $entry.Started).TotalMinutes -gt $entry.Job.TimeoutMinutes
            if (-not $entry.Process.HasExited -and -not $timedOut) { continue }
            if (-not $entry.Process.HasExited) { Stop-ProcessTree $entry.Process.Id }
            $entry.Process.WaitForExit()
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
    $temporary = Join-Path $folder 'temp'
    New-Item -ItemType Directory -Force -Path $temporary | Out-Null
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Tier', $Tier, '-RunJob', "`"$($job.Name)`"", '-JobFolder', "`"$folder`"",
        '-Seed', $Seed, '-Parallel', $Parallel)
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
    [pscustomobject]@{ Job = $job; Process = $process; Folder = $folder; Started = Get-Date }
}
function Stop-ProcessTree([int]$Id) {
    if ($VerifyOnWindows) { & taskkill.exe /T /F /PID $Id 2>&1 | Out-Null } else { & pkill -KILL -P $Id 2>&1 | Out-Null; Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue }
}
function Get-FailureDetail([string]$folder) {
    $read = { param($name) $file = Join-Path $folder $name; if (Test-Path -LiteralPath $file) { @(Get-Content -LiteralPath $file | ForEach-Object { ($_ -replace '\x1b\[[0-9;]*m', '').Trim() } | Where-Object { $_ }) } else { @() } }
    $log = @(& $read 'log.txt'); $errors = @(& $read 'error.txt')
    # The checks' own failure lines first, then what the job threw, then the last thing it printed.
    $failure = @($log | Where-Object { $_ -match '^FAILED\b' }) + @($errors) + @($log | Where-Object { $_ -match '^FAIL\b' }) + @($log | Select-Object -Last 1) | Select-Object -First 1
    $(if ($failure) { "$failure" } else { 'no output' })
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
Write-Host "Replay: scripts/verify.ps1 -Tier $Tier -Seed $Seed$(if ($Filter.Count) { " -Filter '$($Filter -join ',')'" })"
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
$key = "$Tier$(if ($Filter.Count) { ' ' + ($Filter -join ',') })"
$previous = $timings.PSObject.Properties[$key]
$jobSeconds = [math]::Round(($results | Measure-Object durationSeconds -Sum).Sum, 1)
$failed = @($results | Where-Object result -ne 'pass')
$passed = $failed.Count -eq 0 -and $unmatched.Count -eq 0 -and $selected.Count -gt 0 -and (-not $determinism -or $determinism.result -eq 'pass')
$report = [ordered]@{
    tier = $Tier; seed = $Seed; parallel = $Parallel; gpuSlots = $GpuSlots; desktopSlots = $DesktopSlots; filter = $Filter; unmatchedFilters = $unmatched
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
