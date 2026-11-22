[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Commit = 'HEAD',
    # Files or folders published beside the snapshot, such as release notes and an extracted package, scanned with the same rules.
    [string[]]$AlsoScan = @(),
    # More private identifiers to refuse everywhere, such as a name or an internal host.
    [string[]]$DenyText = @(),
    # The public repository (owner/name), which the snapshot may name. Every other GitHub repository of this checkout's remotes and of
    # GITHUB_REPOSITORY is a development repository, and its name is refused everywhere.
    [string]$PublicRepository = 'DemonicPsychoII/PrivateFlow'
)
$ErrorActionPreference = 'Stop'
# What the exported commit says about its own snapshot, in public-snapshot/: exclude.txt lists the internal paths left out, every
# file under files/ replaces the file of the same path, such as a short public AGENTS.md, and the files under workflows/ are the
# snapshot's only workflows, such as the public release build. The development workflows and that folder are always left out; a
# snapshot that still names an excluded path is refused.
$snapshotRules = 'public-snapshot'
$alwaysExcluded = '.github/workflows', $snapshotRules
$targetFolder = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Target))
if (Test-Path -LiteralPath $targetFolder) {
    if (-not (Test-Path -LiteralPath $targetFolder -PathType Container) -or (Get-ChildItem -LiteralPath $targetFolder -Force | Select-Object -First 1)) {
        throw 'The public snapshot target must be an empty folder.'
    }
} else { New-Item -ItemType Directory -Path $targetFolder | Out-Null }

# Two kinds of rule. Deny rules name known private identifiers; they apply to every file name, every text and the raw
# bytes of every file, as ASCII or UTF-8 and as UTF-16LE, and no exception lifts them. Pattern rules (private addresses,
# email addresses, profile paths) apply to file names and text; the two narrow exceptions below lift only pattern rules.
# A deny rule is found anywhere, in any letter case. A word rule, one part of a person's name, is found only as a word of its
# own, also in camel case: "Unicode" holds no name, "`nName" and "NameOther" do.
$wordStart = '(?-i:(?<![\p{L}\p{N}])|(?<=[\p{Ll}\p{N}])(?=\p{Lu}))'
$wordEnd = '(?-i:(?![\p{L}\p{N}])|(?<=\p{Ll})(?=\p{Lu}))'
function Find-DeniedText([string]$Text, [object[]]$Deny) {
    foreach ($rule in $Deny) {
        $found = if ($rule.Word) { [regex]::IsMatch($Text, $wordStart + [regex]::Escape($rule.Text) + $wordEnd, 'IgnoreCase') }
            else { $Text.IndexOf($rule.Text, [StringComparison]::InvariantCultureIgnoreCase) -ge 0 }
        if ($found) { return $rule.Reason }
    }
}

