using System.Net;
using System.Net.Http;
using System.Text.Json;
using PrivateFlow.Windows;
using static Harness;

static class UpdateChecks
{
    private static readonly string Hash = new('a', 64);
    private static string Manifest(string version = "0.5.6", long size = 123, string? hash = null) => JsonSerializer.Serialize(new
    { schema = 1, project = "private-flow", board = "win-x64", files = new[] { new { name = "PrivateFlow-win-x64.zip", version, size, sha256 = hash ?? Hash } } });
    private static string Release(string tag = "v0.5.6", bool draft = false, bool prerelease = false, string? digest = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease, assets = new[] {
            new { name = "PrivateFlow-win-x64.zip", size = 123, digest, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/{tag}/PrivateFlow-win-x64.zip" },
            new { name = "manifest.json", size = 456, digest = (string?)null, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/{tag}/manifest.json" } }
    });
    private sealed class FakeHttp(string release, string manifest, HttpStatusCode status = HttpStatusCode.OK, bool offline = false) : HttpMessageHandler
    {
        public int Calls;
        public bool Anonymous = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Anonymous &= request.Headers.Authorization is null && request.Headers.UserAgent.ToString().Contains("PrivateFlow");
            if (offline) throw new HttpRequestException("offline");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/latest") ? release : manifest) });
        }
    }
    public static async Task VerifyReleaseUpdates()
    {
        var server = new UpdateServer(new("https://example.invalid/"), null);
        Check(ReleaseUpdate.UsesServer(server, "reader") && !ReleaseUpdate.UsesServer(null, "reader") && !ReleaseUpdate.UsesServer(server, null)
            && !ReleaseUpdate.UsesServer(server, "") && !ReleaseUpdate.UsesServer(null, null), "only stored server configuration plus a reader token selects integration; legacy token alone selects public releases");
        Check(ReleaseUpdate.VersionFromTag("v0.5.10") > ReleaseUpdate.VersionFromTag("v0.5.9") && new[] { "0.5.6", "v0.5.6-beta", "v0.5", "v0.5.6.1", "v999999999999.5.6" }.All(tag => ReleaseUpdate.VersionFromTag(tag) is null), "release versions compare numerically and require vX.Y.Z");
        async Task<ReleasePackage?> Find(string release, string? manifest = null, HttpStatusCode status = HttpStatusCode.OK, bool offline = false, Version? installed = null)
        {
            using var handler = new FakeHttp(release, manifest ?? Manifest(), status, offline);
            using var client = new HttpClient(handler);
            var found = await ReleaseUpdate.FindAsync(client, installed ?? new Version(0, 5, 5, 0), CancellationToken.None);
            Check(handler.Anonymous && handler.Calls is >= 1 and <= 2, "release requests are anonymous and bounded");
            return found;
        }
        Check(await Find(Release()) is { Version: "0.5.6", Size: 123 } && await Find(Release(digest: "sha256:" + Hash)) is not null, "a newer stable release with a matching manifest offers an update, with or without an asset digest");
        Check(await Find(Release(), installed: new Version(0, 5, 6, 0)) is null && await Find(Release(), installed: new Version(0, 6, 0, 0)) is null, "equal and older releases never offer a downgrade");
        Check(await Find(Release(draft: true)) is null && await Find(Release(prerelease: true)) is null && await Find(Release(tag: "v0.5.6-beta")) is null, "drafts, pre-releases and invalid tags offer nothing");
        Check(await Find(Release(), Manifest(size: 124)) is null && await Find(Release(), Manifest(version: "0.5.7")) is null
            && await Find(Release(), Manifest(hash: "bad")) is null && await Find(Release(digest: "sha256:" + new string('b', 64))) is null,
            "manifest size, version, hash and optional asset digest must agree");
        foreach (var invalid in new[] { "", "null", "[]", "{}", "{", Release().Replace("PrivateFlow-win-x64.zip", "other.zip"), Release().Replace("https://github.com/", "https://example.invalid/") })
            Check(await Find(invalid) is null, "empty, malformed and mismatched release metadata silently offers nothing");
        foreach (var invalid in new[] { "", "null", "[]", "{}", "{", Manifest().Replace("win-x64", "other"), Manifest().Replace("\"schema\":1", "\"schema\":2") })
            Check(await Find(Release(), invalid) is null, "empty, malformed and mismatched manifests silently offer nothing");
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            Check(await Find(Release(), status: status) is null, $"HTTP {(int)status} silently offers nothing");
        Check(await Find(Release(), offline: true) is null, "an offline PC silently offers nothing");
        using var cancelledClient = new HttpClient(new FakeHttp(Release(), Manifest()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Check(await ReleaseUpdate.FindAsync(cancelledClient, new Version(0, 5, 5), cancelled.Token) is null, "cancelled checks silently offer nothing");
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-update-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Check(UpdateServer.Read(root) is null, "no stored server means release updates");
            var file = Path.Combine(root, "update-server.json");
            foreach (var invalid in new[] { "{", "[]", "null", "{}", "{\"CloudUrl\":false}", "{\"CloudUrl\":\"http://example.invalid\"}", "{\"CloudUrl\":\"https://example.invalid\",\"CloudWakeUrl\":42}" })
            { File.WriteAllText(file, invalid); Check(UpdateServer.Read(root) is null, "invalid stored configuration selects release updates without throwing"); }
            File.WriteAllText(file, "{\"CloudUrl\":\"https://example.invalid/base\",\"CloudWakeUrl\":\"https://example.invalid/healthz\"}");
            Check(UpdateServer.Read(root) is { CloudUrl.AbsoluteUri: "https://example.invalid/base/", CloudWakeUrl.AbsoluteUri: "https://example.invalid/healthz" }, "server and wake URL load from the per-user configuration");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void VerifyInstaller()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "PowerShell installer checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "install.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        Check(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "install.ps1")).Contains(UpdateServer.LegacyNotice), "the proactive app notice and installer fallback give the same update-source explanation and recovery command");
        var root = Path.Combine(Path.GetTempPath(), "PrivateFlow-update-script-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var runner = Path.Combine(root, "check.ps1");
            File.WriteAllText(runner, """
                param([string]$Installer, [string]$DataRoot)
                $ErrorActionPreference = 'Stop'
                $env:PRIVATE_FLOW_DATA_DIR = $DataRoot
                $tokens = $null; $parseErrors = $null
                $tree = [Management.Automation.Language.Parser]::ParseFile($Installer, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors) { throw 'Installer syntax errors.' }
                foreach ($definition in $tree.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]}, $false)) { . ([scriptblock]::Create($definition.Extent.Text)) }
                $releaseFixture = @{tag_name='v0.5.6'; draft=$false; prerelease=$false; assets=@(
                    @{name='PrivateFlow-win-x64.zip'; size=3; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/PrivateFlow-win-x64.zip'},
                    @{name='manifest.json'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/manifest.json'})}
                $bytes = [byte[]](1,2,3)
                $fixture = Join-Path $DataRoot 'fixture.zip'; [IO.File]::WriteAllBytes($fixture, $bytes)
                $hash = (Get-FileHash $fixture -Algorithm SHA256).Hash
                $manifestFixture = @{schema=1; project='private-flow'; board='win-x64'; files=@(@{name='PrivateFlow-win-x64.zip'; version='0.5.6'; size=3; sha256=$hash})}
                function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($Headers.Authorization) { throw 'Release request carried credentials.' }
                    if ($Uri -like '*/releases/latest') { return $releaseFixture }; return $manifestFixture
                }
                function Invoke-WebRequest { param($Uri, $Headers, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($OutFile) { [IO.File]::WriteAllBytes($OutFile, [byte[]](1,2,3)) }
                }
                function Refused([scriptblock]$Action) { try { & $Action | Out-Null } catch { return }; throw "Invalid package was accepted: $Action" }
                $package = Save-ReleaseBuild $DataRoot
                if ((Get-Item $package).Length -ne 3) { throw 'Valid release failed.' }
                $releaseFixture.assets[0].digest = "sha256:$hash"; Save-ReleaseBuild $DataRoot | Out-Null
                $releaseFixture.assets[0].digest = 'sha256:bad'; Refused { Save-ReleaseBuild $DataRoot }
                $releaseFixture.assets[0].digest = $null
                $manifestFixture.files[0].size = 4; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].size = 3
                $manifestFixture.files[0].version = '0.5.7'; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].version = '0.5.6'
                $manifestFixture.files[0].sha256 = 'a' * 64; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].sha256 = $hash
                $releaseFixture.assets[0].size = 4; $manifestFixture.files[0].size = 4; Refused { Save-ReleaseBuild $DataRoot }
                $releaseFixture.assets[0].size = 3; $manifestFixture.files[0].size = 3
                $installation = Join-Path $DataRoot 'installed'; New-Item -ItemType Directory -Force $installation | Out-Null
                $executable = Join-Path $installation 'PrivateFlow.exe'
                if (-not (Test-Path $executable)) {
                    Add-Type -TypeDefinition '[assembly: System.Reflection.AssemblyFileVersion("0.5.7.0")] public class InstalledVersionFixture { public static void Main() {} }' -OutputAssembly $executable
                }
                if ([Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileBuildPart -ne 7) { throw 'The executable fixture has no version resource.' }
                Assert-NewerRelease '0.5.8' $installation
                Refused { Assert-NewerRelease '0.5.7' $installation }
                Refused { Assert-NewerRelease '0.5.6' $installation }
                Assert-NewerRelease '0.5.6' $installation -Force
                Refused { Save-ReleaseBuild $DataRoot -InstallFolder $installation }
                Save-ReleaseBuild $DataRoot -InstallFolder $installation -Force | Out-Null
                function Read-Host { param($Prompt, [switch]$AsSecureString) ConvertTo-SecureString 'test-reader-token' -AsPlainText -Force }
                & $Installer -SetCloudToken -CloudUrl 'https://example.invalid' -CloudWakeUrl 'https://example.invalid/healthz'
                $stored = Get-Content (Join-Path $DataRoot 'update-server.json') -Raw | ConvertFrom-Json
                if ($stored.CloudUrl -ne 'https://example.invalid' -or $stored.CloudWakeUrl -ne 'https://example.invalid/healthz') { throw 'Server URLs were not stored.' }
                $secure = (Get-Content (Join-Path $DataRoot 'cloud-reader.token') -Raw).Trim() | ConvertTo-SecureString
                if ($secure.Length -ne 17) { throw 'Reader token did not survive DPAPI.' }
                $protectedBefore = Get-Content (Join-Path $DataRoot 'cloud-reader.token') -Raw
                function Read-Host { param($Prompt, [switch]$AsSecureString) New-Object Security.SecureString }
                & $Installer -SetCloudToken -CloudUrl 'https://example.invalid/changed'
                if ((Get-Content (Join-Path $DataRoot 'cloud-reader.token') -Raw) -cne $protectedBefore -or
                    (Get-Content (Join-Path $DataRoot 'update-server.json') -Raw | ConvertFrom-Json).CloudUrl -ne 'https://example.invalid/changed') { throw 'Saving URLs did not retain the existing token.' }
                & $Installer -SetCloudToken -CloudUrl 'https://example.invalid' -CloudWakeUrl 'https://example.invalid/healthz'
                function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($Uri -ne 'https://example.invalid/api/firmware/latest?project=private-flow&channel=integration' -or $Headers.Authorization -ne 'Bearer test-reader-token') { throw 'Wrong integration source or token.' }
                    throw 'INTEGRATION_REQUEST'
                }
                try { & $Installer -FromCloud; throw 'No integration request.' } catch { if ($_ -notlike '*INTEGRATION_REQUEST*') { throw } }
                Remove-Item (Join-Path $DataRoot 'update-server.json')
                function Invoke-RestMethod { param($Uri) if ($Uri -like '*/releases/latest') { return $releaseFixture }; return $manifestFixture }
                foreach ($mode in 'FromRelease','FromCloud') {
                    $parameters = @{ Destination = $installation }; $parameters[$mode] = $true
                    try { & $Installer @parameters; throw 'An older release was accepted.' } catch { if ($_ -notlike '*not newer than installed version*') { throw } }
                    function Invoke-WebRequest { throw 'PACKAGE_DOWNLOAD' }
                    try { & $Installer @parameters -Force; throw 'Force did not reach the download.' } catch { if ($_ -notlike '*PACKAGE_DOWNLOAD*') { throw } }
                }
                Add-Type -TypeDefinition 'public class MissingReleaseResponse { public int StatusCode { get { return 404; } } } public class MissingReleaseException : System.Exception { public MissingReleaseResponse Response { get { return new MissingReleaseResponse(); } } }'
                function Invoke-RestMethod { throw (New-Object MissingReleaseException) }
                try { Save-ReleaseBuild $DataRoot; throw 'Missing release was accepted.' } catch { if ($_ -notlike '*public repository or its latest stable release could not be found (HTTP 404)*') { throw } }
                function Invoke-RestMethod { throw 'PUBLIC_RELEASE_REQUEST' }
                try { & $Installer -FromCloud; throw 'No release fallback.' } catch { if ($_ -notlike '*PUBLIC_RELEASE_REQUEST*') { throw } }
                Write-Host 'PASS installer verification, stored configuration and legacy-token release fallback'
                """);
            var shells = new[] { Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "pwsh.exe" };
            foreach (var shell in shells)
            {
                var start = new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                if (Path.GetFileName(shell) == "powershell.exe")
                    start.Environment["PSModulePath"] = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "Modules");
                foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", runner, "-Installer", Path.Combine(repository.FullName, "scripts", "install.ps1"), "-DataRoot", root }) start.ArgumentList.Add(argument);
                using var process = System.Diagnostics.Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(true); throw new Exception("Installer check timed out."); }
                Check(process.ExitCode == 0 && output.Result.Contains("PASS installer verification"), $"{Path.GetFileName(shell)} verifies release downloads, rejects corrupt bytes and mismatched metadata, stores URLs and DPAPI token, and falls back for a legacy token ({errors.Result.Trim()})");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
