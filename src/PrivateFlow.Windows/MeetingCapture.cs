using System.Diagnostics;
using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// Records a meeting into chunk files with a live preview, then runs the final pass after Stop. The file writes, the preview and
// the final pass run on the thread pool, so the window stays responsive; events arrive on the thread pool as well.
internal sealed class MeetingCapture : IDisposable
{
    private readonly MeetingArchive archive;
    private readonly Settings settings;
    private readonly List<IAudioSource> recorders = [];
    private readonly List<Task> writes = [];
    private readonly Channel<MeetingChunk> pending = Channel.CreateUnbounded<MeetingChunk>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource cancellation = new(), preview = new();
    private readonly SemaphoreSlim manifest = new(1);
    private readonly SilenceMonitor microphoneSilence = new();
    private IAudioSource? microphone, systemAudio;
    private bool microphoneFellSilent;
    private Task? recognition, stop;
    // Why the final pass was cancelled; the first reason given is kept.
    private string? cancelReason;
    private readonly Stopwatch watch = new();
    public MeetingDocument Document { get; }
    public double Seconds => watch.Elapsed.TotalSeconds;
    // The first reason the capture stopped on its own, such as a removed device or a full disk.
    public string? CaptureError { get; private set; }
    // Why the chosen output device was not recorded, or null when it was.
    public string? PlaybackFallback { get; private set; }
    public bool MicrophoneSilent => microphoneSilence.Silent;
    public event Action<string>? Changed;
    // The live preview stopped with this reason; the recording goes on.
    public event Action<string>? PreviewStopped;
    public event Action<IReadOnlyList<MeetingSegment>>? Previewed;
    public event Action? Failed;
    public event Action<MeetingProgress>? Progressed;
    public MeetingCapture(MeetingArchive archive, Settings settings, string title, string languages, string vocabulary)
    {
        this.archive = archive; this.settings = settings.Snapshot(); this.settings.OutputStyle = OutputStyle.Clean;
        Document = new() { Title = string.IsNullOrWhiteSpace(title) ? MeetingVocabulary.DefaultTitle : title.Trim(), RetainAudio = settings.RetainMeetingAudio,
            Languages = string.Join(",", SpeechLanguages.Parse(languages)) is { Length: > 0 } chosen ? chosen : SpeechLanguages.Offered, Vocabulary = vocabulary.Trim() };
    }
    // A null device identifier leaves that channel out; an empty one follows the Windows default.
    public void Start(string? microphoneId, string? playbackId, MicrophoneDevices devices)
    {
        if (microphoneId is null && playbackId is null) throw new InvalidOperationException("Choose microphone, system audio, or both.");
        var sources = new List<(IAudioSource Source, string Channel, string Device)>();
        string? fallback = null;
        try
        {
            if (microphoneId is not null) { var device = devices.Open(microphoneId); sources.Add((new AudioRecorder(device), "mic", device.FriendlyName)); }
            if (playbackId is not null)
            {
                var device = devices.OpenPlayback(playbackId);
                fallback = PlaybackFallbackNotice(playbackId, device.ID, device.FriendlyName);
                sources.Add((new AudioRecorder(new WasapiLoopbackCapture(device), device), "system", device.FriendlyName));
            }
        }
        catch { foreach (var source in sources) source.Source.Dispose(); throw; }
        Start(sources, fallback);
    }
    internal static string? PlaybackFallbackNotice(string chosenId, string recordedId, string recordedName) =>
        chosenId.Length == 0 || chosenId == recordedId ? null
            : $"The chosen output device is not connected, so this meeting records the Windows default “{recordedName}” instead.";
    internal void Start(IReadOnlyList<(IAudioSource Source, string Channel, string Device)> sources, string? playbackFallback = null)
    {
        PlaybackFallback = playbackFallback;
        try
        {
            foreach (var (source, channel, device) in sources)
                if (channel == "mic") { Document.MicrophoneDevice = device; microphone = source; } else { Document.PlaybackDevice = device; systemAudio = source; }
            archive.Save(Document); watch.Start();
            foreach (var (source, channel, _) in sources) Add(source, channel);
            recognition = Task.Run(RecognizeAsync);
        }
        catch (Exception ex)
        {
            foreach (var (source, _, _) in sources) source.Dispose();
            cancellation.Cancel(); preview.Cancel(); pending.Writer.TryComplete(); Document.Ended = DateTimeOffset.UtcNow;
            Document.Status = "Interrupted"; Document.UnfinishedReason = MeetingArchive.CaptureStopped(ex.Message);
            try { archive.Save(Document); } catch (Exception saveException) when (saveException is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
    private void Add(IAudioSource recorder, string channel)
    {
        recorders.Add(recorder); recorder.Failed += () => Failed?.Invoke();
        var offset = watch.Elapsed.TotalSeconds;
        recorder.Start(); writes.Add(Task.Run(() => WriteAsync(recorder, channel, offset)));
    }
    // Hands the levels of both channels to the waveform on the interface thread, at its frame rate. Each microphone level also
    // reaches the silence monitor, exactly once.
    public void ReadLevels(Action<float> microphoneLevel, Action<float> systemAudioLevel)
    {
        if (microphone is not null)
            while (microphone.TryReadLevel(out var level)) { microphoneFellSilent |= microphoneSilence.Add(level); microphoneLevel(level); }
        if (systemAudio is not null)
            while (systemAudio.TryReadLevel(out var level)) systemAudioLevel(level);
    }
    // Reads the levels the waveform left; called about once a second while recording. True once per meeting, when the microphone first falls silent.
    public bool CheckMicrophone()
    {
        ReadLevels(_ => { }, _ => { });
        var warn = microphoneFellSilent; microphoneFellSilent = false;
        return warn;
    }
    private async Task WriteAsync(IAudioSource recorder, string channel, double channelOffset)
    {
        var number = 0;
        try
        {
            await foreach (var audio in recorder.Chunks.ReadAllAsync())
            {
                if (new DriveInfo(Path.GetPathRoot(archive.Root)!).AvailableFreeSpace < 256 * 1024 * 1024) throw new IOException("less than 256 MB free disk space.");
                var peak = 0f;
                foreach (var sample in audio.Samples) peak = Math.Max(peak, Math.Abs(sample));
                var chunk = new MeetingChunk { File = $"{channel}-{number++:D6}.wav", Channel = channel, Offset = channelOffset + audio.Offset, Duration = audio.Samples.Length / 16000.0, Peak = Math.Round(peak, 6) };
                var file = archive.ChunkPath(Document.Id, chunk.File); var partial = file + ".partial";
                using (var writer = new WaveFileWriter(partial, new WaveFormat(16000, 16, 1))) writer.WriteSamples(audio.Samples, 0, audio.Samples.Length);
                using (var durable = new FileStream(partial, FileMode.Open, FileAccess.Write, FileShare.None)) durable.Flush(true);
                File.Move(partial, file);
                await manifest.WaitAsync();
                try { Document.Chunks.Add(chunk); archive.Save(Document); } finally { manifest.Release(); }
                pending.Writer.TryWrite(chunk);
            }
        }
        catch (Exception ex)
        {
            CaptureError ??= (channel == "mic" ? "Microphone: " : "System audio: ") + ex.Message;
            Changed?.Invoke("Capture stopped; the saved audio is kept."); Failed?.Invoke();
        }
    }
    // A failing preview does not end the recording: the final pass after Stop transcribes the saved audio anyway.
    private async Task RecognizeAsync()
    {
        try
        {
            await foreach (var chunk in pending.Reader.ReadAllAsync(preview.Token))
            {
                var result = await TranscribeChunkAsync(archive, Document, chunk, settings, preview.Token);
                MeetingSegment[] snapshot;
                await manifest.WaitAsync(preview.Token);
                try { Append(Document, chunk, result); archive.Save(Document); snapshot = [.. Document.Segments]; }
                finally { manifest.Release(); }
                Changed?.Invoke("Meeting recording — live preview updated locally."); Previewed?.Invoke(snapshot);
            }
        }
        catch (OperationCanceledException) when (preview.IsCancellationRequested) { }
        catch (Exception ex) { PreviewStopped?.Invoke(ex.Message); }
    }
    // The live preview: one recognition window, in the most likely of the meeting's languages. The audio stays for the final pass.
    internal static Task<SpeechResult> TranscribeChunkAsync(MeetingArchive archive, MeetingDocument document, MeetingChunk chunk, Settings settings, CancellationToken token)
    {
        var session = settings.Snapshot(); session.Language = "auto"; session.Vocabulary = MeetingTranscriber.Prompt(document, settings);
        return WhisperEngine.TranscribeRecordingAsync(archive.ChunkPath(document.Id, chunk.File), session, token, document.Languages);
    }
    internal static void Append(MeetingDocument document, MeetingChunk chunk, SpeechResult result)
    {
        var channelSegments = document.Segments.Where(s => s.Channel == chunk.Channel).OrderBy(s => s.Start).Select(s => new TranscriptSegment(s.Start, s.End, s.Text, s.Speaker)).ToList();
        var oldCount = channelSegments.Count;
        SegmentAssembly.Append(channelSegments, result.Segments.Select(s => s with { Speaker = chunk.Channel == "mic" ? "You" : "Remote speaker" }), chunk.Offset);
        foreach (var segment in channelSegments.Skip(oldCount)) document.Segments.Add(new() { Start = segment.Start, End = segment.End, Text = segment.Text, Speaker = segment.Speaker, Channel = chunk.Channel });
        document.Segments = document.Segments.OrderBy(s => s.Start).ToList(); chunk.Transcribed = true;
    }
    // Stops the capture and runs the final pass, which replaces the preview. finishLater, CancelProcessing or a failing pass leave
    // the meeting Interrupted with its preview and audio, to be finished later.
    public Task StopAsync(bool finishLater = false)
    {
        if (finishLater) CancelProcessing(MeetingArchive.StoppedToFinishLater);
        return stop ??= Task.Run(StopCoreAsync);
    }
    private async Task StopCoreAsync()
    {
        Progressed?.Invoke(new("Stopping capture and saving the last audio", 0));
        // The final pass replaces the preview, so a backlog and the window being transcribed are not worth waiting for.
        preview.Cancel();
        // A device that does not confirm the stop is closed, which ends its chunks, so the last writes do not wait forever.
        foreach (var recorder in recorders) try { await recorder.StopAsync(); } catch (Exception ex) { CaptureError ??= ex.Message; recorder.Dispose(); }
        await Task.WhenAll(writes);
        pending.Writer.TryComplete();
        if (recognition is not null) await recognition;
        watch.Stop(); Document.Ended = DateTimeOffset.UtcNow;
        foreach (var recorder in recorders) recorder.Dispose();
        recorders.Clear();
        if (!cancellation.IsCancellationRequested)
        {
            Document.Status = "Finalizing";
            try
            {
                // A chunk whose write failed after its audio reached the disk is not listed; the final pass must read it before the audio is deleted.
                archive.RecoverUnlistedChunks(Document);
                await FinalizeAsync(archive, Document, settings, new ProgressCallback<MeetingProgress>(step => Progressed?.Invoke(step)), cancellation.Token); return;
            }
            catch (Exception ex) when (!cancellation.IsCancellationRequested)
            {
                var failure = MeetingArchive.FinalTranscriptFailed(ex.Message);
                Interrupt(failure);
                throw new InvalidOperationException(failure);
            }
            // Cancelled, whatever the worker threw on the way: the preview and the audio stay to finish later.
            catch { }
        }
        Interrupt(cancelReason ?? MeetingArchive.FinishingCancelled);
    }
    private void Interrupt(string reason)
    {
        Document.Status = "Interrupted";
        Document.UnfinishedReason = CaptureError is { } error ? MeetingArchive.CaptureStopped(error) + " " + reason : reason;
        archive.Save(Document);
    }
    // Also ends the final pass after Stop; the meeting then keeps its live preview and its audio to finish later.
    public void CancelProcessing(string reason)
    {
        Interlocked.CompareExchange(ref cancelReason, reason, null);
        cancellation.Cancel();
    }
    // The final pass over the audio still on disk: unfinished meetings and meetings with retained audio. Audio is deleted only after
    // the transcript is saved, and only when the meeting does not retain it.
    public static async Task RetryAsync(MeetingArchive archive, MeetingDocument document, Settings settings, CancellationToken token, IProgress<MeetingProgress>? progress = null)
    {
        archive.RecoverUnlistedChunks(document);
        // A meeting a crash ended while recording has no end time; its audio tells how long it ran.
        if (document.Ended is null && document.Chunks.Count > 0) document.Ended = document.Started.AddSeconds(document.Chunks.Max(chunk => chunk.Offset + chunk.Duration));
        var session = settings.Snapshot(); session.OutputStyle = OutputStyle.Clean;
        try { await FinalizeAsync(archive, document, session, progress, token); }
        catch (Exception ex) when (!token.IsCancellationRequested && document.Status != "Complete")
        {
            document.Status = "Interrupted"; document.UnfinishedReason = MeetingArchive.FinalTranscriptFailed(ex.Message); archive.Save(document);
            throw;
        }
    }
    private static async Task FinalizeAsync(MeetingArchive archive, MeetingDocument document, Settings settings, IProgress<MeetingProgress>? progress, CancellationToken token)
    {
        await MeetingTranscriber.FinalizeAsync(archive, document, settings, progress, token);
        document.Ended ??= DateTimeOffset.UtcNow; document.Status = "Complete"; document.UnfinishedReason = ""; archive.Save(document);
        // The transcript is final and saved; audio a scanner or another program holds open right now is deleted at the next start.
        if (!document.RetainAudio)
            try { archive.DeleteAudio(document.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose()
    {
        cancellation.Cancel(); preview.Cancel();
        foreach (var recorder in recorders) recorder.Dispose();
        cancellation.Dispose(); preview.Dispose();
    }
}
