using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// Optional, opt-in API rewrite for the Concise output style. The transcript leaves the device;
// audio never does. Callers fall back to the rule-based Clean style on any failure.
public static class ConciseRewriter
{
    public static async Task<string> RewriteAsync(string text, Settings settings, CancellationToken cancellation)
    {
        var quotes = new List<string>();
        var sent = ConcisePrompt.Protect(text, quotes);
        // A fixed 8 s cut off long dictations: a 150-word transcript already takes ~4-5 s with
        // gpt-6-luna, so longer ones silently fell back to Clean. Allow time per character.
        var timeLimit = TimeSpan.FromSeconds(Math.Clamp(6 + text.Length / 100.0, 10, 45));
        var result = ConcisePrompt.Unwrap(await ChatEndpoint.CompleteAsync(settings, ConcisePrompt.System(settings.Vocabulary), ConcisePrompt.Wrap(sent), 2048, timeLimit, cancellation));
        if (string.IsNullOrWhiteSpace(result)) throw new InvalidOperationException("Rewrite API returned no text.");
        if (!ConcisePrompt.IsPlausible(sent, result)) throw new InvalidOperationException("Rewrite model replied instead of rewriting.");
        var restored = ConcisePrompt.Restore(result, quotes) ?? throw new InvalidOperationException("Rewrite model changed quoted text.");
        if (!RewriteIntegrity.PreservesMeaning(text, restored, settings.TechnicalMode))
            throw new InvalidOperationException("Rewrite changed protected facts or technical text.");
        return restored;
    }
}
