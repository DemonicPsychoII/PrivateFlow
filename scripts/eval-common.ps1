# Shared by meeting-eval.ps1, dictation-eval.ps1 and full-eval.ps1 (dot-sourced): test-only assets, synthetic
# voices, engine settings and the gated evaluation modes. Everything it writes stays under artifacts\.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$EvalRepository = Split-Path $PSScriptRoot -Parent
$EvalArtifacts = Join-Path $EvalRepository 'artifacts'
$EvalAssets = Join-Path $EvalArtifacts 'test-assets'
$EvalClips = Join-Path $EvalArtifacts 'eval-clips'
# Where a run writes its data folders, reports and summaries; scripts/verify.ps1 gives every job its own.
$EvalOutput = $(if ($env:PRIVATE_FLOW_EVAL_OUTPUT) { [IO.Path]::GetFullPath($env:PRIVATE_FLOW_EVAL_OUTPUT) } else { $EvalArtifacts })

# Test-only text-to-speech and real speech. Never shipped with the app and never redistributed; licences in docs\TEST_COVERAGE.md.
$EvalPiper = @{ Url = 'https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip'; File = 'piper_windows_amd64.zip'; Hash = 'f3c58906402b24f3a96d92145f58acba6d86c9b5db896d207f78dc80811efcea' }
$EvalVoiceRevision = '6b9cfaacb4fb2e02a9621227e3c1a48bec9710c3'
$EvalVoiceModels = [ordered]@{
    'en_GB-vctk-medium' = @{ Path = 'en/en_GB/vctk/medium'; Model = '4e9fc85ab9009385319fc6bae7f55577f8a2d7ee77fd9159a5500eb6531f41e6'; Config = '7f85e6391ed0f7f46e4abd19345929a16be931a0c9945086f96692dce2087fa8' }
    'en_US-l2arctic-medium' = @{ Path = 'en/en_US/l2arctic/medium'; Model = 'd89f6f124bf1e7735b2179d2141b8001c3e19169d5e743ed6e35624f4c76f044'; Config = 'a97e2ba653e9efcdc1bdcec64a398c8beb19ae5e8dfdbfe4ad6841983e56c07c' }
    'es_AR-daniela-high' = @{ Path = 'es/es_AR/daniela/high'; Model = '7ceb1fc0dab349418c5b54a639ae9ee595212d7c9ea422220d8419163d5cc985'; Config = 'aedbf69647e1d754c62ecf8e0366ca5f16af3e768e3c6b5329af6eb6bde3852b' }
    'es_MX-ald-medium' = @{ Path = 'es/es_MX/ald/medium'; Model = '019b3803293c93e34a206dd2e53a3889209a514e786fd7144f7b70196c579b63'; Config = '5a71498158e04afc8099bfd019c7e87c68eb9d042505a2b1a87e5c1ac2b1a61d' }
    'es_MX-claude-high' = @{ Path = 'es/es_MX/claude/high'; Model = '3ef40a71ea63852cd8ab7e6fa7d2ecdcfa67a0b47c9c48e3f10e02ee02083ea0'; Config = '1afc81f703c0e4cb3b4d7c0dca096b8b54a98806807f0170cf5eb5557723c12d' }
    'de_DE-kerstin-low' = @{ Path = 'de/de_DE/kerstin/low'; Model = 'd352a7641892cebf2903859af94e9ba81a141110215fe3943bcda7f7da401b7a'; Config = '56e708556b7b9b7a53c4f8957e021421e69f11a600962bba554cffbe72cf2d47' }
    'de_DE-thorsten-high' = @{ Path = 'de/de_DE/thorsten/high'; Model = '9df1c43c61149ef9b39e618e2b861fbe41e1fcea9390b2dac62e8761573ea4f1'; Config = '6de734444e4c3f9e33b7ebe2746dbc19b71e85f613e79c65acf623200b99a76a' }
    'de_DE-karlsson-low' = @{ Path = 'de/de_DE/karlsson/low'; Model = 'f9793bf7e461a472b3916cf260774bfc9812e9eb72601b147ed649d01064e8fe'; Config = '3b2095adbfeede316f22007a0a09d4bfc25514b7189850740b06800a6cfb9838' }
}
# FLEURS (Google, CC BY 4.0): read Wikipedia sentences by native speakers. Archive hashes are the Hugging Face LFS records.
$EvalFleursRevision = '70bb2e84b976b7e960aa89f1c648e09c59f894dd'
$EvalFleurs = [ordered]@{
    en = @{ Name = 'en_us'; Archive = '2658fda72f199e12676ecac9415094667a4e14e149b146e568ea00b2a2f0954c'; Transcripts = '9d57ee7e91e9d4c92edb39f6bbea668ef8dc2a3ff96eb510d5580b2ad05d17ec' }
    es = @{ Name = 'es_419'; Archive = '94f0f3d9fd89c0496385bf0991f2565410cc489a73dd18cafa0ee0177dc5c815'; Transcripts = '352b2a5f03f02fe752254fab737f76519963650f4630d2fb63957177aeec927f' }
    de = @{ Name = 'de_de'; Archive = '72cac13bbddbddf674d8102a9e7b90a3b1db5d777ed92e88eccc5ae69cb8fc9d'; Transcripts = '055b6ddecc9ad493d9ead86babe3ebe225148dd3c174fa6f95cb83a4f9586e18' }
}

