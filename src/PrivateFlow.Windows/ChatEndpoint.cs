using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PrivateFlow.Windows;

// One request to the configured OpenAI-compatible chat endpoint (RewriteEndpoint, RewriteModel, RewriteKeyVariable).
// Failures surface as InvalidOperationException or TimeoutException with fixed messages that never echo the key.
public static class ChatEndpoint
{
    private static readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    public static async Task<string> CompleteAsync(Settings settings, string system, string user, int maximumTokens, TimeSpan timeLimit, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(settings.RewriteEndpoint)) throw new InvalidOperationException("No API base URL configured. Set it under Settings > Text style.");
        var key = Environment.GetEnvironmentVariable(settings.RewriteKeyVariable)
            ?? Environment.GetEnvironmentVariable(settings.RewriteKeyVariable, EnvironmentVariableTarget.User);
        // Fixed messages only: neither the key nor header parser errors may reach the status line.
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("API key variable is not set.");
        if (key.Any(char.IsControl) || key.Any(c => c > 0x7e)) throw new InvalidOperationException("API key contains invalid characters.");
        if (!Uri.TryCreate(settings.RewriteEndpoint.TrimEnd('/') + "/chat/completions", UriKind.Absolute, out var endpoint) ||
            !(endpoint.Scheme == "https" || (endpoint.Scheme == "http" && endpoint.IsLoopback)))
            throw new InvalidOperationException("API base URL must use https (http only for loopback hosts).");
        if (string.IsNullOrWhiteSpace(settings.RewriteModel)) throw new InvalidOperationException("No API model configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeLimit);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new
            {
                model = settings.RewriteModel,
                temperature = 0,
                max_tokens = maximumTokens,
                messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } }
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"API returned {(int)response.StatusCode}.");
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            var choice = document.RootElement.GetProperty("choices")[0];
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.GetString() != "stop")
                throw new InvalidOperationException("API answer was incomplete.");
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            return string.IsNullOrWhiteSpace(content) ? throw new InvalidOperationException("API returned no text.") : content;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new TimeoutException("API timed out."); }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or IndexOutOfRangeException)
        { throw new InvalidOperationException("API unavailable (" + ex.GetType().Name + ").", ex); }
    }
}
