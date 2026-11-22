using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Threading;
using PrivateFlow.Core;
using Forms = System.Windows.Forms;

namespace PrivateFlow.Windows;

// What a dictation session needs from the window that hosts it.
internal interface IDictationView
{
    bool OtherOperationActive { get; }
    // An update was confirmed and runs until Private Flow restarts or the installer ends.
    bool Updating { get; }
    bool WindowShowing { get; }
    bool MicrophoneSelected { get; }
    // The stored preferences with the choices the window shows, on a copy; the shared settings keep only stored values.
    Settings DictationPreferences();
    void ShowStatus(string message);
    // Speech recognition is not set up: says so where it is set up.
    void SpeechNotSetUp();
    // The Dictate page's cards: a warning, an error or a note about this dictation.
    NoticeList Notices { get; }
    void ShowRecording();
    void ShowTranscribing();
    void ShowFileTranscription();
    void ShowIdle();
    void ShowResult(string original, string final);
    void OpenPage(string title);
    // Runs the setup of Local polish where its progress shows.
    void SetUpLocalPolish();
}

internal sealed class DictationController(Settings settings, MicrophoneDevices devices, GlobalShortcuts shortcuts, TrayIcon tray, IDictationView view, Dispatcher dispatcher)
{
    // A card of the Dictate page; each dictation starts without the cards of the one before.
    internal const string SilentKey = "dictation-silent", FailedKey = "dictation-failed", UsedCleanKey = "dictation-used-clean", NotPastedKey = "dictation-not-pasted",
        QueuedKey = "dictation-queued", RefusedKey = "dictation-refused", GpuMemoryKey = "dictation-gpu-memory", OutcomeKey = "dictation-outcome", EscapeKey = "dictation-escape",
        NoMicrophoneSelectedKey = "dictation-no-microphone-selected";
    private const string ModelFallbackKey = "dictation-model-fallback";
    private static readonly string[] OwnKeys = [SilentKey, FailedKey, UsedCleanKey, NotPastedKey, QueuedKey, RefusedKey, GpuMemoryKey, OutcomeKey, EscapeKey, ModelFallbackKey, NoMicrophoneSelectedKey];
    private void Report(string key, NoticeKind kind, string title, string text) => view.Notices.Report(new(key, kind, title, NoticeText.Short(text), NoticeText.Detail(text)));
    // A new dictation or file transcription starts without the cards of the one before; the window's own cards, such as a shortcut
    // in use by another app, stay.
    private void ClearOwnNotices() { foreach (var key in OwnKeys) view.Notices.Clear(key); }
    private static readonly ActivationShortcut CancelShortcut = new(0, 0x1b);
    private readonly DictationPolicy policy = new();
    // The dictation's one state: set only to what DictationTable.Next returns, in Handle.
    private DictationState state;
    private IAudioSource? recorder;
    private KeptAudio? kept;
    private Task<SpeechResult>? speechTask;
    private Settings? sessionSettings;
    private CancellationTokenSource? cancellation;
    // Set while the polish of a transcribed dictation may wait for another task or run after it; KeepClean skips the polish with it.
    private CancellationTokenSource? skipPolish;
    private long session;
    private int? cancelShortcut;
    private RecordingHud? hud;
    private SilenceWatch? silence;
    // The shortcut that started a hold-to-talk dictation; null in toggle and when started otherwise.
    private ActivationShortcut? heldShortcut;
    public DictationResult? LastResult { get; private set; }
    public IReadOnlyList<TranscriptSegment>? LastFileSegments { get; private set; }
    public bool IsRecording => DictationTable.IsRecording(state);
    internal bool IsStarting => state == DictationState.Starting;
    internal bool EscapeRegistered => cancelShortcut is not null;
    public bool IsBusy => DictationTable.IsBusy(state);
    // The polish of a recording waits for another task, or runs after that wait.
    public bool IsQueued => DictationTable.IsQueued(state);
    // The task that polish waited for, such as "the meeting notes".
    private string queuedBehind = "";

