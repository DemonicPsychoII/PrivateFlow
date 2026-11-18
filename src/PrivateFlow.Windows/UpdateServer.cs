using System.IO;
using System.Text.Json;

namespace PrivateFlow.Windows;

internal sealed record UpdateServer(Uri CloudUrl, Uri? CloudWakeUrl)
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
            return new(new Uri(serverUrl!.AbsoluteUri.TrimEnd('/') + "/"), wakeUrl);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    private static bool ValidUrl(string? value, out Uri? url) => Uri.TryCreate(value, UriKind.Absolute, out url)
        && (url.Scheme == "https" || (url.Scheme == "http" && url.IsLoopback)) && url.UserInfo.Length == 0
        && url.Query.Length == 0 && url.Fragment.Length == 0;
}