function Find-DeniedBytes([byte[]]$Bytes, [object[]]$Deny) {
    # Latin-1 maps each byte to one character, so a byte string search becomes a string search. Content and search
    # term are folded alike, and each identifier is searched in its lower-case and upper-case form, as UTF-8 and as
    # UTF-16LE, so letters outside ASCII, such as an umlaut, are found in either case too. A word rule is searched in the
    # content as it is, with its neighbours read as bytes of UTF-8 or as units of UTF-16LE, and only beside at least four
    # printable ASCII characters on one side, as in a string: a short word would otherwise turn up by chance in compressed data.
    # Text files are searched as text as well, without that condition.
    $latin1 = [Text.Encoding]::GetEncoding(28591)
    $original = $latin1.GetString($Bytes)
    $content = $original.ToLowerInvariant()
    foreach ($rule in $Deny) {
        if ($rule.Word) {
            $utf8 = $wordStart + [regex]::Escape($latin1.GetString([Text.Encoding]::UTF8.GetBytes($rule.Text))) + $wordEnd
            $utf16 = '(?-i:(?<![\p{L}\p{N}]\x00)|(?<=[\p{Ll}\p{N}]\x00)(?=\p{Lu}))' + [regex]::Escape($latin1.GetString([Text.Encoding]::Unicode.GetBytes($rule.Text))) +
                '(?-i:(?![\p{L}\p{N}]\x00)|(?<=\p{Ll}\x00)(?=\p{Lu}\x00))'
            foreach ($pattern in "(?:(?<=[\x20-\x7e]{4})$utf8|$utf8(?=[\x20-\x7e]{4}))", "(?:(?<=(?:[\x20-\x7e]\x00){4})$utf16|$utf16(?=(?:[\x20-\x7e]\x00){4}))") {
                if ([regex]::IsMatch($original, $pattern, 'IgnoreCase')) { return $rule.Reason }
            }
            continue
        }
        foreach ($form in $rule.Text.ToLowerInvariant(), $rule.Text.ToUpperInvariant()) {
            foreach ($encoding in [Text.Encoding]::UTF8, [Text.Encoding]::Unicode) {
                if ($content.Contains($latin1.GetString($encoding.GetBytes($form)).ToLowerInvariant())) { return $rule.Reason }
            }
        }
    }
}

# The private network addresses in a text.
function Get-PrivateAddress([string]$Text) {
    foreach ($match in [regex]::Matches($Text, '(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])')) {
        $address = $null
        if ([Net.IPAddress]::TryParse($match.Value, [ref]$address)) {
            $bytes = $address.GetAddressBytes()
            if ($bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
                ($bytes[0] -eq 192 -and $bytes[1] -eq 168) -or ($bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127)) { $match.Value }
        }
    }
}

function Find-PatternDetail([string]$Text, [switch]$AllowEmail, [switch]$SkipAddresses) {
    if (-not $SkipAddresses) { foreach ($address in Get-PrivateAddress $Text) { return "the private address $address" } }
    foreach ($match in [regex]::Matches($Text, '\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b', 'IgnoreCase')) {
        if (-not $AllowEmail -and $match.Value -notmatch '(?:^|[+._@-])no-?reply(?:$|[+._@-])') { return "the email address $($match.Value)" }
    }
    foreach ($match in [regex]::Matches($Text, '\b[A-Z]:[\\/]+Users[\\/]+([^\\/\r\n"'']+)', 'IgnoreCase')) {
        if ($match.Groups[1].Value.Trim() -notmatch '^(?:<[^>]+>|%[^%]+%|\$[^\s]+|Public|Default)(?:\s|$)') { return 'a user profile path' }
    }
}

# The text with every JSON escape (\uXXXX, \", \\, \/, \b, \f, \n, \r, \t) replaced by its character, for the deny rules only.
# It is decided on the raw text and never on a parsed document, which keeps only one of two duplicate members, and it applies
# to every text file, JSON or not.
function ConvertFrom-JsonEscape([string]$Text) {
    [regex]::Replace($Text, '\\(?:u([0-9A-Fa-f]{4})|(["\\/bfnrt]))', {
            param($match)
            if ($match.Groups[1].Success) { return [string][char][Convert]::ToInt32($match.Groups[1].Value, 16) }
            switch -CaseSensitive ($match.Groups[2].Value) { 'b' { "`b" } 'f' { "`f" } 'n' { "`n" } 'r' { "`r" } 't' { "`t" } default { $match.Groups[2].Value } }
        })
}

