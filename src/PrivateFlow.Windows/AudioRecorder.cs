using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Collections.Concurrent;
using System.Threading.Channels;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

internal sealed record AudioChunk(float[] Samples, double Offset);

// What a meeting or a dictation records from: 16 kHz mono chunks, RMS levels of 25 ms for the silence check and the pill, and the
// seconds recorded so far.
internal interface IAudioSource : IDisposable
{
    ChannelReader<AudioChunk> Chunks { get; }
    double Seconds { get; }
    event Action? Failed;
    bool TryReadLevel(out float level);
    void Start();
    Task StopAsync();
}

internal sealed class AudioRecorder : IAudioSource
{
    // A window holds at most 20 s and is cut in the quietest moment of its last 6 s, so words are not split between windows.
    internal const int ChunkSamples = 320000, CutSearchSamples = 96000;
    private readonly WasapiCapture input;
    private readonly MMDevice? device;
    private readonly BufferedWaveProvider buffer;
    private readonly ISampleProvider samples;
    private readonly Channel<AudioChunk> chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<float> levels = new();
    private readonly float[] chunk = new float[ChunkSamples], scratch = new float[16000];
    private int count, levelCount;
    private long total, offset;
    private double squares;
    private bool stopping, completed, disposed;
    private readonly System.Diagnostics.Stopwatch clock = new();
    public double Seconds => Interlocked.Read(ref total) / 16000.0;
    public ChannelReader<AudioChunk> Chunks => chunks.Reader;
    public event Action? Failed;
    public AudioRecorder(MMDevice device) : this(new WasapiCapture(device), device) { }
    internal AudioRecorder(WasapiCapture capture, MMDevice? device = null)
    {
        input = capture; this.device = device;
        buffer = new(input.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(3), ReadFully = false, DiscardOnBufferOverflow = false };
        samples = WhisperEngine.Mono16Khz(buffer.ToSampleProvider());
        input.DataAvailable += (_, e) =>
        {
            if (completed) return;
            try
            {
                // Loopback emits no packets during silence. Advance timestamps through gaps
                // without generating an unbounded burst of silent audio when playback resumes.
                if (input is WasapiLoopbackCapture)
                {
                    var expected = (long)(Math.Max(0, clock.Elapsed.TotalSeconds - e.BytesRecorded / (double)input.WaveFormat.AverageBytesPerSecond) * 16000);
                    if (total + 1600 < expected)
                    {
                        if (count >= 160) Publish(count);
                        count = 0; offset = expected; Interlocked.Exchange(ref total, expected);
                    }
                }
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); Drain();
            }
            catch (Exception ex) { Finish(ex); Failed?.Invoke(); }
        };
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) { Finish(e.Exception); Failed?.Invoke(); return; }
            try { Drain(); Finish(null); } catch (Exception ex) { Finish(ex); Failed?.Invoke(); }
        };
    }
    private void Drain()
    {
        int read;
        while ((read = samples.Read(scratch, 0, scratch.Length)) > 0)
            for (var i = 0; i < read; i++) AddSample(scratch[i]);
    }
    private void AddSample(float value)
    {
        chunk[count++] = value; Interlocked.Increment(ref total);
        squares += value * value;
        if (++levelCount == 400) { levels.Enqueue((float)Math.Sqrt(squares / 400)); levelCount = 0; squares = 0; while (levels.Count > 64) levels.TryDequeue(out _); }
        if (count != chunk.Length) return;
        var cut = SpeechSegmentation.CutPoint(chunk, ChunkSamples - CutSearchSamples, ChunkSamples);
        Publish(cut);
        Array.Copy(chunk, cut, chunk, 0, count - cut);
        offset += cut; count -= cut;
    }
    private void Publish(int length)
    {
        if (!chunks.Writer.TryWrite(new(chunk[..length], offset / 16000.0)))
            throw new InvalidOperationException("Speech processing cannot keep up. Recording stopped without insertion; choose a faster model.");
    }
    private void Finish(Exception? error)
    {
        if (completed) return;
        completed = true;
        try { if (error is null && count >= 160) Publish(count); }
        catch (Exception ex) { error = ex; }
        chunks.Writer.TryComplete(error);
        if (error is null) stopped.TrySetResult(); else stopped.TrySetException(error);
    }
    public bool TryReadLevel(out float level) => levels.TryDequeue(out level);
    public void Start() { clock.Start(); input.StartRecording(); }
    public async Task StopAsync()
    {
        if (!stopping) { stopping = true; input.StopRecording(); }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
    // Ends the chunks first: closing a device that hangs can block, and a reader must not wait for it.
    public void Dispose() { if (disposed) return; disposed = true; Finish(new OperationCanceledException()); input.Dispose(); device?.Dispose(); }
}
