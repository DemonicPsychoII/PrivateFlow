using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// GpuMemoryNote says that GPU memory was low while this dictation was recognized; the controller adds it, as recovering the result later
// must show its own note, not the current memory.
internal sealed record DictationResult(string Original, string Final, string? Notice, string? GpuMemoryNote = null);
internal static class DictationPipeline
{
    public const string PolishCancelled = "the polish was cancelled.";
    // The result's note when Local polish was chosen without its model: the card offers the setup.
    public static bool PolishNotSetUp(string? notice) => notice == "Used Clean: " + LocalRewriter.PolishMissing;
    // waiting hears at once when Local polish has to wait for another local model task, such as meeting notes, and what that task is.
    // waitEnded hears when that wait ends without cancellation, true when the polish then runs, false when it is skipped for the time limit.
    // skipPolish ends the polish, or the wait for it, and keeps the Clean text with a note; token ends the whole dictation without a result.
    public static async Task<DictationResult> ProcessAsync(string raw, Settings settings, CancellationToken token, Action<string>? waiting = null, CancellationToken skipPolish = default,
        Action<bool>? waitEnded = null)
    {
        if (settings.OutputStyle == OutputStyle.Exact) return new(raw, TextCleanup.Clean(raw), null);
        var prepared = new PreparedDictation(raw, settings.Corrections, settings.Snippets, settings.Language,
            settings.LiteralMode, settings.SpokenFormatting);
        var clean = TextCleanup.RemoveFillers(prepared.Text);
        string? note = null;
        var result = clean;
        if (settings.OutputStyle is OutputStyle.Concise or OutputStyle.LocalPolish && !string.IsNullOrWhiteSpace(clean))
        {
            using var polish = CancellationTokenSource.CreateLinkedTokenSource(token, skipPolish);
            try
            {
                // Bound rewrite requests independently of audio chunking; a long rewrite cannot
                // silently return a truncated final transcript.
                if (clean.Length > 12000) throw new InvalidOperationException("Long transcript uses Clean; select smaller sections for rewriting.");
                // Only the opt-in Concise style may send transcript text to the API, never audio; failures use Clean.
                result = settings.OutputStyle == OutputStyle.Concise
                    ? await ConciseRewriter.RewriteAsync(clean, settings, polish.Token)
                    : await LocalRewriter.RewriteAsync(clean, settings, waiting, polish.Token, waitEnded);
                if (!prepared.PreservesSnippets(result)) throw new InvalidOperationException("Rewrite changed a snippet.");
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                result = clean;
                note = "Used Clean: " + (skipPolish.IsCancellationRequested ? PolishCancelled : ex is InvalidOperationException or TimeoutException ? ex.Message : "rewriting unavailable.");
            }
        }
        token.ThrowIfCancellationRequested();
        // Layout last, before snippet bodies return: those stay exactly as stored.
        if (settings.SpokenFormatting && !settings.LiteralMode) result = SpokenFormatting.Email(result, prepared.Text);
        return new(raw, prepared.Restore(result), note);
    }
}