    public void Toggle(ActivationShortcut? shortcut = null)
    {
        var pressed = DictationTable.Pressed(state, heldShortcut, shortcut, shortcut is { } keys && Native.Held(keys));
        if (Handle(pressed).Actions.HasFlag(DictationActions.Start)) Start(false, shortcut);
    }
    public Task CancelAsync() => Handle(DictationEvent.Cancel).Done;
    public Task QuitAsync() => Handle(DictationEvent.Quit).Done;
    // Moves the dictation to the state the table gives for the event and carries out the actions that need nothing from the caller; the
    // caller carries out the others, such as opening the microphone it was given.
    private (DictationActions Actions, Task Done) Handle(DictationEvent happened, bool stale = false)
    {
        var step = DictationTable.Next(state, happened, new(stale, heldShortcut is not null, TimeSpan.FromSeconds(recorder?.Seconds ?? 0)));
        state = step.Next;
        var actions = step.Actions;
        if (actions.HasFlag(DictationActions.CancelSession)) { policy.Cancel(); cancellation?.Cancel(); }
        if (actions.HasFlag(DictationActions.KeepClean)) skipPolish?.Cancel();
        if (actions.HasFlag(DictationActions.ShowPill))
        {
            hud = new RecordingHud { Starting = true }; hud.Show();
            view.ShowRecording();
            if (!RegisterCancelShortcut()) Report(EscapeKey, NoticeKind.Warning, "Escape does not cancel this recording", "Another app holds Escape. Use Cancel in Private Flow.");
        }
        if (actions.HasFlag(DictationActions.ReleaseEscape)) ReleaseCancelShortcut();
        if (actions.HasFlag(DictationActions.RegisterEscape) && cancelShortcut is null) RegisterCancelShortcut();
        if (actions.HasFlag(DictationActions.ClearQueuedNotice)) view.Notices.Clear(QueuedKey);
        if (actions.HasFlag(DictationActions.AnnounceWait)) AnnounceWait();
        if (actions.HasFlag(DictationActions.SayPolishing)) view.ShowStatus($"Polishing the dictation that waited for {queuedBehind}; {KeepsClean}");
        if (actions.HasFlag(DictationActions.ShowWaiting)) ShowWaiting();
        // Block a new recording until the device has stopped and released its handle; what follows is said after that.
        if (actions.HasFlag(DictationActions.StopDevice)) return (actions, StopDeviceAsync(actions));
        Conclude(actions);
        if (actions.HasFlag(DictationActions.Transcribe)) _ = TranscribeAsync();
        return (actions, Task.CompletedTask);
    }
    private void Conclude(DictationActions actions)
    {
        if (actions.HasFlag(DictationActions.End)) End();
        if (actions.HasFlag(DictationActions.SayCancelled)) view.ShowStatus("Cancelled. No pending insertion.");
        if (actions.HasFlag(DictationActions.SayReleasedTooSoon)) { view.ShowStatus("Ready"); Report(OutcomeKey, NoticeKind.Note, "Released too soon", ReleasedTooSoon); }
    }
    // The dictation keeps its target window and session; the insertion checks run when the polish is done, without bringing the window back.
    internal static string QueuedPolish(string busy) => $"Another task, {busy}, is still running; the polish of this dictation runs afterwards.";
    private string KeepsClean => (cancelShortcut is null ? "Cancel on the Dictate page" : "Esc or Cancel") + " keeps its Clean text without pasting it.";
    // A shortcut pressed while a dictation's polish waits, or runs after the wait, says why nothing starts.
    public void ShowWaiting()
    {
        if (!IsQueued) return;
        var waiting = state == DictationState.WaitingForNotes;
        var message = waiting ? $"The last dictation waits for {queuedBehind}; {KeepsClean}"
            : $"The last dictation is being polished after waiting for {queuedBehind}; {KeepsClean}";
        view.Notices.Report(new(QueuedKey, NoticeKind.Note, waiting ? $"Waiting for {queuedBehind}" : "Polishing the dictation that waited", message));
        tray.ShowBalloon(5000, "Private Flow", message, Forms.ToolTipIcon.Info);
    }
    // Shown once the dispatcher gets to it, unless the dictation no longer waits by then.
    private void AnnounceWait()
    {
        var operation = session; var token = cancellation!.Token; var busy = queuedBehind;
        dispatcher.BeginInvoke(() =>
        {
            if (!policy.IsCurrent(operation) || token.IsCancellationRequested || state != DictationState.WaitingForNotes) return;
            view.ShowStatus($"Waiting for {busy}…");
            view.Notices.Report(new(QueuedKey, NoticeKind.Note, $"Waiting for {busy}", QueuedPolish(busy) + " Cancel keeps the Clean text here without pasting it."));
            tray.ShowBalloon(5000, "Private Flow", QueuedPolish(busy), Forms.ToolTipIcon.Info);
        });
    }
    private const string ReleasedTooSoon = "Nothing was transcribed. Hold the shortcut while you speak.";
    // The last dictation's audio and language while Keep dictations as test clips is on; null otherwise.
    public (float[] Samples, string Language)? LastClip { get; private set; }
    public void ForgetResult() { LastResult = null; LastFileSegments = null; ForgetClip(); }
    public void ForgetClip() { LastClip = null; kept = null; }
    public void ShowRecordingProgress()
    {
        if (recorder is null) return;
        var elapsed = recorder.Seconds;
        view.ShowStatus($"Recording • {elapsed:0}s • Esc cancels");
        while (recorder.TryReadLevel(out var level))
        {
            hud?.PushLevel(level);
            if (silence?.Add(level) == true)
            {
                Report(SilentKey, NoticeKind.Warning, "No sound from the microphone", "Check mute, the input channel or the selected microphone.");
                tray.ShowBalloon(5000, "Private Flow", "No sound from the microphone. Check mute or the selected microphone.", Forms.ToolTipIcon.Warning);
            }
        }
    }
    // Called on every timer tick of a dictation. In hold to talk, releasing the shortcut that started it ends it. With the shortcut's
    // modifiers held, Escape forms another combination than the registered Escape, so it is read here too.
    public void FollowHeldShortcut()
    {
        if (heldShortcut is not { } held || !DictationTable.ReadsHeldKeys(state)) return;
        if (Native.Down(0x1b)) Handle(DictationEvent.Escape);
        else if (!Native.Held(held)) Handle(DictationEvent.Release);
    }
    public void Start(bool explicitProfile = false, ActivationShortcut? shortcut = null)
    {
        if (App.DiagnosticMode) { view.ShowStatus("Diagnostic instance: capture and insertion disabled."); return; }
        // The test build plays the next clip of its audio library in place of the microphone.
        _ = StartAsync(explicitProfile, shortcut, App.LibraryAudio is { } library ? library.Open : microphoneId => new AudioRecorder(devices.Open(microphoneId)));
    }
    // The verification passes a synthetic source for the microphone. stage hears "pill" once the pill has its frame, before the start
    // checks whether it was ended meanwhile, and "recording" once the microphone records.
    internal async Task StartAsync(bool explicitProfile, ActivationShortcut? shortcut, Func<string, IAudioSource> openMicrophone, Action<string>? stage = null)
    {
        if (state != DictationState.Idle) return;
        if (view.Updating)
        {
            Report(RefusedKey, NoticeKind.Warning, "Dictation did not start", UpdateGate.StartRefusal);
            if (!view.WindowShowing) tray.ShowBalloon(5000, "Private Flow", UpdateGate.StartRefusal, Forms.ToolTipIcon.Info);
            return;
        }
        if (view.OtherOperationActive)
        {
            const string refusal = "Finish the meeting, or the setup or check under Speech recognition, before starting dictation.";
            Report(RefusedKey, NoticeKind.Warning, "Dictation did not start", refusal);
            if (!view.WindowShowing) tray.ShowBalloon(5000, "Private Flow", refusal, Forms.ToolTipIcon.Info);
            return;
        }
        if (!view.MicrophoneSelected)
        {
            Report(NoMicrophoneSelectedKey, NoticeKind.Warning, "Dictation did not start", "No microphone is selected.");
            if (!view.WindowShowing) tray.ShowBalloon(5000, "Private Flow", "Dictation did not start: no microphone is selected.", Forms.ToolTipIcon.Warning);
            return;
        }
        // Read before the pill shows; the pill never takes the focus or the foreground, so it could not change the target anyway.
        var target = Native.CaptureTarget();
        var snapshot = view.DictationPreferences();
        var matching = MatchProfile(target);
        if (!explicitProfile && matching is not null) snapshot.ApplyProfile(matching, false);
        if (!System.IO.File.Exists(settings.EnginePath) || !System.IO.File.Exists(settings.ModelPath)) { view.SpeechNotSetUp(); return; }
        session = policy.Begin(target);
        ClearOwnNotices();
        sessionSettings = snapshot;
        LastClip = null;
        kept = snapshot.KeepDictationClips ? new KeptAudio() : null;
        heldShortcut = snapshot.ShortcutBehavior == ShortcutBehavior.HoldToTalk ? shortcut : null;
        cancellation = new CancellationTokenSource();
        var started = session;
        var token = cancellation.Token;
        // Said only while this dictation's own load goes into too little GPU memory; null ends it once the model is loaded.
        void LoadingWithLowMemory(GpuMemory? memory) => dispatcher.BeginInvoke(() =>
        {
            if (!policy.IsCurrent(started)) return;
            if (memory is null) view.Notices.Clear(GpuMemoryKey);
            else view.Notices.Raise(new(GpuMemoryKey, NoticeKind.Warning, "GPU memory is low", SpeechRuntime.GpuMemoryLowLoading(memory) + "."));
        });
        // The pill is the only sign of the press while the window is hidden. Opening the microphone holds this thread, longest at the
        // first press, so the pill shows first, dimmed as "starting" until the microphone records, and gets its frame drawn. From the
        // pill on, the start ends as a recording does: Escape and Cancel work and preferences wait, before the yield.
        IAudioSource? opened = null;
        try
        {
            Handle(DictationEvent.Begin);
            await Dispatcher.Yield(DispatcherPriority.Background);
            stage?.Invoke("pill");
            // A stop, cancel, release or quit came first and ended the start: no device opens.
            if (!Handle(DictationEvent.PillShown, !policy.IsCurrent(started)).Actions.HasFlag(DictationActions.OpenDevice)) return;
            opened = openMicrophone(snapshot.MicrophoneId);
            // Ended while the device opened: closed at once, nothing recorded.
            if (Handle(DictationEvent.DeviceOpened, !policy.IsCurrent(started)).Actions.HasFlag(DictationActions.CloseDevice)) { opened.Dispose(); return; }
            var recording = recorder = opened;
            recording.Failed += () => dispatcher.BeginInvoke(() => Handle(DictationEvent.RecordingFailed, !policy.IsCurrent(started)));
            silence = new SilenceWatch();
            recording.Start();
            hud!.Starting = false;
            stage?.Invoke("recording");
            // A cold speech worker is a new process; started off this thread, it does not hold the interface.
            var keeping = kept;
            speechTask = Task.Run(() => ConsumeAsync(recording.Chunks, () => recording.Seconds, snapshot, token, LoadingWithLowMemory, keeping));
            _ = ObserveSpeechAsync(speechTask, started);
        }
        catch (Exception ex)
        {
            opened?.Dispose();
            // A start already ended, and perhaps followed by another, leaves the controls to that one.
            if (!Handle(DictationEvent.StartFailed, !policy.IsCurrent(started)).Actions.HasFlag(DictationActions.ReportStartFailed)) return;
            recorder = null;
            Report(FailedKey, NoticeKind.Error, "The microphone could not start", ex.Message); view.ShowStatus("Ready");
            if (!view.WindowShowing) tray.ShowBalloon(5000, "Private Flow", "The microphone could not start: " + NoticeText.Short(ex.Message), Forms.ToolTipIcon.Warning);
        }
    }
    private async Task TranscribeAsync()
    {
        var recording = recorder!;
        recorder = null;
        var operation = session;
        var token = cancellation!.Token;
        view.ShowTranscribing();
        view.ShowStatus("Transcribing…");
        hud?.SetTranscribing();
        var watch = Stopwatch.StartNew();
        try
        {
            await recording.StopAsync();
            token.ThrowIfCancellationRequested();
            var speech = await speechTask!;
            var raw = speech.Text;
            if (sessionSettings!.Language == "auto" && SpeechLanguages.IsSpoken(speech.Language)) sessionSettings.Language = speech.Language;
            if (!Handle(DictationEvent.RecognitionDone, !policy.IsCurrent(operation) || token.IsCancellationRequested).Actions.HasFlag(DictationActions.Polish)) return;
            var waited = false;
            DictationResult result;
            using var skip = new CancellationTokenSource();
            skipPolish = skip;
            try
            {
                result = await DictationPipeline.ProcessAsync(raw, sessionSettings!, token, busy =>
                {
                    // Called on this thread before the wait begins.
                    waited = true; queuedBehind = busy;
                    Handle(DictationEvent.WaitBegan);
                }, skip.Token, polishRuns => dispatcher.Invoke(() =>
                    Handle(polishRuns ? DictationEvent.WaitEnded : DictationEvent.WaitTimedOut, !policy.IsCurrent(operation) || token.IsCancellationRequested || skip.IsCancellationRequested)));
            }
            finally { Handle(DictationEvent.PolishDone); skipPolish = null; }
            var final = result.Final;
            var note = WhisperEngine.LowMemory is { } lowMemory ? (result.Notice is null ? "" : result.Notice + " • ") + SpeechRuntime.GpuMemoryLowNote(lowMemory) : result.Notice;
            // A dictation that waited may be pasted long after it was spoken, so a skipped polish is said outside the window too.
            if (waited && note is not null && !skip.IsCancellationRequested && policy.IsCurrent(operation)) tray.ShowBalloon(5000, "Private Flow", note, Forms.ToolTipIcon.Info);
            if (!policy.IsCurrent(operation) || token.IsCancellationRequested) return;
            result = result with { GpuMemoryNote = GpuMemoryNote() };
            LastResult = result;
            LastFileSegments = null;
            // Kept only with a language the clip can carry; a dictation whose language stayed undetermined is not offered.
            LastClip = kept?.Samples is { } clip && SpeechLanguages.IsSpoken(sessionSettings!.Language) ? (clip, sessionSettings.Language) : null;
            kept = null;
            view.ShowResult(raw, final);
            var insertion = DictationTable.Insertion(skip.IsCancellationRequested, final, sessionSettings!.AutoInsert, waited,
                DictationTable.PasteAllowed(App.LibraryAudio is not null, App.DiagnosticMode, DisposableTestVm.IsThisComputer));
            // Cancelled while the polish waited or ran after the wait: the recognized text stays here, never pasted or copied.
            if (insertion == DictationInsertion.KeepClean) { view.ShowStatus("Cancelled. Nothing was pasted; the recognized text is kept here."); return; }
            ShowNotices(result);
            if (insertion == DictationInsertion.NothingRecognized) { view.ShowStatus("Ready"); Report(OutcomeKey, NoticeKind.Note, "No text recognized", "Nothing was inserted."); return; }
            if (insertion == DictationInsertion.Review) { view.ShowStatus($"Ready to review • {watch.Elapsed.TotalSeconds:0.0}s"); view.OpenPage("Dictate"); return; }
            if (insertion == DictationInsertion.PasteRefused)
            {
                view.ShowStatus("Ready");
                Report(NotPastedKey, NoticeKind.Warning, "Not pasted: " + (App.DiagnosticMode ? "a diagnostic instance does not paste" : "the test build pastes only in the disposable test VM"),
                    "Use Copy text to take it from the Dictate page.");
                return;
            }
            // A very short utterance can finish before the stop shortcut, or in hold to talk the rest of the held
            // shortcut, is released; a paste with Ctrl/Alt still held would become a different shortcut in the target.
            for (var retry = 0; retry < 50 && Native.ModifiersDown; retry++)
                await Task.Delay(20, token);
            var selected = policy.Target;
            if (DictationTable.RestoresForeground(insertion)) Native.RestoreForeground(selected.Window);
            bool StillValid(FocusTarget current) => policy.IsCurrent(operation) && !token.IsCancellationRequested &&
                policy.CanInsert(operation, current, Native.ModifiersDown, final);
            var pasted = StillValid(Native.CaptureTarget()) ? await Native.PasteAsync(final, StillValid, PasteTiming.RestoreDelay(ProcessName(selected.Process))) : InsertionResult.Unavailable;
            var seconds = $"{watch.Elapsed.TotalSeconds:0.0}s";
            if (pasted == InsertionResult.Complete) { view.ShowStatus($"Pasted • {seconds}"); return; }
            // Do not pop the window over the user's work: leave the text on the clipboard instead, unless the dictation waited and the
            // user may have copied something else since, or the clipboard could not be saved for the paste.
            var clipboardUnreadable = pasted == InsertionResult.ClipboardUnreadable;
            var copied = DictationTable.CopiesWhenNotPasted(insertion, clipboardUnreadable) && Native.SetClipboardText(final);
            var reason = DictationPolicy.NotPastedReason(selected, Native.CaptureTarget(), clipboardUnreadable);
            view.ShowStatus(copied ? $"Copied • press Ctrl+V • {seconds}" : "Ready");
            Report(NotPastedKey, NoticeKind.Warning, "Not pasted: " + reason, copied ? "The text is on the clipboard; press Ctrl+V where you want it." : "Use Copy text to take it from the Dictate page.");
            tray.ShowBalloon(2500, "Private Flow", copied ? "Copied — press Ctrl+V to paste." : "Open Private Flow to copy your text.", Forms.ToolTipIcon.None);
        }
        catch (OperationCanceledException) { view.ShowStatus("Cancelled. No pending insertion."); }
        // Full GPU memory is not fixed in the window, so the window does not open over the user's work; a hidden window gets a tray notice.
        catch (GpuMemoryException ex)
        {
            cancellation?.Cancel(); view.ShowStatus("Ready"); Report(FailedKey, NoticeKind.Error, "Not transcribed", ex.Message);
            if (!view.WindowShowing) tray.ShowBalloon(5000, "Private Flow", "Not transcribed. " + ex.Message, Forms.ToolTipIcon.Warning);
        }
        catch (Exception ex) { cancellation?.Cancel(); view.ShowStatus("Ready"); Report(FailedKey, NoticeKind.Error, "Recording ended without insertion", ex.Message); view.OpenPage("Dictate"); }
        finally
        {
            recording.Dispose();
            if (speechTask is not null) try { await speechTask; } catch { }
            speechTask = null;
            policy.Complete(operation);
            Handle(DictationEvent.Ended);
        }
    }
    // A cancelled recording: the device stops, then the dictation ends and then says what the table gave after StopDevice.
    private async Task StopDeviceAsync(DictationActions then)
    {
        var recording = recorder!; recorder = null;
        try { await recording.StopAsync(); if (speechTask is not null) await speechTask; }
        // The cancel itself ends the speech task with OperationCanceledException; that is no error to report.
        catch (OperationCanceledException) { }
        catch (Exception ex) { Report(FailedKey, NoticeKind.Error, "The recording stopped with an error", ex.Message); }
        finally { speechTask = null; recording.Dispose(); Handle(DictationEvent.Ended); }
        Conclude(then);
    }
    public async Task TranscribeFileAsync(string path)
    {
        var snapshot = view.DictationPreferences();
        LastClip = null;
        if (!Handle(DictationEvent.FileChosen).Actions.HasFlag(DictationActions.TranscribeFile)) return;
        cancellation = new(); view.ShowFileTranscription(); ClearOwnNotices();
        var token = cancellation.Token;
        try
        {
            view.ShowStatus("Transcribing file locally…");
            var (result, model) = await WhisperEngine.TranscribeSegmentsAsync(path, snapshot, token, new Progress<double>(share => view.ShowStatus($"Transcribing file locally · {share:P0}")));
            if (snapshot.Language == "auto" && SpeechLanguages.IsSpoken(result.Language)) snapshot.Language = result.Language;
            var processed = await DictationPipeline.ProcessAsync(result.Text, snapshot, token, busy => dispatcher.BeginInvoke(() =>
                { view.ShowStatus($"Waiting for {busy}…"); view.Notices.Report(new(QueuedKey, NoticeKind.Note, $"Waiting for {busy}", QueuedPolish(busy))); }));
            view.Notices.Clear(QueuedKey);
            token.ThrowIfCancellationRequested(); processed = processed with { GpuMemoryNote = GpuMemoryNote() }; LastResult = processed; LastFileSegments = result.Segments;
            view.ShowResult(processed.Original, processed.Final);
            if (model.Fallback.Length > 0) Report(ModelFallbackKey, NoticeKind.Warning, "Transcribed with another model", model.Fallback);
            ShowNotices(processed);
            view.ShowStatus("File ready to review.");
        }
        catch (OperationCanceledException) { view.ShowStatus("File transcription cancelled."); }
        catch (Exception ex) { view.ShowStatus("Ready"); Report(FailedKey, NoticeKind.Error, "File transcription failed", ex.Message); }
        finally { Handle(DictationEvent.Ended); }
    }
    // The dictation's resources go: its cancellation, Escape and the pill; the Dictate page is idle.
    private void End()
    {
        cancellation?.Dispose(); cancellation = null;
        // The recording it was about is over.
        view.Notices.Clear(EscapeKey);
        view.ShowIdle();
        heldShortcut = null;
        ReleaseCancelShortcut();
        hud?.Close(); hud = null;
    }
    // The cards a result leaves: Clean used instead of the chosen style and why, and GPU memory that was low while it was recognized.
    internal void ShowNotices(DictationResult result)
    {
        if (result.Notice is { } notice)
        {
            var reason = notice.StartsWith("Used Clean: ") ? char.ToUpperInvariant(notice[12]) + notice[13..] : notice;
            view.Notices.Report(new(UsedCleanKey, NoticeKind.Warning, "Used Clean", NoticeText.Short(reason), NoticeText.Detail(reason),
                DictationPipeline.PolishNotSetUp(notice) ? new(LocalRewriter.PolishSetupLabel, view.SetUpLocalPolish) : null));
        }
        if (result.GpuMemoryNote is { } lowMemory) Report(GpuMemoryKey, NoticeKind.Warning, "GPU memory was low", lowMemory);
        else view.Notices.Clear(GpuMemoryKey);
    }
    private static string? GpuMemoryNote() => WhisperEngine.LowMemory is { } lowMemory ? SpeechRuntime.GpuMemoryLowNote(lowMemory) : null;
    // A diagnostic instance takes no keys from other programs: it counts Escape as registered without registering it.
    private const int DiagnosticEscape = -1;
    private bool RegisterCancelShortcut()
    {
        cancelShortcut = App.DiagnosticMode ? DiagnosticEscape : shortcuts.Register(CancelShortcut, () => Handle(DictationEvent.Escape));
        return cancelShortcut is not null;
    }
    private void ReleaseCancelShortcut()
    {
        if (cancelShortcut is { } identifier && identifier != DiagnosticEscape) shortcuts.Unregister(identifier);
        cancelShortcut = null;
    }
    // Transcribes a dictation's windows as the recording delivers them; seconds is the recorded length once the chunks end.
    // loadingWithLowMemory hears the GPU memory when the model loads into too little of it, and null once the load is done.
    internal static async Task<SpeechResult> ConsumeAsync(System.Threading.Channels.ChannelReader<AudioChunk> chunks, Func<double> seconds, Settings snapshot, CancellationToken token,
        Action<GpuMemory?>? loadingWithLowMemory = null, KeptAudio? kept = null)
    {
        // The model loads while the user speaks: a dictation shorter than a window sends its only chunk at the stop. A load that
        // fails, such as when another program holds the GPU memory, ends the recording at once with its reason; chunks wait meanwhile.
        var spilling = false;
        await WhisperEngine.LoadAsync(snapshot, token, memory => { spilling = true; loadingWithLowMemory?.Invoke(memory); });
        if (spilling) loadingWithLowMemory?.Invoke(null);
        var segments = new List<TranscriptSegment>();
        var languages = new Dictionary<string, double>();
        await foreach (var chunk in chunks.ReadAllAsync(token))
        {
            kept?.Add(chunk.Samples);
            var result = await WhisperEngine.TranscribeChunkAsync(chunk.Samples, snapshot, token, WhisperEngine.AllowedLanguages(snapshot));
            SegmentAssembly.Append(segments, result.Segments, chunk.Offset);
            languages[result.Language] = languages.GetValueOrDefault(result.Language) + result.Seconds;
        }
        return new(segments, seconds(), languages.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault() ?? snapshot.Language);
    }
    private async Task ObserveSpeechAsync(Task<SpeechResult> task, long started)
    {
        try { await task; }
        catch { _ = dispatcher.BeginInvoke(() => Handle(DictationEvent.RecordingFailed, !policy.IsCurrent(started))); }
    }
    private DictationProfile? MatchProfile(FocusTarget target)
    {
        try
        {
            using var process = Process.GetProcessById((int)target.Process);
            return DictationProfiles.Match(settings.Profiles, process.ProcessName + ".exe");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }
    private static string? ProcessName(uint id)
    {
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return null; }
    }
}