function Get-PinnedFile([string]$Url, [string]$File, [string]$Hash) {
    if ((Test-Path -LiteralPath $File) -and ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -eq $Hash)) { return $File }
    New-Item -ItemType Directory -Force -Path (Split-Path $File -Parent) | Out-Null
    Write-Host "Downloading $([IO.Path]::GetFileName($File))..."
    & (Join-Path $env:SystemRoot 'System32\curl.exe') --fail --location --silent --show-error --retry 3 --output "$File.partial" $Url
    if ($LASTEXITCODE -ne 0) { throw "Download of $Url failed." }
    if ((Get-FileHash -LiteralPath "$File.partial" -Algorithm SHA256).Hash -ne $Hash) { Remove-Item -LiteralPath "$File.partial"; throw "Checksum mismatch for $File; nothing was kept." }
    Move-Item -LiteralPath "$File.partial" -Destination $File -Force
    $File
}

# Piper and its voices, downloaded once and verified on every run; returns the piper.exe path.
function Get-Piper {
    $downloads = Join-Path $EvalAssets 'downloads'
    $archive = Get-PinnedFile $EvalPiper.Url (Join-Path $downloads $EvalPiper.File) $EvalPiper.Hash
    $folder = Join-Path $EvalAssets 'piper'
    $marker = Join-Path $folder '.archive-sha256'
    if (-not (Test-Path -LiteralPath $marker) -or (Get-Content -LiteralPath $marker -Raw).Trim() -ne $EvalPiper.Hash) {
        if (Test-Path -LiteralPath $folder) { Remove-Item -LiteralPath $folder -Recurse -Force }
        Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
        Set-Content -LiteralPath $marker -Value $EvalPiper.Hash -Encoding ascii
    }
    foreach ($name in $EvalVoiceModels.Keys) {
        $voice = $EvalVoiceModels[$name]; $base = "https://huggingface.co/rhasspy/piper-voices/resolve/$EvalVoiceRevision/$($voice.Path)/$name"
        $null = Get-PinnedFile "$base.onnx" (Join-Path $downloads "$name.onnx") $voice.Model
        $null = Get-PinnedFile "$base.onnx.json" (Join-Path $downloads "$name.onnx.json") $voice.Config
    }
    Join-Path $folder 'piper\piper.exe'
}

# Voices per speaker and language: "piper:<model>#<speaker>@<length scale>".
# Mixed-channel speakers (Ana, Bruno, Carmen) speak English with a Spanish accent (L2-ARCTIC speakers with Spanish as first language).
$EvalPiperVoices = @{
    'Alice|en' = 'piper:en_GB-vctk-medium#p250@1.0'; 'Ben|en' = 'piper:en_GB-vctk-medium#p259@0.92'; 'Carl|en' = 'piper:en_GB-vctk-medium#p274@1.06'; 'Narrator|en' = 'piper:en_GB-vctk-medium#p283@1.0'
    'Ana|en' = 'piper:en_US-l2arctic-medium#MBMPS@1.0'; 'Bruno|en' = 'piper:en_US-l2arctic-medium#ERMS@0.95'; 'Carmen|en' = 'piper:en_US-l2arctic-medium#NJS@1.05'
    'Alice|es' = 'piper:es_AR-daniela-high#0@1.0'; 'Ben|es' = 'piper:es_MX-ald-medium#0@0.92'; 'Carl|es' = 'piper:es_MX-claude-high#0@1.06'
    'Ana|es' = 'piper:es_AR-daniela-high#0@1.0'; 'Bruno|es' = 'piper:es_MX-ald-medium#0@0.95'; 'Carmen|es' = 'piper:es_MX-claude-high#0@1.05'
    'Alice|de' = 'piper:de_DE-kerstin-low#0@1.0'; 'Ben|de' = 'piper:de_DE-thorsten-high#0@0.92'; 'Carl|de' = 'piper:de_DE-karlsson-low#0@1.06'
}
# Speaker|language -> voice, with Piper and its voices ready.
function Get-VoiceTable { $null = Get-Piper; $EvalPiperVoices.Clone() }

