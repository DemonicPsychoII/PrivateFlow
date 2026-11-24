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
    private static string Release(string tag = "v0.5.6", bool draft = false, bool prerelease = false, string? digest = null, int checksumAssets = 0) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease, assets = new[] {
            new { name = "PrivateFlow-win-x64.zip", size = 123, digest, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/{tag}/PrivateFlow-win-x64.zip" },
            new { name = "manifest.json", size = 456, digest = (string?)null, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/{tag}/manifest.json" } }
            .Concat(Enumerable.Repeat(new { name = "SHA256SUMS", size = 200, digest = (string?)null, browser_download_url = $"https://github.com/{ReleaseUpdate.Repository}/releases/download/{tag}/SHA256SUMS" }, checksumAssets))
    });
    private static string Checksums(string? hash = null) => $"{hash ?? Hash}  PrivateFlow-win-x64.zip\n{new string('b', 64)}  manifest.json\n";
    private sealed class FakeHttp(string release, string manifest, HttpStatusCode status = HttpStatusCode.OK, bool offline = false, string? checksums = null) : HttpMessageHandler
    {
        public int Calls;
        public bool Anonymous = true;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Anonymous &= request.Headers.Authorization is null && request.Headers.UserAgent.ToString().Contains("PrivateFlow");
            if (offline) throw new HttpRequestException("offline");
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(path.EndsWith("/latest") ? release : path.EndsWith("/SHA256SUMS") ? checksums ?? "" : manifest) });
        }
    }
    public static async Task VerifyReleaseUpdates()
    {
        var server = new UpdateServer(new("https://example.invalid/"), null);
        Check(ReleaseUpdate.UsesServer(server, "reader") && !ReleaseUpdate.UsesServer(null, "reader") && !ReleaseUpdate.UsesServer(server, null)
            && !ReleaseUpdate.UsesServer(server, "") && !ReleaseUpdate.UsesServer(null, null), "only stored server configuration plus a reader token selects integration; legacy token alone selects public releases");
        Check(ReleaseUpdate.VersionFromTag("v0.5.10") > ReleaseUpdate.VersionFromTag("v0.5.9") && new[] { "0.5.6", "v0.5.6-beta", "v0.5", "v0.5.6.1", "v999999999999.5.6" }.All(tag => ReleaseUpdate.VersionFromTag(tag) is null), "release versions compare numerically and require vX.Y.Z");
        async Task<ReleasePackage?> Find(string release, string? manifest = null, HttpStatusCode status = HttpStatusCode.OK, bool offline = false, Version? installed = null, string? checksums = null)
        {
            using var handler = new FakeHttp(release, manifest ?? Manifest(), status, offline, checksums);
            using var client = new HttpClient(handler);
            var found = await ReleaseUpdate.FindAsync(client, installed ?? new Version(0, 5, 5, 0), CancellationToken.None);
            Check(handler.Anonymous && handler.Calls is >= 1 and <= 3, "release requests are anonymous and bounded");
            return found;
        }
        Check(await Find(Release()) is { Version: "0.5.6", Size: 123 } && await Find(Release(digest: "sha256:" + Hash)) is not null, "a newer stable release with a matching manifest offers an update, with or without an asset digest");
        Check(await Find(Release(), installed: new Version(0, 5, 6, 0)) is null && await Find(Release(), installed: new Version(0, 6, 0, 0)) is null, "equal and older releases never offer a downgrade");
        Check(await Find(Release(draft: true)) is null && await Find(Release(prerelease: true)) is null && await Find(Release(tag: "v0.5.6-beta")) is null, "drafts, pre-releases and invalid tags offer nothing");
        Check(await Find(Release(), Manifest(size: 124)) is null && await Find(Release(), Manifest(version: "0.5.7")) is null
            && await Find(Release(), Manifest(hash: "bad")) is null && await Find(Release(digest: "sha256:" + new string('b', 64))) is null,
            "manifest size, version, hash and optional asset digest must agree");
        Check(await Find(Release(checksumAssets: 1), checksums: Checksums()) is { Sha256: var listed } && listed == Hash
            && await Find(Release(checksumAssets: 1), checksums: Checksums().Replace("\n", "\r\n")) is not null,
            "a release with SHA256SUMS offers the update when its line for the package agrees with the manifest");
        foreach (var checksums in new[] { "", Checksums(new string('c', 64)), Checksums(Hash.ToUpperInvariant()), Checksums().Replace("  ", " "),
            Checksums() + Checksums(), $"{new string('b', 64)}  manifest.json\n", Checksums().Replace("PrivateFlow-win-x64.zip", "../PrivateFlow-win-x64.zip"), Checksums() + "garbage\n" })
            Check(await Find(Release(checksumAssets: 1), checksums: checksums) is null, "a missing, malformed or disagreeing SHA256SUMS offers nothing");
        Check(await Find(Release(checksumAssets: 2), checksums: Checksums()) is null
            && await Find(Release(checksumAssets: 1).Replace("download/v0.5.6/SHA256SUMS", "download/v0.5.5/SHA256SUMS"), checksums: Checksums()) is null,
            "two checksum files or one outside the release's own download folder offer nothing");
        Check(ReleaseUpdate.ListedChecksum(Checksums(), "manifest.json") == new string('b', 64) && ReleaseUpdate.ListedChecksum(Checksums(), "other.zip") is null
            && ReleaseUpdate.ListedChecksum(Checksums(), "privateflow-win-x64.zip") is null && ReleaseUpdate.ListedChecksum("\n" + Checksums() + "\r\n\r\n", "manifest.json") is not null,
            "SHA256SUMS lists each file by its exact name, blank lines aside");
        // The same cases are refused by scripts/release.py (scripts/release_test.py) and by install.ps1 below.
        var line = $"{Hash}  PrivateFlow-win-x64.zip";
        foreach (var invalid in new[] { line + "\r\r\n", line + " \n", line + "\n" + new string('b', 64) + "  privateflow-WIN-x64.zip", line + "\n" + new string('b', 64) + "  dir\\x.zip",
            line + "\n" + string.Concat(Enumerable.Range(0, 1000).Select(index => $"{new string('b', 64)}  f{index}.zip\n")) })
            Check(ReleaseUpdate.ListedChecksum(invalid, "PrivateFlow-win-x64.zip") is null, "a stray carriage return, trailing space, case-only repeat, path or file over 64 KiB yields no hash");
        foreach (var invalid in new[] { "", "null", "[]", "{}", "{", Release().Replace("PrivateFlow-win-x64.zip", "other.zip"), Release().Replace("https://github.com/", "https://example.invalid/") })
            Check(await Find(invalid) is null, "empty, malformed and mismatched release metadata silently offers nothing");
        foreach (var invalid in new[] { "", "null", "[]", "{}", "{", Manifest().Replace("win-x64", "other"), Manifest().Replace("\"schema\":1", "\"schema\":2"),
            Manifest().Replace("\"project\":\"private-flow\"", "\"project\":[\"private-flow\"]"), Manifest().Replace("\"version\":\"0.5.6\"", "\"version\":[\"0.5.6\"]"),
            Manifest().Replace("\"size\":123", "\"size\":\"123\""), Manifest(hash: Hash.ToUpperInvariant()), Manifest().Replace("\"files\":[{", "\"files\":[{\"name\":\"other.zip\"},{"), "[" + Manifest() + "]",
            Manifest().Replace("\"schema\":1", "\"schema\":1,\"note\":\"x\""), Manifest().Replace("\"name\":", "\"note\":\"x\",\"name\":"),
            Manifest().Replace("\"schema\":1", "\"schema\":1,\"schema\":1"), Manifest().Replace("\"name\":", "\"size\":123,\"name\":"), Manifest().Replace("\"schema\":1", "/* x */\"schema\":1") })
            Check(await Find(Release(), invalid) is null, "empty, malformed and mismatched manifests silently offer nothing");
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError })
            Check(await Find(Release(), status: status) is null, $"HTTP {(int)status} silently offers nothing");
        Check(await Find(Release(), offline: true) is null, "an offline PC silently offers nothing");
        // The manual update check says "up to date" only when the latest release was read and is not newer, and otherwise that the check failed.
        async Task<ReleaseLookup> Lookup(string release, HttpStatusCode status = HttpStatusCode.OK, bool offline = false, Version? installed = null)
        {
            using var handler = new FakeHttp(release, Manifest(), status, offline, null);
            using var client = new HttpClient(handler);
            return await ReleaseUpdate.LookupAsync(client, installed ?? new Version(0, 5, 5, 0), CancellationToken.None);
        }
        Check(await Lookup(Release(), installed: new Version(0, 5, 6, 0)) is { Package: null, UpToDate: true } && await Lookup(Release(), installed: new Version(0, 6, 0, 0)) is { Package: null, UpToDate: true }
            && await Lookup(Release()) is { Package: not null, UpToDate: false },
            "an equal or older latest release is up to date; a newer one is offered");
        Check(await Lookup(Release(), offline: true) is { Package: null, UpToDate: false } && await Lookup(Release(), status: HttpStatusCode.Forbidden) is { Package: null, UpToDate: false }
            && await Lookup("{") is { Package: null, UpToDate: false } && await Lookup(Release(tag: "v0.5.6-beta")) is { Package: null, UpToDate: false },
            "an offline PC, a refused request, malformed metadata or an invalid tag is a failed check, not up to date");
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
            Check(UpdateServer.Read(root) is { CloudUrl.AbsoluteUri: "https://example.invalid/base/", CloudWakeUrl.AbsoluteUri: "https://example.invalid/healthz", SourceRepository: null }, "server and wake URL load from the per-user configuration");
            // The repository the server's builds are compared in comes from the same file; the package names none.
            File.WriteAllText(file, "{\"CloudUrl\":\"https://example.invalid\",\"SourceRepository\":\"example-owner/internal-app\"}");
            Check(UpdateServer.Read(root) is { SourceRepository: "example-owner/internal-app" }, "the source repository loads from the per-user configuration");
            var installed = new Version(0, 5, 20, 0);
            string commitA = new('a', 40), commitB = new('b', 40);
            Check(UpdateServer.Offers("0.5.21", commitB, installed, commitA) && UpdateServer.Offers("0.5.21", commitB, installed, null) && UpdateServer.Offers("0.5.20", commitB, installed, commitA)
                && !UpdateServer.Offers("0.5.19", commitB, installed, commitA) && !UpdateServer.Offers("0.5.19", commitB, installed, null)
                && !UpdateServer.Offers("0.5.20", commitB, installed, null) && !UpdateServer.Offers("0.5.20", commitA.ToUpperInvariant(), installed, commitA)
                && !UpdateServer.Offers("", commitB, installed, commitA) && !UpdateServer.Offers("0.5", commitB, installed, commitA) && !UpdateServer.Offers("x.5.21", commitB, installed, commitA),
                "a server build is never offered at a lower or unreadable version; at the same version only to a development build, for another commit");
            foreach (var invalid in new[] { "42", "\"\"", "\"example-owner\"", "\"example-owner/internal-app/compare\"", "\"../x\"", "\"owner/app?x\"" })
            { File.WriteAllText(file, "{\"CloudUrl\":\"https://example.invalid\",\"SourceRepository\":" + invalid + "}"); Check(UpdateServer.Read(root) is null, $"a malformed source repository ({invalid}) is refused with the whole configuration"); }
        }
        finally { Directory.Delete(root, true); }
    }

    public static void VerifyInstaller()
    {
        if (!OperatingSystem.IsWindows()) { Check(true, "PowerShell installer checks run on Windows"); return; }
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "install.ps1"))) repository = repository.Parent;
        if (repository is null) throw new Exception("The checkout was not found.");
        Check(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "install.ps1")).Contains("\"" + PrivateFlow.Core.UpdateGate.InstallerMutexPrefix + "$env:USERNAME\""),
            "install.ps1 holds the mutex under the name the app checks before it starts, so a copy started by hand during an install is refused");
        Check(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "install.ps1")).Contains(UpdateServer.LegacyNotice),"the proactive app notice and installer fallback give the same update-source explanation and recovery command");
        // The Visual C++ runtime a fresh Windows lacks: the installer, the README and the app's card name the same Microsoft download.
        Check(File.ReadAllText(Path.Combine(repository.FullName, "scripts", "install.ps1")).Contains(PrivateFlow.Core.SpeechRuntime.VisualCppDownload)
            && File.ReadAllText(Path.Combine(repository.FullName, "README.md")).Contains(PrivateFlow.Core.SpeechRuntime.VisualCppDownload)
            && PrivateFlow.Core.SpeechRuntime.VisualCppRuntimeMissing.Contains(PrivateFlow.Core.SpeechRuntime.VisualCppDownload),
            "the installer, the README and the app name the Microsoft download of the Visual C++ runtime");
        Check(PrivateFlow.Core.SpeechRuntime.LoadFailedMessage("ggml.dll", 126, "The specified module could not be found.", false) == PrivateFlow.Core.SpeechRuntime.VisualCppRuntimeMissing
            && PrivateFlow.Core.SpeechRuntime.LoadFailedMessage("ggml.dll", 126, "The specified module could not be found.", true) == "Could not load ggml.dll from the speech runtime: The specified module could not be found."
            && PrivateFlow.Core.SpeechRuntime.LoadFailedMessage("ggml.dll", 5, "Access is denied.", false).StartsWith("Could not load ggml.dll")
            && PrivateFlow.Core.SpeechRuntime.VisualCppRuntimePresent(1, false) && PrivateFlow.Core.SpeechRuntime.VisualCppRuntimePresent(null, true)
            && !PrivateFlow.Core.SpeechRuntime.VisualCppRuntimePresent(0, false) && !PrivateFlow.Core.SpeechRuntime.VisualCppRuntimePresent(null, false) && !PrivateFlow.Core.SpeechRuntime.VisualCppRuntimePresent("1", false),
            "a missing DLL while loading the runtime says to install the Visual C++ runtime only when it is missing; it counts as installed by its registry value 1 or its DLLs");
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
                $hash = (Get-FileHash $fixture -Algorithm SHA256).Hash.ToLowerInvariant()
                $manifestFixture = [pscustomobject]@{schema=1; project='private-flow'; board='win-x64'; files=@([pscustomobject]@{name='PrivateFlow-win-x64.zip'; version='0.5.6'; size=3; sha256=$hash})}
                function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($Headers.Authorization) { throw 'Release request carried credentials.' }
                    if ($Uri -like '*/releases/latest') { return $releaseFixture }; return $manifestFixture
                }
                # The manifest is downloaded as text, like the package; the fixture writes it from $manifestFixture.
                function Invoke-WebRequest { param($Uri, $Headers, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($Uri -like '*/manifest.json') { [IO.File]::WriteAllText($OutFile, ($manifestFixture | ConvertTo-Json -Depth 5)); return }
                    if ($OutFile) { [IO.File]::WriteAllBytes($OutFile, [byte[]](1,2,3)) }
                }
                function Refused([scriptblock]$Action) { try { & $Action | Out-Null } catch { return }; throw "Invalid package was accepted: $Action" }
                $package = Save-ReleaseBuild $DataRoot
                if ((Get-Item $package.Path).Length -ne 3 -or $package.Version -cne '0.5.6') { throw 'Valid release failed.' }
                $releaseFixture.assets[0].digest = "sha256:$hash"; Save-ReleaseBuild $DataRoot | Out-Null
                $releaseFixture.assets[0].digest = 'sha256:bad'; Refused { Save-ReleaseBuild $DataRoot }
                $releaseFixture.assets[0].digest = $null
                $manifestFixture.files[0].size = 4; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].size = 3
                $manifestFixture.files[0].version = '0.5.7'; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].version = '0.5.6'
                $manifestFixture.files[0].sha256 = 'a' * 64; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].sha256 = $hash
                $releaseFixture.assets[0].size = 4; $manifestFixture.files[0].size = 4; Refused { Save-ReleaseBuild $DataRoot }
                $releaseFixture.assets[0].size = 3; $manifestFixture.files[0].size = 3
                # Exact manifest types and values: a one-element array is no string, a text is no number, one package only.
                $manifestFixture.project = @('private-flow'); Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.project = 'private-flow'
                $manifestFixture.files[0].version = @('0.5.6'); Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].version = '0.5.6'
                $manifestFixture.files[0].size = '3'; Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].size = 3
                $manifestFixture.files[0].sha256 = $hash.ToUpperInvariant(); Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files[0].sha256 = $hash
                $onePackage = $manifestFixture.files; $manifestFixture.files = @($onePackage[0], $onePackage[0]); Refused { Save-ReleaseBuild $DataRoot }; $manifestFixture.files = $onePackage
                Save-ReleaseBuild $DataRoot | Out-Null
                # The release the user confirmed in the app: the one found must be exactly it.
                Save-ReleaseBuild $DataRoot -ExpectedTag 'v0.5.6' | Out-Null
                try { Save-ReleaseBuild $DataRoot -ExpectedTag 'v0.5.5'; throw 'A release other than the confirmed one was accepted.' } catch { if ($_ -notlike '*not v0.5.5, the release you confirmed*') { throw } }
                # The setup result the installer stores, checked as SpeechSetup.ReadResult checks it in the app.
                $resultRoot = Join-Path $DataRoot 'result-root'; New-Item -ItemType Directory -Force (Join-Path $resultRoot 'runtime\cpu'), (Join-Path $resultRoot 'models'), (Join-Path $DataRoot 'outside-target') | Out-Null
                $junction = Join-Path $resultRoot 'runtime\junction'
                if (-not (Test-Path -LiteralPath $junction)) { New-Item -ItemType Junction -Path $junction -Target (Join-Path $DataRoot 'outside-target') | Out-Null }
                function New-SetupResult { [ordered]@{ EnginePath = (Join-Path $resultRoot 'runtime\cpu\whisper-cli.exe'); ModelPath = (Join-Path $resultRoot 'models\ggml-base.bin'); MeetingModelPath = ''
                    VadModelPath = (Join-Path $resultRoot 'models\ggml-silero-v6.2.0.bin'); SpeechBackend = 'auto'; SpeechModel = 'auto'; SpeechPathsCustom = $false; SpeechSetupVersion = '0.5.19+test' } }
                $read = Read-SetupResult (New-SetupResult | ConvertTo-Json) $resultRoot
                if ($read.Problem -or $read.Values.Count -ne 8 -or $read.Values.EnginePath -ne (New-SetupResult).EnginePath) { throw "A valid setup result was refused: $($read.Problem)" }
                foreach ($change in { $args[0].AutoInsert = $true }, { $args[0].EnginePath = Join-Path $DataRoot 'elsewhere\whisper-cli.exe' }, { $args[0].ModelPath = Join-Path $resultRoot 'models\..\outside.bin' },
                    { $args[0].EnginePath = Join-Path $junction 'whisper-cli.exe' }, { $args[0].VadModelPath = '' }, { $args[0].EnginePath = 42 }, { $args[0].SpeechBackend = 'rocm' }, { $args[0].SpeechModel = 'tiny' },
                    { $args[0].SpeechSetupVersion = 'anything goes' }, { $args[0].SpeechPathsCustom = $true }) {
                    $changed = New-SetupResult; & $change $changed
                    if (-not (Read-SetupResult ($changed | ConvertTo-Json) $resultRoot).Problem) { throw "A setup result changed by { $change } was accepted." }
                }
                foreach ($invalidText in 'not json', '[1, 2]', '') { if (-not (Read-SetupResult $invalidText $resultRoot).Problem) { throw "A setup result that is no JSON object was accepted: $invalidText" } }
                # Removed as a link, never through it, so the check's own cleanup does not meet it.
                [IO.Directory]::Delete($junction, $false)
                # settings.json held open by another program, or not settings at all: the install goes on and leaves the file alone.
                $settingsRoot = Join-Path $DataRoot 'settings-read'; New-Item -ItemType Directory -Force $settingsRoot | Out-Null
                [IO.File]::WriteAllText((Join-Path $settingsRoot 'settings.json'), '{"EnginePath":"x"}')
                if ((Read-InstallSettings $settingsRoot).EnginePath -ne 'x') { throw 'Readable settings were not read.' }
                $held = [IO.File]::Open((Join-Path $settingsRoot 'settings.json'), 'Open', 'ReadWrite', 'None')
                try { $read = Read-InstallSettings $settingsRoot 3>$null } finally { $held.Dispose() }
                if ($read -isnot [bool] -or $read) { throw 'A settings.json held open by another program stopped the install or was read.' }
                [IO.File]::WriteAllText((Join-Path $settingsRoot 'settings.json'), '{broken')
                $read = Read-InstallSettings $settingsRoot 3>$null
                if ($read -isnot [bool] -or $read -or [IO.File]::ReadAllText((Join-Path $settingsRoot 'settings.json')) -cne '{broken') { throw 'An unreadable settings.json was not left alone.' }
                function New-PublicManifest { [pscustomobject]@{ schema = 1; project = 'private-flow'; board = 'win-x64'
                    files = @([pscustomobject]@{ name = 'PrivateFlow-win-x64.zip'; profile = 'release'; version = '0.5.6'; size = 3; sha256 = $hash }) } }
                $complete = New-PublicManifest | ConvertTo-Json -Depth 5
                if (Get-ManifestProblem $complete '0.5.6' 3 $hash -Public) { throw 'A public release manifest was refused.' }
                # Alike in both PowerShell versions: one JSON object (PowerShell 7 would unroll an array).
                foreach ($invalidText in "[$complete]", 'not json') {
                    if (-not (Get-ManifestProblem $invalidText '0.5.6' 3 $hash -Public)) { throw "A malformed manifest text was accepted: $invalidText" }
                }
                # A development build's provenance, each field alone, in any value: the build's commit, tree state, build time and merge.
                foreach ($change in { $args[0] | Add-Member -NotePropertyName commit -NotePropertyValue ('c' * 40) }, { $args[0] | Add-Member -NotePropertyName dirty -NotePropertyValue $false },
                    { $args[0] | Add-Member -NotePropertyName built_at -NotePropertyValue '2026-11-19T10:00:00Z' }, { $args[0] | Add-Member -NotePropertyName merge_commit -NotePropertyValue $null },
                    { $args[0].files[0].profile = @('release') }, { $args[0].files[0].profile = 'debug' },
                    { $args[0] | Add-Member -NotePropertyName source -NotePropertyValue 'a private build folder' }, { $args[0].files[0] | Add-Member -NotePropertyName note -NotePropertyValue 'x' }) {
                    $changed = New-PublicManifest; & $change $changed
                    if (-not (Get-ManifestProblem ($changed | ConvertTo-Json -Depth 5) '0.5.6' 3 $hash -Public)) { throw "A manifest changed by { $change } was accepted for publication." }
                }
                # The installer and the app still read an update-server manifest with its provenance.
                $development = New-PublicManifest; $development | Add-Member -NotePropertyName commit -NotePropertyValue ('c' * 40)
                if (Get-ManifestProblem ($development | ConvertTo-Json -Depth 5) '0.5.6' 3 $hash) { throw 'A development manifest was refused outside publication.' }
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
                $checksumText = "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`n$('b' * 64)  manifest.json`n"
                function Invoke-WebRequest { param($Uri, $Headers, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
                    if (-not $OutFile) { return }
                    if ($Uri -like '*/manifest.json') { [IO.File]::WriteAllText($OutFile, ($manifestFixture | ConvertTo-Json -Depth 5)); return }
                    if ($Uri -like '*/SHA256SUMS') { [IO.File]::WriteAllText($OutFile, $checksumText) } else { [IO.File]::WriteAllBytes($OutFile, [byte[]](1,2,3)) }
                }
                $releaseFixture.assets += @{name='SHA256SUMS'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/SHA256SUMS'}
                Save-ReleaseBuild $DataRoot | Out-Null
                foreach ($badText in '', ($checksumText -replace '  ', ' '), ($checksumText + $checksumText), ($checksumText.ToUpperInvariant()), "$('c' * 64)  PrivateFlow-win-x64.zip`n", "$('b' * 64)  manifest.json`n") {
                    $checksumText = $badText; Refused { Save-ReleaseBuild $DataRoot }
                }
                $checksumText = "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`r`n"; Save-ReleaseBuild $DataRoot | Out-Null
                $releaseFixture.assets[2].browser_download_url = 'https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.5/SHA256SUMS'; Refused { Save-ReleaseBuild $DataRoot }
                $releaseFixture.assets = @($releaseFixture.assets[0], $releaseFixture.assets[1])
                if ((Get-ListedChecksum "$('d' * 64)  a.zip`n$('e' * 64)  b.zip" 'b.zip') -ne ('e' * 64)) { throw 'Checksum lookup failed.' }
                Refused { Get-ListedChecksum "$('d' * 64)  a.zip" 'b.zip' }
                Refused { Get-ListedChecksum "$('d' * 64)  ../a.zip" '../a.zip' }
                $checksumLine = "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip"
                foreach ($invalidText in "$checksumLine`r`r`n", "$checksumLine `n", "$checksumLine`n$('b' * 64)  privateflow-WIN-x64.zip", "$checksumLine`n$('b' * 64)  dir\x.zip",
                    ("$checksumLine`n" + (-join (0..999 | ForEach-Object { "$('b' * 64)  f$_.zip`n" })))) { Refused { Get-ListedChecksum $invalidText 'PrivateFlow-win-x64.zip' } }
                if ((Get-ListedChecksum "`n$checksumLine`r`n`r`n" 'PrivateFlow-win-x64.zip') -ne $hash.ToLowerInvariant()) { throw 'Blank lines around a valid line were refused.' }
                Refused { Get-ListedChecksum $checksumLine 'privateflow-win-x64.zip' }
                # The release key: SHA256SUMS and its signature are required, and they must cover the package and the manifest as downloaded.
                $signingKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
                $otherSigningKey = [Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
                $point = $signingKey.ExportParameters($false).Q; $publicKey = [Convert]::ToBase64String([byte[]]($point.X + $point.Y))
                $manifestPath = Join-Path $DataRoot 'manifest-as-served.json'; [IO.File]::WriteAllText($manifestPath, ($manifestFixture | ConvertTo-Json -Depth 5))
                $manifestHash = (Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
                $signedText = "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`n$manifestHash  manifest.json`n"
                function Sign([string]$Text, $Key) { [Convert]::ToBase64String($Key.SignData([Text.Encoding]::UTF8.GetBytes($Text), [Security.Cryptography.HashAlgorithmName]::SHA256)) + "`n" }
                $checksumText = $signedText; $signatureText = Sign $signedText $signingKey
                function Invoke-WebRequest { param($Uri, $Headers, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
                    if (-not $OutFile) { return }
                    if ($Uri -like '*/manifest.json') { [IO.File]::WriteAllText($OutFile, ($manifestFixture | ConvertTo-Json -Depth 5)); return }
                    if ($Uri -like '*/SHA256SUMS.sig') { [IO.File]::WriteAllText($OutFile, $signatureText); return }
                    if ($Uri -like '*/SHA256SUMS') { [IO.File]::WriteAllText($OutFile, $checksumText) } else { [IO.File]::WriteAllBytes($OutFile, [byte[]](1,2,3)) }
                }
                $releaseFixture.assets += @{name='SHA256SUMS'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/SHA256SUMS'}
                # No signature in the release: refused once a key is pinned, accepted as before without one.
                Refused { Save-ReleaseBuild $DataRoot -ReleaseKey $publicKey }
                Save-ReleaseBuild $DataRoot | Out-Null
                $releaseFixture.assets += @{name='SHA256SUMS.sig'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/SHA256SUMS.sig'}
                Save-ReleaseBuild $DataRoot -ReleaseKey $publicKey | Out-Null
                foreach ($bad in @(
                    @{ Checksums = $signedText; Signature = (Sign $signedText $otherSigningKey) },
                    @{ Checksums = ($signedText -replace $manifestHash, ('c' * 64)); Signature = (Sign ($signedText -replace $manifestHash, ('c' * 64)) $signingKey) },
                    @{ Checksums = "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`n"; Signature = (Sign "$($hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`n" $signingKey) },
                    @{ Checksums = ($signedText + "`n"); Signature = $signatureText },
                    @{ Checksums = $signedText; Signature = 'not base64' },
                    @{ Checksums = $signedText; Signature = '' })) {
                    $checksumText = $bad.Checksums; $signatureText = $bad.Signature; Refused { Save-ReleaseBuild $DataRoot -ReleaseKey $publicKey }
                }
                $checksumText = $signedText; $signatureText = Sign $signedText $signingKey
                $releaseFixture.assets[3].browser_download_url = 'https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.5/SHA256SUMS.sig'; Refused { Save-ReleaseBuild $DataRoot -ReleaseKey $publicKey }
                $releaseFixture.assets = @($releaseFixture.assets[0], $releaseFixture.assets[1])
                if (-not (Test-ReleaseSignature ([Text.Encoding]::UTF8.GetBytes($signedText)) $signatureText $publicKey) -or (Test-ReleaseSignature ([Text.Encoding]::UTF8.GetBytes($signedText)) $signatureText 'AAAA')) { throw 'Test-ReleaseSignature does not decide by the key.' }
                $inventoryPackage = Join-Path $DataRoot 'inventory-package'; New-Item -ItemType Directory -Force (Join-Path $inventoryPackage 'scripts') | Out-Null
                Copy-Item $executable (Join-Path $inventoryPackage 'PrivateFlow.exe')
                [IO.File]::WriteAllText((Join-Path $inventoryPackage 'scripts\models.json'), '{"Models":[]}')
                [IO.File]::WriteAllBytes((Join-Path $inventoryPackage 'NAudio.dll'), [byte[]](4,5))
                function Write-Inventory([string]$Version = '0.5.6', [string[]]$Skip = @(), [string]$Header = '# Private Flow package inventory: fixture') {
                    $entries = foreach ($item in Get-ChildItem -LiteralPath $inventoryPackage -Recurse -File | Sort-Object FullName) {
                        $relative = $item.FullName.Substring($inventoryPackage.Length + 1).Replace('\', '/')
                        if ($relative -eq 'scripts/package-inventory.ps1' -or $Skip -contains $relative) { continue }
                        @{ path = $relative; size = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
                    }
                    $json = @{ schema = 1; project = 'private-flow'; version = $Version; files = @($entries) } | ConvertTo-Json -Depth 4 -Compress
                    [IO.File]::WriteAllText((Join-Path $inventoryPackage 'scripts\package-inventory.ps1'), "$Header`r`n# Data only.`r`n<#`r`n$json`r`n#>`r`n")
                }
                Assert-PackageInventory $inventoryPackage '' '0.5.6'
                Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Write-Inventory
                # The real signature check: the fixture inventory carries no signature.
                Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                # Stand-in signatures from here on; only a real certificate can show that Windows accepts a jsign signature.
                $standInStatus = 'Valid'; $standInSubject = 'CN=Example Publisher'
                function Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{ Status = $standInStatus; SignerCertificate = [pscustomobject]@{ Subject = $standInSubject } } }
                Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6'
                Refused { Assert-PackageInventory $inventoryPackage 'CN=Other Publisher' '0.5.6' }
                Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.7' }
                $standInStatus = 'NotTrusted'; Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }; $standInStatus = 'Valid'
                [IO.File]::WriteAllText((Join-Path $inventoryPackage 'scripts\models.json'), '{"Models":[{"Url":"https://example.invalid/runtime.zip"}]}')
                Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Write-Inventory; Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6'
                [IO.File]::WriteAllBytes((Join-Path $inventoryPackage 'extra.dll'), [byte[]](6)); Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Remove-Item (Join-Path $inventoryPackage 'extra.dll')
                Write-Inventory -Skip 'NAudio.dll'; Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Write-Inventory; Remove-Item (Join-Path $inventoryPackage 'NAudio.dll'); Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                [IO.File]::WriteAllBytes((Join-Path $inventoryPackage 'NAudio.dll'), [byte[]](4,5))
                Write-Inventory -Skip 'PrivateFlow.exe'; Remove-Item (Join-Path $inventoryPackage 'PrivateFlow.exe'); Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Copy-Item $executable (Join-Path $inventoryPackage 'PrivateFlow.exe')
                Write-Inventory -Header '# Something else'; Refused { Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6' }
                Write-Inventory -Version '0.5.6'; Assert-PackageInventory $inventoryPackage 'CN=Example Publisher' '0.5.6'
                Remove-Item function:Get-AuthenticodeSignature
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
                & $Installer -SetCloudToken -CloudUrl 'https://example.invalid' -CloudWakeUrl 'https://example.invalid/healthz' -SourceRepository 'example-owner/internal-app'
                if ((Get-Content (Join-Path $DataRoot 'update-server.json') -Raw | ConvertFrom-Json).SourceRepository -cne 'example-owner/internal-app') { throw 'The source repository was not stored.' }
                & $Installer -SetCloudToken -CloudUrl 'https://example.invalid' -CloudWakeUrl 'https://example.invalid/healthz'
                if ((Get-Content (Join-Path $DataRoot 'update-server.json') -Raw | ConvertFrom-Json).SourceRepository -cne 'example-owner/internal-app') { throw 'Saving URLs did not keep the stored source repository.' }
                Refused { & $Installer -SetCloudToken -CloudUrl 'https://example.invalid' -SourceRepository 'example-owner' }
                # The update-server build the user confirmed: another newest build is refused before anything is downloaded.
                function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing) [pscustomobject]@{ commit = 'a' * 40; version = '0.5.6'; files = @() } }
                try { Save-CloudBuild 'https://example.invalid' '' 'integration' 'token' $DataRoot -ExpectedCommit ('b' * 40); throw 'A build other than the confirmed one was accepted.' } catch { if ($_ -notlike '*not the build you confirmed*') { throw } }
                # Never older than the installed copy (0.5.7 here), on the confirmed commit too and whatever -Force says; no version is refused as well.
                foreach ($olderVersion in '0.5.6', '', 'next') {
                    function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing) [pscustomobject]@{ commit = 'a' * 40; version = $olderVersion; files = @() } }
                    try { Save-CloudBuild 'https://example.invalid' '' 'integration' 'token' $DataRoot -ExpectedCommit ('a' * 40) -InstallFolder $installation; throw "An update-server build $olderVersion was accepted." }
                    catch { if ($_ -notlike '*never downgraded*' -and $_ -notlike '*names no version*') { throw } }
                }
                function Invoke-RestMethod { param($Uri, $Headers, $TimeoutSec, [switch]$UseBasicParsing) [pscustomobject]@{ commit = 'a' * 40; version = '0.5.7'; files = @() } }
                try { Save-CloudBuild 'https://example.invalid' '' 'integration' 'token' $DataRoot -InstallFolder $installation; throw 'No listing check.' } catch { if ($_ -notlike '*exactly one PrivateFlow-win-x64.zip*') { throw } }
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
                # The installer's own call sites, with a publisher set and stand-in signatures: a downloaded release (-FromRelease,
                # also the in-app update) and a release ZIP the installer runs from. The setup script of the package is a
                # sentinel, so reaching it shows that verification passed before anything from the package ran.
                $signedInstaller = Join-Path $DataRoot 'signed-install.ps1'
                # Its release key is this check's, so the stand-in release below can carry a valid signature.
                [IO.File]::WriteAllText($signedInstaller, ([IO.File]::ReadAllText($Installer).Replace("`$taskReleasePublisher = ''", "`$taskReleasePublisher = 'CN=Example Publisher'") -replace "(?m)^\`$taskReleaseKey = '[^']*'", "`$taskReleaseKey = '$publicKey'"))
                if ([IO.File]::ReadAllText($signedInstaller) -notmatch [regex]::Escape("taskReleaseKey = '$publicKey'")) { throw 'The release key fixture was not set.' }
                if ([IO.File]::ReadAllText($signedInstaller) -notmatch "taskReleasePublisher = 'CN=Example Publisher'") { throw 'The publisher fixture was not set.' }
                # Both PowerShell versions share the data folder of this check, so the package starts afresh.
                $releasePackage = Join-Path $DataRoot 'release-package'; Remove-Item $releasePackage -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force (Join-Path $releasePackage 'scripts') | Out-Null
                Copy-Item $executable (Join-Path $releasePackage 'PrivateFlow.exe')
                [IO.File]::WriteAllText((Join-Path $releasePackage 'scripts\setup-engine.ps1'), "throw 'SETUP_RAN'")
                Copy-Item $signedInstaller (Join-Path $releasePackage 'scripts\install.ps1')
                $inventoryPackage = $releasePackage
                $releaseZip = Join-Path $DataRoot 'release-package.zip'
                function Publish-Fixture([switch]$WithoutInventory, [string]$Version = '0.5.6') {
                    if ($WithoutInventory) { Remove-Item (Join-Path $releasePackage 'scripts\package-inventory.ps1') -ErrorAction SilentlyContinue } else { Write-Inventory -Version $Version }
                    Remove-Item $releaseZip -ErrorAction SilentlyContinue
                    Compress-Archive -Path (Join-Path $releasePackage '*') -DestinationPath $releaseZip
                    $zipHash = (Get-FileHash $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant(); $zipSize = (Get-Item $releaseZip).Length
                    $releaseFixture.assets[0].size = $zipSize; $releaseFixture.assets[0].digest = $null
                    $manifestFixture.files[0].size = $zipSize; $manifestFixture.files[0].sha256 = $zipHash
                    Serve-Release
                }
                # The signed SHA256SUMS of the package and manifest as they are served now.
                function Serve-Release {
                    [IO.File]::WriteAllText($manifestPath, ($manifestFixture | ConvertTo-Json -Depth 5))
                    $served.Checksums = "$((Get-FileHash $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant())  PrivateFlow-win-x64.zip`n$((Get-FileHash $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())  manifest.json`n"
                    $served.Signature = Sign $served.Checksums $signingKey
                }
                # What the stand-in release serves, in one table this script owns: the mock below also runs inside the installer's scope.
                $served = @{}
                $releaseFixture.assets = @($releaseFixture.assets[0], $releaseFixture.assets[1],
                    @{name='SHA256SUMS'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/SHA256SUMS'},
                    @{name='SHA256SUMS.sig'; browser_download_url='https://github.com/DemonicPsychoII/PrivateFlow/releases/download/v0.5.6/SHA256SUMS.sig'})
                function Invoke-WebRequest { param($Uri, $Headers, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
                    if ($Uri -like '*/manifest.json') { [IO.File]::WriteAllText($OutFile, ($manifestFixture | ConvertTo-Json -Depth 5)); return }
                    if ($Uri -like '*/SHA256SUMS') { [IO.File]::WriteAllText($OutFile, $served.Checksums); return }
                    if ($Uri -like '*/SHA256SUMS.sig') { [IO.File]::WriteAllText($OutFile, $served.Signature); return }
                    if ($OutFile) { Copy-Item $releaseZip $OutFile } }
                function Invoke-RestMethod { param($Uri) if ($Uri -like '*/releases/latest') { return $releaseFixture }; return $manifestFixture }
                $standInStatus = 'Valid'; $standInSubject = 'CN=Example Publisher'
                function Get-AuthenticodeSignature { param([string]$LiteralPath) [pscustomobject]@{ Status = $standInStatus; SignerCertificate = [pscustomobject]@{ Subject = $standInSubject } } }
                function Expect-Install([scriptblock]$Run, [string]$Expected) {
                    try { & $Run | Out-Null; throw 'The installer finished.' } catch { if ($_ -notlike "*$Expected*") { throw "Expected '$Expected', got: $_" } }
                }
                $releaseTarget = Join-Path $DataRoot 'release-target'
                Publish-Fixture
                Expect-Install { & $signedInstaller -FromRelease -Destination $releaseTarget -NoLaunch } 'SETUP_RAN'
                $standInStatus = 'NotSigned'; Expect-Install { & $signedInstaller -FromRelease -Destination $releaseTarget -NoLaunch } 'is not signed by CN=Example Publisher'; $standInStatus = 'Valid'
                Publish-Fixture -WithoutInventory
                Expect-Install { & $signedInstaller -FromRelease -Destination $releaseTarget -NoLaunch } 'no signed inventory'
                [IO.File]::WriteAllText((Join-Path $releasePackage 'scripts\models.json'), '{"Models":[]}'); Publish-Fixture
                [IO.File]::WriteAllText((Join-Path $releasePackage 'scripts\models.json'), '{"Models":[{"Url":"https://example.invalid/runtime.zip"}]}')
                Compress-Archive -Path (Join-Path $releasePackage '*') -DestinationPath $releaseZip -Force
                $manifestFixture.files[0].size = (Get-Item $releaseZip).Length; $releaseFixture.assets[0].size = (Get-Item $releaseZip).Length; $manifestFixture.files[0].sha256 = (Get-FileHash $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
                Serve-Release
                Expect-Install { & $signedInstaller -FromRelease -Destination $releaseTarget -NoLaunch } 'scripts/models.json does not match the package inventory'
                if (Test-Path $releaseTarget) { throw 'A refused release created its destination.' }
                # Run from the extracted ZIP itself, with the version of its PrivateFlow.exe (0.5.7).
                Remove-Item (Join-Path $releasePackage 'scripts\models.json'); Write-Inventory -Version '0.5.7'
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination (Join-Path $DataRoot 'zip-target') -NoLaunch } 'SETUP_RAN'
                Remove-Item (Join-Path $releasePackage 'scripts\package-inventory.ps1')
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination (Join-Path $DataRoot 'zip-target') -NoLaunch } 'no signed inventory'
                # An installed copy (with its install marker) run in place is not checked again, even without an inventory;
                # copied elsewhere, it is checked, and its marker is the one file allowed beside the inventory.
                Set-Content (Join-Path $releasePackage '.private-flow-install') 'Installed by Private Flow scripts\install.ps1'
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination $releasePackage -NoLaunch } 'SETUP_RAN'
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination (Join-Path $DataRoot 'copy-target') -NoLaunch } 'no signed inventory'
                Remove-Item (Join-Path $releasePackage '.private-flow-install'); Write-Inventory -Version '0.5.7'
                Set-Content (Join-Path $releasePackage '.private-flow-install') 'Installed by Private Flow scripts\install.ps1'
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination (Join-Path $DataRoot 'copy-target') -NoLaunch } 'SETUP_RAN'
                Set-Content (Join-Path $releasePackage 'added-after-install.txt') 'side file'
                Expect-Install { & (Join-Path $releasePackage 'scripts\install.ps1') -Destination (Join-Path $DataRoot 'copy-target') -NoLaunch } 'added-after-install.txt is in the release package but not in its inventory'
                Remove-Item function:Get-AuthenticodeSignature
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
