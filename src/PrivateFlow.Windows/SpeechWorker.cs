using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

internal enum SpeechOperation { Transcribe, DetectLanguage, SpeechProbabilities }
// Languages lists the languages "auto" may choose; empty leaves Whisper's own detection over all languages.
internal sealed record SpeechRequest(string Language, string Vocabulary, string VadPath, bool DetectSpeech, string Languages = "", int BeamSize = 0,
    SpeechOperation Operation = SpeechOperation.Transcribe, bool ResetSpeechState = false);
// Memory is the GPU memory when the model did not fit the free memory: with an error the worker refused or failed the load, without
// one it loaded anyway. Loading marks the message a worker sends before such a load, ahead of the one that says it is ready; OnGpu in
// that ready message says the model runs on the GPU.
internal sealed record WorkerReply(SpeechResult? Result, string? Error, Dictionary<string, double>? LanguageProbabilities = null, float[]? SpeechProbabilities = null,
    GpuMemory? Memory = null, bool Loading = false, bool OnGpu = false);
// A model the worker did not load because the GPU memory it needs is held by other work; loading it anyway would move it to
// system memory, where recognition runs many times slower.
internal sealed class GpuMemoryException(string message, GpuMemory memory) : InvalidOperationException(message)
{
    public GpuMemory Memory { get; } = memory;
}

internal sealed class SpeechWorker : IDisposable
{
    internal const int MaximumSamples = 480000;
    private readonly SemaphoreSlim gate = new(1);
    private NamedPipeServerStream? pipe;
    private Process? process;
    private string? engine, model;
    // What the request limit depends on besides the audio (SpeechRuntime.SpeechRequestLimit), from the loaded model and its worker.
    private long modelBytes;
    private bool onGpu;
    // When the model was last used, for the five-minute idle unload; --verify-worker-faults moves it by hand.
    internal TimeProvider Clock { get; set; } = TimeProvider.System;
    private DateTimeOffset lastUse = DateTimeOffset.UtcNow;
    private Task exited = Task.CompletedTask;
    private static readonly AsyncLocal<bool> finalPass = new();
    // Set by a final pass for its loads: it can wait for GPU memory or finish with a smaller model, so a model that does not fit the
    // free memory is refused. Other loads, such as dictation's, go ahead with a warning.
    internal static bool FinalPassLoads { get => finalPass.Value; set => finalPass.Value = value; }
    private static readonly AsyncLocal<bool> otherModelLeft = new();
    // Set by a final pass for an attempt that another model can still finish, so its requests get the shorter limit
    // (SpeechRuntime.SpeechRequestLimit); every other request keeps five minutes.
    internal static bool OtherModelLeft { get => otherModelLeft.Value; set => otherModelLeft.Value = value; }
    // The GPU memory when the loaded model did not fit the free memory and loaded anyway; null otherwise.
    public GpuMemory? LowMemory { get; private set; }
    public bool IsWarm => process is { HasExited: false };

