using System.IO;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// Notice is the yellow hint for extractive notes, shown with them by default and after a fallback.
// Model names the model that wrote the notes and where it ran, null for extractive notes. Run says how a press of Write notes ended
// that stored no notes: failed (with FallbackReason), cancelled, or the model found nothing to note; null when notes were written.
public sealed record MeetingNotesResult(string Text, bool Written, int Dropped, string? FallbackReason, string? Notice, string? Model = null, NotesRun? Run = null);

// The pinned model the Local model notes style downloads, Qwen3.5-4B Q4_K_M (Apache-2.0), chosen in the notes evaluation of 2026-11-17
// (docs/VALIDATION.md). Its setup is optional: the History page can offer it with its size, and nothing is downloaded before the user asks.
// It installs like every other model, through Settings → Models (WorkspacePages.SetupAsync, setup-rewrite.ps1 -Part notes).
// With 32,768 tokens of context it took about 3.9 GB of GPU memory, leaving room for the speech recognition model beside it.
public static class MeetingNotesModel
{
    public static string FileName => ModelCatalogue.Model(ModelCatalogue.NotesModel).File;
    public const int ContextTokens = 32768;
    // The model and llama.cpp: the CUDA build and its runtime on a PC with an NVIDIA GPU, otherwise the processor build.
    public static long DownloadBytes => ModelCatalogue.Model(ModelCatalogue.NotesModel).Bytes + ModelCatalogue.LlamaRuntimes(true, SpeechSetup.HasNvidiaGpu).Sum(runtime => runtime.Bytes);
    public static bool IsInstalled(Settings settings) => File.Exists(settings.MeetingNotesEnginePath) && File.Exists(settings.MeetingNotesModelPath);
}

// Notes in the style chosen in Settings. LocalModel keeps everything on this PC; Api sends the transcript text of one meeting in one
// request, audio never. Any failure, cancellation included, returns the extractive notes headed by the reason instead of throwing.
public static class MeetingNotesWriter
{
    public const string LocalDestination = "the local model";
    // Where the button on the History page sends the transcript: "gpt-6-luna at example.org", with a port that is not the scheme's
    // default ("example.org:8443"), or the local model on this PC.
    public static string Destination(Settings settings) => settings.MeetingNotesStyle == MeetingNotesStyle.Api
        ? $"{settings.RewriteModel} at {(Uri.TryCreate(settings.RewriteEndpoint, UriKind.Absolute, out var endpoint) ? endpoint.Authority : settings.RewriteEndpoint)}"
        : LocalDestination;
    // A complete function, given the system prompt and the request, replaces the model so the verification never reaches one.
    public static async Task<MeetingNotesResult> WriteAsync(MeetingDocument document, Settings settings, CancellationToken cancellation,
        Func<string, string, CancellationToken, Task<string>>? complete = null)
    {
        if (settings.MeetingNotesStyle == MeetingNotesStyle.Extractive)
            return new(MeetingArchive.Notes(document), false, 0, null, MeetingArchive.ExtractiveNotice(document));
        try
        {
            if (document.Segments.Count == 0) throw new InvalidOperationException("The meeting has no transcript yet.");
            var local = settings.MeetingNotesStyle == MeetingNotesStyle.LocalModel;
            if (local && ModelNotes.UnsuitableLocalModel(settings.MeetingNotesModelPath, Size(settings.MeetingNotesModelPath)) is { } unsuitable) throw new InvalidOperationException(unsuitable);
            var length = ModelNotes.Transcript(document).Length;
            var maximum = local ? ModelNotes.MaximumLocalTranscriptCharacters(settings.MeetingNotesContextTokens) : ModelNotes.MaximumTranscriptCharacters;
            if (length > maximum)
                throw new InvalidOperationException($"The transcript is too long for {(local ? "the local model" : "one request")} ({length:N0} characters, at most {maximum:N0}).");
            var model = local ? Path.GetFileNameWithoutExtension(settings.MeetingNotesModelPath) + " on this PC" : settings.RewriteModel + " through the API";
            var answer = complete is not null ? await complete(ModelNotes.System, ModelNotes.Request(document), cancellation)
                : local ? await LocalRewriter.WriteNotesAsync(settings, ModelNotes.System, ModelNotes.Request(document), ModelNotes.MaximumLocalAnswerTokens,
                    TimeSpan.FromSeconds(Math.Clamp(300 + length / 10.0, 600, 1800)), ModelNotes.AnswerSchema, cancellation)
                : await ChatEndpoint.CompleteAsync(settings, ModelNotes.System, ModelNotes.Request(document), 8192,
                    TimeSpan.FromSeconds(Math.Clamp(30 + length / 500.0, 60, 240)), cancellation);
            var notes = ModelNotes.Read(answer, document, model);
            if (notes.NothingFound) return new(MeetingArchive.Notes(document), false, 0, null, MeetingArchive.ExtractiveNotice(document), model, new(NotesRunOutcome.NothingFound, model));
            return new(notes.Text, true, notes.Dropped + notes.NotTimes, null, null, model);
        }
        catch (Exception ex)
        {
            var reason = ex switch
            {
                InvalidOperationException or TimeoutException => ex.Message,
                OperationCanceledException => "Writing the notes was cancelled.",
                _ => $"Writing the notes failed ({ex.GetType().Name})."
            };
            return new(ModelNotes.Fallback(document, reason), false, 0, reason, MeetingArchive.ExtractiveNotice(document), null,
                new(ex is OperationCanceledException ? NotesRunOutcome.Cancelled : NotesRunOutcome.Failed, reason));
        }
    }
    private static long? Size(string path) { try { return new FileInfo(path) is { Exists: true } file ? file.Length : null; } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; } }
}
