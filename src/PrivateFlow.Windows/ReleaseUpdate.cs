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
            return hash is null ? null : new(tag!, version.ToString(), downloadUrl!, size, hash, digest);
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        { return null; }
    }

    private static bool AssetUrl(JsonElement asset, string tag, out Uri? url) =>
        Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out url)
        && url.AbsoluteUri == $"https://github.com/{Repository}/releases/download/{tag}/{asset.GetProperty("name").GetString()}";

    internal static string? ManifestHash(JsonElement manifest, string version, long size, string? digest)
    {
        if (manifest.GetProperty("schema").GetInt32() != 1 || manifest.GetProperty("project").GetString() != "private-flow"
            || manifest.GetProperty("board").GetString() != "win-x64") return null;
        var files = manifest.GetProperty("files").EnumerateArray().Where(file => file.GetProperty("name").GetString() == "PrivateFlow-win-x64.zip").ToArray();
        if (files.Length != 1) return null;
        var package = files[0];
        var hash = package.GetProperty("sha256").GetString();
        return package.GetProperty("version").GetString() == version && package.GetProperty("size").GetInt64() == size
            && hash is not null && Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")
            && (digest is null || digest.Equals("sha256:" + hash, StringComparison.OrdinalIgnoreCase)) ? hash : null;
    }
}
