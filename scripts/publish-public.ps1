[CmdletBinding()]
param(
    # The version tag on main of the development repository to publish, for example v0.5.21.
    [Parameter(Mandatory = $true)][string]$Tag,
    # Second step: check the draft release that the public repository's release workflow built for the tag, and publish it.
    [switch]$Finish,
    # With -Finish, take PrivateFlow-win-x64.zip, manifest.json, SHA256SUMS and SHA256SUMS.sig from this folder instead of the draft.
    [string]$AssetFolder = '',
    [string]$PublicRemote = 'https://github.com/DemonicPsychoII/PrivateFlow.git',
    [string]$PublicRepository = 'DemonicPsychoII/PrivateFlow',
    # Require the package's signed inventory, and so every file of the package, to come from exactly this certificate
    # subject. Defaults to $taskReleasePublisher of this checkout's install.ps1, so a signed release is never published unchecked.
    [string]$Publisher = '',
    # More private identifiers for the privacy scan to refuse everywhere, such as a name or an internal host.
    [string[]]$DenyText = @(),
    # The public identity, which the owner chooses: the author and committer of the public commit and the tagger of its tag, never
    # taken from this PC's Git settings or GIT_AUTHOR_* and GIT_COMMITTER_* variables, and the GitHub account that must be signed in to
    # the GitHub CLI, which pushes the snapshot and publishes the release. No defaults: each publication names them.
    [Parameter(Mandatory = $true)][string]$AuthorName,
    [Parameter(Mandatory = $true)][string]$AuthorEmail,
    [Parameter(Mandatory = $true)][string]$UploadAccount,
    # Check everything and commit the snapshot in a temporary clone, then stop before pushing or changing the public release.
    [switch]$DryRun
)
# Publishes a release in two steps.
# 1. Without -Finish: exports a history-free snapshot of the tagged commit with export-public.ps1, which scans it for private detail,
#    commits it on top of the public main and pushes it with the tag. The tag starts the public repository's release workflow, which
#    builds the package on GitHub's runners from that snapshot, signs SHA256SUMS with the release key and leaves a draft release.
# 2. With -Finish: downloads the draft's files and checks them as install.ps1 and the in-app update will: SHA256SUMS against the
#    package and the manifest, its signature against the release key install.ps1 pins, the manifest's public form, the version inside
#    PrivateFlow.exe, and with -Publisher the signed inventory. It exports the snapshot again, which must be the public tag's tree, and
#    scans the package, its entry names, the manifest, SHA256SUMS and the release notes with the snapshot's rules and this PC's deny
#    rules, which the public repository cannot know. Only then is the draft published, with the snapshot's notes.
# A rerun after a partial step finishes it; a rerun after success changes nothing. A published release is never changed.
$ErrorActionPreference = 'Stop'
if (-not $AuthorName.Trim() -or $AuthorEmail -notmatch '\A[^@\s<>]+@[^@\s<>]+\z' -or $UploadAccount -notmatch '\A[A-Za-z0-9-]+\z') { throw 'Give the public identity: -AuthorName, -AuthorEmail and -UploadAccount (a GitHub login).' }
if ($Tag -cnotmatch '\Av(\d+\.\d+\.\d+)(-[0-9A-Za-z][0-9A-Za-z.]*)?\z') { throw "Tag $Tag is not vX.Y.Z or vX.Y.Z-<label>." }
if ($AssetFolder -and -not $Finish) { throw '-AssetFolder belongs to -Finish.' }
$version = $Matches[1]; $prerelease = [bool]$Matches[2]
$names = 'PrivateFlow-win-x64.zip', 'manifest.json', 'SHA256SUMS', 'SHA256SUMS.sig'
$work = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-publish-' + [Guid]::NewGuid().ToString('N'))
$assets = Join-Path $work 'assets'
New-Item -ItemType Directory -Path $assets | Out-Null
function Invoke-Checked([string]$Command, [string[]]$Arguments) {
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Command $($Arguments -join ' ') failed." }
}
# The same section rule as scripts/release.py notes; the snapshot's notes have no internal subsections left (export-public.ps1).
function Get-ReleaseNotesSection([string]$Text, [string]$Version) {
    $lines = $Text -split "`r?`n"
    $heading = '\A# Private Flow v?' + [regex]::Escape($Version) + '(?![\d.])'
    $start = -1
    for ($index = 0; $index -lt $lines.Count; $index++) { if ($lines[$index] -cmatch $heading) { $start = $index; break } }
    if ($start -lt 0) { throw "docs/RELEASE_NOTES.md of $Tag has no ""# Private Flow $Version"" heading." }
    $end = $lines.Count
    for ($index = $start + 1; $index -lt $lines.Count; $index++) { if ($lines[$index].StartsWith('# ')) { $end = $index; break } }
    $body = if ($end -gt $start + 1) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
    if (-not $body) { throw "The release notes of $Version are empty." }
    $body + "`n"
}
# What is wrong with the bytes of a ZIP beyond its entries, or nothing: the publication scan reads the entries' names and contents only,
# so the archive must hold nothing else. From its first byte to its last, it is the local entries one after another (header, name,
# data, an optional data descriptor), then their central directory, then the end record, with no comment anywhere, no extra field,
# no encryption, no gap and nothing after it, and each central record names the entry it points to.
function Get-ZipLayoutProblem([byte[]]$Bytes) {
    $length = $Bytes.Length
    if ($length -lt 22 -or [BitConverter]::ToUInt32($Bytes, $length - 22) -ne 0x06054b50) { return 'it does not end with its end record (an archive comment or bytes after it)' }
    $end = $length - 22
    if ([BitConverter]::ToUInt16($Bytes, $end + 20) -ne 0) { return 'it carries an archive comment' }
    $count = [BitConverter]::ToUInt16($Bytes, $end + 10)
    $directorySize = [BitConverter]::ToUInt32($Bytes, $end + 12); $directory = [BitConverter]::ToUInt32($Bytes, $end + 16)
    if ([BitConverter]::ToUInt16($Bytes, $end + 4) -ne 0 -or [BitConverter]::ToUInt16($Bytes, $end + 8) -ne $count -or [long]$directory + $directorySize -ne $end) { return 'its central directory is not where its end record says' }
    $local = 0L; $central = [long]$directory
    for ($index = 0; $index -lt $count; $index++) {
        if ($central + 46 -gt $end -or [BitConverter]::ToUInt32($Bytes, [int]$central) -ne 0x02014b50) { return "central record $index is malformed" }
        $nameLength = [BitConverter]::ToUInt16($Bytes, [int]$central + 28)
        if ([BitConverter]::ToUInt16($Bytes, [int]$central + 30) -ne 0 -or [BitConverter]::ToUInt16($Bytes, [int]$central + 32) -ne 0) { return "entry $index carries an extra field or a comment" }
        $flags = [BitConverter]::ToUInt16($Bytes, [int]$central + 8)
        $compressed = [BitConverter]::ToUInt32($Bytes, [int]$central + 20)
        if (($flags -band 1) -or [BitConverter]::ToUInt32($Bytes, [int]$central + 42) -ne $local) { return "entry $index is encrypted or not where the entry before it ends" }
        if ($local + 30 -gt $directory -or [BitConverter]::ToUInt32($Bytes, [int]$local) -ne 0x04034b50 -or [BitConverter]::ToUInt16($Bytes, [int]$local + 26) -ne $nameLength -or
            [BitConverter]::ToUInt16($Bytes, [int]$local + 28) -ne 0) { return "entry $index has a malformed local header or an extra field" }
        for ($at = 0; $at -lt $nameLength; $at++) { if ($Bytes[$local + 30 + $at] -ne $Bytes[$central + 46 + $at]) { return "entry $index has another name in its local header" } }
        $local += 30 + $nameLength + $compressed
        if ($flags -band 8) {
            if ($local + 4 -le $directory -and [BitConverter]::ToUInt32($Bytes, [int]$local) -eq 0x08074b50) { $local += 4 }
            $local += 12
        }
        $central += 46 + $nameLength
    }
    if ($local -ne $directory -or $central -ne $end) { return 'it holds bytes outside its entries' }
}
$repository = Split-Path $PSScriptRoot -Parent
# The clients' own checks and the pinned release key, so publication refuses exactly what install.ps1 and the in-app update refuse.
$installer = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'install.ps1'), [ref]$null, [ref]$null)
foreach ($definition in $installer.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in 'Get-ListedChecksum', 'Assert-PackageInventory', 'Get-ManifestProblem', 'Test-ReleaseSignature' }, $false)) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
function Get-InstallerConstant([string]$Name) {
    $assigned = @($installer.FindAll({ param($node) $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq $Name }, $false))
    if ($assigned.Count -ne 1 -or $assigned[0].Right.Expression -isnot [Management.Automation.Language.StringConstantExpressionAst]) { throw "install.ps1 does not set $Name to one constant." }
    $assigned[0].Right.Expression.Value
}
if (-not $PSBoundParameters.ContainsKey('Publisher')) { $Publisher = Get-InstallerConstant '$taskReleasePublisher' }
$releaseKey = Get-InstallerConstant '$taskReleaseKey'
if (-not $releaseKey) { throw 'install.ps1 pins no release key ($taskReleaseKey), so no installed copy could check a release.' }
Push-Location $repository
try {
    Invoke-Checked git @('fetch', '--quiet', 'origin', 'main')
    $commit = git rev-parse --verify --quiet "$Tag^{commit}"
    if ($LASTEXITCODE -ne 0 -or -not $commit) { throw "Tag $Tag is not in this checkout; run git fetch origin --tags first." }
    git merge-base --is-ancestor $commit origin/main
    if ($LASTEXITCODE -ne 0) { throw "$Tag is not on main of the development repository." }
    # The account that would push and publish, named before anything else, also in a dry run.
    $login = "$(gh api user --jq .login 2>$null)".Trim()
    if ($LASTEXITCODE -ne 0 -or -not $login) { throw 'The GitHub CLI is not signed in, so the account that would publish is unknown; nothing was published.' }
    Write-Host "The GitHub CLI is signed in as $login, which would publish $Tag."
    if ($login -ne $UploadAccount) { throw "The GitHub CLI is signed in as $login, not as -UploadAccount $UploadAccount; nothing was published." }
    # The tagged tree must be a numbered release: its project version is the tag's and no change waits for a number.
    $taggedNotes = @(git show "${commit}:docs/RELEASE_NOTES.md")
    if ($LASTEXITCODE -ne 0) { throw "$Tag has no docs/RELEASE_NOTES.md." }
    if (@($taggedNotes | Where-Object { $_ -cmatch '\A# Private Flow next(?![\w.])' }).Count) { throw "docs/RELEASE_NOTES.md of $Tag still has a ""# Private Flow next"" section, changes without a version; nothing was published." }
    $taggedProject = (@(git show "${commit}:src/PrivateFlow.Windows/PrivateFlow.Windows.csproj") -join "`n")
    if ($taggedProject -cnotmatch ('<Version>' + [regex]::Escape($version) + '</Version>')) { throw "The project version of $Tag is not $version." }
    $notesFolder = Join-Path $work 'notes'
    New-Item -ItemType Directory -Path $notesFolder | Out-Null
    # The release body is the tagged commit's section of the version, never the editable release text, and is scanned with the snapshot.
    $notes = Join-Path $notesFolder 'release-notes.md'
    [IO.File]::WriteAllText($notes, (Get-ReleaseNotesSection ($taggedNotes -join "`n") $version))
    $snapshot = Join-Path $work 'snapshot'

    if (-not $Finish) {
        & (Join-Path $PSScriptRoot 'export-public.ps1') -Target $snapshot -Commit $commit -AlsoScan @($notes) -DenyText $DenyText -PublicRepository $PublicRepository
        $public = Join-Path $work 'public'
        Invoke-Checked git @('clone', '--quiet', $PublicRemote, $public)
        Push-Location $public
        try {
            Get-ChildItem -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force
            Get-ChildItem -LiteralPath $snapshot -Force | Copy-Item -Destination $public -Recurse -Force
            Invoke-Checked git @('add', '--all')
            $tree = git write-tree
            $taggedTree = git rev-parse --verify --quiet "refs/tags/$Tag^{tree}"
            if ($LASTEXITCODE -eq 0) {
                if ($taggedTree -cne $tree) { throw "The public repository already has $Tag with other content; it is never replaced." }
                Write-Host "The public repository already has $Tag with this snapshot; nothing to push."
            } else {
                # The public identity for author, committer and tagger alike: the variables win over every Git setting and are set here,
                # over any this shell inherited, then restored. The result is read back.
                $identityVariables = 'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL'
                $inherited = @{}
                foreach ($variable in $identityVariables) { $inherited[$variable] = [Environment]::GetEnvironmentVariable($variable) }
                try {
                    $env:GIT_AUTHOR_NAME = $env:GIT_COMMITTER_NAME = $AuthorName
                    $env:GIT_AUTHOR_EMAIL = $env:GIT_COMMITTER_EMAIL = $AuthorEmail
                    $identity = @('-c', "user.name=$AuthorName", '-c', "user.email=$AuthorEmail", '-c', 'commit.gpgsign=false', '-c', 'tag.gpgsign=false')
                    # Empty when a pre-release of the same commit was published before, such as v0.5.21-rc.1 before v0.5.21.
                    Invoke-Checked git ($identity + @('commit', '--quiet', '--allow-empty', '-m', "Private Flow $version"))
                    Invoke-Checked git ($identity + @('tag', '--annotate', $Tag, '--message', "Private Flow $version"))
                } finally { foreach ($variable in $identityVariables) { [Environment]::SetEnvironmentVariable($variable, $inherited[$variable]) } }
                $expected = "$AuthorName`t$AuthorEmail"
                $made = @("$(git log -1 --format='%an%x09%ae')", "$(git log -1 --format='%cn%x09%ce')", "$(git for-each-ref "refs/tags/$Tag" --format='%(taggername)%09%(taggeremail:trim)')")
                if (@($made | Where-Object { $_ -cne $expected }).Count) { throw "The public commit or tag was not made by $AuthorName <$AuthorEmail> alone; nothing was pushed." }
                Write-Host "The public commit and tag are by $AuthorName <$AuthorEmail>."
                if ($DryRun) { Write-Host "Dry run: the public commit and tag $Tag are prepared in $public; nothing was pushed." }
                else {
                    Invoke-Checked git @('push', '--atomic', 'origin', 'HEAD:main', "refs/tags/$Tag")
                    Write-Host "Pushed $Tag. The release workflow of $PublicRepository now builds its draft; then run this again with -Finish."
                }
            }
        } finally { Pop-Location }
        return
    }

    # -Finish: the draft the release workflow built, or a published release this step already finished.
    # The tag reaches jq through the environment: Windows PowerShell does not pass embedded quotes to native programs.
    $env:PRIVATE_FLOW_PUBLISH_TAG = $Tag
    $releaseFilter = '{id, draft, assets: [.assets[] | {name, digest}]}'
    $listing = gh api --paginate "repos/$PublicRepository/releases?per_page=100" --jq ".[] | select(.tag_name == `$ENV.PRIVATE_FLOW_PUBLISH_TAG) | $releaseFilter"
    if ($LASTEXITCODE -ne 0) { throw "Could not list the releases of $PublicRepository." }
    $existing = @($listing | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($existing.Count -ne 1) { throw "The public repository has $($existing.Count) releases of $Tag, not the one draft its release workflow leaves. Check its workflow run." }
    $remoteNames = @($existing[0].assets | ForEach-Object name)
    if ($remoteNames.Count -ne $names.Count -or @($names | Where-Object { $remoteNames -cnotcontains $_ }).Count) { throw "The release of $Tag holds $($remoteNames -join ', '), not exactly $($names -join ', ')." }
    if ($AssetFolder) { foreach ($name in $names) { Copy-Item -LiteralPath (Join-Path $AssetFolder $name) -Destination $assets } }
    else { Invoke-Checked gh (@('release', 'download', $Tag, '--repo', $PublicRepository, '--dir', $assets) + ($names | ForEach-Object { '--pattern', $_ })) }
    # The checks below hold for these exact files: the release's digest of each must be the file checked.
    $verified = @{}
    foreach ($name in $names) {
        $digest = "$(@($existing[0].assets | Where-Object name -ceq $name)[0].digest)"
        $local = (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $digest) { throw "The release of $Tag gives no digest of $name, so its file cannot be tied to the one checked." }
        if ($digest -cne "sha256:$local") { throw "$name is not the file the release of $Tag holds." }
        $verified[$name] = $digest
    }
    # The release by its ID holds exactly the files checked; a rerun of the release workflow replaces a draft's files.
    function Assert-VerifiedRelease([string]$When) {
        $current = "$(gh api "repos/$PublicRepository/releases/$($existing[0].id)" --jq $releaseFilter)" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or -not $current) { throw "Could not read the release of $Tag $When." }
        $currentAssets = @($current.assets)
        if ($currentAssets.Count -ne $verified.Count -or @($currentAssets | Where-Object { $verified[$_.name] -cne "$($_.digest)" }).Count) {
            throw "The files of the release of $Tag changed $When; a rerun of its workflow replaced them."
        }
        $current
    }
    $packagePath = Join-Path $assets 'PrivateFlow-win-x64.zip'
    if ((Get-Item -LiteralPath (Join-Path $assets 'SHA256SUMS')).Length -gt 65536 -or (Get-Item -LiteralPath (Join-Path $assets 'SHA256SUMS.sig')).Length -gt 1024) { throw 'SHA256SUMS or its signature is too large.' }
    if (-not (Test-ReleaseSignature ([IO.File]::ReadAllBytes((Join-Path $assets 'SHA256SUMS'))) ([IO.File]::ReadAllText((Join-Path $assets 'SHA256SUMS.sig'))) $releaseKey)) {
        throw 'SHA256SUMS is not signed by the release key install.ps1 pins; nothing was published.'
    }
    $checksums = [IO.File]::ReadAllText((Join-Path $assets 'SHA256SUMS'))
    $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-ListedChecksum $checksums 'PrivateFlow-win-x64.zip') -cne $packageHash) { throw 'PrivateFlow-win-x64.zip does not match SHA256SUMS.' }
    if ((Get-ListedChecksum $checksums 'manifest.json') -cne (Get-FileHash -LiteralPath (Join-Path $assets 'manifest.json') -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'manifest.json does not match SHA256SUMS.' }
    # Every field the release workflow writes must be of its exact type and value, and none may describe the development build.
    $problem = Get-ManifestProblem ([IO.File]::ReadAllText((Join-Path $assets 'manifest.json'))) $version (Get-Item -LiteralPath $packagePath).Length $packageHash -Public
    if ($problem) { throw "The manifest does not describe this package of $Tag`: $problem." }

    $package = Join-Path $work 'package'
    $layout = Get-ZipLayoutProblem ([IO.File]::ReadAllBytes($packagePath))
    if ($layout) { throw "PrivateFlow-win-x64.zip holds more than the entries the scan reads: $layout." }
    Expand-Archive -LiteralPath $packagePath -DestinationPath $package
    $executable = Join-Path $package 'PrivateFlow.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'The package has no PrivateFlow.exe.' }
    # The executable itself must be this release: a release build's identity is "<version>+release" (Directory.Build.props).
    $identity = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion
    if ($identity -cne "$version+release") { throw "PrivateFlow.exe of the package is $identity, not the release build $version+release." }
    if ($Publisher) { Assert-PackageInventory $package $Publisher $version; Write-Host "Every file of the package matches its inventory signed by $Publisher." }

    # Every published asset is scanned as it is published: the manifest and the checksum files as they are, the ZIP by its own entry
    # names, which extraction may change, and by its extracted files. Its compressed bytes are not searched: they hold no text.
    $entryNames = Join-Path $work 'package-entry-names.txt'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try { [IO.File]::WriteAllLines($entryNames, [string[]]@($zip.Entries | ForEach-Object FullName)) } finally { $zip.Dispose() }
    $published = @($package, $entryNames, (Join-Path $assets 'manifest.json'), (Join-Path $assets 'SHA256SUMS'), (Join-Path $assets 'SHA256SUMS.sig'), $notes)
    & (Join-Path $PSScriptRoot 'export-public.ps1') -Target $snapshot -Commit $commit -AlsoScan $published -DenyText $DenyText -PublicRepository $PublicRepository
    # The draft was built from the public tag, so that tag must hold exactly the snapshot of the development tag.
    $public = Join-Path $work 'public'
    Invoke-Checked git @('clone', '--quiet', '--no-checkout', $PublicRemote, $public)
    Push-Location $public
    try {
        Get-ChildItem -LiteralPath $snapshot -Force | Copy-Item -Destination $public -Recurse -Force
        Invoke-Checked git @('add', '--all')
        if ("$(git write-tree)" -cne "$(git rev-parse "refs/tags/$Tag^{tree}")") { throw "The public tag $Tag does not hold the snapshot of the development tag; nothing was published." }
    } finally { Pop-Location }
    if (-not $existing[0].draft) { Write-Host "The public release of $Tag is already published with these files."; return }
    if ($DryRun) { Write-Host "Dry run: every check passed; the draft release of $Tag was not published. Files and notes are in $work."; return }
    # Published by its ID, just after its files were found unchanged, and read once more afterwards: files a rerun replaced in between
    # take the release back to a draft.
    [void](Assert-VerifiedRelease 'during the checks')
    $release = "repos/$PublicRepository/releases/$($existing[0].id)"
    Invoke-Checked gh @('api', '--method', 'PATCH', $release, '-F', 'draft=false', '-f', "name=Private Flow $version", '-F', "body=@$notes",
        '-F', "prerelease=$(if ($prerelease) { 'true' } else { 'false' })", '-f', "make_latest=$(if ($prerelease) { 'false' } else { 'true' })", '--silent')
    try { [void](Assert-VerifiedRelease 'while it was published') }
    catch {
        Invoke-Checked gh @('api', '--method', 'PATCH', $release, '-F', 'draft=true', '--silent')
        throw "$_ It is a draft again; check it and run -Finish again."
    }
    Write-Host "Published $Tag in $PublicRepository."
} finally {
    Pop-Location
    if (-not $DryRun -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
