using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// A speech worker that fails on cue, for --verify-worker-faults only: with an isolated data folder, PRIVATE_FLOW_TEST_WORKER_FAULT
// makes the worker end, hang, refuse for want of GPU memory or answer with an error instead of loading the model, so the app's
// handling of each case is checked without a broken runtime or a full GPU.
internal static class WorkerFaults
{
    public const string Variable = "PRIVATE_FLOW_TEST_WORKER_FAULT";
    public static readonly string[] All = ["exit-while-loading", "hang-while-loading", "gpu-memory", "error-on-request", "exit-on-request"];

    public static string? Requested => Settings.IsolatedRoot && Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } fault && All.Contains(fault) ? fault : null;

    // The host's speech worker answers with the requested fault in place of loading the model; false when none is requested.
    public static async Task<bool> TryActAsync(NamedPipeClientStream connection)
    {
        if (Requested is not { } fault) return false;
        await ActAsync(fault, connection);
        return true;
    }

    public static async Task ActAsync(string fault, NamedPipeClientStream connection)
    {
        switch (fault)
        {
            case "exit-while-loading": Environment.Exit(7); break;
            case "hang-while-loading": await Task.Delay(Timeout.Infinite); break;
            case "gpu-memory":
                await PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, "Not enough free GPU memory for the test model.", Memory: new GpuMemory(500_000_000, 8_000_000_000, 4_600_000_000)), CancellationToken.None);
                break;
            default:
                await PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, null), CancellationToken.None);
                await PipeProtocol.ReadJsonAsync<SpeechRequest>(connection, CancellationToken.None);
                await PipeProtocol.ReadBytesAsync(connection, SpeechWorker.MaximumSamples * sizeof(float), CancellationToken.None);
                if (fault == "exit-on-request") Environment.Exit(9);
                await PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, "whisper_full failed (test)."), CancellationToken.None);
                break;
        }
    }

    // --verify-worker-faults <report.json>: each fault once against the installed engine settings, then a normal request that must
    // work again with a new worker. The report lists the exception and message per fault.
    public static async Task RunAsync(string report)
    {
        var results = new List<object>();
        var passed = true;
        try
        {
            var settings = Settings.Load();
            // A worker that ends right after it connects is seen either by the exit watch or by the pipe closing first; both messages
            // name the runtime and model pair, and which one comes first is a race.
            var expected = new Dictionary<string, (Type Type, string[] Messages)>
            {
                ["exit-while-loading"] = (typeof(InvalidOperationException), ["Local speech worker ended while loading the model (exit code 7)", "Local speech worker exited unexpectedly"]),
                ["hang-while-loading"] = (typeof(TimeoutException), ["Local speech worker stopped responding while loading the model"]),
                ["gpu-memory"] = (typeof(GpuMemoryException), ["Not enough free GPU memory for the test model."]),
                ["error-on-request"] = (typeof(InvalidOperationException), ["whisper_full failed (test)."]),
                ["exit-on-request"] = (typeof(InvalidOperationException), ["Local speech worker exited unexpectedly"]),
            };
            foreach (var fault in All)
            {
                // A warm worker from the request before would answer instead of a new one that has the fault.
                await WhisperEngine.UnloadAsync();
                Environment.SetEnvironmentVariable(Variable, fault);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                Exception? error = null;
                try { await WhisperEngine.TranscribeChunkAsync(new float[16000], settings, CancellationToken.None); }
                catch (Exception caught) { error = caught; }
                Environment.SetEnvironmentVariable(Variable, null);
                var (type, messages) = expected[fault];
                var ok = error is not null && type.IsInstanceOfType(error) && messages.Any(message => error.Message.StartsWith(message, StringComparison.Ordinal));
                // The next dictation starts a new, working worker.
                var recovered = false;
                try { await WhisperEngine.TranscribeChunkAsync(new float[16000], settings, CancellationToken.None); recovered = true; }
                catch (Exception) { }
                passed &= ok && recovered;
                results.Add(new { fault, passed = ok && recovered, error = error?.GetType().Name, message = error?.Message, recovered, seconds = Math.Round(clock.Elapsed.TotalSeconds, 1) });
            }
        }
        catch (Exception error) { passed = false; results.Add(new { fault = "setup", passed = false, error = error.GetType().Name, message = error.Message }); }
        // The idle unload: a warm worker stays up to five minutes without use and is unloaded after, on a clock moved by hand.
        try
        {
            var clock = new StepClock();
            WhisperEngine.Clock = clock;
            await WhisperEngine.TranscribeChunkAsync(new float[16000], Settings.Load(), CancellationToken.None);
            clock.Advance(TimeSpan.FromSeconds(299));
            WhisperEngine.UnloadIfIdle();
            var warmBefore = WhisperEngine.IsWarm;
            clock.Advance(TimeSpan.FromSeconds(2));
            WhisperEngine.UnloadIfIdle();
            for (var wait = 0; wait < 50 && WhisperEngine.IsWarm; wait++) await Task.Delay(100);
            var ok = warmBefore && !WhisperEngine.IsWarm;
            passed &= ok;
            results.Add(new { fault = "idle-unload", passed = ok, warmAfter299Seconds = warmBefore, warmAfter301Seconds = WhisperEngine.IsWarm });
        }
        catch (Exception error) { passed = false; results.Add(new { fault = "idle-unload", passed = false, error = error.GetType().Name, message = error.Message }); }
        finally { WhisperEngine.Clock = TimeProvider.System; }
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { Passed = passed, Faults = results }, new JsonSerializerOptions { WriteIndented = true }));
        WhisperEngine.Shutdown();
        System.Windows.Application.Current.Shutdown(passed ? 0 : 1);
    }
}

// A clock for the idle-unload check that only moves when told to.
internal sealed class StepClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan time) => now += time;
}
