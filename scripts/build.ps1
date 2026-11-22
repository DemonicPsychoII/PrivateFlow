[CmdletBinding()]
param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskRepo = Split-Path $PSScriptRoot -Parent
Push-Location $taskRepo
try {
    dotnet build src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    # The verification host, which runs the verification and evaluation modes; it is not part of the package.
    dotnet build src/PrivateFlow.Verification/PrivateFlow.Verification.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Verification host build failed.' }
    dotnet run --project tests/PrivateFlow.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Safety checks failed.' }
    # The release helpers are Python because the Linux CI guest runs them; build.sh always runs these checks.
    $taskPython = Get-Command python3, python -CommandType Application -ErrorAction SilentlyContinue | Where-Object { $_.Source -notlike '*\WindowsApps\*' } | Select-Object -First 1
    if ($taskPython) {
        # unittest reports on standard error. Under a caller that redirects every stream, such as full-eval.ps1, Windows PowerShell 5.1
        # would turn its first line into a terminating error, so the exit code alone decides.
        $taskErrorPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & $taskPython.Source -I scripts/release_test.py 2>&1 | ForEach-Object { "$_" } | Out-Host } finally { $ErrorActionPreference = $taskErrorPreference }
        if ($LASTEXITCODE -ne 0) { throw 'Release helper checks failed.' }
    } else { Write-Warning 'Python not found: the release helper checks (scripts/release_test.py) were skipped; build.sh runs them in CI.' }
    if ($Portable) {
        dotnet publish src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        if ($taskPython) {
            & $taskPython.Source -I scripts/release.py check-symbols artifacts/app
            if ($LASTEXITCODE -ne 0) { throw 'The published binaries name this PC or the development repository.' }
        }
        Copy-Item -LiteralPath README.md,LICENSE,THIRD_PARTY_NOTICES.md -Destination artifacts/app
        New-Item -ItemType Directory -Force -Path artifacts/app/scripts | Out-Null
        Copy-Item -LiteralPath scripts/setup-engine.ps1,scripts/setup-rewrite.ps1,scripts/settings-file.ps1,scripts/models.json,scripts/install.ps1,scripts/uninstall.ps1 -Destination artifacts/app/scripts
        Compress-Archive -Path artifacts/app/* -DestinationPath artifacts/PrivateFlow-win-x64.zip -Force
        $taskCommit = git rev-parse HEAD
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the source commit.' }
        $taskVersion = ([xml](Get-Content src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ }
        $taskManifest = @{ schema = 1; project = 'private-flow'; board = 'win-x64'; commit = $taskCommit; dirty = [bool](git status --porcelain); built_at = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'); merge_commit = $null
            files = @(@{ name = 'PrivateFlow-win-x64.zip'; profile = 'release'; version = [string]$taskVersion; size = (Get-Item artifacts/PrivateFlow-win-x64.zip).Length; sha256 = (Get-FileHash artifacts/PrivateFlow-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant() })
        } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText((Join-Path $taskRepo 'artifacts/manifest.json'), $taskManifest, (New-Object Text.UTF8Encoding $false))
        Write-Host 'Portable app: artifacts/app/PrivateFlow.exe'
        # The verification host beside the package, and its proof that the package carries no verification code and refuses the modes
        # that moved to the host.
        dotnet publish src/PrivateFlow.Verification/PrivateFlow.Verification.csproj -c Release -r win-x64 --self-contained true -o artifacts/verification
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the verification host failed.' }
        $taskCheckReport = Join-Path $taskRepo 'artifacts/package-check.txt'
        $taskPreviousData = $env:PRIVATE_FLOW_DATA_DIR
        try {
            $env:PRIVATE_FLOW_DATA_DIR = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-package-check-' + [Guid]::NewGuid().ToString('N'))
            $taskCheck = Start-Process -FilePath (Join-Path $taskRepo 'artifacts/verification/PrivateFlow.Verification.exe') -ArgumentList @('--check-package', "`"$(Join-Path $taskRepo 'artifacts/app')`"", "`"$taskCheckReport`"") -PassThru -Wait
        } finally { $env:PRIVATE_FLOW_DATA_DIR = $taskPreviousData }
        Get-Content -LiteralPath $taskCheckReport | Write-Host
        if ($taskCheck.ExitCode -ne 0) { throw 'The package check failed.' }
        # A host given another app build refuses every mode: a copy of the host's build whose PrivateFlow.dll names another commit, by
        # one character of its identity, must end --check-package with exit code 4 and say why.
        $taskOther = Join-Path ([IO.Path]::GetTempPath()) ('PrivateFlow-other-build-' + [Guid]::NewGuid().ToString('N'))
        try {
            Copy-Item -Recurse -LiteralPath src/PrivateFlow.Verification/bin/Release/net10.0-windows -Destination $taskOther
            $taskAssembly = Join-Path $taskOther 'PrivateFlow.dll'
            $taskBytes = [IO.File]::ReadAllBytes($taskAssembly)
            $taskIdentity = (Get-Item -LiteralPath $taskAssembly).VersionInfo.ProductVersion
            $taskAt = [Text.Encoding]::GetEncoding(28591).GetString($taskBytes).IndexOf($taskIdentity, [StringComparison]::Ordinal)
            if ($taskAt -lt 0) { throw "The identity $taskIdentity was not found in PrivateFlow.dll." }
            $taskLast = $taskAt + $taskIdentity.Length - 1
            $taskBytes[$taskLast] = $(if ($taskBytes[$taskLast] -eq [byte][char]'0') { [byte][char]'1' } else { [byte][char]'0' })
            [IO.File]::WriteAllBytes($taskAssembly, $taskBytes)
            $taskOtherError = Join-Path $taskOther 'refusal.txt'
            $taskRefusal = Start-Process -FilePath dotnet -ArgumentList @("`"$(Join-Path $taskOther 'PrivateFlow.Verification.dll')`"", '--check-package', "`"$(Join-Path $taskRepo 'artifacts/app')`"", "`"$(Join-Path $taskOther 'report.txt')`"") -PassThru -Wait -NoNewWindow -RedirectStandardError $taskOtherError
            $taskMessage = (Get-Content -LiteralPath $taskOtherError -Raw).Trim()
            if ($taskRefusal.ExitCode -ne 4 -or $taskMessage -notmatch 'built with Private Flow' -or (Test-Path -LiteralPath (Join-Path $taskOther 'report.txt'))) { throw "A host with another app build was not refused (exit code $($taskRefusal.ExitCode)): $taskMessage" }
            Write-Host "PASS a verification host given another app build runs nothing (exit code 4: $taskMessage)"
        } finally { Remove-Item -LiteralPath $taskOther -Recurse -Force -ErrorAction SilentlyContinue }
        Write-Host 'Verification host: artifacts/verification/PrivateFlow.Verification.exe'
    }
} finally { Pop-Location }
