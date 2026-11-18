[CmdletBinding()]
param([switch]$Portable)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskRepo = Split-Path $PSScriptRoot -Parent
Push-Location $taskRepo
try {
    dotnet build src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    dotnet run --project tests/PrivateFlow.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Safety checks failed.' }
    if ($Portable) {
        dotnet publish src/PrivateFlow.Windows/PrivateFlow.Windows.csproj -c Release -r win-x64 --self-contained true -o artifacts/app
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
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
    }
} finally { Pop-Location }
