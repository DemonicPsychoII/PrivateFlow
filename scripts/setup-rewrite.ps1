# -Part notes installs the meeting notes model and a GPU build of llama.cpp instead of Local polish; both are optional.
# -ResultFile, passed by the app, receives the installed paths as JSON instead of settings.json, which only the app writes then.
[CmdletBinding()]
param([ValidateSet('polish','notes')][string]$Part = 'polish', [string]$ResultFile = '', [string]$DataRoot = $(if ($env:PRIVATE_FLOW_DATA_DIR) { $env:PRIVATE_FLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PrivateFlow' }))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($DataRoot -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') { throw 'DataRoot / PRIVATE_FLOW_DATA_DIR must be an absolute path.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
# Names, sources, sizes and hashes of every pinned download, in one file the app reads as well.
$taskCatalogue = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# Run without the app, the settings step goes through the locks Private Flow shares (settings-file.ps1); with Private Flow running on
# the data folder, nothing is downloaded or changed.
. (Join-Path $PSScriptRoot 'settings-file.ps1')
if (-not $ResultFile -and (Test-PrivateFlowRunning $DataRoot)) { throw "$PrivateFlowRunningMessage Nothing was downloaded or changed." }
function Get-Pinned([string]$Key) {
    $taskFound = @($taskCatalogue.Models) + @($taskCatalogue.Runtimes) | Where-Object { $_.Key -eq $Key } | Select-Object -First 1
    if (-not $taskFound) { throw "models.json has no entry $Key." }
    $taskFound
}
$taskRuntime = Join-Path $DataRoot $(if ($Part -eq 'notes') { 'runtime\notes' } else { 'runtime\rewrite' })
$taskModels = Join-Path $DataRoot 'models'
$taskCache = Join-Path $DataRoot 'downloads'
New-Item -ItemType Directory -Force -Path $taskRuntime,$taskModels,$taskCache | Out-Null
function Get-VerifiedDownload([string]$Url,[string]$File,[string]$Hash,[long]$Size=0) {
    if ((Test-Path -LiteralPath $File) -and ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -eq $Hash)) { return }
    Write-Host "Downloading $([IO.Path]::GetFileName($File))..."
    # Use a distinct cache key for these pinned downloads rather than a shared redirect.
    if ($Url.StartsWith('https://huggingface.co/')) { $Url += '&privateflow=20261013' }
    if ($Size -gt 0) {
        # Bounded range requests also work behind download proxies that buffer whole bodies.
        $taskOutput = [IO.File]::Open("$File.partial",[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {
            for ([long]$taskOffset=0; $taskOffset -lt $Size; $taskOffset+=16777216) {
                $taskEnd = [Math]::Min($Size-1,$taskOffset+16777215)
                & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 180 --range "$taskOffset-$taskEnd" --output "$File.range" $Url
                if ($LASTEXITCODE -ne 0 -or (Get-Item -LiteralPath "$File.range").Length -ne ($taskEnd-$taskOffset+1)) { throw 'Pinned range download failed; nothing installed.' }
                $taskPart = [IO.File]::OpenRead("$File.range")
                try { $taskPart.CopyTo($taskOutput) } finally { $taskPart.Dispose() }
                Write-Host "Local model download: $([Math]::Round(($taskEnd+1)*100.0/$Size))%"
            }
            $taskOutput.Flush($true)
        } finally { $taskOutput.Dispose(); if (Test-Path -LiteralPath "$File.range") { Remove-Item -LiteralPath "$File.range" } }
    } else {
        & curl.exe --fail --location --silent --show-error --retry 3 --connect-timeout 30 --max-time 1800 --output "$File.partial" $Url
        if ($LASTEXITCODE -ne 0) { throw 'Pinned download failed; nothing installed.' }
    }
    if ((Get-FileHash -LiteralPath "$File.partial" -Algorithm SHA256).Hash -ne $Hash) { throw 'Checksum mismatch; nothing installed.' }
    Move-Item -LiteralPath "$File.partial" -Destination $File -Force
}
if ($Part -eq 'notes') {
    # Meeting notes need the GPU: Qwen3.5-4B wrote notes for a 30-minute meeting in 20 to 33 s with the CUDA build on an 8 GB NVIDIA GPU
    # and in 4 to 5 minutes on the processor (docs/VALIDATION.md). Without an NVIDIA GPU the processor build is installed instead.
    $taskArchives = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { @((Get-Pinned 'llama-cuda12'), (Get-Pinned 'llama-cudart12')) } else { @(Get-Pinned 'llama-cpu') }
    $taskPinned = Get-Pinned 'qwen3.5-4b'
    $taskSettings = @('MeetingNotesEnginePath', 'MeetingNotesModelPath')
    $taskReady = 'Meeting notes model ready. Choose the local model for meeting notes; setup does not change the notes style.'
} else {
    $taskArchives = @(Get-Pinned 'llama-cpu')
    $taskPinned = Get-Pinned 'qwen3-4b'
    $taskSettings = @('LocalRewriteEnginePath', 'LocalRewriteModelPath')
    $taskReady = 'Local polish ready. Select Local polish explicitly; setup does not change output style.'
}
foreach ($taskEntry in $taskArchives) {
    $taskArchive = Join-Path $taskCache $taskEntry.File
    Get-VerifiedDownload $taskEntry.Url $taskArchive $taskEntry.Sha256
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskRuntime -Force
}
# The app waits for this line before it lets a local model run again; the model download that follows does not touch the runtime.
Write-Host 'Local model runtime is in place.'
$taskExe = Get-ChildItem -LiteralPath $taskRuntime -Filter llama-server.exe -Recurse | Select-Object -First 1
if (-not $taskExe) { throw 'Verified runtime did not contain llama-server.exe.' }
$taskModel = Join-Path $taskModels $taskPinned.File
Get-VerifiedDownload $taskPinned.Url $taskModel $taskPinned.Sha256 ([long]$taskPinned.Bytes)
$taskInstalled = [ordered]@{ $taskSettings[0] = $taskExe.FullName; $taskSettings[1] = $taskModel }
if ($ResultFile) { $taskInstalled | ConvertTo-Json -Compress | Set-Content -LiteralPath $ResultFile -Encoding UTF8 }
else { Save-InstalledSettings $DataRoot $taskInstalled }
Write-Host $taskReady