$EvalJobs = @{}
function Get-Clip([string]$Voice, [string]$Text) {
    $plain = $Text.Replace('[', '').Replace(']', '')
    $hash = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes("$Voice|$plain"))).Replace('-', '').Substring(0, 16)
    $file = Join-Path $EvalClips "$hash.wav"
    if (-not (Test-Path -LiteralPath $file)) { $EvalJobs[$file] = [ordered]@{ Voice = $Voice; Text = $plain; File = $file } }
    $file
}
# Synthesizes every clip that Get-Clip handed out and that is not cached yet.
function Invoke-Synthesis {
    $pending = @($EvalJobs.Values | Where-Object { -not (Test-Path -LiteralPath $_.File) })
    if ($pending.Count -eq 0) { return }
    Write-Host "Synthesizing $($pending.Count) clips..."
    New-Item -ItemType Directory -Force -Path $EvalClips | Out-Null
    $piper = Join-Path $EvalAssets 'piper\piper\piper.exe'
    foreach ($group in @($pending | Group-Object { $_.Voice })) {
        if ($group.Name -notmatch '^piper:(?<model>[^#]+)#(?<speaker>[^@]+)@(?<scale>[0-9.]+)$') { throw "Unknown voice $($group.Name)." }
        $model = Join-Path $EvalAssets "downloads\$($Matches.model).onnx"
        $name = $Matches.speaker; $scale = $Matches.scale
        # Windows PowerShell's JSON reader rejects the phoneme map, whose keys differ only in case.
        $configuration = Get-Content -LiteralPath "$model.json" -Raw -Encoding UTF8
        $named = [regex]::Match($configuration, '"speaker_id_map"\s*:\s*\{[^}]*"' + [regex]::Escape($name) + '"\s*:\s*(\d+)')
        # A speaker ID crashes Piper on a single-speaker voice.
        $single = $configuration -match '"num_speakers"\s*:\s*1\b'
        $lines = foreach ($job in $group.Group) {
            $request = [ordered]@{ text = $job.Text }
            if (-not $single) { $request.speaker_id = $(if ($named.Success) { [int]$named.Groups[1].Value } else { [int]$name }) }
            $request.output_file = "$($job.File).$PID.partial.wav"
            ConvertTo-Json -Compress -InputObject $request
        }
        # Per process, in case two evaluations synthesize at once; the clip that arrives first is kept.
        $jobFile = Join-Path $EvalClips "jobs-piper-$PID.jsonl"
        [IO.File]::WriteAllLines($jobFile, [string[]]$lines, (New-Object Text.UTF8Encoding($false)))
        & cmd.exe /d /c "`"$piper`" --model `"$model`" --length_scale $scale --json-input --quiet < `"$jobFile`" > nul"
        if ($LASTEXITCODE -ne 0) { throw "Piper failed for $($group.Name)." }
        foreach ($job in $group.Group) {
            $partial = "$($job.File).$PID.partial.wav"
            if (-not (Test-Path -LiteralPath $partial) -or (Get-Item -LiteralPath $partial).Length -lt 1000) { throw "Piper produced no audio for '$($job.Text)'." }
            try { Move-Item -LiteralPath $partial -Destination $job.File } catch { if (-not (Test-Path -LiteralPath $job.File)) { throw }; Remove-Item -LiteralPath $partial }
        }
    }
}

