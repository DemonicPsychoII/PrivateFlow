using System.Runtime.ExceptionServices;

namespace PrivateFlow.Core;

// Free and total memory of the GPU when a model did not fit, and what the model needs, in bytes.
public sealed record GpuMemory(long Free, long Total, long Needed)
{
    // Only a model that fits the whole GPU can fit once other work releases memory.
    public bool CanFree => Needed <= Total;
}

// The model a final pass ends with, and why when it is the speech model standing in for the meeting model; empty otherwise.
public sealed record AttemptResult<T>(T Result, string Model, string Fallback);

// How long a final pass waits for GPU memory, and how often it tries the load meanwhile.
public sealed record MemoryWaitTiming(TimeSpan Limit, TimeSpan Poll, TimeSpan NotesPoll);

// Which model a final pass uses after each failure. The meeting model runs first; any failure, also one while or after waiting for
// GPU memory, gets one more try with a fresh worker, then the speech model finishes the pass with the reason kept. GPU memory that
// other work can free is waited for once per model; a model that still does not fit gives way at once; when nothing is left the
// pass ends with the reason.
public static class ModelAttempts
{
    // work runs the pass with a model and hears whether another model can still finish the pass if this one fails; memoryOf reads a GPU
    // memory shortage from a failure; waitForMemory waits for memory to free up and returns null once the model loaded, else the failure
    // that ended the wait; next gets the step to show and whether the same model is tried again, before the following attempt.
    public static async Task<AttemptResult<T>> RunAsync<T>(string meetingModel, string speechModel, Func<string, bool, Task<T>> work,
        Func<Exception, GpuMemory?> memoryOf, Func<string, GpuMemory, Task<Exception?>> waitForMemory, Func<string, bool, Task> next, Func<string, string> name,
        CancellationToken token)
    {
        var separate = meetingModel.Length > 0 && !Same(meetingModel, speechModel);
        string[] models = separate ? [meetingModel, meetingModel, speechModel] : [speechModel, speechModel];
        var waited = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var fallback = "";
        for (var attempt = 0; ;)
        {
            var model = models[attempt];
            var otherModel = Array.FindIndex(models, attempt + 1, other => !Same(other, model));
            Exception failure;
            try { return new(await work(model, otherModel >= 0), model, fallback); }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException && !token.IsCancellationRequested) { failure = ex; }
            if (memoryOf(failure) is { CanFree: true } shortage && waited.Add(model))
            {
                var ended = await waitForMemory(model, shortage);
                if (ended is null) continue;
                failure = ended;
            }
            var memory = memoryOf(failure);
            var following = memory is null ? attempt + 1 : otherModel;
            if (following < 0 || following >= models.Length)
            {
                if (memory is not null) throw new InvalidOperationException(failure.Message + (memory.CanFree ? " Close what uses the GPU or let it finish, then finish the transcript again." : ""));
                ExceptionDispatchInfo.Throw(failure);
            }
            var retry = Same(models[following], model);
            if (retry) await next($"Trying {name(model)} again after an error ({failure.Message})", true);
            else
            {
                fallback = FallbackNote(name(model), name(models[following]), failure, memory);
                await next($"Finishing with {name(models[following])}: " + (memory is null ? $"{name(model)} failed twice" : $"not enough free GPU memory for {name(model)}"), false);
            }
            attempt = following;
        }
    }

    // Tries the load again until the model fits or the wait ends, and returns null once it loaded, otherwise the failure that ended the
    // wait: a shortage, or at once any other failure. While the meeting notes hold the GPU the wait lasts as long as they run and tries
    // less often, since every try starts a speech worker. step says what the pass waits for, again whenever that changes. One last try
    // follows the wait, because the notes or other work may have freed the memory during the last pause.
    public static async Task<Exception?> WaitForMemoryAsync(GpuMemory memory, MemoryWaitTiming timing, Func<bool> notesRunning, Func<Task> load,
        Func<Exception, GpuMemory?> memoryOf, Action<bool, GpuMemory> step, Func<TimeSpan, Task> delay, Func<TimeSpan> elapsed,
        CancellationToken token)
    {
        var forNotes = notesRunning();
        step(forNotes, memory);
        async Task<Exception?> TryLoadAsync()
        {
            try { await load(); return null; }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException && !token.IsCancellationRequested) { return ex; }
        }
        while (elapsed() < timing.Limit || notesRunning())
        {
            await delay(notesRunning() ? timing.NotesPoll : timing.Poll);
            if (await TryLoadAsync() is not { } failure) return null;
            if (memoryOf(failure) is not { } again) return failure;
            memory = again;
            if (notesRunning() != forNotes) { forNotes = !forNotes; step(forNotes, memory); }
        }
        return await TryLoadAsync();
    }

    public static string FallbackNote(string failed, string speech, Exception failure, GpuMemory? memory) => memory is null
        ? $"The meeting model {failed} failed twice ({failure.Message}), so the speech model {speech} produced this transcript."
        : $"The meeting model {failed} needs about {SpeechRuntime.Gigabytes(memory.Needed)} GB of GPU memory, " + (memory.CanFree
            ? $"but only {SpeechRuntime.Gigabytes(memory.Free)} of {SpeechRuntime.Gigabytes(memory.Total)} GB were free because other GPU work was using the rest, "
            : $"more than the {SpeechRuntime.Gigabytes(memory.Total)} GB this GPU has, ") + $"so the speech model {speech} produced this transcript.";

    private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
}
