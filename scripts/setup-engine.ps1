[CmdletBinding()]
# -Download <model> only downloads and verifies one speech model of models.json into the models folder; it changes no setting.
# -Recommend prints, as one line of JSON, what these arguments would install and whether the engine build is in place, and downloads nothing.
# -ResultFile, passed by the app, receives the installed settings as JSON instead of settings.json, which only the app writes then.
param([ValidateSet('auto','cpu','cuda12')][string]$Backend = 'auto', [ValidateSet('auto','large-v3-turbo-q8_0','medium','base')][string]$Model = 'auto', [string]$SetupVersion = '',
    [ValidateSet('none','large-v3-turbo-q8_0','medium','base','large-v3','silero-v6.2.0')][string]$Download = 'none', [switch]$Recommend, [string]$ResultFile = '',
    [string]$DataRoot = $(if ($env:PRIVATE_FLOW_DATA_DIR) { $env:PRIVATE_FLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PrivateFlow' }))
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($DataRoot -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') { throw 'DataRoot / PRIVATE_FLOW_DATA_DIR must be an absolute path.' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
# Names, sources, sizes and hashes of every pinned download, in one file the app reads as well.
$taskCatalogue = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'models.json') -Raw -Encoding UTF8 | ConvertFrom-Json
# Run without the app, the settings step goes through the locks Private Flow shares (settings-file.ps1); with Private Flow running on
# the data folder, nothing is downloaded or changed.
. (Join-Path $PSScriptRoot 'settings-file.ps1')
if (-not $ResultFile -and -not $Recommend -and $Download -eq 'none' -and (Test-PrivateFlowRunning $DataRoot)) { throw "$PrivateFlowRunningMessage Nothing was downloaded or changed." }
function Get-Pinned([string]$Key) {
    $taskFound = @($taskCatalogue.Models) + @($taskCatalogue.Runtimes) | Where-Object { $_.Key -eq $Key } | Select-Object -First 1
    if (-not $taskFound) { throw "models.json has no entry $Key." }
    $taskFound
}
# Saved as the preference: 'auto' keeps following the recommendation below after app updates.
$taskRequestedBackend = $Backend
$taskRequestedModel = $Model
# Medium and large-v3-turbo recognize names and unusual pronunciation better than base but need the
# GPU: on an NVIDIA GPU, 20 s of speech took medium 2.5 s with CUDA and 14 s on the CPU (base on the CPU: 1.6 s).
# On an 8 GB NVIDIA GPU, large-v3-turbo-q8_0 transcribed 40 s of English and German in 0.6 s against
# 1.15 s for medium, loaded in 0.7 s against 1.25 s and matched its accuracy on generated speech.
# Automatic on the GPU adds large-v3 (3.1 GB) for the meeting and file final pass: it lost about a quarter fewer words
# than large-v3-turbo on generated meetings and as many on real speech, but takes twice as long, which dictation cannot afford.
if ($Backend -eq 'auto') { $Backend = if (Get-Command nvidia-smi -ErrorAction SilentlyContinue) { 'cuda12' } else { 'cpu' } }
if ($Model -eq 'auto') { $Model = if ($Backend -eq 'cuda12') { 'large-v3-turbo-q8_0' } else { 'base' } }
$taskAsset = Get-Pinned "whisper-$Backend"
$taskMeetingKey = if ($taskRequestedModel -eq 'auto' -and $Backend -eq 'cuda12') { 'large-v3' } else { '' }
if ($Recommend) {
    $taskMarker = Join-Path $DataRoot "runtime\$Backend\.archive-sha256"
    $taskEngineInstalled = (Test-Path -LiteralPath $taskMarker) -and ((Get-Content -LiteralPath $taskMarker -Raw).Trim() -eq $taskAsset.Sha256)
    [ordered]@{ Backend = $Backend; Model = $Model; MeetingModel = $taskMeetingKey; VoiceDetection = 'silero-v6.2.0'; EngineInstalled = [bool]$taskEngineInstalled } | ConvertTo-Json -Compress
    return
}
$taskRuntime = Join-Path $DataRoot "runtime\$Backend"
$taskModels = Join-Path $DataRoot 'models'
$taskCache = Join-Path $DataRoot 'downloads'
function Get-VerifiedDownload([string]$Url,[string]$File,[string]$Hash) {
    if ((Test-Path -LiteralPath $File) -and ((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -eq $Hash)) { return }
    Write-Host "Downloading $([IO.Path]::GetFileName($File))..."
    $taskPartial = "$File.partial"
    Invoke-WebRequest -Uri $Url -OutFile $taskPartial
    if ((Get-FileHash -LiteralPath $taskPartial -Algorithm SHA256).Hash -ne $Hash) { throw "Checksum mismatch for $File; file was not installed." }
    Move-Item -LiteralPath $taskPartial -Destination $File -Force
}
if ($Download -ne 'none') {
    New-Item -ItemType Directory -Force -Path $taskModels | Out-Null
    $taskPinned = Get-Pinned $Download
    Get-VerifiedDownload $taskPinned.Url (Join-Path $taskModels $taskPinned.File) $taskPinned.Sha256
    Write-Host "$($taskPinned.Name) is downloaded and verified."
    return
}
New-Item -ItemType Directory -Force -Path $taskRuntime,$taskModels,$taskCache | Out-Null
$taskMissingBytes = 0
foreach ($taskEntry in @(@{ Pinned = $taskAsset; Folder = $taskCache }, @{ Pinned = (Get-Pinned $Model); Folder = $taskModels },
        @{ Pinned = $(if ($taskMeetingKey) { Get-Pinned $taskMeetingKey }); Folder = $taskModels }, @{ Pinned = (Get-Pinned 'silero-v6.2.0'); Folder = $taskModels })) {
    if (-not $taskEntry.Pinned) { continue }
    $taskExisting = Get-Item -LiteralPath (Join-Path $taskEntry.Folder $taskEntry.Pinned.File) -ErrorAction SilentlyContinue
    if (-not $taskExisting -or $taskExisting.Length -ne [long]$taskEntry.Pinned.Bytes) { $taskMissingBytes += [long]$taskEntry.Pinned.Bytes }
}
$taskInvariant = [Globalization.CultureInfo]::InvariantCulture
if ($taskMissingBytes -ge 1e9) { Write-Host ([string]::Format($taskInvariant, 'Download size: {0:0.0} GB', $taskMissingBytes / 1e9)) }
elseif ($taskMissingBytes -gt 0) { Write-Host ([string]::Format($taskInvariant, 'Download size: {0:0} MB', $taskMissingBytes / 1e6)) }
$taskHash = $taskAsset.Sha256
$taskArchive = Join-Path $taskCache $taskAsset.File
Get-VerifiedDownload $taskAsset.Url $taskArchive $taskHash
# Re-extracting an unchanged archive would overwrite DLLs a running speech worker may hold.
$taskExtracted = Join-Path $taskRuntime '.archive-sha256'
if (-not (Test-Path -LiteralPath $taskExtracted) -or (Get-Content -LiteralPath $taskExtracted -Raw).Trim() -ne $taskHash) {
    # Installs from before the marker already hold this archive's files: every entry on disk with its size.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
    try {
        $taskDifferent = $taskZip.Entries | Where-Object { $_.Name } | Where-Object {
            $taskFile = Get-Item -LiteralPath (Join-Path $taskRuntime $_.FullName) -ErrorAction SilentlyContinue
            -not $taskFile -or $taskFile.Length -ne $_.Length
        } | Select-Object -First 1
    } finally { $taskZip.Dispose() }
    if ($taskDifferent) { Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskRuntime -Force }
    Set-Content -LiteralPath $taskExtracted -Value $taskHash -Encoding ascii
}
$taskExe = Get-ChildItem -LiteralPath $taskRuntime -Filter whisper-cli.exe -Recurse | Select-Object -First 1
if (-not $taskExe) { throw 'Verified archive did not contain whisper-cli.exe.' }
$taskPinned = Get-Pinned $Model
$taskModel = Join-Path $taskModels $taskPinned.File
Get-VerifiedDownload $taskPinned.Url $taskModel $taskPinned.Sha256
$taskMeetingModel = ''
if ($taskMeetingKey) {
    $taskPinned = Get-Pinned $taskMeetingKey
    $taskMeetingModel = Join-Path $taskModels $taskPinned.File
    Get-VerifiedDownload $taskPinned.Url $taskMeetingModel $taskPinned.Sha256
}
$taskPinned = Get-Pinned 'silero-v6.2.0'
$taskVad = Join-Path $taskModels $taskPinned.File
Get-VerifiedDownload $taskPinned.Url $taskVad $taskPinned.Sha256
$taskInstalled = [ordered]@{ EnginePath = $taskExe.FullName; ModelPath = $taskModel; MeetingModelPath = $taskMeetingModel; VadModelPath = $taskVad
    SpeechBackend = $taskRequestedBackend; SpeechModel = $taskRequestedModel; SpeechPathsCustom = $false }
# The app passes its build identity so it reapplies Automatic only after the next update.
if ($SetupVersion) { $taskInstalled.SpeechSetupVersion = $SetupVersion }
if ($ResultFile) { $taskInstalled | ConvertTo-Json -Compress | Set-Content -LiteralPath $ResultFile -Encoding UTF8 }
else { Save-InstalledSettings $DataRoot $taskInstalled }
Write-Host "Local $Backend engine and multilingual $Model model are ready.$(if ($taskMeetingModel) { ' Meetings and files get a final pass with large-v3.' })"
Write-Host "Engine: $($taskExe.FullName)"
Write-Host 'No network is needed for transcription after setup. Restart Private Flow if it was already open.'