    public async Task<SpeechResult> TranscribeAsync(float[] samples, Settings settings, CancellationToken cancellation, string languages = "", int beamSize = 0) =>
        (await RequestAsync(samples, settings, new(settings.Language, settings.Vocabulary, settings.VadModelPath, settings.SpeechDetection, languages, beamSize), cancellation)).Result
        ?? throw new InvalidOperationException("Speech worker failed.");
    public async Task<WorkerReply> RequestAsync(float[] samples, Settings settings, SpeechRequest request, CancellationToken cancellation)
    {
        // The worker stops reading a longer message, which would leave this side writing until the request's time limit.
        if (samples.Length > MaximumSamples) throw new InvalidOperationException("Invalid speech chunk length.");
        await gate.WaitAsync(cancellation);
        try
        {
            await EnsureStartedAsync(settings, cancellation);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(SpeechRuntime.SpeechRequestLimit(samples.Length / (double)SpeechSegmentation.SampleRate, modelBytes, onGpu, OtherModelLeft));
            await PipeProtocol.WriteJsonAsync(pipe!, request, timeout.Token);
            var bytes = new byte[samples.Length * sizeof(float)];
            Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
            await PipeProtocol.WriteBytesAsync(pipe!, bytes, timeout.Token);
            var result = await PipeProtocol.ReadJsonAsync<WorkerReply>(pipe!, timeout.Token);
            if (result.Error is not null) throw new InvalidOperationException(result.Error);
            lastUse = Clock.GetUtcNow();
            if (!settings.KeepModelWarm) Stop();
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        { Stop(); throw new TimeoutException("Local speech worker timed out. Check the runtime and model pair.", ex); }
        catch (IOException) { Stop(); throw new InvalidOperationException(ExitedMessage); }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }
    private const string ExitedMessage = "Local speech worker exited unexpectedly. Check the runtime and model pair.";
    // Loads the model before a long pass, so loading shows as a step of its own; the first request would load it the same way.
    // loadingWithLowMemory hears on a worker thread, before this load is done, that it goes into too little free GPU memory.
    public async Task LoadAsync(Settings settings, CancellationToken cancellation, Action<GpuMemory>? loadingWithLowMemory = null)
    {
        await gate.WaitAsync(cancellation);
        try { await EnsureStartedAsync(settings, cancellation, loadingWithLowMemory); lastUse = Clock.GetUtcNow(); }
        catch (OperationCanceledException ex) when (!cancellation.IsCancellationRequested)
        { Stop(); throw new TimeoutException("Local speech worker timed out. Check the runtime and model pair.", ex); }
        catch (IOException) { Stop(); throw new InvalidOperationException(ExitedMessage); }
        catch { Stop(); throw; }
        finally { gate.Release(); }
    }
    private async Task EnsureStartedAsync(Settings settings, CancellationToken cancellation, Action<GpuMemory>? loadingWithLowMemory = null)
    {
        if (IsWarm && engine == settings.EnginePath && model == settings.ModelPath) return;
        Stop();
        // The stopped worker's GPU memory is free only once it has ended.
        await exited;
        if (!File.Exists(settings.EnginePath) || !File.Exists(settings.ModelPath)) throw new InvalidOperationException("Run speech setup first.");
        var bytes = new FileInfo(settings.ModelPath).Length;
        var name = "PrivateFlow-speech-" + Guid.NewGuid().ToString("N");
        var connection = pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(settings.EnginePath)! };
        foreach (var arg in new[] { "--speech-worker", name, settings.EnginePath, settings.ModelPath, FinalPassLoads ? RefuseShort : WarnShort }) start.ArgumentList.Add(arg);
        var started = process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the speech worker.");
        HelperProcessJob.Attach(started);
        _ = DrainAsync(started.StandardOutput); _ = DrainAsync(started.StandardError);
        var ready = await WaitUntilLoadedAsync(started, connection, memory => loadingWithLowMemory?.Invoke(memory), cancellation);
        LowMemory = ready.Memory; onGpu = ready.OnGpu; modelBytes = bytes;
        engine = settings.EnginePath; model = settings.ModelPath;
    }
    internal const string RefuseShort = "refuse-short-memory", WarnShort = "warn-short-memory";
    // A worker that ends has failed. One that still uses processor time or reads its model is still loading, which can take far longer
    // than its usual seconds when another program takes GPU memory after the worker checked it; a worker without any progress for
    // StalledLimit has hung, and one still loading after LoadingLimit is given up.
    private static async Task<WorkerReply> WaitUntilLoadedAsync(Process worker, NamedPipeServerStream connection, Action<GpuMemory> loadingWithLowMemory, CancellationToken cancellation)
    {
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var ready = ReadyAsync(connection, loadingWithLowMemory, abandon.Token);
        var clock = Stopwatch.StartNew(); var lastProgress = clock.Elapsed; var activity = Activity(worker);
        try
        {
            while (!ready.IsCompleted)
            {
                await Task.WhenAny(ready, worker.WaitForExitAsync(abandon.Token), Task.Delay(ProgressInterval, abandon.Token));
                cancellation.ThrowIfCancellationRequested();
                if (ready.IsCompleted) break;
                if (worker.HasExited) throw new InvalidOperationException(EndedWhileLoading(worker.ExitCode));
                var now = Activity(worker);
                if (now != activity) { activity = now; lastProgress = clock.Elapsed; }
                else if (clock.Elapsed - lastProgress > StalledLimit)
                    throw new TimeoutException($"Local speech worker stopped responding while loading the model ({clock.Elapsed.TotalSeconds:0} s). Check the runtime and model pair.");
                if (clock.Elapsed > LoadingLimit)
                    throw new TimeoutException($"The speech model was still loading after {LoadingLimit.TotalMinutes:0} minutes. Check the runtime and model pair.");
            }
            // A worker that ends while connected breaks the pipe, and the failed read can come before its exit is seen: its exit code is
            // the reason then, once it has ended.
            if (ready.IsFaulted && ready.Exception!.GetBaseException() is IOException && await ExitedWithinAsync(worker, TimeSpan.FromSeconds(2)))
                throw new InvalidOperationException(EndedWhileLoading(worker.ExitCode));
            return await ready;
        }
        finally { abandon.Cancel(); }
    }
    private static string EndedWhileLoading(int exitCode) => $"Local speech worker ended while loading the model (exit code {exitCode}). Check the runtime and model pair.";
    private static async Task<bool> ExitedWithinAsync(Process worker, TimeSpan limit)
    {
        using var wait = new CancellationTokenSource(limit);
        try { await worker.WaitForExitAsync(wait.Token); return true; }
        catch (OperationCanceledException) { return worker.HasExited; }
    }
    private static async Task<WorkerReply> ReadyAsync(NamedPipeServerStream connection, Action<GpuMemory> loadingWithLowMemory, CancellationToken cancellation)
    {
        await connection.WaitForConnectionAsync(cancellation);
        var ready = await PipeProtocol.ReadJsonAsync<WorkerReply>(connection, cancellation);
        if (ready is { Loading: true, Memory: { } low })
        {
            loadingWithLowMemory(low);
            ready = await PipeProtocol.ReadJsonAsync<WorkerReply>(connection, cancellation);
        }
        if (ready.Error is not null) throw ready.Memory is { } memory ? new GpuMemoryException(ready.Error, memory) : new InvalidOperationException(ready.Error);
        return ready;
    }
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(2), StalledLimit = TimeSpan.FromSeconds(60), LoadingLimit = TimeSpan.FromMinutes(3);
    // Processor time plus bytes read and written: either moves while a model is read from disk and copied to the GPU.
    private static long Activity(Process worker)
    {
        try
        {
            worker.Refresh();
            var counters = GetProcessIoCounters(worker.Handle, out var io) ? (long)(io.ReadBytes + io.WriteBytes + io.OtherBytes) : 0;
            return worker.TotalProcessorTime.Ticks + counters;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);
    public void UnloadIfIdle()
    {
        if (Clock.GetUtcNow() - lastUse < TimeSpan.FromMinutes(5) || !gate.Wait(0)) return;
        try { Stop(); } finally { gate.Release(); }
    }
    public async Task UnloadAsync()
    {
        await gate.WaitAsync();
        try { Stop(); } finally { gate.Release(); }
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        // Discard native diagnostics; transcripts and model output must not become persistent logs.
        try { var buffer = new char[2048]; while (await reader.ReadAsync(buffer) > 0) { } }
        catch (ObjectDisposedException) { } catch (IOException) { }
    }
    // Dispose at exit runs without the gate, possibly while a background load holds it, so each caller takes the worker for itself.
    // Stop runs on the interface thread too (Escape, a cancelled load), so it does not wait for the worker to end; the next start does.
    private void Stop()
    {
        Interlocked.Exchange(ref pipe, null)?.Dispose();
        if (Interlocked.Exchange(ref process, null) is { } running)
        {
            try { if (!running.HasExited) running.Kill(true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            exited = EndedAsync(running);
        }
        engine = model = null; LowMemory = null;
    }
    // Kill only requests the end; the driver releases the worker's GPU memory once it has ended.
    private static async Task EndedAsync(Process running)
    {
        try { await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        finally { running.Dispose(); }
    }
    public void Dispose() => Stop();
    // For a setup that replaces the runtime files the worker maps: the worker stops and has ended when this returns, and no request starts
    // it again until the lease is disposed.
    public async Task<IDisposable> ReserveAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        Stop();
        await exited;
        return new GateLease(gate);
    }
    private sealed class GateLease(SemaphoreSlim held) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) held.Release(); }
    }

