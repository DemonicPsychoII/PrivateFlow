[CmdletBinding()]
param(
    # Folder containing PrivateFlow.exe. Defaults to the extracted package this script ships in,
    # or to artifacts\app in a repository checkout.
    [string]$Source,
    # Download the newest build of -CloudChannel from the update server instead.
    [switch]$FromCloud,
    # Download the latest stable release from the public repository.
    [switch]$FromRelease,
    # Allow an equal or older public release to replace an installed copy.
    [switch]$Force,
    # Store this user's update server reader token (prompted, never shown) for -FromCloud and the in-app update check.
    [switch]$SetCloudToken,
    # "integration" is the newest main build; a branch built with workflow_dispatch publishes "branch/<name>".
    [string]$CloudChannel = 'integration',
    # With -SetCloudToken, store the server and optional wake URL beside the token. Otherwise use the stored URLs.
    [string]$CloudUrl = '',
    [string]$CloudWakeUrl = '',
    [switch]$Autostart,
    [switch]$NoLaunch,
    # Also rerun the engine setup now. The app itself reapplies Automatic speech choices after an update.
    [switch]$UpdateEngine,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\PrivateFlow')
)
# SmartScreen only warns about files carrying the Mark-of-the-Web (a browser download).
# Installing a local build, a cloud download by this script, or files unblocked here never triggers it.
# Signing would remove the warning for browser downloads too; see README.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if ($env:PRIVATE_FLOW_DATA_DIR -and $env:PRIVATE_FLOW_DATA_DIR -notmatch '^(?:[A-Za-z]:[\\/]|[\\/]{2}[^\\/]+[\\/][^\\/]+)') { throw 'PRIVATE_FLOW_DATA_DIR must be an absolute path.' }
$taskDataRoot = $(if ($env:PRIVATE_FLOW_DATA_DIR) { $env:PRIVATE_FLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PrivateFlow' })
# Outside the repository and the package; DPAPI ties it to this Windows user. The app reads the same file.
$taskCloudTokenPath = Join-Path $taskDataRoot 'cloud-reader.token'
$taskCloudConfigurationPath = Join-Path $taskDataRoot 'update-server.json'

function Test-ServerUrl([string]$Value) {
    $serverUri = $null
    [Uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$serverUri) -and
        ($serverUri.Scheme -eq 'https' -or ($serverUri.Scheme -eq 'http' -and $serverUri.IsLoopback)) -and
        -not $serverUri.UserInfo -and -not $serverUri.Query -and -not $serverUri.Fragment
}

function Assert-ServerConfiguration([string]$ServerUrl, [string]$WakeUrl) {
    if (-not (Test-ServerUrl $ServerUrl) -or ($WakeUrl -and -not (Test-ServerUrl $WakeUrl))) {
        throw 'Set -CloudUrl to an HTTPS update server URL and optionally -CloudWakeUrl to its HTTPS wake URL (HTTP is allowed only on loopback). URLs must have no credentials, query or fragment.'
    }
}

function Read-CloudToken([string]$TokenPath) {
    if (-not (Test-Path -LiteralPath $TokenPath)) { throw 'No update server reader token is stored for this Windows user. Run install.ps1 -SetCloudToken first.' }
    try { $secureToken = (Get-Content -LiteralPath $TokenPath -Raw).Trim() | ConvertTo-SecureString }
    catch { throw 'The stored update server reader token cannot be decrypted by this Windows user. Run install.ps1 -SetCloudToken again.' }
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer) }
}