# Critical items in real speech are marked automatically: numbers, negations and, in English and Spanish, capitalised words inside a sentence.
function Get-MarkedReference([string]$Text, [string]$Language) {
    $marked = $Text.Replace('[', '(').Replace(']', ')')
    $negation = @{ en = "not|no|never|nobody|nothing|none|neither|nor|cannot|[a-z]+n't"; es = 'no|nunca|nadie|nada|ning[uú]n[oa]?|ni|jam[aá]s|tampoco'; de = 'nicht|nie|niemals|kein\w*|niemand\w*|nichts|weder' }[$Language]
    # A number grouped with spaces ("400 000") is one item.
    $pattern = '(?<![\p{L}\p{N}])(?:(?:\d{1,3}(?: \d{3})+(?!\d)|\d+(?:[.,:]\d+)*)(?:\s?%|(?:st|nd|rd|th)\b)?|(?i:' + $negation + ')(?![\p{L}\p{N}])'
    if ($Language -ne 'de') { $pattern += '|(?<=[\p{L}\p{N},;:)]\s)(?!I\b)\p{Lu}[\p{L}''-]+' }
    [regex]::Replace($marked, $pattern + ')', '[$0]')
}

# Selects real FLEURS utterances per language, extracts them from the verified archives and returns clip and marked reference.
# Sentences with numbers or negations come first; each sentence is used once.
function Get-RealSpeech([int]$Count) {
    $downloads = Join-Path $EvalAssets 'downloads'
    $items = @()
    foreach ($language in $EvalFleurs.Keys) {
        $source = $EvalFleurs[$language]; $base = "https://huggingface.co/datasets/google/fleurs/resolve/$EvalFleursRevision/data/$($source.Name)"
        $transcripts = Get-PinnedFile "$base/dev.tsv" (Join-Path $downloads "fleurs-$($source.Name)-dev.tsv") $source.Transcripts
        $archive = Get-PinnedFile "$base/audio/dev.tar.gz" (Join-Path $downloads "fleurs-$($source.Name)-dev.tar.gz") $source.Archive
        $rows = foreach ($line in [IO.File]::ReadAllLines($transcripts, [Text.Encoding]::UTF8)) {
            $fields = $line -split "`t"
            $critical = $fields[2] -match '\d' -or (Get-MarkedReference $fields[2] $language) -match '\[(?i:not|no|never|nicht|kein|nunca|nadie)'
            [pscustomobject]@{ Sentence = $fields[0]; File = $fields[1]; Text = $fields[2]; Gender = $fields[6]; Critical = $critical }
        }
        $chosen = @($rows | Sort-Object @{ Expression = 'Critical'; Descending = $true }, File | Group-Object Sentence | ForEach-Object { $_.Group[0] } |
            Sort-Object @{ Expression = 'Critical'; Descending = $true }, File | Select-Object -First $Count)
        $folder = Join-Path $EvalAssets "fleurs\$language"; New-Item -ItemType Directory -Force -Path $folder | Out-Null
        $missing = @($chosen | Where-Object { -not (Test-Path -LiteralPath (Join-Path $folder "dev\$($_.File)")) })
        if ($missing.Count -gt 0) {
            Write-Host "Extracting $($missing.Count) FLEURS $language recordings..."
            & (Join-Path $env:SystemRoot 'System32\tar.exe') -xzf $archive -C $folder @($missing | ForEach-Object { "dev/$($_.File)" })
            if ($LASTEXITCODE -ne 0) { throw "Could not extract FLEURS $language recordings." }
        }
        foreach ($row in $chosen) {
            $items += [pscustomobject]@{ Language = $language; Clip = (Join-Path $folder "dev\$($row.File)"); Text = (Get-MarkedReference $row.Text $language); Gender = $row.Gender; Name = "fleurs-$language-$($row.File.Replace('.wav', ''))" }
        }
    }
    $items
}