# Every text a file's bytes hold, for the deny rules: read as UTF-8 and, separately, as UTF-16LE, whatever the byte-order mark says
# (a reading that fails, holds NUL or, for UTF-16LE, other control characters is no text), each with JSON escapes and then percent
# escapes decoded in turn until nothing changes, at most five rounds, every form on the way included. A backstop against accidents:
# it does not find text in other encodings, compressed or assembled at run time.
function Get-TextForms([byte[]]$Bytes) {
    $readings = New-Object Collections.Generic.List[string]
    try { $readings.Add(([Text.UTF8Encoding]::new($false, $true)).GetString($Bytes).TrimStart([char]0xFEFF)) } catch [Text.DecoderFallbackException] { }
    if ($Bytes.Length % 2 -eq 0) {
        try {
            $utf16 = ([Text.UnicodeEncoding]::new($false, $false, $true)).GetString($Bytes).TrimStart([char]0xFEFF)
            if ($utf16 -notmatch '[\x00-\x08\x0B\x0E-\x1F]') { $readings.Add($utf16) }
        } catch [Text.DecoderFallbackException] { }
    }
    foreach ($reading in $readings) {
        if ($reading.IndexOf([char]0) -ge 0) { continue }
        $current = $reading
        $current
        for ($round = 0; $round -lt 5; $round++) {
            $json = $(if ($current.Contains('\')) { ConvertFrom-JsonEscape $current } else { $current })
            if ($json -cne $current) { $json }
            $percent = $(if ($json.Contains('%')) { [Uri]::UnescapeDataString($json) } else { $json })
            if ($percent -cne $json) { $percent }
            if ($percent -ceq $current) { break }
            $current = $percent
        }
    }
}

# The one exemption from the address pattern, decided on the raw text that gets published and never on a parsed form:
# in the package's own PrivateFlow.deps.json, a line that is exactly one version member as the .NET SDK writes it,
# one member per line. A file with any backslash gets no exemption, so escapes cannot disguise a line.
function Test-DependencyVersionLine([string]$Line) {
    $Line.TrimEnd("`r") -cmatch '\A[ \t]*"(?:assemblyVersion|fileVersion)": "[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+",?[ \t]*\z'
}

function Find-DependencyDetail([string]$Text) {
    if ($Text.Contains('\')) { return Find-PatternDetail $Text }
    foreach ($line in $Text.Split("`n")) {
        $detail = if (Test-DependencyVersionLine $line) { Find-PatternDetail $line -SkipAddresses } else { Find-PatternDetail $line }
        if ($detail) { return $detail }
    }
}

# A pull request or issue number of the development repository, such as "(#48)", in a Markdown text; public documents name none.
function Find-PullRequestReference([string]$Text) {
    $match = [regex]::Match($Text, '(?<![\w&#/])#\d+(?!\w)')
    if ($match.Success) { "the pull request reference $($match.Value)" }
}

# The development repositories by owner/name: every GitHub repository of the remote URLs and the repository of the environment
# (GITHUB_REPOSITORY in a workflow), except the public repository.
function Get-PrivateRepository([string[]]$RemoteUrls, [string]$EnvironmentRepository, [string]$PublicRepository) {
    $found = @(foreach ($url in $RemoteUrls) {
            $match = [regex]::Match("$url".Trim(), 'github\.com[:/]+([A-Za-z0-9-]+/[A-Za-z0-9._-]+?)(?:\.git)?/*\z', 'IgnoreCase')
            if ($match.Success) { $match.Groups[1].Value }
        }) + @("$EnvironmentRepository".Trim())
    @($found | Where-Object { $_ -and $_ -ne $PublicRepository } | Sort-Object -Unique)
}

# The full ID of a commit of the development repository in Text, or nothing. Only an ID that names one of its commits counts, so other
# hex strings, such as checksums, never do.
$developmentCommits = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
function Find-DevelopmentCommit([string]$Text) {
    foreach ($match in [regex]::Matches($Text, '(?<![0-9A-Za-z])[0-9a-fA-F]{40}(?![0-9A-Za-z])')) {
        if ($developmentCommits.Contains($match.Value.ToLowerInvariant())) { return "the ID of a commit of the development repository ($($match.Value))" }
    }
}

# Scans the file names and contents of a folder, or one file.
function Assert-NoPrivateDetail([string]$Path, [object[]]$Deny, [string]$What, [hashtable]$PinnedLicences = @{}) {
    $root = [IO.Path]::GetFullPath($Path)
    $files = if (Test-Path -LiteralPath $root -PathType Leaf) { @(Get-Item -LiteralPath $root -Force) } else { @(Get-ChildItem -LiteralPath $root -Recurse -File -Force) }
    foreach ($file in $files) {
        $name = if ($file.FullName.Length -gt $root.Length) { $file.FullName.Substring($root.Length).TrimStart('\').Replace('\', '/') } else { $file.Name }
        # A name is read as it is and with its JSON and percent escapes decoded, as file contents are.
        $detail = $null
        foreach ($form in @($name) + @(Get-TextForms ([Text.Encoding]::UTF8.GetBytes($name)))) { $detail = Find-DeniedText $form $Deny; if ($detail) { break } }
        if (-not $detail) { $detail = Find-PatternDetail $name }
        if (-not $detail) { $detail = Find-DevelopmentCommit $name }
        if ($detail) { throw "Private detail in $What`: the name $name contains $detail. Do not publish this folder." }
        $bytes = [IO.File]::ReadAllBytes($file.FullName)
        $detail = Find-DeniedBytes $bytes $Deny
        if (-not $detail) { foreach ($form in @(Get-TextForms $bytes)) { $detail = Find-DeniedText $form $Deny; if ($detail) { break } } }
        # In any file, also a binary: as single-byte text and as UTF-16LE strings at either byte alignment, as .NET assemblies store them.
        if (-not $detail) { $detail = Find-DevelopmentCommit ([Text.Encoding]::GetEncoding(28591).GetString($bytes)) }
        if (-not $detail -and $bytes.Length -gt 1) { $detail = Find-DevelopmentCommit ([Text.Encoding]::Unicode.GetString($bytes)) }
        if (-not $detail -and $bytes.Length -gt 2) { $detail = Find-DevelopmentCommit ([Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1)) }
        if (-not $detail) {
            $text = $null
            try {
                $reader = New-Object IO.StreamReader (New-Object IO.MemoryStream (, $bytes)),([Text.UTF8Encoding]::new($false, $true)),$true
                try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
            } catch [Text.DecoderFallbackException] { $text = $null }
            # Files that are not text were searched above for the deny rules only, which the pattern rules would flood.
            if ($null -ne $text -and $text.IndexOf([char]0) -lt 0) {
                $published = $What -ne 'public snapshot'
                # The deny rules were applied to every text form above; the pattern rules apply to the text as it is.
                if (-not $detail) {
                    $detail = if ($published -and $name -ceq 'PrivateFlow.deps.json') { Find-DependencyDetail $text }
                        elseif ($published -and $PinnedLicences.ContainsKey($name) -and
                            $PinnedLicences[$name] -ceq [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '').ToLowerInvariant()) {
                            # A bundled third-party licence text, unchanged from upstream, may name its authors.
                            Find-PatternDetail $text -AllowEmail
                        } else { Find-PatternDetail $text }
                }
                if (-not $detail) { foreach ($form in @(Get-TextForms $bytes)) { $detail = Find-DevelopmentCommit $form; if ($detail) { break } } }
                if (-not $detail -and $name -like '*.md') { foreach ($form in @(Get-TextForms $bytes)) { $detail = Find-PullRequestReference $form; if ($detail) { break } } }
            }
        }
        if ($detail) { throw "Private detail in $What`: $name contains $detail. Do not publish this folder." }
    }
}

$archive = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-public-' + [Guid]::NewGuid().ToString('N') + '.zip')
$repository = Split-Path $PSScriptRoot -Parent
Push-Location $repository
try {
    git archive --format=zip "--output=$archive" $Commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the requested commit.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $targetFolder
    $workflows = [IO.Path]::GetFullPath((Join-Path $targetFolder '.github/workflows'))
    if (-not $workflows.StartsWith($targetFolder.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected workflow folder.' }
    # The deny rules come from the exported commit and from this PC at run time, so this script names none of them.
    $deny = New-Object Collections.Generic.List[object]
    function Add-Deny([string]$Text, [string]$Reason, [switch]$Word) {
        if ($Text -and $Text.Trim().Length -ge $(if ($Word) { 3 } else { 4 })) { $deny.Add([pscustomobject]@{ Text = $Text.Trim(); Reason = $Reason; Word = [bool]$Word }) }
    }
    Add-Deny ('.ts' + '.net') 'a private host name'
    # Every commit of the development repository by its full ID (Find-DevelopmentCommit).
    foreach ($developmentCommit in @(git rev-list --all)) { [void]$developmentCommits.Add($developmentCommit) }
    if ($LASTEXITCODE -ne 0 -or $developmentCommits.Count -eq 0) { throw 'Could not list the commits of the development repository.' }
    if (Test-Path -LiteralPath $workflows) {
        foreach ($workflow in Get-ChildItem -LiteralPath $workflows -Recurse -File -Force) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($workflow.FullName), '(?m)^\s*runs-on:\s*[''"]?([^\s''"$\[]+)')) {
                $label = $match.Groups[1].Value.TrimEnd('-')
                if ($label.Length -ge 4 -and $label -notmatch '^(?:ubuntu|windows|macos)-') { Add-Deny $label 'the internal runner label' }
            }
        }
    }
    foreach ($folder in $env:USERPROFILE, $repository) {
        if ($folder -and $folder -notmatch '[\\/](?:Default|Public)$') { Add-Deny $folder 'a folder path of this PC'; Add-Deny $folder.Replace('\', '/') 'a folder path of this PC' }
    }
    foreach ($email in @(git config --get-all user.email)) { if ($email -notmatch '(?:^|[+._@-])no-?reply(?:$|[+._@-])') { Add-Deny $email 'the email address of this Git identity' } }
    # The people behind the Git identities: every author of the repository's history and this checkout's identity, by name. A name
    # only ever seen with a no-reply or relay address is an account, such as an agent's or a hosting service's, not a person; any
    # other name is refused whole, and each of its parts of three or more letters as a word. Their addresses are refused too.
    # A shallow checkout does not know every author, so it cannot be scanned for them.
    if ("$(git rev-parse --is-shallow-repository)".Trim() -ceq 'true') { throw 'This checkout has a shallow history, so the scan does not know every author of it. Fetch the whole history (git fetch --unshallow) and run this again.' }
    $anonymous = '(?:^|[+._@-])no-?reply(?:$|[+._@-])|@privaterelay\.appleid\.com$'
    $identities = @(git log --all '--format=%an%x09%ae') + @("$(git config user.name)`t$(git config user.email)")
    $people = @($identities | Where-Object { $_ -match '\A([^\t]+)\t(.+)\z' -and $Matches[2].Trim() -notmatch $anonymous } | ForEach-Object { ($_ -split "`t")[0].Trim() } | Sort-Object -Unique)
    foreach ($person in $people) {
        Add-Deny $person 'the name of a Git identity'
        foreach ($part in [regex]::Split($person, '[^\p{L}]+')) { Add-Deny $part 'a part of the name of a Git identity' -Word }
    }
    foreach ($identity in $identities) { if ($identity -match '\t(.+)\z' -and $Matches[1].Trim() -notmatch $anonymous) { Add-Deny $Matches[1].Trim() 'the email address of a Git identity' } }
    foreach ($text in $DenyText) { Add-Deny $text 'a private identifier given with -DenyText' }
    $remoteUrls = @(git config --get-regexp '^remote\..*\.url$' | ForEach-Object { ($_ -split ' ', 2)[-1] })
    foreach ($development in Get-PrivateRepository $remoteUrls $env:GITHUB_REPOSITORY $PublicRepository) { Add-Deny $development 'the name of a development repository' }
    $rules = Join-Path $targetFolder $snapshotRules
    if (-not (Test-Path -LiteralPath (Join-Path $rules 'exclude.txt') -PathType Leaf)) { throw "The commit has no $snapshotRules/exclude.txt, so it does not say which of its paths are internal." }
    $excludedPaths = @([IO.File]::ReadAllLines((Join-Path $rules 'exclude.txt')) | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
    foreach ($excluded in $excludedPaths) {
        if ($excluded -notmatch '\A[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)*\z' -or $excluded -match '(?:\A|/)\.\.?(?:/|\z)') { throw "$snapshotRules/exclude.txt lists $excluded, which is no repository path." }
        # A link to a left-out document or a call of a left-out script would dangle in the snapshot: its path and, when it has an
        # extension, its file name are refused everywhere.
        $reason = "a reference to $excluded, which public snapshots leave out"
        Add-Deny $excluded $reason; Add-Deny $excluded.Replace('/', '\') $reason
        $leaf = $excluded.Split('/')[-1]
        if ($leaf.Contains('.')) { Add-Deny $leaf $reason }
    }
    # Bundled licence texts and their upstream SHA-256, as recorded in the exported commit.
    $pinnedLicences = @{}
    $pins = Join-Path $targetFolder 'THIRD_PARTY_NOTICES.sha256'
    if (Test-Path -LiteralPath $pins) {
        foreach ($line in [IO.File]::ReadAllLines($pins)) {
            if ($line -cmatch '\A([0-9a-f]{64})  (licenses/[A-Za-z0-9._/+-]+)\z') { $pinnedLicences[$Matches[2]] = $Matches[1] }
            elseif ($line.Trim()) { throw "THIRD_PARTY_NOTICES.sha256 has a malformed line: $line" }
        }
    }
    # The public counterparts replace their internal files before the rules and the excluded paths go.
    $replacements = Join-Path $rules 'files'
    if (Test-Path -LiteralPath $replacements -PathType Container) {
        foreach ($replacement in Get-ChildItem -LiteralPath $replacements -Recurse -File -Force) {
            $relative = $replacement.FullName.Substring($replacements.Length).TrimStart('\', '/')
            $replaced = Join-Path $targetFolder $relative
            if (-not (Test-Path -LiteralPath $replaced -PathType Leaf)) { throw "$snapshotRules/files/$($relative.Replace('\', '/')) replaces no file of the commit." }
            Copy-Item -LiteralPath $replacement.FullName -Destination $replaced -Force
        }
    }
    # The public workflows, read before their folder goes with the rules.
    $publicWorkflows = @()
    $publicWorkflowFolder = Join-Path $rules 'workflows'
    if (Test-Path -LiteralPath $publicWorkflowFolder -PathType Container) {
        $publicWorkflows = @(Get-ChildItem -LiteralPath $publicWorkflowFolder -File -Force | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Bytes = [IO.File]::ReadAllBytes($_.FullName) } })
    }
    foreach ($excluded in @($alwaysExcluded) + $excludedPaths) {
        $excludedPath = Join-Path $targetFolder $excluded
        if (Test-Path -LiteralPath $excludedPath) { Remove-Item -LiteralPath $excludedPath -Recurse -Force }
    }
    if ($publicWorkflows.Count) {
        New-Item -ItemType Directory -Force -Path $workflows | Out-Null
        foreach ($publicWorkflow in $publicWorkflows) { [IO.File]::WriteAllBytes((Join-Path $workflows $publicWorkflow.Name), $publicWorkflow.Bytes) }
    }
    Assert-NoPrivateDetail $targetFolder $deny 'public snapshot'
    foreach ($published in $AlsoScan) { Assert-NoPrivateDetail $published $deny 'published file' $pinnedLicences }
    Write-Host "Public snapshot exported from $Commit to $targetFolder without history, development workflows or internal paths."
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
}