function Save-VerifiedPackage([string]$Url, [hashtable]$Headers, [long]$Size, [string]$Sha256, [string]$Digest, [string]$DownloadFolder) {
    if ($Size -le 0 -or $Sha256 -notmatch '\A[0-9a-fA-F]{64}\z' -or ($Digest -and $Digest -ine "sha256:$Sha256")) {
        throw 'The package listing has no valid size and matching SHA-256; nothing was installed.'
    }
    $packagePath = Join-Path $DownloadFolder 'PrivateFlow-win-x64.zip'
    Invoke-WebRequest -Uri $Url -Headers $Headers -OutFile $packagePath -TimeoutSec 900 -UseBasicParsing
    if ((Get-Item -LiteralPath $packagePath).Length -ne $Size) { throw 'The downloaded package size does not match its listing; nothing was installed.' }
    if ((Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash -ine $Sha256) { throw 'The downloaded package SHA-256 does not match its listing; nothing was installed.' }
    $packagePath
}

function Assert-NewerRelease([string]$OfferedVersion, [string]$InstallFolder, [switch]$Force) {
    if (-not $InstallFolder) { return }
    $installedExecutable = Join-Path $InstallFolder 'PrivateFlow.exe'
    if ($Force -or -not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) { return }
    $offered = [version]$OfferedVersion
    $installed = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    $installedVersion = New-Object Version $installed.FileMajorPart,$installed.FileMinorPart,$installed.FileBuildPart
    if ($offered -le $installedVersion) { throw "Release $offered is not newer than installed version $installedVersion; nothing was installed. Use -Force to allow a reinstall or downgrade." }
}

function Save-ReleaseBuild([string]$DownloadFolder, [string]$InstallFolder, [switch]$Force) {
    $requestHeaders = @{ 'User-Agent' = 'PrivateFlow-updater'; Accept = 'application/vnd.github+json' }
    try { $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/DemonicPsychoII/PrivateFlow/releases/latest' -Headers $requestHeaders -TimeoutSec 60 -UseBasicParsing }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            throw 'The public repository or its latest stable release could not be found (HTTP 404). The repository may not be public yet, or no stable release has been published.'
        }
        throw
    }
    if (-not $release -or $release.draft -or $release.prerelease -or $release.tag_name -notmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'No stable public release is available.' }
    Assert-NewerRelease -OfferedVersion $release.tag_name.Substring(1) -InstallFolder $InstallFolder -Force:$Force
    $packages = @($release.assets | Where-Object name -eq 'PrivateFlow-win-x64.zip')
    $manifests = @($release.assets | Where-Object name -eq 'manifest.json')
    if ($packages.Count -ne 1 -or $manifests.Count -ne 1) { throw 'The release must contain one package and one manifest.' }
    $package = $packages[0]; $manifestAsset = $manifests[0]
    $releaseBase = "https://github.com/DemonicPsychoII/PrivateFlow/releases/download/$($release.tag_name)"
    if ($package.browser_download_url -cne "$releaseBase/PrivateFlow-win-x64.zip" -or $manifestAsset.browser_download_url -cne "$releaseBase/manifest.json") { throw 'Unexpected release asset URL.' }
    $manifest = Invoke-RestMethod -Uri $manifestAsset.browser_download_url -Headers $requestHeaders -TimeoutSec 60 -UseBasicParsing
    $files = @($manifest.files | Where-Object name -eq 'PrivateFlow-win-x64.zip')
    if ($manifest.schema -ne 1 -or $manifest.project -cne 'private-flow' -or $manifest.board -cne 'win-x64' -or $files.Count -ne 1 -or
        $files[0].version -cne $release.tag_name.Substring(1) -or [long]$files[0].size -ne [long]$package.size) { throw 'The release manifest does not match the package and version.' }
    Write-Host "Downloading Private Flow $($release.tag_name)..."
    Save-VerifiedPackage -Url $package.browser_download_url -Headers $requestHeaders -Size $files[0].size -Sha256 $files[0].sha256 -Digest $package.digest -DownloadFolder $DownloadFolder
}

# Wakes the update server, then downloads the newest build of the channel into DownloadFolder and returns the
# ZIP path only after its size and SHA-256 match the cloud listing.
function Save-CloudBuild([string]$CloudUrl, [string]$CloudWakeUrl, [string]$Channel, [string]$Token, [string]$DownloadFolder) {
    $requestHeaders = @{ Authorization = "Bearer $Token" }
    $CloudUrl = $CloudUrl.TrimEnd('/')
    if ($CloudWakeUrl) {
        Write-Host 'Waking the update server...'
        # The wake call carries no token: the server rejects unrecognised credentials on its health route.
        try { Invoke-WebRequest -Uri $CloudWakeUrl -TimeoutSec 150 -UseBasicParsing | Out-Null }
        catch {
            if ($_.Exception.Response) { throw "The update server answered the wake request at $CloudWakeUrl with HTTP $([int]$_.Exception.Response.StatusCode); it may have failed to wake. Try again later." }
            throw "The update server did not become reachable through $CloudWakeUrl within 150 s ($($_.Exception.Message)). Check the server connection."
        }
    }
    try { $latestBuild = Invoke-RestMethod -Uri "$CloudUrl/api/firmware/latest?project=private-flow&channel=$([Uri]::EscapeDataString($Channel))" -Headers $requestHeaders -TimeoutSec 60 -UseBasicParsing }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) { throw "The update server has no '$Channel' build of Private Flow yet." }
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { throw 'The update server rejected the reader token; store a new one with -SetCloudToken.' }
        throw "Could not query the update server for the newest '$Channel' build ($($_.Exception.Message))."
    }
    $packageListing = @($latestBuild.files | Where-Object { $_.name -eq 'PrivateFlow-win-x64.zip' })
    if ($packageListing.Count -ne 1) { throw "The newest '$Channel' build does not list exactly one PrivateFlow-win-x64.zip." }
    $packageListing = $packageListing[0]
    if ([string]$packageListing.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$packageListing.size -le 0) { throw "The newest '$Channel' build lists no valid size and SHA-256 for its package." }
    Write-Host "Downloading Private Flow $($latestBuild.version), commit $($latestBuild.commit), built $($latestBuild.builtAt)..."
    Save-VerifiedPackage -Url "$CloudUrl/api/firmware/files/$([Uri]::EscapeDataString([string]$packageListing.id))/download" -Headers $requestHeaders -Size $packageListing.size -Sha256 $packageListing.sha256 -DownloadFolder $DownloadFolder
}

