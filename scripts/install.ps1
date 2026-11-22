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
    # With -SetCloudToken, the GitHub repository (owner/name) the update server's builds come from, optional: the in-app update then
    # lists the pull requests a newer build brings, through the signed-in GitHub CLI. Otherwise the stored one is kept.
    [string]$SourceRepository = '',
    [switch]$Autostart,
    [switch]$NoLaunch,
    # Also rerun the engine setup now. The app itself reapplies Automatic speech choices after an update.
    [switch]$UpdateEngine,
    # The release tag (with -FromRelease) or build commit (with -FromCloud) the user confirmed in the app's update; anything else
    # found is refused, so the update installs what it offered.
    [string]$ExpectedRelease = '',
    [string]$ExpectedCommit = '',
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\PrivateFlow')
)
# SmartScreen only warns about files carrying the Mark-of-the-Web (a browser download).
# Installing a local build, a cloud download by this script, or files unblocked here never triggers it.
# Signing would remove the warning for browser downloads too; see README.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# A local drive only: the speech setup refuses the paths it would write below a network share (Read-SetupResult).
if ($env:PRIVATE_FLOW_DATA_DIR -and $env:PRIVATE_FLOW_DATA_DIR -notmatch '^[A-Za-z]:[\\/]') { throw 'PRIVATE_FLOW_DATA_DIR must be an absolute path on a local drive.' }
$taskDataRoot = $(if ($env:PRIVATE_FLOW_DATA_DIR) { $env:PRIVATE_FLOW_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'PrivateFlow' })
# Outside the repository and the package; DPAPI ties it to this Windows user. The app reads the same file.
$taskCloudTokenPath = Join-Path $taskDataRoot 'cloud-reader.token'
$taskCloudConfigurationPath = Join-Path $taskDataRoot 'update-server.json'
# Once releases are signed, the exact certificate subject of their publisher. Then a downloaded
# release, and a release ZIP this script runs from, must carry an inventory signed by it that lists every file of the
# package. While it is empty, release downloads are checked by size and SHA-256 only. Update-server builds and local
# builds are never signed and never checked. publish-public.ps1 reads this value as its default -Publisher.
$taskReleasePublisher = ''
# The public key of the release signing key (ECDSA P-256, X and Y in Base64): every release download must carry SHA256SUMS.sig, this
# key's signature of its SHA256SUMS, and the package and manifest must match that SHA256SUMS. The release workflow of the public
# repository signs with the private key, which only its release environment holds.
$taskReleaseKey = '3KeurSHads1Xlj75eYmkW7s+0Hx4/+2sl9fJxyfEuuDiyf7iZc+rpYaTvAXzZnqke94W9fXqkeSEnW85ttDMtw=='

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

# SHA256SUMS lines are "<64 lowercase hex>  <file name>", as sha256sum writes them, ending with LF or CRLF; blank lines
# are skipped. A malformed line, a name repeated in any letter case or a file over 64 KiB is refused. The same rules
# hold in scripts/release.py, ReleaseUpdate.ListedChecksum and scripts/publish-public.ps1.
# Whether Signature (the Base64 of r and s, 32 bytes each, as scripts/sign-checksums.py writes it) is a valid ECDSA P-256 signature with
# SHA-256 of Data by PublicKey (the Base64 of X and Y). Anything malformed is no signature.
function Test-ReleaseSignature([byte[]]$Data, [string]$Signature, [string]$PublicKey) {
    try { $keyBytes = [Convert]::FromBase64String($PublicKey); $signatureBytes = [Convert]::FromBase64String($Signature.Trim()) } catch { return $false }
    if ($keyBytes.Length -ne 64 -or $signatureBytes.Length -ne 64) { return $false }
    $point = New-Object Security.Cryptography.ECPoint
    $point.X = [byte[]]$keyBytes[0..31]; $point.Y = [byte[]]$keyBytes[32..63]
    $parameters = New-Object Security.Cryptography.ECParameters
    $parameters.Curve = [Security.Cryptography.ECCurve+NamedCurves]::nistP256; $parameters.Q = $point
    try { $verifier = [Security.Cryptography.ECDsa]::Create($parameters) } catch { return $false }
    try { $verifier.VerifyData($Data, $signatureBytes, [Security.Cryptography.HashAlgorithmName]::SHA256) } finally { $verifier.Dispose() }
}

