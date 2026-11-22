param([string]$Checkout = '', [string]$App = '')
# Resolved here, as Windows PowerShell 5.1 can leave $PSScriptRoot empty in parameter defaults.
if (-not $Checkout) { $Checkout = Split-Path $PSScriptRoot -Parent }
# Runs --verify-worker-faults of a checkout's verification host, built beside its package, with an isolated data folder under
# artifacts\worker-faults: a speech worker that ends, hangs, refuses for want of GPU memory or answers with an error on cue, and a working one after each. About two
# minutes, most of it the 60 s a hung worker is given while loading.
$ErrorActionPreference = 'Stop'
. (Join-Path $Checkout 'scripts\eval-common.ps1')
$output = Join-Path $EvalOutput 'worker-faults'
$data = Join-Path $output 'data'; $report = Join-Path $output 'report.json'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
Write-EvalSettings $data (Get-InstalledEngine) 'auto' $true
# Given the package (as verify.ps1 passes it), the verification host built beside it.
$app = $(if ($App) { Get-VerificationHost $App } else { Join-Path $Checkout 'artifacts\verification\PrivateFlow.Verification.exe' })
$previous = $env:PRIVATE_FLOW_DATA_DIR
try { $env:PRIVATE_FLOW_DATA_DIR = $data; $process = Start-Process -FilePath $app -ArgumentList @('--verify-worker-faults', "`"$report`"") -WindowStyle Hidden -PassThru } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
if (-not $process.WaitForExit(600000)) { $process.Kill(); throw 'The worker fault check timed out.' }
$result = Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($fault in $result.Faults | Where-Object fault -eq 'idle-unload') { Write-Host ('{0} idle-unload: warm after 299 s {1}, unloaded after 301 s {2}' -f $(if ($fault.passed) { 'PASS' } else { 'FAIL' }), $fault.warmAfter299Seconds, (-not $fault.warmAfter301Seconds)) }
foreach ($fault in $result.Faults | Where-Object fault -ne 'idle-unload') { Write-Host ('{0} {1}: {2} "{3}", next worker {4} ({5} s)' -f $(if ($fault.passed) { 'PASS' } else { 'FAIL' }), $fault.fault, $fault.error, $fault.message, $(if ($fault.recovered) { 'works' } else { 'failed' }), $fault.seconds) }
if (-not $result.Passed) { exit 1 }