if ($FromCloud -and $FromRelease) { throw 'Choose either -FromCloud or -FromRelease.' }
if ($SetCloudToken) {
    Assert-ServerConfiguration $CloudUrl $CloudWakeUrl
    $taskSecureToken = Read-Host 'Update server reader token (Enter keeps the stored token)' -AsSecureString
    if ($taskSecureToken.Length -eq 0) { $null = Read-CloudToken $taskCloudTokenPath }
    New-Item -ItemType Directory -Force -Path $taskDataRoot | Out-Null
    if ($taskSecureToken.Length -gt 0) {
        ConvertFrom-SecureString -SecureString $taskSecureToken | Set-Content -LiteralPath "$taskCloudTokenPath.partial" -Encoding ascii
        Move-Item -LiteralPath "$taskCloudTokenPath.partial" -Destination $taskCloudTokenPath -Force
    }
    @{ CloudUrl = $CloudUrl.TrimEnd('/'); CloudWakeUrl = $CloudWakeUrl } | ConvertTo-Json | Set-Content -LiteralPath "$taskCloudConfigurationPath.partial" -Encoding UTF8
    Move-Item -LiteralPath "$taskCloudConfigurationPath.partial" -Destination $taskCloudConfigurationPath -Force
    Write-Host "Stored the reader token for this Windows user, DPAPI-encrypted, in $taskCloudTokenPath"
    if (-not $FromCloud -and -not $FromRelease) { return }
}

