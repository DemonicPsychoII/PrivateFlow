param([string]$Checkout = '', [string]$App = '')
# Resolved here, as Windows PowerShell 5.1 can leave $PSScriptRoot empty in parameter defaults.
if (-not $Checkout) { $Checkout = Split-Path $PSScriptRoot -Parent }
# Transcribe file with compressed audio (--verify-imports of the verification host): generated speech encoded by Windows' Media Foundation into mp3, m4a, wma
# and flac, each transcribed by the installed engine in an isolated data folder under artifacts\imports. Generated speech only.
$ErrorActionPreference = 'Stop'
. (Join-Path $Checkout 'scripts\eval-common.ps1')
$output = Join-Path $EvalOutput 'imports'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
$data = Join-Path $output 'data'; $report = Join-Path $output 'report.json'; $speech = Join-Path $output 'speech.wav'
Write-EvalSettings $data (Get-InstalledEngine) 'en' $true
Add-Type -AssemblyName System.Speech
$voice = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice.SetOutputToWaveFile($speech, (New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono)))
$voice.Speak('This is a private voice test. Please do not delete the file. The number is forty two.')
$voice.Dispose()
# Given the package (as verify.ps1 passes it), the verification host built beside it.
$app = $(if ($App) { Get-VerificationHost $App } else { Join-Path $Checkout 'artifacts\verification\PrivateFlow.Verification.exe' })
$previous = $env:PRIVATE_FLOW_DATA_DIR
try { $env:PRIVATE_FLOW_DATA_DIR = $data; $process = Start-Process -FilePath $app -ArgumentList @('--verify-imports', "`"$speech`"", "`"$report`"") -WindowStyle Hidden -PassThru } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'The import check timed out.' }
$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($format in $result.Formats) { Write-Host ('{0} {1} ({2} bytes): {3}{4}' -f $(if ($format.passed) { 'PASS' } else { 'FAIL' }), $format.format, $format.bytes, $format.text, $format.error) }
if (-not $result.Passed) { exit 1 }
