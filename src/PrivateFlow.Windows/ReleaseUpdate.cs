using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateFlow.Windows;

internal sealed record ReleasePackage(string Tag, string Version, Uri DownloadUrl, long Size, string Sha256, string? Digest);

internal static class ReleaseUpdate
{
    internal const string Repository = "DemonicPsychoII/PrivateFlow";
    internal static bool UsesServer(UpdateServer? server, string? readerToken) => server is not null && !string.IsNullOrWhiteSpace(readerToken);
    internal static Version? VersionFromTag(string? tag) => tag is not null && Regex.IsMatch(tag, @"^v\d+\.\d+\.\d+$")
        && Version.TryParse(tag[1..], out var version) ? version : null;

    internal static async Task<ReleasePackage?> FindAsync(HttpClient client, Version installed, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("PrivateFlow-updater");
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var release = document.RootElement;
            var tag = release.GetProperty("tag_name").GetString();
            if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()
                || VersionFromTag(tag) is not { } version || version <= new Version(installed.Major, installed.Minor, installed.Build)) return null;
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var packages = assets.Where(asset => asset.GetProperty("name").GetString() == "PrivateFlow-win-x64.zip").ToArray();
            var manifests = assets.Where(asset => asset.GetProperty("name").GetString() == "manifest.json").ToArray();
            if (packages.Length != 1 || manifests.Length != 1) return null;
            var package = packages[0];
            var size = package.GetProperty("size").GetInt64();
            var digest = package.TryGetProperty("digest", out var digestValue) && digestValue.ValueKind != JsonValueKind.Null ? digestValue.GetString() : null;
            if (size <= 0 || !AssetUrl(package, tag!, out var downloadUrl) || !AssetUrl(manifests[0], tag!, out var manifestUrl)) return null;
            using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
            manifestRequest.Headers.UserAgent.ParseAdd("PrivateFlow-updater");
            using var manifestResponse = await client.SendAsync(manifestRequest, timeout.Token);
            if (!manifestResponse.IsSuccessStatusCode) return null;
            using var manifest = JsonDocument.Parse(await manifestResponse.Content.ReadAsStringAsync(timeout.Token));
            var hash = ManifestHash(manifest.RootElement, version.ToString(), size, digest);
            if (hash is null) return null;
            // Releases made by the release workflow also carry SHA256SUMS, which must agree with the manifest; install.ps1 checks the same.
            var checksums = assets.Where(asset => asset.GetProperty("name").GetString() == "SHA256SUMS").ToArray();
            if (checksums.Length > 1) return null;
            if (checksums.Length == 1)
            {
                if (!AssetUrl(checksums[0], tag!, out var checksumUrl)) return null;
                using var checksumRequest = new HttpRequestMessage(HttpMethod.Get, checksumUrl);
                checksumRequest.Headers.UserAgent.ParseAdd("PrivateFlow-updater");
                using var checksumResponse = await client.SendAsync(checksumRequest, timeout.Token);
                if (!checksumResponse.IsSuccessStatusCode
                    || !hash.Equals(ListedChecksum(await checksumResponse.Content.ReadAsStringAsync(timeout.Token), "PrivateFlow-win-x64.zip"), StringComparison.OrdinalIgnoreCase)) return null;
            }
            return new(tag!, version.ToString(), downloadUrl!, size, hash, digest);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { return null; }
    }

    private static bool AssetUrl(JsonElement asset, string tag, out Uri? url) =>
        Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out url)
        && url.AbsoluteUri == $"https://github.com/{Repository}/releases/download/{tag}/{asset.GetProperty("name").GetString()}";

    // SHA256SUMS lines are "<64 lowercase hex>  <file name>", as sha256sum writes them, ending with LF or CRLF; blank lines
    // are skipped. A malformed line, a name repeated in any letter case or a file over 64 KiB means no hash. The same rules
    // hold in scripts/release.py, install.ps1 (Get-ListedChecksum) and scripts/publish-public.ps1.
    internal static string? ListedChecksum(string text, string name)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(text) > 65536) return null;
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0) continue;
            var match = Regex.Match(line, @"\A([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._-]*)\z");
            if (!match.Success || !seen.Add(match.Groups[2].Value)) return null;
            listed[match.Groups[2].Value] = match.Groups[1].Value;
        }
        return listed.GetValueOrDefault(name);
    }

    // Types are exact, as in install.ps1 (Get-ManifestProblem): GetString and GetInt64 throw on any other JSON kind, and files lists exactly one package.
    // built_at is not read here or by install.ps1: it is provenance, which publish-public.ps1 checks before a release is public.
    internal static string? ManifestHash(JsonElement manifest, string version, long size, string? digest)
    {
        // Only the fields the build writes, as install.ps1 allows them.
        string[] manifestFields = ["schema", "project", "board", "commit", "dirty", "built_at", "merge_commit", "files"], packageFields = ["name", "profile", "version", "size", "sha256"];
        // Each member once: JsonDocument keeps duplicates, and install.ps1 refuses them.
        static bool Repeats(JsonElement element) => element.EnumerateObject().GroupBy(member => member.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
        if (manifest.EnumerateObject().Any(member => !manifestFields.Contains(member.Name)) || Repeats(manifest)
            || manifest.GetProperty("files").EnumerateArray().Any(entry => entry.EnumerateObject().Any(member => !packageFields.Contains(member.Name)) || Repeats(entry))) return null;
        if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("project").GetString() != "private-flow"
            || manifest.GetProperty("board").GetString() != "win-x64") return null;
        var files = manifest.GetProperty("files").EnumerateArray().ToArray();
        if (files.Length != 1 || files[0].GetProperty("name").GetString() != "PrivateFlow-win-x64.zip") return null;
        var package = files[0];
        var hash = package.GetProperty("sha256").GetString();
        return package.GetProperty("version").GetString() == version && package.GetProperty("size").GetInt64() == size
            && hash is not null && Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z")
            && (digest is null || digest.Equals("sha256:" + hash, StringComparison.OrdinalIgnoreCase)) ? hash : null;
    }
}
