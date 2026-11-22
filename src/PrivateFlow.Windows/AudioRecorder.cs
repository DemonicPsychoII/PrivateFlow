using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Threading.Channels;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

internal sealed class AudioRecorder : IAudioSource
{
    internal const int ChunkSamples = AudioWindows.ChunkSamples, CutSearchSamples = AudioWindows.CutSearchSamples;
    private readonly WasapiCapture input;
    private readonly MMDevice? device;
    private readonly BufferedWaveProvider buffer;
    private readonly ISampleProvider samples;
    private readonly AudioWindows windows = new();
    private readonly float[] scratch = new float[16000];
    private bool stopping, disposed;
    private readonly System.Diagnostics.Stopwatch clock = new();
    public double Seconds => windows.Seconds;
    public ChannelReader<AudioChunk> Chunks => windows.Chunks;
    public event Action? Failed;
    public AudioRecorder(MMDevice device) : this(new WasapiCapture(device), device) { }
    internal AudioRecorder(WasapiCapture capture, MMDevice? device = null)
    {
        input = capture; this.device = device;
        buffer = new(input.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(3), ReadFully = false, DiscardOnBufferOverflow = false };
        samples = WhisperEngine.Mono16Khz(buffer.ToSampleProvider());
        input.DataAvailable += (_, e) =>
        {
            if (windows.Completed) return;
            try
            {
                // Loopback emits no packets during silence. Advance timestamps through gaps
                // without generating an unbounded burst of silent audio when playback resumes.
                if (input is WasapiLoopbackCapture)
                {
                    var expected = (long)(Math.Max(0, clock.Elapsed.TotalSeconds - e.BytesRecorded / (double)input.WaveFormat.AverageBytesPerSecond) * 16000);
                    if (windows.Total + 1600 < expected) windows.SkipTo(expected);
                }
                buffer.AddSamples(e.Buffer, 0, e.BytesRecorded); Drain();
            }
            catch (Exception ex) { windows.Finish(ex); Failed?.Invoke(); }
        };
        input.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null) { windows.Finish(e.Exception); Failed?.Invoke(); return; }
            try { Drain(); windows.Finish(null); } catch (Exception ex) { windows.Finish(ex); Failed?.Invoke(); }
        };
    }
    private void Drain()
    {
        int read;
        while ((read = samples.Read(scratch, 0, scratch.Length)) > 0)
            for (var i = 0; i < read; i++) windows.Add(scratch[i]);
    }
    public bool TryReadLevel(out float level) => windows.TryReadLevel(out level);
    public void Start() { clock.Start(); input.StartRecording(); }
    public async Task StopAsync()
    {
        if (!stopping) { stopping = true; input.StopRecording(); }
        await windows.Stopped.WaitAsync(TimeSpan.FromSeconds(5));
    }
    // Ends the chunks first: closing a device that hangs can block, and a reader must not wait for it.
    public void Dispose() { if (disposed) return; disposed = true; windows.Finish(new OperationCanceledException()); input.Dispose(); device?.Dispose(); }
}
