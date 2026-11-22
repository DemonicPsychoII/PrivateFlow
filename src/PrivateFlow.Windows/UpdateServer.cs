using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrivateFlow.Windows;

// SourceRepository, optional, is the GitHub repository (owner/name) the server's builds come from; the package names none.
internal sealed record UpdateServer(Uri CloudUrl, Uri? CloudWakeUrl, string? SourceRepository = null)
{
    internal const string LegacyNotice = "Updates now come from public releases because no usable update-server URL and reader token are stored. Restore update-server updates with: install.ps1 -SetCloudToken -CloudUrl \"<server URL>\" -CloudWakeUrl \"<wake URL>\". Press Enter at the token prompt to keep the stored token.";
    internal static UpdateServer? Read(string dataRoot)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dataRoot, "update-server.json")));
            var configuration = document.RootElement;
            if (configuration.ValueKind != JsonValueKind.Object ||
                !configuration.TryGetProperty("CloudUrl", out var server) || server.ValueKind != JsonValueKind.String ||
                !ValidUrl(server.GetString(), out var serverUrl)) return null;
            Uri? wakeUrl = null;
            if (configuration.TryGetProperty("CloudWakeUrl", out var wake))
            {
                if (wake.ValueKind != JsonValueKind.String) return null;
                if (!string.IsNullOrWhiteSpace(wake.GetString()) && !ValidUrl(wake.GetString(), out wakeUrl)) return null;
            }
            string? repository = null;
            if (configuration.TryGetProperty("SourceRepository", out var source))
            {
                if (source.ValueKind != JsonValueKind.String || !Regex.IsMatch(source.GetString()!, @"\A[A-Za-z0-9-]+/[A-Za-z0-9._-]+\z")) return null;
                repository = source.GetString();
            }
            return new(new Uri(serverUrl!.AbsoluteUri.TrimEnd('/') + "/"), wakeUrl, repository);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    // Whether a server build is offered to this copy: never a lower version, nor an unreadable one. An equal version only to a copy that
    // names its own commit (a development build) and only for another commit; a release build ("+release") names none, so only a
    // higher version. The ancestry check in the source repository comes on top of this where it can run.
    internal static bool Offers(string offeredVersion, string offeredCommit, Version installed, string? ownCommit)
    {
        if (!Version.TryParse(offeredVersion, out var offered) || offered.Build < 0) return false;
        var order = new Version(offered.Major, offered.Minor, offered.Build).CompareTo(new Version(installed.Major, installed.Minor, Math.Max(installed.Build, 0)));
        return order > 0 || (order == 0 && ownCommit is not null && !offeredCommit.Equals(ownCommit, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ValidUrl(string? value, out Uri? url) => Uri.TryCreate(value, UriKind.Absolute, out url)
        && (url.Scheme == "https" || (url.Scheme == "http" && url.IsLoopback)) && url.UserInfo.Length == 0
        && url.Query.Length == 0 && url.Fragment.Length == 0;
}
