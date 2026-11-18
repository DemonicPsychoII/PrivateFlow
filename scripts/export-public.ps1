[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [string]$Commit = 'HEAD'
)
$ErrorActionPreference = 'Stop'
$targetFolder = [IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Target))
if (Test-Path -LiteralPath $targetFolder) {
    if (-not (Test-Path -LiteralPath $targetFolder -PathType Container) -or (Get-ChildItem -LiteralPath $targetFolder -Force | Select-Object -First 1)) {
        throw 'The public snapshot target must be an empty folder.'
    }
} else { New-Item -ItemType Directory -Path $targetFolder | Out-Null }
$archive = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-public-' + [Guid]::NewGuid().ToString('N') + '.zip')
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    git archive --format=zip "--output=$archive" $Commit
    if ($LASTEXITCODE -ne 0) { throw 'Could not archive the requested commit.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $targetFolder
    $workflows = [IO.Path]::GetFullPath((Join-Path $targetFolder '.github/workflows'))
    if (-not $workflows.StartsWith($targetFolder.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected workflow folder.' }
    # The self-hosted runner labels are read from the exported commit's workflows, so this script names none of them.
    $runnerLabels = @()
    if (Test-Path -LiteralPath $workflows) {
        foreach ($workflow in Get-ChildItem -LiteralPath $workflows -Recurse -File -Force) {
            foreach ($match in [regex]::Matches([IO.File]::ReadAllText($workflow.FullName), '(?m)^\s*runs-on:\s*[''"]?([^\s''"$\[]+)')) {
                $label = $match.Groups[1].Value.TrimEnd('-')
                if ($label.Length -ge 4 -and $label -notmatch '^(?:ubuntu|windows|macos)-') { $runnerLabels += $label }
            }
        }
        Remove-Item -LiteralPath $workflows -Recurse -Force
    }
    foreach ($file in Get-ChildItem -LiteralPath $targetFolder -Recurse -File -Force) {
        try {
            $reader = New-Object IO.StreamReader $file.FullName,([Text.UTF8Encoding]::new($false, $true)),$true
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        } catch [Text.DecoderFallbackException] { continue }
        if ($text.IndexOf([char]0) -ge 0) { continue }
        $leaks = $text -match '\.ts[.]net\b' -or @($runnerLabels | Where-Object { $text.Contains($_) }).Count -gt 0
        foreach ($match in [regex]::Matches($text, '(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])')) {
            $address = $null
            if ([Net.IPAddress]::TryParse($match.Value, [ref]$address)) {
                $bytes = $address.GetAddressBytes()
                $leaks = $leaks -or $bytes[0] -eq 10 -or ($bytes[0] -eq 172 -and $bytes[1] -ge 16 -and $bytes[1] -le 31) -or
                    ($bytes[0] -eq 192 -and $bytes[1] -eq 168) -or ($bytes[0] -eq 100 -and $bytes[1] -ge 64 -and $bytes[1] -le 127)
            }
        }
        foreach ($match in [regex]::Matches($text, '\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b', 'IgnoreCase')) {
            $leaks = $leaks -or $match.Value -notmatch '(?:^|[+._@-])no-?reply(?:$|[+._@-])'
        }
        foreach ($match in [regex]::Matches($text, '\b[A-Z]:[\\/]+Users[\\/]+([^\\/\r\n"'']+)', 'IgnoreCase')) {
            $leaks = $leaks -or $match.Groups[1].Value.Trim() -notmatch '^(?:<[^>]+>|%[^%]+%|\$[^\s]+|Public|Default)(?:\s|$)'
        }
        if ($leaks) { throw "Private detail in public snapshot: $($file.FullName.Substring($targetFolder.Length + 1)). Do not publish this folder." }
    }
    Write-Host "Public snapshot exported from $Commit to $targetFolder without history or workflows."
} finally {
    Pop-Location
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
}
