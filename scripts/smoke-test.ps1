[CmdletBinding()]
param([string]$App = '')
$ErrorActionPreference = 'Stop'
# Resolved here: Windows PowerShell 5.1 leaves $PSScriptRoot empty in the defaults of a [CmdletBinding()] script's parameters.
if (-not $App) { $App = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\app\PrivateFlow.exe' }
if ($env:PRIVATE_FLOW_DATA_DIR -and $env:PRIVATE_FLOW_DATA_DIR -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') { throw 'PRIVATE_FLOW_DATA_DIR must be an absolute path.' }
$taskRoot = Join-Path $(if ($env:PRIVATE_FLOW_EVAL_OUTPUT) { $env:PRIVATE_FLOW_EVAL_OUTPUT } else { Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts' }) 'smoke'
New-Item -ItemType Directory -Force -Path $taskRoot | Out-Null
$taskData = Join-Path $taskRoot 'data'
New-Item -ItemType Directory -Force -Path $taskData | Out-Null
$taskOwnerData = $(if ($env:PRIVATE_FLOW_DATA_DIR) { $env:PRIVATE_FLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PrivateFlow' })
$taskOwnerSettingsFile = Join-Path $taskOwnerData 'settings.json'
if (-not (Test-Path -LiteralPath $taskOwnerSettingsFile -PathType Leaf)) { throw "No Private Flow settings at $taskOwnerSettingsFile. Run scripts\setup-engine.ps1 first." }
$taskOwnerSettings = Get-Content -LiteralPath $taskOwnerSettingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($taskEngineFile in $taskOwnerSettings.EnginePath, $taskOwnerSettings.ModelPath) {
    if (-not $taskEngineFile -or -not (Test-Path -LiteralPath $taskEngineFile -PathType Leaf)) { throw 'Engine or model file missing. Run scripts\setup-engine.ps1 first.' }
}
$taskVad = $taskOwnerSettings.VadModelPath
if (-not $taskVad) {
    $taskVad = Join-Path $taskOwnerData 'models\ggml-silero-v6.2.0.bin'
    if (-not (Test-Path -LiteralPath $taskVad -PathType Leaf)) { $taskVad = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\workspace-verification\data\models\ggml-silero-v6.2.0.bin' }
}
if (-not (Test-Path -LiteralPath $taskVad -PathType Leaf)) { throw 'VAD model file missing. Run scripts\setup-engine.ps1 first.' }
@{SettingsVersion=4; EnginePath=$taskOwnerSettings.EnginePath; ModelPath=$taskOwnerSettings.ModelPath; VadModelPath=$taskVad; SpeechDetection=$true; AutoInsert=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskData 'settings.json')
# Generate synthetic speech, not microphone audio. Windows PowerShell hosts System.Speech.
$taskSpeechFile = Join-Path $taskRoot 'english.wav'
$taskGenerator = Join-Path $taskRoot 'generate-speech.ps1'
@'
param([string]$Output)
Add-Type -AssemblyName System.Speech
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.SelectVoiceByHints([System.Speech.Synthesis.VoiceGender]::NotSet,[System.Speech.Synthesis.VoiceAge]::NotSet,0,[Globalization.CultureInfo]::GetCultureInfo('en-US'))
$synth.SetOutputToWaveFile($Output)
$synth.Speak('This is a private voice test. Please do not delete the file. The number is forty two.')
$synth.Dispose()
'@ | Set-Content -LiteralPath $taskGenerator
& powershell.exe -NoProfile -File $taskGenerator -Output $taskSpeechFile
if ($LASTEXITCODE -ne 0) { throw 'Speech synthesis failed.' }
$taskText = Join-Path $taskRoot 'english.txt'
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
$taskArgs = @('--transcribe-file',"`"$taskSpeechFile`"","`"$taskText`"")
$taskPreviousData = $env:PRIVATE_FLOW_DATA_DIR
try {
    $env:PRIVATE_FLOW_DATA_DIR = $taskData
    $taskProcess = Start-Process -FilePath $App -ArgumentList $taskArgs -WindowStyle Hidden -PassThru
} finally { $env:PRIVATE_FLOW_DATA_DIR = $taskPreviousData }
if (-not $taskProcess.WaitForExit(120000)) { $taskProcess.Kill(); throw 'Speech smoke test timed out.' }
if ($taskProcess.ExitCode -ne 0) { throw "Speech test failed: $(Get-Content -LiteralPath $taskText -Raw)" }
$taskTranscript = Get-Content -LiteralPath $taskText -Raw
if ($taskTranscript -notmatch '(?i)private' -or $taskTranscript -notmatch '(?i)do not delete' -or $taskTranscript -notmatch '(?i)(forty.two|42)') { throw "Unexpected transcription: $taskTranscript" }
Write-Host "PASS synthetic speech with negation and number ($([Math]::Round($taskWatch.Elapsed.TotalSeconds,2)) seconds)"
Write-Host $taskTranscript
if ((Test-Path -LiteralPath (Join-Path $taskData 'temp')) -and (Get-ChildItem -LiteralPath (Join-Path $taskData 'temp') -File | Where-Object Extension -in '.wav','.txt')) { throw 'Temporary audio or transcript was not removed.' }
Write-Host 'PASS isolated native-worker transcription without temporary audio files'