# The installed engine is only read; evaluations always write their own settings into an isolated data folder.
function Get-InstalledEngine {
    $file = Join-Path $env:LOCALAPPDATA 'PrivateFlow\settings.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "No Private Flow settings at $file. Run scripts\setup-engine.ps1 first." }
    $owner = Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($path in $owner.EnginePath, $owner.ModelPath, $owner.VadModelPath) {
        if (-not $path -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Engine, model or speech detection model missing. Run scripts\setup-engine.ps1 first.' }
    }
    $backend = $(if ($owner.EnginePath -match 'cuda') { 'cuda12' } else { 'cpu' })
    $meeting = $(if ($owner.MeetingModelPath -and (Test-Path -LiteralPath $owner.MeetingModelPath -PathType Leaf)) { $owner.MeetingModelPath } else { '' })
    [pscustomobject]@{ Label = "$backend-$([IO.Path]::GetFileNameWithoutExtension($owner.ModelPath).Replace('ggml-', ''))" + $(if ($meeting) { '+' + [IO.Path]::GetFileNameWithoutExtension($meeting).Replace('ggml-', '') })
        EnginePath = $owner.EnginePath; ModelPath = $owner.ModelPath; MeetingModelPath = $meeting; VadModelPath = $owner.VadModelPath }
}
# MeetingModelPath is the final-pass model for meetings and files; empty uses ModelPath.
function Resolve-Engine([string]$EnginePath, [string]$ModelPath, [string]$VadModelPath, [string]$Label, [string]$MeetingModelPath = '') {
    if (-not $EnginePath) { return Get-InstalledEngine }
    [pscustomobject]@{ Label = $Label; EnginePath = $EnginePath; ModelPath = $ModelPath; MeetingModelPath = $MeetingModelPath; VadModelPath = $VadModelPath }
}
# Extra adds settings fields, such as the dictation Vocabulary or Corrections the meeting prompt merges.
function Write-EvalSettings([string]$Data, $Engine, [string]$Language, [bool]$SpeechDetection, [bool]$KeepModelWarm = $true, [hashtable]$Extra = @{}) {
    New-Item -ItemType Directory -Force -Path $Data | Out-Null
    $settings = @{ SettingsVersion = 5; EnginePath = $Engine.EnginePath; ModelPath = $Engine.ModelPath; MeetingModelPath = "$($Engine.MeetingModelPath)"; VadModelPath = $Engine.VadModelPath; SpeechPathsCustom = $true
        Language = $Language; SpeechDetection = $SpeechDetection; KeepModelWarm = $KeepModelWarm; AutoInsert = $false }
    foreach ($key in $Extra.Keys) { $settings[$key] = $Extra[$key] }
    # UTF-8 so names such as Joaquín survive; Windows PowerShell's default would write the ANSI code page.
    ConvertTo-Json -InputObject $settings -Depth 4 | Set-Content -LiteralPath (Join-Path $Data 'settings.json') -Encoding UTF8
}
# One desktop run per Windows session (AGENTS.md): every entry point that takes the foreground, the clipboard or the keyboard
# (every desktop suite and every desktop job of verify.ps1) runs its work through here, under a mutex of this session (Local\). The
# holder's child processes inherit PRIVATE_FLOW_DESKTOP_SESSION, its process ID, and run under its lock instead of waiting for it.
# A holder that ended without releasing leaves the mutex abandoned, which counts as free.
function Invoke-DesktopSession([scriptblock]$Action, [TimeSpan]$Wait = [TimeSpan]::FromHours(2)) {
    $holder = "$env:PRIVATE_FLOW_DESKTOP_SESSION"
    if ($holder -match '^\d+$' -and (Get-Process -Id ([int]$holder) -ErrorAction SilentlyContinue)) { return & $Action }
    $lock = New-Object Threading.Mutex($false, 'Local\PrivateFlow-desktop-session')
    try { $owned = $lock.WaitOne($Wait) } catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { $lock.Dispose(); throw "Another desktop run held this Windows session for $([int]$Wait.TotalMinutes) minutes; nothing ran." }
    $env:PRIVATE_FLOW_DESKTOP_SESSION = "$PID"
    try { & $Action } finally { $env:PRIVATE_FLOW_DESKTOP_SESSION = $holder; $lock.ReleaseMutex(); $lock.Dispose() }
}
function Get-EvalApp([string]$App) {
    if (-not $App) { $App = Join-Path $EvalArtifacts 'app\PrivateFlow.exe' }
    if (-not (Test-Path -LiteralPath $App -PathType Leaf)) { throw "No Private Flow package at $App. Run scripts\build.ps1 -Portable first." }
    $App
}
# The verification host, PrivateFlow.Verification.exe, runs the verification and evaluation modes; the package no longer has them. It
# is built beside the package in artifacts\verification. Given the package's PrivateFlow.exe, as these scripts took before, the host
# built beside that package is used.
function Get-VerificationHost([string]$App) {
    if (-not $App) { $App = Join-Path $EvalArtifacts 'verification\PrivateFlow.Verification.exe' }
    elseif ([IO.Path]::GetFileName($App) -eq 'PrivateFlow.exe') { $App = Join-Path (Split-Path (Split-Path $App -Parent) -Parent) 'verification\PrivateFlow.Verification.exe' }
    if (-not (Test-Path -LiteralPath $App -PathType Leaf)) { throw "No verification host at $App. Run scripts\build.ps1 -Portable first." }
    # The host beside a package checks that package's build only.
    $taskPackage = Join-Path (Split-Path (Split-Path $App -Parent) -Parent) 'app\PrivateFlow.dll'
    if ((Test-Path -LiteralPath $taskPackage) -and (Get-FileHash -LiteralPath $taskPackage).Hash -ne (Get-FileHash -LiteralPath (Join-Path (Split-Path $App -Parent) 'PrivateFlow.dll')).Hash) {
        throw "The verification host at $App was built from another build than the package beside it. Run scripts\build.ps1 -Portable again."
    }
    $App
}
# Runs a gated mode of the verification host against an isolated data folder and returns its JSON report.
function Invoke-EvalMode([string]$App, [string]$Mode, [string]$InputPath, [string]$Report, [string]$Data, [int]$TimeoutMinutes = 120, [string[]]$Extra = @()) {
    if (Test-Path -LiteralPath $Report) { Remove-Item -LiteralPath $Report }
    $previous = $env:PRIVATE_FLOW_DATA_DIR
    try {
        $env:PRIVATE_FLOW_DATA_DIR = $Data
        $process = Start-Process -FilePath $App -ArgumentList (@($Mode, "`"$InputPath`"", "`"$Report`"") + $Extra) -WindowStyle Hidden -PassThru
    } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
    if (-not $process.WaitForExit($TimeoutMinutes * 60000)) { $process.Kill(); throw "$Mode timed out." }
    if (-not (Test-Path -LiteralPath $Report)) { throw "$Mode wrote no report (exit code $($process.ExitCode))." }
    $result = Get-Content -LiteralPath $Report -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $result.Passed) { throw "$Mode failed: $($result.Error)" }
    $result
}
# Your own recordings: counts only, never text. Without reference.txt the words are agreement with a pinned earlier transcript, not accuracy.
function Get-RecordingLines($result) {
    foreach ($recording in @($result.Recordings)) {
        $words = $recording.Words
        $scored = $(if ($words) { 'words {0:0.0} % ({1}/{2}), insertions {3}' -f (100 * $words.WordRecall), ($words.ReferenceWords - $words.Substitutions - $words.Deletions), $words.ReferenceWords, $words.Insertions } else { 'not scored' })
        $items = $(if ($recording.CriticalItems) { ", critical $($recording.CriticalRecalled)/$($recording.CriticalItems)" } else { '' })
        $segments = (@($recording.Segments.PSObject.Properties | ForEach-Object { "$($_.Name) $($_.Value)" }) -join ', ')
        "RECORDING $($recording.Name), $($recording.Scoring): $scored$items; segments $(if ($segments) { $segments } else { 'none' })"
        "RECORDING $($recording.Name) finishing: $($recording.Chunks) chunks, status $($recording.Status)$(if ($recording.Failure) { " ($($recording.Failure))" }) in $($recording.FinishSeconds) s, progress reached 100 %: $(if ($recording.ProgressReached100) { 'yes' } else { 'no' }), audio files left with retention off: $($recording.AudioFilesLeft)"
    }
}
function Format-Percent($Value) { if ($null -eq $Value) { '-' } else { '{0:0.0} %' -f (100 * $Value) } }

# Meeting goals (docs\VALIDATION.md). Sets: synthetic (generated speech, mixed-channel included), mixed-channel (a show
# discussion in each layout) and real (FLEURS, each distinct recording once: the "-show" variant repeats the same speech).
function Get-MeetingSet($scenario) { if ($scenario.Name -like 'real-*') { 'real' } else { 'synthetic' } }
function Select-MeetingSet($report, [string]$set) {
    @($report.Scenarios | Where-Object { $scenario = $_; switch ($set) {
        'mixed-channel' { $scenario.Name -like 'mixed-channel-*' }
        'real' { (Get-MeetingSet $scenario) -eq 'real' -and $scenario.Name -notlike 'real-*-show' }
        default { (Get-MeetingSet $scenario) -eq $set } } })
}
# Words recalled, insertions and critical items of a group of scenarios, all speech or a tag group such as "people" or "show".
function Get-MeetingTally($scenarios, [string]$group = 'all') {
    $tally = [ordered]@{ Words = 0; Matched = 0; Insertions = 0; Items = 0; Recalled = 0 }
    foreach ($scenario in $scenarios) {
        $entry = @($scenario.Tags | Where-Object Name -eq $group) | Select-Object -First 1
        if (-not $entry) { continue }
        $tally.Words += $entry.Words.ReferenceWords; $tally.Matched += $entry.Words.ReferenceWords - $entry.Words.Substitutions - $entry.Words.Deletions
        $tally.Insertions += $entry.Words.Insertions; $tally.Items += $entry.CriticalItems; $tally.Recalled += $entry.CriticalRecalled
    }
    [pscustomobject]$tally
}
function Format-Share([int]$part, [int]$whole) { if ($whole -eq 0) { '-' } else { '{0}/{1} = {2:0.0} %' -f $part, $whole, (100 * $part / $whole) } }
function Format-Status([bool]$met) { if ($met) { 'MET' } else { 'NOT MET' } }
function Get-WordRecall($tally) { if ($tally.Words) { $tally.Matched / $tally.Words } else { $null } }
# Insertions per channel of a set, next to recall: words in the transcript that the reference does not have.
function Get-InsertionLine($scenarios) {
    $channels = @($scenarios | ForEach-Object { $_.Channels } | Group-Object Channel | ForEach-Object { "$($_.Name) $(($_.Group | ForEach-Object { $_.Words.Insertions } | Measure-Object -Sum).Sum)" })
    "insertions $($channels -join ', ')"
}
# Every critical item of a set with whether it was recalled.
function Get-CriticalItems($scenarios) {
    foreach ($scenario in $scenarios) {
        $missed = @{}; foreach ($miss in $scenario.Missed) { $missed["$($miss.Channel)|$($miss.Start)|$($miss.Item)"] = $true }
        foreach ($utterance in $scenario.Utterances) {
            foreach ($item in @($utterance.Critical)) { if ($item) {
                [pscustomobject]@{ Item = $item; Recalled = -not $missed["$($utterance.Channel)|$($utterance.Start)|$item"] } } }
        }
    }
}
# The goal lines of one final-pass report; the oracle and vocabulary reports add goals 1 and 3 when given.
function Get-MeetingGoalLines($report, $oracle = $null, $vocabulary = $null) {
    $lines = New-Object System.Collections.Generic.List[string]
    if ($oracle) {
        $met = $true
        foreach ($set in 'synthetic', 'mixed-channel', 'real') {
            $final = @(Select-MeetingSet $report $set); $names = @($final | ForEach-Object Name)
            $reference = @(Select-MeetingSet $oracle $set | Where-Object { $names -contains $_.Name })
            if (-not $final.Count -or -not $reference.Count) { $lines.Add("GOAL 1 pipeline vs oracle, ${set}: OPEN (no scenarios)"); $met = $false; continue }
            $pipeline = Get-WordRecall (Get-MeetingTally $final); $ceiling = Get-WordRecall (Get-MeetingTally $reference)
            $setMet = $pipeline -ge $ceiling - 0.005; $met = $met -and $setMet
            $lines.Add(('GOAL 1 pipeline vs oracle, {0}: final {1:0.0} % vs oracle {2:0.0} % words ({3:+0.0;-0.0} points; target at most 0.5 below) - {4}' -f $set, (100 * $pipeline), (100 * $ceiling), (100 * ($pipeline - $ceiling)), (Format-Status $setMet)))
        }
        $lines.Add("GOAL 1 overall - $(Format-Status $met)")
    }
    $findable = @($report.Scenarios | ForEach-Object { $scenario = $_; $_.Findability | ForEach-Object { $_ | Add-Member -NotePropertyName Scenario -NotePropertyValue $scenario.Name -PassThru -Force } })
    $produced = @($findable | Where-Object Produced)
    if ($produced.Count) {
        $found = @($produced | Where-Object Found)
        $lines.Add("GOAL 2 findability of produced times and numbers (search in typed forms, times in the notes): $(Format-Share $found.Count $produced.Count) (target 100 %) - $(Format-Status ($found.Count -eq $produced.Count))")
        foreach ($miss in $produced | Where-Object { -not $_.Found } | Select-Object -First 12) {
            $lines.Add("  not found: $($miss.Scenario) $($miss.Start) s '$($miss.Item)'$(if (@($miss.NotFoundBy).Count) { " by '$(@($miss.NotFoundBy) -join "', '")'" })$(if (-not $miss.InNotes) { ', not in the notes' })")
        }
    }
    if ($vocabulary) {
        $synthetic = @(Select-MeetingSet $vocabulary 'synthetic')
        $terms = @("$($vocabulary.Prompt)" -split ',\s*' | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
        $items = @(Get-CriticalItems $synthetic); $named = @($items | Where-Object { $terms -contains $_.Item.ToLowerInvariant() })
        $all = @($items | Where-Object Recalled).Count; $names = @($named | Where-Object Recalled).Count
        $lines.Add("GOAL 3 vocabulary supplied (prompt '$($vocabulary.Prompt)'), synthetic: critical $(Format-Share $all $items.Count) - $(Format-Status ($items.Count -and $all / $items.Count -ge 0.95)); vocabulary names $(Format-Share $names $named.Count) - $(Format-Status ($named.Count -and $names / $named.Count -ge 0.95)) (targets 95 %)")
    }
    $real = Get-MeetingTally (Select-MeetingSet $report 'real')
    if ($real.Words) {
        $recall = Get-WordRecall $real; $critical = $real.Recalled / [math]::Max(1, $real.Items)
        $lines.Add(('GOAL 4 real speech, distinct recordings: words {0:0.0} % (target 97 %), critical {1} (target 93 %) - {2}' -f (100 * $recall), (Format-Share $real.Recalled $real.Items), (Format-Status ($recall -ge 0.97 -and $critical -ge 0.93))))
    }
    $owner = @(Select-MeetingSet $report 'mixed-channel')
    if ($owner.Count) {
        $layouts = [ordered]@{ 'show on the system channel' = @($owner | Where-Object { $_.Name -notlike '*-shared-system' -and $_.Name -notlike '*-mic-only' }); 'shared system-channel (two people and the show on the system channel)' = @($owner | Where-Object Name -like '*-shared-system') }
        $met = $true
        foreach ($layout in $layouts.Keys) {
            $names = @($layouts[$layout] | ForEach-Object Name)
            $times = @($findable | Where-Object { $names -contains $_.Scenario -and $_.Kind -ne 'number' -and @($_.Tags) -notcontains 'show' })
            if (-not $times.Count) { continue }
            # Found by search and in the notes, however the recognizer wrote the time; the strict count is the reference's written form.
            $found = @($times | Where-Object Found).Count; $written = @($times | Where-Object Written).Count; $layoutMet = $found / $times.Count -ge 0.9; $met = $met -and $layoutMet
            $lines.Add("GOAL 5 mixed-channel spoken times found by search and notes, people only, ${layout}: $(Format-Share $found $times.Count) (target 90 %) - $(Format-Status $layoutMet); written as the time: $(Format-Share $written $times.Count)")
        }
        $lines.Add("GOAL 5 overall - $(Format-Status $met)")
    }
    # Replies on the microphone that repeat what the system channel just said must survive echo removal.
    foreach ($scenario in @($report.Scenarios | Where-Object { @($_.Tags | Where-Object Name -eq 'confirmation').Count })) {
        $replies = Get-MeetingTally @($scenario) 'confirmation'
        $lines.Add(('Replies repeating the system channel, {0}: words {1:0.0} %, critical {2}; {3}' -f $scenario.Name, (100 * (Get-WordRecall $replies)), (Format-Share $replies.Recalled $replies.Items), (Get-InsertionLine @($scenario))))
    }
    foreach ($set in 'synthetic', 'mixed-channel', 'real') {
        $scenarios = @(Select-MeetingSet $report $set); if (-not $scenarios.Count) { continue }
        $all = Get-MeetingTally $scenarios; $people = Get-MeetingTally $scenarios 'people'; $show = Get-MeetingTally $scenarios 'show'
        $lines.Add(('Set {0} ({1} scenarios): words {2:0.0} %, critical {3}; people {4:0.0} %, critical {5}{6}; {7}' -f $set, $scenarios.Count, (100 * (Get-WordRecall $all)), (Format-Share $all.Recalled $all.Items),
            (100 * (Get-WordRecall $people)), (Format-Share $people.Recalled $people.Items), $(if ($show.Words) { '; show {0:0.0} %, critical {1}' -f (100 * (Get-WordRecall $show)), (Format-Share $show.Recalled $show.Items) } else { '' }), (Get-InsertionLine $scenarios)))
    }
    $lines
}
