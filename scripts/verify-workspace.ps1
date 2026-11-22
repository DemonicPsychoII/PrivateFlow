param([string]$Checkout = '', [string]$Name = 'verify')
# Resolved here, as Windows PowerShell 5.1 can leave $PSScriptRoot empty in parameter defaults.
if (-not $Checkout) { $Checkout = Split-Path $PSScriptRoot -Parent }
# Runs the workspace verification (--verify-workspace) of a checkout's verification host, built beside its package (scripts\build.ps1 -Portable), with an
# isolated data folder under the checkout's artifacts\<Name>, using the installed speech engine read by eval-common.ps1.
# The output folder is deleted first, so only a simple name inside the checkout's artifacts folder is accepted.
if ($Name -notmatch '^[A-Za-z0-9_-]+$') { throw "-Name takes letters, digits, dashes and underscores only; got '$Name'." }
$artifacts = [IO.Path]::GetFullPath((Join-Path $Checkout 'artifacts'))
$output = [IO.Path]::GetFullPath((Join-Path $artifacts $Name))
if (-not $output.StartsWith($artifacts.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "$output is not inside $artifacts." }
. (Join-Path $Checkout 'scripts\eval-common.ps1')
$app = Join-Path $Checkout 'artifacts\verification\PrivateFlow.Verification.exe'
$engine = Get-InstalledEngine
$data = Join-Path $output 'data'; $report = Join-Path $output 'report'
if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Recurse -Force }
Write-EvalSettings $data $engine 'auto' $true
$previous = $env:PRIVATE_FLOW_DATA_DIR
try { $env:PRIVATE_FLOW_DATA_DIR = $data; $process = Start-Process -FilePath $app -ArgumentList @('--verify-workspace', "`"$report`"") -WindowStyle Hidden -PassThru } finally { $env:PRIVATE_FLOW_DATA_DIR = $previous }
if (-not $process.WaitForExit(2400000)) { $process.Kill(); throw 'Workspace verification timed out.' }
$result = Get-Content -LiteralPath (Join-Path $report 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
"Passed: $($result.Passed); checks: $(@($result.Checks).Count); error: $($result.Error)"