function Get-ListedChecksum([string]$Text, [string]$Name) {
    if ([Text.Encoding]::UTF8.GetByteCount($Text) -gt 65536) { throw 'The release checksum file is larger than 64 KiB; nothing was installed.' }
    $listed = New-Object 'Collections.Generic.Dictionary[string,string]' ([StringComparer]::Ordinal)
    $seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $Text.Split("`n")) {
        if ($line.EndsWith("`r")) { $line = $line.Substring(0, $line.Length - 1) }
        if ($line -eq '') { continue }
        if ($line -cnotmatch '\A([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)\z' -or -not $seen.Add($Matches[2])) { throw 'The release checksum file is malformed; nothing was installed.' }
        $listed[$Matches[2]] = $Matches[1]
    }
    if (-not $listed.ContainsKey($Name)) { throw "The release checksum file does not list $Name; nothing was installed." }
    $listed[$Name]
}

# With a publisher set, a release package must carry scripts\package-inventory.ps1 with a valid signature of exactly
# that publisher, for this version, and hold exactly the files it lists with their sizes and SHA-256, no more and no
# fewer. This authenticates every file, scripts\models.json and third-party files included, before any of them runs.
function Assert-PackageInventory([string]$PackageFolder, [string]$Publisher, [string]$Version, [string[]]$Ignore = @()) {
    if (-not $Publisher) { return }
    $PackageFolder = [IO.Path]::GetFullPath($PackageFolder).TrimEnd('\')
    $inventoryName = 'scripts/package-inventory.ps1'
    $inventoryPath = Join-Path $PackageFolder $inventoryName
    if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) { throw 'The release package has no signed inventory; nothing was installed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $inventoryPath
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -cne $Publisher) {
        throw "The package inventory is not signed by $Publisher (signature: $($signature.Status)); nothing was installed."
    }
    if ((Get-Item -LiteralPath $inventoryPath).Length -gt 4MB) { throw 'The package inventory is larger than 4 MB; nothing was installed.' }
    if ([IO.File]::ReadAllText($inventoryPath) -cnotmatch '\A# Private Flow package inventory:[^\r\n]*\r\n#[^\r\n]*\r\n<#\r\n(\{[^\r\n]*\})\r\n#>\r\n') {
        throw 'The package inventory is malformed; nothing was installed.'
    }
    $inventory = $Matches[1] | ConvertFrom-Json
    if ($inventory.schema -ne 1 -or $inventory.project -cne 'private-flow' -or $inventory.version -cne $Version) { throw "The package inventory is not the one of Private Flow $Version; nothing was installed." }
    $listed = New-Object 'Collections.Generic.Dictionary[string,object]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in @($inventory.files)) {
        if ([string]$entry.path -cnotmatch '\A[A-Za-z0-9_][A-Za-z0-9._+-]*(?:/[A-Za-z0-9_][A-Za-z0-9._+-]*)*\z' -or [string]$entry.sha256 -cnotmatch '\A[0-9a-f]{64}\z' -or
            -not ($entry.size -is [int] -or $entry.size -is [long]) -or $entry.size -lt 0 -or $listed.ContainsKey($entry.path)) { throw 'The package inventory is malformed; nothing was installed.' }
        $listed[$entry.path] = $entry
    }
    if (-not $listed.ContainsKey('PrivateFlow.exe')) { throw 'The package inventory lists no PrivateFlow.exe; nothing was installed.' }
    $found = 0
    foreach ($item in Get-ChildItem -LiteralPath $PackageFolder -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "The release package holds a link ($($item.Name)); nothing was installed." }
        if ($item.PSIsContainer) { continue }
        $relative = $item.FullName.Substring($PackageFolder.Length + 1).Replace('\', '/')
        if ($relative -ceq $inventoryName -or $relative -cin $Ignore) { continue }
        $entry = $null
        if (-not $listed.TryGetValue($relative, [ref]$entry) -or $entry.path -cne $relative) { throw "$relative is in the release package but not in its inventory; nothing was installed." }
        if ($item.Length -ne [long]$entry.size -or (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ine $entry.sha256) { throw "$relative does not match the package inventory; nothing was installed." }
        $found++
    }
    if ($found -ne $listed.Count) { throw "$($listed.Count - $found) files of the package inventory are missing from the release package; nothing was installed." }
}