$taskTemp = $null
try {
    if ($FromCloud) {
        try {
            if (Test-Path -LiteralPath $taskCloudConfigurationPath) {
                $taskCloudConfiguration = Get-Content -LiteralPath $taskCloudConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if (-not $PSBoundParameters.ContainsKey('CloudUrl')) { $CloudUrl = [string]$taskCloudConfiguration.CloudUrl }
                if (-not $PSBoundParameters.ContainsKey('CloudWakeUrl')) { $CloudWakeUrl = [string]$taskCloudConfiguration.CloudWakeUrl }
            }
            Assert-ServerConfiguration $CloudUrl $CloudWakeUrl
            $taskCloudToken = Read-CloudToken $taskCloudTokenPath
        } catch {
            if ($SetCloudToken -or $PSBoundParameters.ContainsKey('CloudUrl') -or $PSBoundParameters.ContainsKey('CloudWakeUrl')) { throw }
            Write-Warning 'Updates now come from public releases because no usable update-server URL and reader token are stored. Restore update-server updates with: install.ps1 -SetCloudToken -CloudUrl "<server URL>" -CloudWakeUrl "<wake URL>". Press Enter at the token prompt to keep the stored token.'
            $FromCloud = $false; $FromRelease = $true
        }
    }
    if ($FromCloud -or $FromRelease) {
        $taskTemp = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-install-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $taskTemp | Out-Null
        $taskDownload = if ($FromCloud) { Save-CloudBuild -CloudUrl $CloudUrl -CloudWakeUrl $CloudWakeUrl -Channel $CloudChannel -Token $taskCloudToken -DownloadFolder $taskTemp } else { Save-ReleaseBuild -DownloadFolder $taskTemp -InstallFolder $Destination -Force:$Force }
        Expand-Archive -LiteralPath $taskDownload -DestinationPath (Join-Path $taskTemp 'app')
        $Source = Join-Path $taskTemp 'app'
    }
    if (-not $Source) {
        $taskPackage = Split-Path $PSScriptRoot -Parent
        $Source = if (Test-Path -LiteralPath (Join-Path $taskPackage 'PrivateFlow.exe')) { $taskPackage } else { Join-Path $taskPackage 'artifacts\app' }
    }
    $Source = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Source).Path).TrimEnd('\')
    $Destination = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)).TrimEnd('\')
    if (-not (Test-Path -LiteralPath (Join-Path $Source 'PrivateFlow.exe'))) { throw "PrivateFlow.exe not found in $Source. Build with scripts\build.ps1 -Portable first or use -FromCloud." }

    # Safety before anything is stopped or changed: the destination may only be replaced when it
    # is absent, empty, or a previous install carrying our marker. Staging and backup folders get
    # fresh unique names (never pre-existing folders), and none of the paths this script creates,
    # moves or deletes may contain or be contained in the source package.
    $taskMarker = '.private-flow-install'
    $taskSame = $Source -ieq $Destination
    $taskId = [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $taskStage = "$Destination.new-$taskId"; $taskOld = "$Destination.old-$taskId"
    function Test-Overlap([string]$A, [string]$B) {
        ($A + '\').StartsWith($B + '\', [StringComparison]::OrdinalIgnoreCase) -or ($B + '\').StartsWith($A + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if (-not $taskSame) {
        foreach ($taskPath in $Destination, $taskStage, $taskOld) {
            if (Test-Overlap $Source $taskPath) { throw "$taskPath overlaps the source package $Source. Choose a separate destination folder." }
        }
        foreach ($taskPath in $taskStage, $taskOld) { if (Test-Path -LiteralPath $taskPath) { throw "$taskPath already exists; rerun the installer." } }
    }
    if (-not $taskSame -and (Test-Path -LiteralPath $Destination)) {
        $taskOwned = Test-Path -LiteralPath (Join-Path $Destination $taskMarker)
        $taskEmpty = -not (Get-ChildItem -LiteralPath $Destination -Force | Select-Object -First 1)
        if (-not ($taskOwned -or $taskEmpty)) { throw "$Destination exists and is not a Private Flow installation. Choose an empty or new folder." }
    }

    # Engine downloads and checks run from the new package before anything is stopped or replaced, so a failed
    # download or checksum leaves the previous install and a running app untouched. What they installed is
    # stored only after the running app is stopped, below, as only one program writes settings.json at a time.
    $taskSettings = Join-Path $taskDataRoot 'settings.json'
    $taskEngine = if (Test-Path -LiteralPath $taskSettings) { (Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8 | ConvertFrom-Json).EnginePath }
    $taskVad = if (Test-Path -LiteralPath $taskSettings) { (Get-Content -LiteralPath $taskSettings -Raw -Encoding UTF8 | ConvertFrom-Json).VadModelPath }
    $taskInstalled = $null
    if ($UpdateEngine -or -not $taskEngine -or -not (Test-Path -LiteralPath $taskEngine)) {
        Write-Host 'Setting up the local speech engine and model (first download ~1.5 GB with an NVIDIA GPU, ~160 MB without)...'
        $taskResult = Join-Path $taskDataRoot "setup-result-$([Guid]::NewGuid().ToString('N')).json"
        & (Join-Path $Source 'scripts\setup-engine.ps1') -DataRoot $taskDataRoot -ResultFile $taskResult
        $taskInstalled = [ordered]@{}
        (Get-Content -LiteralPath $taskResult -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $taskInstalled[$_.Name] = $_.Value }
        Remove-Item -LiteralPath $taskResult
    } elseif (-not $taskVad -or -not (Test-Path -LiteralPath $taskVad)) {
        # An upgrade adds VAD without resetting a deliberate engine/model choice.
        # The pinned source and hash come from the package's models.json, through the setup script.
        & (Join-Path $Source 'scripts\setup-engine.ps1') -Download 'silero-v6.2.0' -DataRoot $taskDataRoot
        $taskPinned = (Get-Content -LiteralPath (Join-Path $Source 'scripts\models.json') -Raw -Encoding UTF8 | ConvertFrom-Json).Models | Where-Object { $_.Key -eq 'silero-v6.2.0' }
        $taskInstalled = [ordered]@{ VadModelPath = (Join-Path (Join-Path $taskDataRoot 'models') $taskPinned.File) }
    }

    $taskExe = Join-Path $Destination 'PrivateFlow.exe'
    $taskStopped = $false
    if ($taskSame) {
        # In-place install of a validated package: mark it so later upgrades may replace it.
        Set-Content -LiteralPath (Join-Path $Destination $taskMarker) -Value 'Installed by Private Flow scripts\install.ps1'
    } else {
        # Stage the new copy next to the destination; the old install stays intact until the swap.
        # Swaps use Directory.Move: an atomic rename on one volume, unlike Move-Item, which can
        # leave a half-moved folder when a file is locked.
        New-Item -ItemType Directory -Path $taskStage | Out-Null
        try {
            Set-Content -LiteralPath (Join-Path $taskStage $taskMarker) -Value 'Installed by Private Flow scripts\install.ps1'
            Copy-Item -Path (Join-Path $Source '*') -Destination $taskStage -Recurse -Force
        } catch { Remove-Item -LiteralPath $taskStage -Recurse -Force -ErrorAction SilentlyContinue; throw }
        # Replace a running installed copy; settings and models live in %LOCALAPPDATA%\PrivateFlow and are kept.
        Get-Process PrivateFlow -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $taskExe } | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null; $taskStopped = $true }
        $taskHadOld = Test-Path -LiteralPath $Destination
        if ($taskHadOld) {
            try { [IO.Directory]::Move($Destination, $taskOld) }
            catch {
                Remove-Item -LiteralPath $taskStage -Recurse -Force -ErrorAction SilentlyContinue
                throw "Could not replace $Destination ($($_.Exception.Message)). The previous installation was kept."
            }
        }
        try { [IO.Directory]::Move($taskStage, $Destination) }
        catch {
            # Promotion failed: put the previous install back so the Start menu and autostart keep working.
            if ($taskHadOld -and -not (Test-Path -LiteralPath $Destination)) { [IO.Directory]::Move($taskOld, $Destination) }
            Remove-Item -LiteralPath $taskStage -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $taskStage) { Write-Warning "The unused staged copy $taskStage is still locked; delete it later." }
            throw "Could not replace $Destination ($($_.Exception.Message)). The previous installation was kept."
        }
        if ($taskHadOld) {
            try { Remove-Item -LiteralPath $taskOld -Recurse -Force }
            catch { Write-Warning "Installed, but the previous copy could not be removed: $taskOld" }
        }
    }
    Get-ChildItem -LiteralPath $Destination -Recurse -File | Unblock-File
    Write-Host "Installed to $Destination"

    # The installed paths are stored now that this script has stopped the previous app, under the settings lock and before the new app
    # starts. The shared step comes from the package being installed: the in-app update runs this script from a lone copy in %TEMP%.
    if ($taskInstalled) {
        try {
            . (Join-Path $Source 'scripts\settings-file.ps1')
            Save-InstalledSettings $taskDataRoot $taskInstalled
            Write-Host 'Stored the new speech engine and model paths.'
        } catch {
            Write-Warning "Private Flow is installed and the new speech files are downloaded and verified, but their paths were not stored: $($_.Exception.Message) settings.json keeps the previous paths, so Private Flow goes on using the previous speech setup; press Apply under Settings > Speech recognition to switch. After an update Private Flow also brings Automatic choices up to date by itself at its next start."
        }
    }

    $taskShell = New-Object -ComObject WScript.Shell
    $taskLink = $taskShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'Private Flow.lnk'))
    $taskLink.TargetPath = $taskExe
    $taskLink.WorkingDirectory = $Destination
    $taskLink.IconLocation = "$taskExe,0"
    $taskLink.Description = 'Private Flow voice dictation'
    $taskLink.Save()
    Write-Host 'Start menu: Private Flow'

    if ($Autostart) {
        Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PrivateFlow -Value "`"$taskExe`""
        Write-Host 'Starts with Windows.'
    }

    if (-not $NoLaunch) { Start-Process -FilePath $taskExe -WorkingDirectory $Destination }
} catch {
    # A failed swap restores the previous install; restart it if this script had stopped it.
    if ($taskStopped -and (Test-Path -LiteralPath $taskExe)) { Start-Process -FilePath $taskExe -WorkingDirectory $Destination }
    throw
} finally {
    if ($taskTemp -and (Test-Path -LiteralPath $taskTemp)) { Remove-Item -LiteralPath $taskTemp -Recurse -Force }
}
