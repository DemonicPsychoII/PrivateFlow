using System.Collections.Concurrent;
using System.Threading.Channels;

namespace PrivateFlow.Core;

public sealed record AudioChunk(float[] Samples, double Offset);

// What a dictation or a meeting records from: 16 kHz mono chunks for the transcription and RMS levels of 25 ms for the pill and
// the silence check.
public interface IAudioSource : IDisposable
{
    ChannelReader<AudioChunk> Chunks { get; }
    // The audio captured so far.
    double Seconds { get; }
    event Action? Failed;
    bool TryReadLevel(out float level);
    void Start();
    Task StopAsync();
}

// Cuts 16 kHz mono samples into the windows the live transcription reads, as they arrive: a window holds at most 20 s and is cut
// in the quietest moment of its last 6 s, so words are not split between windows. Every 400 samples (25 ms) give a level.
public sealed class AudioWindows
{
    public const int ChunkSamples = 320000, CutSearchSamples = 96000;
    private readonly Channel<AudioChunk> chunks = Channel.CreateBounded<AudioChunk>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<float> levels = new();
    private readonly float[] chunk = new float[ChunkSamples];
    private int count, levelCount;
    private long total, offset;
    private double squares;
    public bool Completed { get; private set; }
    public long Total => Interlocked.Read(ref total);
    public double Seconds => Total / 16000.0;
    public ChannelReader<AudioChunk> Chunks => chunks.Reader;
    public Task Stopped => stopped.Task;
    public void Add(float value)
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
    // Moves the clock over a gap without audio, publishing what came before it.
    public void SkipTo(long sample)
    {
        if (count >= 160) Publish(count);
        count = 0; offset = sample; Interlocked.Exchange(ref total, sample);
    }
    private void Publish(int length)
    {
        if (!chunks.Writer.TryWrite(new(chunk[..length], offset / 16000.0)))
            throw new InvalidOperationException("Speech processing cannot keep up. Recording stopped without insertion; choose a faster model.");
    }
    public void Finish(Exception? error)
    {
        if (Completed) return;
        Completed = true;
        try { if (error is null && count >= 160) Publish(count); }
        catch (Exception ex) { error = ex; }
        chunks.Writer.TryComplete(error);
        if (error is null) stopped.TrySetResult(); else stopped.TrySetException(error);
    }
    public bool TryReadLevel(out float level) => levels.TryDequeue(out level);
}

// Plays a clip as a microphone delivers it: in real time, in steps of about 10 ms, through the same windows and levels as the
// recorder, so the pill, the silence watch and the live transcription behave as they do with speech. After the clip the source
// stays silent, as a microphone does when the speaker has finished, until the dictation stops.
public sealed class LibraryAudioSource(float[] samples) : IAudioSource
{
    private readonly AudioWindows windows = new();
    private readonly CancellationTokenSource stopping = new();
    private Task playing = Task.CompletedTask;
    private bool disposed;
    public double Seconds => windows.Seconds;
    public ChannelReader<AudioChunk> Chunks => windows.Chunks;
    // A clip cannot be unplugged.
    public event Action? Failed { add { } remove { } }
    public bool TryReadLevel(out float level) => windows.TryReadLevel(out level);
    public void Start() => playing = Task.Run(PlayAsync);

    private async Task PlayAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long played = 0;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var due = (long)(clock.Elapsed.TotalSeconds * SpeechSegmentation.SampleRate);
                for (; played < due; played++) windows.Add(played < samples.Length ? samples[played] : 0f);
                await Task.Delay(10, stopping.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { windows.Finish(error); return; }
        windows.Finish(null);
    }

    public async Task StopAsync()
    {
        stopping.Cancel();
        await playing;
        await windows.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        stopping.Cancel();
        windows.Finish(new OperationCanceledException());
    }
}

// The audio of one dictation for "Keep as test clip", only while the user has turned that on: the windows put back together, up
// to ten minutes, after which the dictation is too long to keep and nothing is kept.
public sealed class KeptAudio
{
    public const int MaximumSamples = 10 * 60 * SpeechSegmentation.SampleRate;
    private readonly List<float> samples = [];
    private bool tooLong;
    public void Add(float[] chunk)
    {
        lock (samples)
        {
            if (tooLong) return;
            if (samples.Count + chunk.Length > MaximumSamples) { tooLong = true; samples.Clear(); return; }
            samples.AddRange(chunk);
        }
    }
    public float[]? Samples { get { lock (samples) return tooLong || samples.Count == 0 ? null : [.. samples]; } }
}
