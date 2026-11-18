using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PrivateFlow.Windows;

internal sealed record UpdateOffer(string Commit, string Version, int? Behind, IReadOnlyList<string> Changes, bool FromRelease = false);
internal sealed record CloudBuild(string Commit, string Version);

// Uses the configured update server and reader token, otherwise anonymous public releases.
// Unavailable sources and unexpected replies mean no offer. No audio or transcript is sent.
internal static class UpdateCheck
{
    private const string Repository = "DemonicPsychoII/private-flow";
    // Same cloud, channel, wake call and token file as install.ps1 -FromCloud, so the offer matches what Update installs.
    private static readonly TimeSpan CloudWakeTimeout = TimeSpan.FromSeconds(150);
    private static string CloudTokenPath => Path.Combine(Settings.Root, "cloud-reader.token");
    internal static bool LegacyTokenWithoutServer => File.Exists(CloudTokenPath) && UpdateServer.Read(Settings.Root) is null;
    // The SDK appends the source commit to the informational version: "0.3.0+<sha>".
    private static string? Commit => typeof(UpdateCheck).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+') is [_, var sha] ? sha : null;

    public static async Task<UpdateOffer?> FindAsync(CancellationToken cancellation)
    {
        var server = UpdateServer.Read(Settings.Root);
        var readerToken = ReadCloudToken();
        if (!ReleaseUpdate.UsesServer(server, readerToken))
        {
            using var client = new HttpClient();
            var release = await ReleaseUpdate.FindAsync(client, typeof(UpdateCheck).Assembly.GetName().Version!, cancellation);
            return release is null ? null : new(release.Tag, release.Version, null, [], true);
        }
        if (Commit is not { Length: > 0 } own) return null;
        if (await LatestCloudBuildAsync(server!.CloudUrl, server.CloudWakeUrl, readerToken!, cancellation) is not { } latest) return null;
        if (latest.Commit.Equals(own, StringComparison.OrdinalIgnoreCase)) return null;
        // ahead_by counts commits in the build that this copy lacks; a copy built from a newer
        // branch is not offered an older main build. Merge commits carry the PR titles.
        var compare = await GhAsync(cancellation, "api", $"repos/{Repository}/compare/{own}...{latest.Commit}", "--jq",
            ".ahead_by, ([.commits[] | select((.parents | length) > 1)] | reverse | .[] | .commit.message | split(\"\\n\")[0])");
        var lines = compare?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines is not [var behindText, ..] || !int.TryParse(behindText, out var behind)) return new UpdateOffer(latest.Commit, latest.Version, null, []);
        return behind > 0 ? new UpdateOffer(latest.Commit, latest.Version, behind, lines[1..]) : null;
    }

    internal static async Task<CloudBuild?> LatestCloudBuildAsync(Uri cloudUrl, Uri? cloudWakeUrl, string readerToken, CancellationToken cancellation)
    {
        // Device requests to the update server must not carry an Origin header; HttpClient sends none.
        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            // The wake call carries no token: the cloud rejects any unrecognised credential on its health route.
            if (cloudWakeUrl is not null)
            {
                using var wake = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                wake.CancelAfter(CloudWakeTimeout);
                using var awake = await client.GetAsync(cloudWakeUrl, HttpCompletionOption.ResponseHeadersRead, wake.Token);
                if (!awake.IsSuccessStatusCode) return null;
            }
            using var query = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            query.CancelAfter(TimeSpan.FromSeconds(60));
            using var latestRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(cloudUrl, "api/firmware/latest?project=private-flow&channel=integration"));
            latestRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", readerToken);
            using var response = await client.SendAsync(latestRequest, query.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var listing = JsonDocument.Parse(await response.Content.ReadAsStringAsync(query.Token));
            var build = listing.RootElement;
            if (build.ValueKind != JsonValueKind.Object ||
                !build.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.String ||
                !build.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String) return null;
            var commitText = commit.GetString()!;
            return commitText.Length == 40 && commitText.All(Uri.IsHexDigit) ? new CloudBuild(commitText, version.GetString()!) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException) { return null; }
    }

    // install.ps1 -SetCloudToken stores the token with ConvertFrom-SecureString: hex of a CurrentUser DPAPI blob of UTF-16 text.
    internal static string? ReadCloudToken()
    {
        try
        {
            var protectedToken = Convert.FromHexString(File.ReadAllText(CloudTokenPath).Trim());
            var readerToken = Encoding.Unicode.GetString(ProtectedData.Unprotect(protectedToken, null, DataProtectionScope.CurrentUser));
            return readerToken.Length > 0 && readerToken.All(character => character is > ' ' and <= '~') ? readerToken : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException or InvalidOperationException)
        { return null; }
    }

    private static async Task<string?> GhAsync(CancellationToken token, params string[] arguments)
    {
        var start = new ProcessStartInfo("gh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        start.Environment["GH_NO_UPDATE_NOTIFIER"] = "1";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw; }
            return process.ExitCode == 0 ? await output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or OperationCanceledException)
        { return null; }
    }
}