    // instead, given the connected pipe, may answer in place of the worker and return true; the verification host's worker that fails
    // on cue is the only one that does, so the app passes nothing.
    public static async Task RunChildAsync(string name, string engine, string model, string memoryMode, Func<NamedPipeClientStream, Task<bool>>? instead = null)
    {
        using var connection = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await connection.ConnectAsync(30000);
            if (instead is not null && await instead(connection)) return;
            // The constructor loads the model; the message before it lets a dictation say why the load is slow while it lasts.
            using var speech = new NativeSpeech(engine, model, memoryMode == RefuseShort,
                low => PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, null, Memory: low, Loading: true), CancellationToken.None).GetAwaiter().GetResult());
            await PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, null, Memory: speech.LowMemory, OnGpu: speech.OnGpu), CancellationToken.None);
            while (connection.IsConnected)
            {
                var request = await PipeProtocol.ReadJsonAsync<SpeechRequest>(connection, CancellationToken.None);
                var bytes = await PipeProtocol.ReadBytesAsync(connection, MaximumSamples * sizeof(float), CancellationToken.None);
                if (bytes.Length % sizeof(float) != 0) throw new InvalidDataException();
                var samples = new float[bytes.Length / sizeof(float)];
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                WorkerReply response;
                try { response = speech.Handle(samples, request); }
                catch (InvalidOperationException ex) { response = new(null, ex.Message); }
                await PipeProtocol.WriteJsonAsync(connection, response, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            var error = ex is InvalidOperationException ? ex.Message : "Local speech worker could not initialize or continue.";
            var memory = (ex as GpuMemoryException)?.Memory;
            if (connection.IsConnected)
                try { await PipeProtocol.WriteJsonAsync(connection, new WorkerReply(null, error, Memory: memory), CancellationToken.None); }
                catch (IOException) { }
        }
        finally { System.Windows.Application.Current.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown()); }
    }
}

internal static class PipeProtocol
{
    public static Task WriteJsonAsync<T>(Stream stream, T value, CancellationToken cancellation) =>
        WriteBytesAsync(stream, JsonSerializer.SerializeToUtf8Bytes(value), cancellation);
    public static async Task<T> ReadJsonAsync<T>(Stream stream, CancellationToken cancellation) =>
        JsonSerializer.Deserialize<T>(await ReadBytesAsync(stream, 4 * 1024 * 1024, cancellation)) ?? throw new InvalidDataException();
    public static async Task WriteBytesAsync(Stream stream, byte[] bytes, CancellationToken cancellation)
    {
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellation);
        await stream.WriteAsync(bytes, cancellation);
        await stream.FlushAsync(cancellation);
    }
    public static async Task<byte[]> ReadBytesAsync(Stream stream, int maximum, CancellationToken cancellation)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, cancellation);
        var length = BitConverter.ToInt32(header);
        if (length < 0 || length > maximum) throw new InvalidDataException("Invalid worker message length.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, cancellation); return bytes;
    }
}