# Names what is wrong with the text of a release manifest, or returns nothing. Types are exact: a one-element array is
# no string. With -Public (publication) it must describe a public release: profile release and none of the provenance fields, which
# describe the development repository's build (commit, dirty, built_at, merge_commit).
# publish-public.ps1 imports this function, so publication refuses what the installer refuses; ReleaseUpdate.ManifestHash
# applies the same client rules in the app.
function Get-ManifestProblem([string]$Text, [string]$Version, [long]$Size, [string]$Sha256 = '', [switch]$Public) {
    # One JSON object, alike in Windows PowerShell 5.1 and PowerShell 7, which would unroll a top-level array.
    if ($Text.Length -gt 65536 -or $Text -notmatch '\A\s*\{[\s\S]*\}\s*\z') { return 'it is not one JSON object' }
    # Strict JSON, as the app's parser reads it, whatever ConvertFrom-Json of either version would forgive: no value the build writes
    # needs a slash or a backslash, so none may appear (no comment, no escape); outside the strings only JSON's punctuation, numbers
    # and literals, no trailing comma; and each member name once, in any letter case, since PowerShell reads names without case.
    if ($Text.IndexOfAny([char[]]'/\') -ge 0) { return 'it holds a comment or an escape' }
    $outsideStrings = [regex]::Replace($Text, '"[^"]*"', '""')
    if ([regex]::Replace($outsideStrings, '""|\btrue\b|\bfalse\b|\bnull\b|-?[0-9]+(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?', '') -notmatch '\A[\s{}\[\],:]*\z' -or
        $outsideStrings -match ',\s*[}\]]') { return 'it is not strict JSON' }
    $names = @([regex]::Matches($Text, '"([^"]*)"\s*:') | ForEach-Object { $_.Groups[1].Value.ToLowerInvariant() })
    if (@($names | Sort-Object -Unique).Count -ne $names.Count) { return 'it names a member twice' }
    try { $Manifest = $Text | ConvertFrom-Json -ErrorAction Stop } catch { return 'it is not valid JSON' }
    if ($Manifest -isnot [Management.Automation.PSCustomObject]) { return 'it is not one JSON object' }
    $fields = @($Manifest.PSObject.Properties.Name)
    # Only the fields the build writes (build.ps1, package.py, and build.sh for 0.5.5): any other member could carry anything into a
    # published release. ReleaseUpdate.ManifestHash allows the same names.
    if (@($fields | Where-Object { $_ -cnotin 'schema', 'project', 'board', 'commit', 'dirty', 'built_at', 'merge_commit', 'files' }).Count) { return 'it has a field the build does not write' }
    $isWhole = { param($Value) $Value -is [int] -or $Value -is [long] }
    $isText = { param($Value, [string]$Pattern) $Value -is [string] -and $Value -cmatch $Pattern }
    if (-not (& $isWhole $Manifest.schema) -or $Manifest.schema -ne 1) { return 'schema is not 1' }
    if (-not (& $isText $Manifest.project '\Aprivate-flow\z') -or -not (& $isText $Manifest.board '\Awin-x64\z')) { return 'project or board is wrong' }
    $files = $Manifest.files
    if ($files -isnot [array] -or $files.Count -ne 1 -or $files[0] -isnot [Management.Automation.PSCustomObject]) { return 'files does not list exactly one package' }
    $file = $files[0]
    if (@($file.PSObject.Properties.Name | Where-Object { $_ -cnotin 'name', 'profile', 'version', 'size', 'sha256' }).Count) { return 'its package entry has a field the build does not write' }
    if (-not (& $isText $file.name '\APrivateFlow-win-x64\.zip\z') -or -not (& $isText $file.version ('\A' + [regex]::Escape($Version) + '\z'))) { return "files does not list the package of $Version" }
    if (-not (& $isWhole $file.size) -or $file.size -ne $Size) { return 'size does not match the package' }
    if (-not (& $isText $file.sha256 '\A[0-9a-f]{64}\z') -or ($Sha256 -and $file.sha256 -cne $Sha256)) { return 'sha256 is malformed or does not match the package' }
    if (-not $Public) { return }
    if (-not (& $isText $file.profile '\Arelease\z')) { return 'profile is not release' }
    $provenance = @('commit', 'dirty', 'built_at', 'merge_commit' | Where-Object { $_ -in $fields })
    if ($provenance.Count) { return "it describes the development build ($($provenance -join ', ')), which a public release does not name" }
}

# settings.json read under settings.lock, which every writer holds from its read to its rename (settings-file.ps1): its settings, or
# $null when there is none. A file that cannot be opened (after a short bounded retry), whose lock stays taken, or that is no JSON
# object is left as it is, with a warning, and $false is returned: the install goes on and Private Flow deals with it at its next start.
function Read-InstallSettings([string]$DataRoot) {
    $file = Join-Path $DataRoot 'settings.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    $unreadable = { param($Reason)
        Write-Warning "settings.json cannot be read ($Reason), so the install leaves it unchanged and skips the speech setup. Private Flow deals with the file at its next start: it waits for or sets aside a file it cannot use, and offers the speech setup."
        $false }
    $lock = $null
    for ($attempt = 1; -not $lock; $attempt++) {
        try { $lock = [IO.File]::Open((Join-Path $DataRoot 'settings.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
        catch {
            if ($attempt -ge 600 -or $_.Exception.GetBaseException() -isnot [IO.IOException]) { return & $unreadable "its lock stayed taken: $($_.Exception.GetBaseException().Message)" }
            Start-Sleep -Milliseconds 50
        }
    }
    $text = $null
    try {
        for ($attempt = 1; $null -eq $text; $attempt++) {
            try { $text = [IO.File]::ReadAllText($file) }
            catch {
                $reason = $_.Exception.GetBaseException()
                if ($attempt -ge 20 -or -not ($reason -is [IO.IOException] -or $reason -is [UnauthorizedAccessException])) { return & $unreadable $reason.Message }
                Start-Sleep -Milliseconds 50
            }
        }
    } finally { $lock.Dispose() }
    $settings = $null
    try { if ($text -match '\A\s*\{') { $settings = $text | ConvertFrom-Json -ErrorAction Stop } } catch { $settings = $null }
    if ($settings -isnot [Management.Automation.PSCustomObject]) { return & $unreadable 'it is not a settings file' }
    $settings
}

# The settings a setup script installed, from the text of its result file, checked as SpeechSetup.ReadResult checks them in the app: at
# most 64 KiB, one JSON object, only the settings a setup installs, each path inside this data folder's models or runtime folder with no
# link below the data folder (the final-pass model may be empty), the backend and model among those offered, the setup version in its
# form, and paths not marked as typed. Returns the values and $null, or what is wrong.
function Read-SetupResult([string]$Text, [string]$DataRoot) {
    $values = [ordered]@{}
    $refuse = { param($Reason) [pscustomobject]@{ Values = [ordered]@{}; Problem = $Reason } }
    if ($Text.Length -gt 65536 -or $Text -notmatch '\A\s*\{[\s\S]*\}\s*\z') { return & $refuse 'it is not one JSON object of a setup result' }
    try { $result = $Text | ConvertFrom-Json -ErrorAction Stop } catch { return & $refuse 'it is not valid JSON' }
    $root = [IO.Path]::GetFullPath($DataRoot).TrimEnd('\')
    foreach ($property in $result.PSObject.Properties) {
        $name = $property.Name; $value = $property.Value
        if ($name -cnotin 'EnginePath', 'ModelPath', 'LocalRewriteEnginePath', 'LocalRewriteModelPath', 'VadModelPath', 'SpeechBackend', 'SpeechModel', 'MeetingModelPath', 'SpeechPathsCustom', 'SpeechSetupVersion', 'MeetingNotesEnginePath', 'MeetingNotesModelPath') {
            return & $refuse "it names $name, which a setup does not install"
        }
        $problem = $null
        if ($name.EndsWith('Path')) {
            if ($value -isnot [string]) { $problem = "its $name is not a path" }
            elseif ($value -eq '' -and $name -ceq 'MeetingModelPath') { }
            elseif ($value.Length -gt 1024 -or $value.IndexOfAny([IO.Path]::GetInvalidPathChars()) -ge 0 -or $value -notmatch '\A[A-Za-z]:\\') { $problem = "its $name is not a path inside this data folder's models or runtime folder" }
            else {
                $full = [IO.Path]::GetFullPath($value)
                if (-not (@('models', 'runtime') | Where-Object { $full.StartsWith("$root\$_\", [StringComparison]::OrdinalIgnoreCase) })) { $problem = "its $name is not a path inside this data folder's models or runtime folder" }
                for ($current = Split-Path $full -Parent; -not $problem -and $current.Length -gt $root.Length; $current = Split-Path $current -Parent) {
                    if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { $problem = "its $name leads through a link" }
                }
            }
        } elseif ($name -ceq 'SpeechBackend') { if ($value -isnot [string] -or $value -cnotin 'auto', 'cpu', 'cuda12') { $problem = "its $name is not one offered" } }
        elseif ($name -ceq 'SpeechModel') { if ($value -isnot [string] -or $value -cnotin 'auto', 'large-v3-turbo-q8_0', 'medium', 'base') { $problem = "its $name is not one offered" } }
        elseif ($name -ceq 'SpeechSetupVersion') { if ($value -isnot [string] -or $value -cnotmatch '\A\d+\.\d+\.\d+(?:[+.-][0-9A-Za-z.+-]{1,80})?\z') { $problem = "its $name is not a version" } }
        elseif ($value -isnot [bool] -or $value) { $problem = 'it marks the paths as entered by hand' }
        if ($problem) { return & $refuse $problem }
        $values[$name] = $value
    }
    [pscustomobject]@{ Values = $values; Problem = $null }
}

# whisper.cpp's DLLs need the Microsoft Visual C++ 2015-2022 runtime (x64), which a fresh Windows lacks and the package does not bring:
# its documented registry value Installed is 1, or its three DLLs are in System32 (SpeechRuntime.VisualCppRuntimePresent in the app).
function Test-VisualCppRuntime {
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') {
        if ((Get-ItemProperty -LiteralPath $key -Name Installed -ErrorAction SilentlyContinue).Installed -eq 1) { return $true }
    }
    $system = [Environment]::GetFolderPath('System')
    @('vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll' | Where-Object { -not (Test-Path -LiteralPath (Join-Path $system $_)) }).Count -eq 0
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

# An update-server build is never older than the installed copy, whatever the commit: a lower or unreadable version is refused, also
# with -Force. An equal version is another commit of the same version, which the app offers only to a build that names its commit.
function Assert-NotOlderBuild([string]$OfferedVersion, [string]$InstallFolder) {
    $offered = $null
    if (-not [version]::TryParse($OfferedVersion, [ref]$offered) -or $offered.Build -lt 0) { throw "The update server names no version x.y.z for its newest build ($OfferedVersion); nothing was installed." }
    if (-not $InstallFolder) { return }
    $installedExecutable = Join-Path $InstallFolder 'PrivateFlow.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable -PathType Leaf)) { return }
    $installed = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    $installedVersion = New-Object Version $installed.FileMajorPart,$installed.FileMinorPart,$installed.FileBuildPart
    if ((New-Object Version $offered.Major,$offered.Minor,$offered.Build) -lt $installedVersion) { throw "The newest update-server build, $offered, is older than the installed version $installedVersion; builds are never downgraded, so nothing was installed." }
}

function Save-ReleaseBuild([string]$DownloadFolder, [string]$InstallFolder, [switch]$Force, [string]$ExpectedTag = '', [string]$ReleaseKey = '') {
    $requestHeaders = @{ 'User-Agent' = 'PrivateFlow-updater'; Accept = 'application/vnd.github+json' }
    try { $release = Invoke-RestMethod -Uri 'https://api.github.com/repos/DemonicPsychoII/PrivateFlow/releases/latest' -Headers $requestHeaders -TimeoutSec 60 -UseBasicParsing }
    catch {
        if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            throw 'The public repository or its latest stable release could not be found (HTTP 404). The repository may not be public yet, or no stable release has been published.'
        }
        throw
    }
    if (-not $release -or $release.draft -or $release.prerelease -or $release.tag_name -notmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'No stable public release is available.' }
    if ($ExpectedTag -and $release.tag_name -cne $ExpectedTag) { throw "The latest release is now $($release.tag_name), not $ExpectedTag, the release you confirmed; nothing was installed. Check for updates again." }
    Assert-NewerRelease -OfferedVersion $release.tag_name.Substring(1) -InstallFolder $InstallFolder -Force:$Force
    $packages = @($release.assets | Where-Object name -eq 'PrivateFlow-win-x64.zip')
    $manifests = @($release.assets | Where-Object name -eq 'manifest.json')
    if ($packages.Count -ne 1 -or $manifests.Count -ne 1) { throw 'The release must contain one package and one manifest.' }
    $package = $packages[0]; $manifestAsset = $manifests[0]
    $releaseBase = "https://github.com/DemonicPsychoII/PrivateFlow/releases/download/$($release.tag_name)"
    if ($package.browser_download_url -cne "$releaseBase/PrivateFlow-win-x64.zip" -or $manifestAsset.browser_download_url -cne "$releaseBase/manifest.json") { throw 'Unexpected release asset URL.' }
    # The manifest's text, not a parsed reply: PowerShell 7 would unroll a top-level array.
    $manifestPath = Join-Path $DownloadFolder 'manifest.json'
    Invoke-WebRequest -Uri $manifestAsset.browser_download_url -Headers $requestHeaders -OutFile $manifestPath -TimeoutSec 60 -UseBasicParsing
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 65536) { throw 'The release manifest is larger than 64 KiB; nothing was installed.' }
    $manifestText = [IO.File]::ReadAllText($manifestPath)
    $manifestProblem = Get-ManifestProblem $manifestText $release.tag_name.Substring(1) ([long]$package.size)
    if ($manifestProblem) { throw "The release manifest does not match the package and version ($manifestProblem); nothing was installed." }
    $files = @(($manifestText | ConvertFrom-Json).files)
    # Releases made by the release workflow also carry SHA256SUMS, which must agree with the manifest. With a release key, SHA256SUMS and
    # its signature are required, and SHA256SUMS must also list the manifest as it was downloaded: the signature then covers all three.
    $checksumAssets = @($release.assets | Where-Object name -eq 'SHA256SUMS')
    if ($checksumAssets.Count -gt 1 -or ($checksumAssets.Count -eq 1 -and $checksumAssets[0].browser_download_url -cne "$releaseBase/SHA256SUMS")) { throw 'Unexpected release checksum asset.' }
    $signatureAssets = @($release.assets | Where-Object name -eq 'SHA256SUMS.sig')
    if ($ReleaseKey -and ($checksumAssets.Count -ne 1 -or $signatureAssets.Count -ne 1 -or $signatureAssets[0].browser_download_url -cne "$releaseBase/SHA256SUMS.sig")) {
        throw 'The release carries no signed checksum file (SHA256SUMS and SHA256SUMS.sig); nothing was installed.'
    }
    if ($checksumAssets.Count -eq 1) {
        $checksumPath = Join-Path $DownloadFolder 'SHA256SUMS'
        Invoke-WebRequest -Uri $checksumAssets[0].browser_download_url -Headers $requestHeaders -OutFile $checksumPath -TimeoutSec 60 -UseBasicParsing
        if ((Get-Item -LiteralPath $checksumPath).Length -gt 65536) { throw 'The release checksum file is larger than 64 KiB; nothing was installed.' }
        $checksumText = [IO.File]::ReadAllText($checksumPath)
        if ($ReleaseKey) {
            $signaturePath = Join-Path $DownloadFolder 'SHA256SUMS.sig'
            Invoke-WebRequest -Uri $signatureAssets[0].browser_download_url -Headers $requestHeaders -OutFile $signaturePath -TimeoutSec 60 -UseBasicParsing
            if ((Get-Item -LiteralPath $signaturePath).Length -gt 1024 -or -not (Test-ReleaseSignature ([IO.File]::ReadAllBytes($checksumPath)) ([IO.File]::ReadAllText($signaturePath)) $ReleaseKey)) {
                throw 'The release checksum file is not signed by the Private Flow release key; nothing was installed.'
            }
            if ((Get-ListedChecksum $checksumText 'manifest.json') -cne (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'The release manifest does not match the signed checksum file; nothing was installed.' }
        }
        if ((Get-ListedChecksum $checksumText 'PrivateFlow-win-x64.zip') -ine $files[0].sha256) { throw 'The release checksum file and manifest disagree; nothing was installed.' }
    }
    Write-Host "Downloading Private Flow $($release.tag_name)..."
    $packagePath = Save-VerifiedPackage -Url $package.browser_download_url -Headers $requestHeaders -Size $files[0].size -Sha256 $files[0].sha256 -Digest $package.digest -DownloadFolder $DownloadFolder
    [pscustomobject]@{ Path = $packagePath; Version = $release.tag_name.Substring(1) }
}

# Wakes the update server, then downloads the newest build of the channel into DownloadFolder and returns the
# ZIP path only after its size and SHA-256 match the cloud listing.
function Save-CloudBuild([string]$CloudUrl, [string]$CloudWakeUrl, [string]$Channel, [string]$Token, [string]$DownloadFolder, [string]$ExpectedCommit = '', [string]$InstallFolder = '') {
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
    if ($ExpectedCommit -and "$($latestBuild.commit)" -ine $ExpectedCommit) { throw "The newest '$Channel' build is now commit $($latestBuild.commit), not the build you confirmed ($ExpectedCommit); nothing was installed. Check for updates again." }
    Assert-NotOlderBuild ([string]$latestBuild.version) $InstallFolder
    $packageListing = @($latestBuild.files | Where-Object { $_.name -eq 'PrivateFlow-win-x64.zip' })
    if ($packageListing.Count -ne 1) { throw "The newest '$Channel' build does not list exactly one PrivateFlow-win-x64.zip." }
    $packageListing = $packageListing[0]
    if ([string]$packageListing.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or [long]$packageListing.size -le 0) { throw "The newest '$Channel' build lists no valid size and SHA-256 for its package." }
    Write-Host "Downloading Private Flow $($latestBuild.version), commit $($latestBuild.commit), built $($latestBuild.builtAt)..."
    Save-VerifiedPackage -Url "$CloudUrl/api/firmware/files/$([Uri]::EscapeDataString([string]$packageListing.id))/download" -Headers $requestHeaders -Size $packageListing.size -Sha256 $packageListing.sha256 -DownloadFolder $DownloadFolder
}

if ($FromCloud -and $FromRelease) { throw 'Choose either -FromCloud or -FromRelease.' }
if (($ExpectedRelease -and ($ExpectedRelease -cnotmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z' -or -not $FromRelease)) -or ($ExpectedCommit -and ($ExpectedCommit -notmatch '\A[0-9a-fA-F]{40}\z' -or -not $FromCloud))) {
    throw '-ExpectedRelease takes a tag such as v0.5.19 with -FromRelease, and -ExpectedCommit a full commit ID with -FromCloud.'
}
if ($SetCloudToken) {
    Assert-ServerConfiguration $CloudUrl $CloudWakeUrl
    if (-not $PSBoundParameters.ContainsKey('SourceRepository') -and (Test-Path -LiteralPath $taskCloudConfigurationPath)) {
        try { $SourceRepository = [string](Get-Content -LiteralPath $taskCloudConfigurationPath -Raw -Encoding UTF8 | ConvertFrom-Json).SourceRepository } catch { $SourceRepository = '' }
    }
    if ($SourceRepository -and $SourceRepository -cnotmatch '\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z') { throw 'Set -SourceRepository to a GitHub repository as owner/name.' }
    $taskSecureToken = Read-Host 'Update server reader token (Enter keeps the stored token)' -AsSecureString
    if ($taskSecureToken.Length -eq 0) { $null = Read-CloudToken $taskCloudTokenPath }
    New-Item -ItemType Directory -Force -Path $taskDataRoot | Out-Null
    if ($taskSecureToken.Length -gt 0) {
        ConvertFrom-SecureString -SecureString $taskSecureToken | Set-Content -LiteralPath "$taskCloudTokenPath.partial" -Encoding ascii
        Move-Item -LiteralPath "$taskCloudTokenPath.partial" -Destination $taskCloudTokenPath -Force
    }
    $taskServerConfiguration = [ordered]@{ CloudUrl = $CloudUrl.TrimEnd('/'); CloudWakeUrl = $CloudWakeUrl }
    if ($SourceRepository) { $taskServerConfiguration.SourceRepository = $SourceRepository }
    $taskServerConfiguration | ConvertTo-Json | Set-Content -LiteralPath "$taskCloudConfigurationPath.partial" -Encoding UTF8
    Move-Item -LiteralPath "$taskCloudConfigurationPath.partial" -Destination $taskCloudConfigurationPath -Force
    Write-Host "Stored the reader token for this Windows user, DPAPI-encrypted, in $taskCloudTokenPath"
    if (-not $FromCloud -and -not $FromRelease) { return }
}

$taskTemp = $null
# While this script runs, a start of Private Flow by hand is refused (App.xaml.cs): the swap below would stop that copy without saving.
# Only an installation for the user's own data folder holds it, so isolated test runs never block each other or the user's app.
$taskInstallerMutex = $null
try {
    if (-not $env:PRIVATE_FLOW_DATA_DIR) {
        # Only one installation at a time: a second one would hold the mutex after the first had started the new copy.
        $taskCreatedMutex = $false
        $taskInstallerMutex = New-Object Threading.Mutex($true, "Local\PrivateFlow-Install-$env:USERNAME", [ref]$taskCreatedMutex)
        if (-not $taskCreatedMutex) {
            $taskInstallerMutex.Dispose(); $taskInstallerMutex = $null
            throw 'Another installation of Private Flow is running; nothing was changed. Try again when it has finished.'
        }
    }
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
            # A confirmed update-server build is never replaced by a release.
            if ($SetCloudToken -or $ExpectedCommit -or $PSBoundParameters.ContainsKey('CloudUrl') -or $PSBoundParameters.ContainsKey('CloudWakeUrl')) { throw }
            Write-Warning 'Updates now come from public releases because no usable update-server URL and reader token are stored. Restore update-server updates with: install.ps1 -SetCloudToken -CloudUrl "<server URL>" -CloudWakeUrl "<wake URL>". Press Enter at the token prompt to keep the stored token.'
            $FromCloud = $false; $FromRelease = $true
        }
    }
    if ($FromCloud -or $FromRelease) {
        $taskTemp = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-install-' + [Guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $taskTemp | Out-Null
        $taskRelease = if ($FromRelease) { Save-ReleaseBuild -DownloadFolder $taskTemp -InstallFolder $Destination -Force:$Force -ExpectedTag $ExpectedRelease -ReleaseKey $taskReleaseKey }
        $taskDownload = if ($FromCloud) { Save-CloudBuild -CloudUrl $CloudUrl -CloudWakeUrl $CloudWakeUrl -Channel $CloudChannel -Token $taskCloudToken -DownloadFolder $taskTemp -ExpectedCommit $ExpectedCommit -InstallFolder $Destination } else { $taskRelease.Path }
        Expand-Archive -LiteralPath $taskDownload -DestinationPath (Join-Path $taskTemp 'app')
        $Source = Join-Path $taskTemp 'app'
        # Before anything from the downloaded package runs; the in-app update installs through this same path.
        if ($FromRelease) { Assert-PackageInventory $Source $taskReleasePublisher $taskRelease.Version }
    }
    $taskOwnPackage = $false
    if (-not $Source) {
        $taskPackage = Split-Path $PSScriptRoot -Parent
        $taskOwnPackage = Test-Path -LiteralPath (Join-Path $taskPackage 'PrivateFlow.exe')
        $Source = if ($taskOwnPackage) { $taskPackage } else { Join-Path $taskPackage 'artifacts\app' }
    }
    $Source = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Source).Path).TrimEnd('\')
    $Destination = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination)).TrimEnd('\')
    # Run from an extracted release ZIP, the installer checks the package it came in against that package's signed
    # inventory before anything else from it runs. It cannot vouch for itself: a tampered install.ps1 could skip this.
    # An installed copy run in place (its own folder as the destination) was checked when it was installed and is not
    # checked again; copied elsewhere, its install marker is the one file allowed beside the inventory.
    # Local builds, -Source and update-server builds are not signed and not checked.
    if ($taskOwnPackage -and $taskReleasePublisher -and $Source -ine $Destination) {
        $taskOwnVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Source 'PrivateFlow.exe'))
        Assert-PackageInventory $Source $taskReleasePublisher ('{0}.{1}.{2}' -f $taskOwnVersion.FileMajorPart, $taskOwnVersion.FileMinorPart, $taskOwnVersion.FileBuildPart) -Ignore '.private-flow-install'
    }
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
    $taskSettings = Read-InstallSettings $taskDataRoot
    $taskEngine = if ($taskSettings) { "$($taskSettings.EnginePath)" }
    $taskVad = if ($taskSettings) { "$($taskSettings.VadModelPath)" }
    $taskInstalled = $null
    # An unreadable settings.json was reported above and stays as it is; the app sets it aside at its next start.
    if ($taskSettings -is [bool]) { }
    elseif ($UpdateEngine -or -not $taskEngine -or -not (Test-Path -LiteralPath $taskEngine)) {
        Write-Host 'Setting up the local speech engine and models...'
        $taskResult = Join-Path $taskDataRoot "setup-result-$([Guid]::NewGuid().ToString('N')).json"
        & (Join-Path $Source 'scripts\setup-engine.ps1') -DataRoot $taskDataRoot -ResultFile $taskResult
        $taskResultText = if (Test-Path -LiteralPath $taskResult -PathType Leaf) { [IO.File]::ReadAllText($taskResult) } else { '' }
        Remove-Item -LiteralPath $taskResult -ErrorAction SilentlyContinue
        $taskRead = Read-SetupResult $taskResultText $taskDataRoot
        if ($taskRead.Problem) { throw "The speech setup finished, but its result was refused: $($taskRead.Problem). Nothing was installed." }
        $taskInstalled = $taskRead.Values
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
    if (-not (Test-VisualCppRuntime)) {
        Write-Warning 'Speech recognition needs the Microsoft Visual C++ Redistributable 2015-2022 (x64), which this PC does not have. Install it from Microsoft, https://aka.ms/vs/17/release/vc_redist.x64.exe, then start Private Flow again.'
    }

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

    # A second installation elsewhere, such as a test copy, must not take over the shortcut of one that still exists.
    $taskShell = New-Object -ComObject WScript.Shell
    $taskLinkPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Private Flow.lnk'
    $taskLink = $taskShell.CreateShortcut($taskLinkPath)
    $taskLinkTarget = $(if ((Test-Path -LiteralPath $taskLinkPath -PathType Leaf) -and $taskLink.TargetPath) { [IO.Path]::GetFullPath($taskLink.TargetPath) })
    if ($taskLinkTarget -and $taskLinkTarget -ine $taskExe -and (Test-Path -LiteralPath $taskLinkTarget -PathType Leaf)) {
        Write-Host "Start menu: kept Private Flow, which opens $taskLinkTarget. Start this copy with $taskExe"
    } else {
        $taskLink.TargetPath = $taskExe
        $taskLink.WorkingDirectory = $Destination
        $taskLink.IconLocation = "$taskExe,0"
        $taskLink.Description = 'Private Flow voice dictation'
        $taskLink.Save()
        Write-Host 'Start menu: Private Flow'
    }

    if ($Autostart) {
        Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PrivateFlow -Value "`"$taskExe`""
        Write-Host 'Starts with Windows.'
    }

    if ($taskInstallerMutex) { $taskInstallerMutex.Dispose(); $taskInstallerMutex = $null }
    if (-not $NoLaunch) { Start-Process -FilePath $taskExe -WorkingDirectory $Destination }
} catch {
    # A failed swap restores the previous install; restart it if this script had stopped it.
    if ($taskInstallerMutex) { $taskInstallerMutex.Dispose(); $taskInstallerMutex = $null }
    if ($taskStopped -and (Test-Path -LiteralPath $taskExe)) { Start-Process -FilePath $taskExe -WorkingDirectory $Destination }
    throw
} finally {
    if ($taskInstallerMutex) { $taskInstallerMutex.Dispose() }
    if ($taskTemp -and (Test-Path -LiteralPath $taskTemp)) { Remove-Item -LiteralPath $taskTemp -Recurse -Force }
}
