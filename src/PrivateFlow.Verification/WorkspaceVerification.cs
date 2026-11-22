using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NAudio.Wave;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

internal static class WorkspaceVerification
{
    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var settings = Settings.Load();
            Check($"{0.42:P0} · {SpeechRuntime.Gigabytes(3_400_000_000)} GB · {12345:N0} · {new DateTime(2026, 11, 17):d MMM}" == "42% · 3.4 GB · 12,345 · 17 Nov",
                "numbers and dates in the English interface do not follow the region format of Windows");
            var health = await HealthChecks.SyntheticAsync(settings, CancellationToken.None); Check(health.Contains("passed"), "cold/warm synthetic ASR and VAD");
            var silence = await WhisperEngine.TranscribeChunkAsync(new float[32000], settings, CancellationToken.None);
            Check(silence.Segments.Count == 0, "Silero rejects generated silence");
            using (var cancel = new CancellationTokenSource())
            {
                var pending = WhisperEngine.TranscribeChunkAsync(new float[320000], settings, cancel.Token); cancel.Cancel();
                try { await pending; Check(false, "worker cancellation"); } catch (OperationCanceledException) { Check(true, "worker cancellation terminates request"); }
            }
            var wav = Path.Combine(output, "long-generated.wav");
            float[] speech = [];
            try
            {
                // Repeated generated speech exercises several chunk boundaries without a microphone.
                var sourceWav = Path.Combine(output, "source.wav");
                await GenerateAsync(sourceWav);
                using (var reader = new WaveFileReader(sourceWav))
                using (var writer = new WaveFileWriter(wav, reader.WaveFormat))
                {
                    var buffer = new byte[32000];
                    for (var i = 0; i < 5; i++) { reader.Position = 0; int read; while ((read = reader.Read(buffer, 0, buffer.Length)) > 0) writer.Write(buffer, 0, read); }
                }
                File.Delete(sourceWav);
                var (longResult, _) = await WhisperEngine.TranscribeSegmentsAsync(wav, settings, CancellationToken.None);
                Check(longResult.Seconds > 20 && longResult.Segments.Count > 1 && longResult.Text.Contains("not delete", StringComparison.OrdinalIgnoreCase), "long file uses bounded native chunks");
                var archive = new MeetingArchive(Path.Combine(Settings.Root, "verification-meetings"));
                var meeting = new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" }; archive.Save(meeting);
                var chunk = new MeetingChunk { File = "mic-000000.wav", Channel = "mic", Offset = 0, Duration = longResult.Seconds };
                File.Copy(wav, archive.ChunkPath(meeting.Id, chunk.File)); meeting.Chunks.Add(chunk); archive.Save(meeting);
                await MeetingCapture.RetryAsync(archive, meeting, settings, CancellationToken.None);
                var restored = archive.Load(meeting.Id);
                Check(restored.Status == "Complete" && restored.Segments.Count > 1 && !File.Exists(archive.ChunkPath(meeting.Id, chunk.File)), "meeting recovery saves timestamps and removes processed audio when retention off");
                restored.Segments[0].Speaker = "Alex"; archive.Save(restored);
                Check(archive.Load(meeting.Id).Segments[0].Speaker == "Alex", "speaker edits persist"); archive.Delete(meeting.Id);
                MeetingDocument Saved(MeetingArchive target, MeetingDocument document)
                {
                    var chunk = new MeetingChunk { File = "mic-000000.wav", Channel = "mic", Offset = 0, Duration = longResult.Seconds };
                    target.Save(document); File.Copy(wav, target.ChunkPath(document.Id, chunk.File)); document.Chunks.Add(chunk); target.Save(document); return document;
                }
                // Stop on a capture that recorded nothing live: the same finalizing path a meeting takes after its recorders stop.
                var capture = new MeetingCapture(archive, settings, "Generated meeting", "en,es", "");
                Saved(archive, capture.Document);
                var reports = new List<(MeetingProgress Step, string Status)>();
                capture.Progressed += step => reports.Add((step, archive.Load(capture.Document.Id).Status));
                await capture.StopAsync(); capture.Dispose();
                var finalized = archive.Load(capture.Document.Id);
                var steps = reports.Select(report => report.Step.Step).Distinct().ToArray();
                Check(reports.Count > 0 && reports.Skip(1).All(report => report.Status == "Finalizing") && reports.Zip(reports.Skip(1)).All(pair => pair.Second.Step.Share >= pair.First.Step.Share)
                    && reports[^1].Step.Share == 1 && steps.First().StartsWith("Stopping capture") && steps.Any(step => step.StartsWith("Loading the model"))
                    && new[] { "Detecting speech · microphone", "Detecting languages · microphone", "Transcribing · microphone", "Saving the transcript" }.All(steps.Contains)
                    && finalized.Status == "Complete" && finalized.Segments.Count > 1 && archive.AudioBytes(finalized.Id) == 0,
                    $"Stop finalizes: named steps from stopping to saving ({string.Join(", ", steps)}), progress never falls and ends at 100 %, then Complete with the final transcript and no audio left");
                archive.Delete(finalized.Id);
                // The same Stop with Retain processed audio on: the final transcript is written and the audio stays with the meeting.
                var retainedSettings = settings.Snapshot(); retainedSettings.RetainMeetingAudio = true;
                var retainedCapture = new MeetingCapture(archive, retainedSettings, "Generated meeting", "en,es", "");
                Saved(archive, retainedCapture.Document);
                await retainedCapture.StopAsync(); retainedCapture.Dispose();
                var retained = archive.Load(retainedCapture.Document.Id);
                Check(retained is { Status: "Complete", RetainAudio: true } && retained.Segments.Count > 1 && archive.AudioBytes(retained.Id) > 0
                    && archive.Unfinished().All(document => document.Document.Id != retained.Id),
                    "with Retain processed audio on, Stop finalizes the same way and keeps the audio with the complete meeting, which is not offered as unfinished");
                archive.Delete(retained.Id);
                // Cancel while the final pass runs: at the microphone channel's first run, before the system channel.
                var cancelled = new MeetingCapture(archive, settings, "Generated meeting", "en,es", "");
                Saved(archive, cancelled.Document);
                File.Copy(wav, archive.ChunkPath(cancelled.Document.Id, "system-000000.wav"));
                cancelled.Document.Chunks.Add(new() { File = "system-000000.wav", Channel = "system", Offset = 0, Duration = longResult.Seconds });
                cancelled.Document.Segments.Add(new() { Start = 0, End = 2, Channel = "mic", Speaker = "You", Text = "Live preview line." }); archive.Save(cancelled.Document);
                cancelled.Progressed += step => { if (step.Step.StartsWith("Transcribing")) cancelled.CancelProcessing(MeetingArchive.FinishingCancelled); };
                await cancelled.StopAsync(); cancelled.Dispose();
                var kept = archive.Load(cancelled.Document.Id);
                Check(kept.Status == "Interrupted" && kept.Segments.Select(segment => segment.Text).SequenceEqual(["Live preview line."]) && kept.Chunks.All(chunk => File.Exists(archive.ChunkPath(kept.Id, chunk.File)))
                    && kept.UnfinishedReason == MeetingArchive.FinishingCancelled,
                    "Cancel during the final pass keeps the live preview and the audio for Re-transcribe and stores that finishing was cancelled");
                archive.Delete(kept.Id);
                // Cancel after the last decode, while the pass only has the transcript left to save.
                var late = new MeetingCapture(archive, settings, "Generated meeting", "en,es", "");
                Saved(archive, late.Document);
                late.Document.Segments.Add(new() { Start = 0, End = 2, Channel = "mic", Speaker = "You", Text = "Live preview line." }); archive.Save(late.Document);
                late.Progressed += step => { if (step.Step.StartsWith("Transcribing") && step.Share >= 1) late.CancelProcessing(MeetingArchive.FinishingCancelled); };
                await late.StopAsync(); late.Dispose();
                var keptLate = archive.Load(late.Document.Id);
                Check(keptLate.Status == "Interrupted" && keptLate.Segments.Select(segment => segment.Text).SequenceEqual(["Live preview line."]) && archive.AudioBytes(keptLate.Id) > 0,
                    "Cancel after the last decode still keeps the live preview and the audio");
                archive.Delete(keptLate.Id);
                var later = new MeetingCapture(archive, settings, "Generated meeting", "en,es", "");
                Saved(archive, later.Document);
                await later.StopAsync(true); later.Dispose();
                var keptForLater = archive.Load(later.Document.Id);
                Check(keptForLater.Status == "Interrupted" && keptForLater.UnfinishedReason == MeetingArchive.StoppedToFinishLater && archive.AudioBytes(keptForLater.Id) > 0,
                    "Stop; finish later keeps the audio and stores that the meeting was stopped to finish later");
                archive.Delete(keptForLater.Id);
                // A chunk whose write reached the disk but never the meeting's list.
                var unlisted = new MeetingCapture(archive, settings, "Generated meeting", "en,es", "");
                archive.Save(unlisted.Document); File.Copy(wav, archive.ChunkPath(unlisted.Document.Id, "mic-000000.wav") + ".partial");
                await unlisted.StopAsync(); unlisted.Dispose();
                var recoveredUnlisted = archive.Load(unlisted.Document.Id);
                Check(recoveredUnlisted.Status == "Complete" && recoveredUnlisted.Segments.Count > 1 && archive.AudioBytes(recoveredUnlisted.Id) == 0,
                    "Stop transcribes a saved chunk that is missing from the meeting's list before its audio is deleted");
                archive.Delete(recoveredUnlisted.Id);
                var unusable = Path.Combine(output, "unusable-meeting-model.bin"); await File.WriteAllTextAsync(unusable, "not a model");
                var speechModel = SpeechSetup.ModelName(settings.ModelPath);
                bool Transcribed(MeetingDocument document) => document.Status == "Complete" && document.Segments.Any(segment => segment.Text.Contains("not delete", StringComparison.OrdinalIgnoreCase));
                static bool Forward(IReadOnlyList<MeetingProgress> steps) => steps.Count > 0 && steps.Zip(steps.Skip(1)).All(pair => pair.Second.Share >= pair.First.Share) && steps[^1].Share == 1;
                var fallback = settings.Snapshot(); fallback.MeetingModelPath = unusable;
                var fallbackMeeting = Saved(archive, new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" });
                var fallbackSteps = new List<MeetingProgress>();
                await MeetingCapture.RetryAsync(archive, fallbackMeeting, fallback, CancellationToken.None, new ProgressCallback<MeetingProgress>(fallbackSteps.Add));
                var fellBack = archive.Load(fallbackMeeting.Id);
                Check(Transcribed(fellBack) && fellBack.TranscriptModel == speechModel && fellBack.ModelFallback.StartsWith("The meeting model unusable-meeting-model.bin failed twice (Speech model could not load.")
                    && MeetingArchive.Notes(fellBack).StartsWith(fellBack.ModelFallback) && fallbackSteps.Count(step => step.Step == "Loading the model unusable-meeting-model.bin") == 2
                    && fallbackSteps.Any(step => step.Step.StartsWith("Trying unusable-meeting-model.bin again")) && fallbackSteps.Any(step => step.Step.StartsWith($"Finishing with {speechModel}")) && Forward(fallbackSteps),
                    $"a meeting model that cannot load is tried again, then the speech model finishes; the record and its notes say which model produced the transcript and why, and progress never falls ({fellBack.ModelFallback})");
                archive.Delete(fallbackMeeting.Id);
                var missing = settings.Snapshot(); missing.MeetingModelPath = Path.Combine(output, "missing-meeting-model.bin");
                var missingMeeting = Saved(archive, new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" });
                await MeetingCapture.RetryAsync(archive, missingMeeting, missing, CancellationToken.None);
                Check(archive.Load(missingMeeting.Id) is { ModelFallback: "" } withoutMeetingModel && Transcribed(withoutMeetingModel) && withoutMeetingModel.TranscriptModel == speechModel,
                    "a missing meeting model leaves the final pass to the speech model, recorded without a fallback");
                archive.Delete(missingMeeting.Id);
                if (settings.EnginePath.Contains("cuda", StringComparison.OrdinalIgnoreCase))
                {
                    // A model larger than the GPU stands in for one that does not fit in the free GPU memory: the worker refuses it before
                    // reading it. Its length is set without writing data. Waiting for other programs to free memory is pointless for it, so the
                    // checks see the reason and the fallback without the wait; three final passes at once show the wait (docs/VALIDATION.md).
                    var oversized = Path.Combine(output, "oversized-model.bin");
                    using (var stream = File.Create(oversized)) stream.SetLength(16L << 30);
                    try
                    {
                        var shortSpeech = settings.Snapshot(); shortSpeech.ModelPath = oversized;
                        var dictationClock = System.Diagnostics.Stopwatch.StartNew(); var dictationError = "";
                        try { await DictationController.ConsumeAsync(System.Threading.Channels.Channel.CreateUnbounded<AudioChunk>().Reader, () => 0, shortSpeech, CancellationToken.None); }
                        catch (GpuMemoryException ex) { dictationError = ex.Message; }
                        Check(dictationError.StartsWith("Not enough free GPU memory for oversized-model.bin: ") && dictationError.EndsWith("The GPU has too little memory for this model.") && dictationClock.Elapsed.TotalSeconds < 20,
                            $"a dictation whose model does not fit in the free GPU memory ends while recording, within {dictationClock.Elapsed.TotalSeconds:0.0} s, and says why ({dictationError})");
                        var shortMeeting = settings.Snapshot(); shortMeeting.MeetingModelPath = oversized;
                        var memoryMeeting = Saved(archive, new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" });
                        var memorySteps = new List<MeetingProgress>();
                        await MeetingCapture.RetryAsync(archive, memoryMeeting, shortMeeting, CancellationToken.None, new ProgressCallback<MeetingProgress>(memorySteps.Add));
                        var smaller = archive.Load(memoryMeeting.Id);
                        Check(Transcribed(smaller) && smaller.TranscriptModel == speechModel && smaller.ModelFallback.StartsWith("The meeting model oversized-model.bin needs about ")
                            && smaller.ModelFallback.Contains("GB of GPU memory, more than the ") && !memorySteps.Any(step => step.Step.StartsWith("Waiting for GPU memory"))
                            && memorySteps.Any(step => step.Step == $"Finishing with {speechModel}: not enough free GPU memory for oversized-model.bin") && Forward(memorySteps),
                            $"a meeting model larger than the GPU is refused before loading, without waiting, and the speech model finishes with the reason in the record, and progress never falls ({smaller.ModelFallback})");
                        archive.Delete(memoryMeeting.Id);
                        var nothingFits = settings.Snapshot(); nothingFits.ModelPath = oversized; nothingFits.MeetingModelPath = "";
                        var failedForMemory = Saved(archive, new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" });
                        var memoryFailure = "";
                        try { await MeetingCapture.RetryAsync(archive, failedForMemory, nothingFits, CancellationToken.None); }
                        catch (InvalidOperationException ex) { memoryFailure = ex.Message; }
                        Check(memoryFailure.StartsWith("Not enough free GPU memory for oversized-model.bin") && memoryFailure.EndsWith("The GPU has too little memory for this model.") && archive.AudioBytes(failedForMemory.Id) > 0,
                            $"when no model fits, the final pass ends with the reason and keeps the audio ({memoryFailure})");
                        archive.Delete(failedForMemory.Id);
                    }
                    finally { File.Delete(oversized); }
                }
                var broken = settings.Snapshot(); broken.ModelPath = unusable; broken.MeetingModelPath = "";
                var failing = new MeetingCapture(archive, broken, "Generated meeting", "en,es", "");
                Saved(archive, failing.Document);
                var failureReported = "";
                try { await failing.StopAsync(); } catch (InvalidOperationException ex) { failureReported = ex.Message; }
                failing.Dispose();
                var failed = archive.Load(failing.Document.Id);
                Check(failed.Status == "Interrupted" && failed.UnfinishedReason.StartsWith("The final transcript failed: ") && failed.UnfinishedReason == failureReported && archive.AudioBytes(failed.Id) > 0,
                    $"a failing final pass keeps the audio and stores its message ({failed.UnfinishedReason})");
                archive.Delete(failed.Id);
                File.Delete(unusable);
                var panelArchive = new MeetingArchive(Path.Combine(Settings.Root, "meetings"));
                // Left by earlier runs: a complete meeting without retained audio that still has a chunk and an unfinished chunk write,
                // and an interrupted meeting.
                var leftover = Saved(panelArchive, new MeetingDocument { Title = "Generated meeting", Status = "Complete" });
                File.WriteAllBytes(panelArchive.ChunkPath(leftover.Id, "system-000003.wav") + ".partial", new byte[100]);
                var interrupted = Saved(panelArchive, new MeetingDocument { Title = "Generated meeting", Status = "Interrupted" });
                var messages = new List<string>(); var notifications = new List<string>();
                // The shared microphone and output selections of the audio settings, with synthetic devices.
                var microphoneChoice = new System.Windows.Controls.ComboBox { ItemsSource = new List<MicrophoneDevice> { new("", "Follow Windows default microphone"), new("synthetic-microphone", "Synthetic microphone") }, SelectedIndex = 0 };
                var outputChoice = new System.Windows.Controls.ComboBox { ItemsSource = new List<MicrophoneDevice> { new("", "Follow Windows default output"), new("synthetic-headset", "Synthetic headset") }, SelectedIndex = 0 };
                var shortcutSettingsOpened = 0; var meetingSettingsOpened = 0;
                // The notes model is never reached: completeNotes stands in for it, and behind it are an endpoint that cannot resolve and
                // a key variable that is not set. The settings file keeps them for the window opened further down.
                settings.RewriteEndpoint = "https://notes.invalid/v1"; settings.RewriteModel = "test-model";
                settings.RewriteKeyVariable = "PRIVATE_FLOW_VERIFICATION_KEY_" + Guid.NewGuid().ToString("N"); settings.MeetingNotesStyle = MeetingNotesStyle.Extractive; settings.SaveAs(settings);
                var notesCalls = 0; var notesRequest = ""; Func<CancellationToken, Task<string>> notesAnswer = _ => throw new InvalidOperationException("No notes answer is set.");
                // The install of the meeting notes model runs only in the window, under Settings → Models; checked there with a stand-in for the
                // setup script. Here its offers are inert, so nothing is downloaded.
                var notesInstall = new NotesModelInstall(new(new() { Content = MeetingsPanel.NotesInstallLabel }, new()), new(new() { Content = MeetingsPanel.NotesInstallLabel }, new()), () => false, () => null);
                var panel = new MeetingsPanel(settings, microphoneChoice, outputChoice, messages.Add, notesInstall, (text, _) => notifications.Add(text), openShortcutSettings: () => shortcutSettingsOpened++,
                    openNotesSettings: () => meetingSettingsOpened++) { CompleteNotes = (_, user, token) => { notesCalls++; notesRequest = user; return notesAnswer(token); } };
                // The Meetings page, the History page with its header search, the colour swatches and the meeting settings side by side, as one window shows them.
                var pagesGrid = new Grid();
                var historyColumn = new DockPanel(); DockPanel.SetDock(panel.HistorySearch, Dock.Top); historyColumn.Children.Add(panel.HistorySearch); historyColumn.Children.Add(panel.History);
                // The notes style, which the window shows under Models.
                var modelsColumn = panel.NotesStyleChoice;
                foreach (var (page, width) in new (FrameworkElement, double)[] { (panel, 420), (historyColumn, 860), (panel.SystemAudioColorChoice, 120), (panel.MeetingSettings.Content, 400), (modelsColumn, 400) })
                { pagesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) }); Grid.SetColumn(page, pagesGrid.Children.Count); pagesGrid.Children.Add(page); }
                var host = new Window { Content = pagesGrid, ShowActivated = false, Width = 1800, Height = 900, Left = SystemParameters.WorkArea.Left, Top = SystemParameters.WorkArea.Top };
                Check(panelArchive.AudioBytes(leftover.Id) == 0 && panelArchive.AudioBytes(interrupted.Id) > 0 && notifications.Count == 1 && notifications[0].StartsWith("1 meeting is not finished")
                    && panel.ShowsUnfinished && panel.UnfinishedText.Contains("of its audio is kept on this PC only to finish it"),
                    "at start, audio left in a complete meeting without retained audio is deleted; an unfinished meeting keeps its audio and is announced with its size");
                panelArchive.Delete(leftover.Id);
                host.Show();
                var pageWave = Descendants(host).OfType<MirroredWaveform>().Single();
                string Named(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);
                // A chip's name carries its current choice, such as "Meeting languages: English only".
                System.Windows.Controls.Button Chip(string name) => Descendants(panel).OfType<System.Windows.Controls.Button>().Single(button => Named(button).StartsWith(name + ": "));
                // Picks an entry of a chip's menu, which opens in its own popup.
                void Choose(string chip, string entry)
                {
                    Press(Chip(chip));
                    var menu = PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Select(source => source.RootVisual).OfType<Visual>().SelectMany(VisualDescendants)
                        .OfType<System.Windows.Controls.ContextMenu>().First(open => open.IsOpen);
                    menu.Items.OfType<System.Windows.Controls.MenuItem>().First(item => ((TextBlock)item.Header).Text == entry).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                    menu.IsOpen = false;
                }
                string ChipText(string name) => Descendants(Chip(name)).OfType<TextBlock>().First().Text;
                var meetingTitle = Descendants(panel).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Meeting title");
                var meetingTranscript = Descendants(panel).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Meeting transcript");
                var shortcutLink = Descendants(panel).OfType<System.Windows.Documents.Hyperlink>().Single(link => Named(link) == "Set a meeting shortcut");
                var hintWithoutShortcut = System.Windows.Automation.AutomationProperties.GetName(panel.ShortcutHint);
                shortcutLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                panel.ShowShortcut(["Ctrl", "Alt", "M"]);
                var hintWithShortcut = System.Windows.Automation.AutomationProperties.GetName(panel.ShortcutHint); var linkHiddenWithShortcut = !((FrameworkElement)shortcutLink.Parent).IsVisible;
                panel.ShowShortcut(null);
                Check(hintWithoutShortcut == "Start a meeting below" && shortcutSettingsOpened == 1 && hintWithShortcut == "Press Ctrl + Alt + M to start a meeting" && linkHiddenWithShortcut,
                    "the Meetings hint spells a set meeting shortcut as key caps; without one it links to the shortcut settings");
                Check(pageWave.IsVisible && pageWave.MicrophoneHeight == 0 && pageWave.SystemAudioHeight == 0 && meetingTitle.Text == MeetingVocabulary.DefaultTitle && meetingTranscript.IsReadOnly
                    && ChipText("Meeting microphone") == "Default microphone" && ChipText("Meeting system audio") == "Default output" && ChipText("Meeting audio") == "Audio deleted when final"
                    && ChipText("Meeting languages") == "English, German, Spanish" && Descendants(panel).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Start meeting").IsEnabled,
                    "the idle Meetings page shows the title, the read-only transcript, a flat wave, Start meeting and chips with the shared microphone, the output device, the languages and the audio choice");
                Check(Named(Chip("Meeting languages")) == "Meeting languages: English, German, Spanish" && Named(Chip("Meeting microphone")) == "Meeting microphone: Default microphone"
                    && Named(Chip("Meeting audio")) == "Meeting audio: Audio deleted when final", "the Meetings chips announce their current choice with their name");
                outputChoice.SelectedIndex = 1;
                var chosenOutputChip = ChipText("Meeting system audio"); var chosenOutput = panel.PlaybackChoice;
                outputChoice.SelectedIndex = 0;
                Check(new Settings().PlaybackDeviceId == "" && JsonSerializer.Deserialize<Settings>("{\"MicrophoneId\":\"older\"}")!.PlaybackDeviceId == "" && panel.PlaybackChoice == ""
                    && chosenOutputChip == "Synthetic headset" && chosenOutput == "synthetic-headset"
                    && MeetingCapture.PlaybackFallbackNotice("", "default-output", "Speakers") is null && MeetingCapture.PlaybackFallbackNotice("synthetic-headset", "synthetic-headset", "Synthetic headset") is null,
                    "the output device follows the Windows default by default and in older settings files; a chosen output shows on its chip and is recorded without a notice while connected");
                var indicator = Descendants(host).OfType<TextBlock>().First(block => block.Text.StartsWith("Meeting recorder idle"));
                var texts = new List<(double Seconds, string Text)>(); var clock = System.Diagnostics.Stopwatch.StartNew();
                System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock)).AddValueChanged(indicator, (_, _) => texts.Add((clock.Elapsed.TotalSeconds, indicator.Text)));
                void Click(string content)
                {
                    var button = Descendants(host).OfType<System.Windows.Controls.Button>().First(button => button.Content as string == content);
                    if (!button.IsEnabled) throw new InvalidOperationException($"“{content}” is not available.");
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                // What Save preferences in the window does with the meeting defaults and the colour this panel shows.
                void SaveMeetingPreferences()
                {
                    var shown = settings.Snapshot(); foreach (var control in panel.PreferenceControls) control.Read(shown);
                    var changed = PreferenceSections.Changed(shown, settings);
                    settings.SaveAs(shown); panel.PreferencesStored(changed);
                }
                var meetingList = Descendants(host).OfType<System.Windows.Controls.ListBox>().Single(list => Named(list) == "Past meetings");
                System.Windows.Controls.ListBoxItem Listed(string id) => meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().First(item => ((MeetingDocument)item.Tag).Id == id);
                // Selected by its id: a complete meeting left in the folder would ask for confirmation before Re-transcribe.
                void SelectMeeting(string id) => meetingList.SelectedItem = Listed(id);
                void Menu(string entry)
                {
                    var more = Descendants(host).OfType<System.Windows.Controls.Button>().Single(button => Named(button) == "More meeting actions");
                    if (!more.IsEnabled) throw new InvalidOperationException($"“{entry}” is not available.");
                    more.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().First(item => ((TextBlock)item.Header).Text == entry).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                }
                SelectMeeting(interrupted.Id);
                // A choice changed on the Meetings page before the pass, and defaults saved under Settings while it runs.
                Choose("Meeting audio", "Keep the audio to re-transcribe later");
                Menu("Re-transcribe");
                var finishingOlder = panel.IsRecording;
                System.Windows.Controls.ComboBox Selector(string name) => Descendants(host).OfType<System.Windows.Controls.ComboBox>().Single(box => Named(box) == name);
                Selector("Default meeting languages").SelectedItem = Selector("Default meeting languages").Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "en");
                SaveMeetingPreferences();
                var chipDuringPass = ChipText("Meeting languages");
                await WaitAsync(() => !panel.IsRecording, 600);
                Check(texts.Any(text => text.Text.StartsWith("Finishing the transcript · ")) && panelArchive.Load(interrupted.Id).Status == "Complete" && panelArchive.AudioBytes(interrupted.Id) == 0
                    && indicator.Text.StartsWith("Meeting recorder idle") && !panel.ShowsUnfinished, "Re-transcribe on the History page shows its progress on the Meetings page, completes an interrupted meeting and deletes its audio");
                Check(panel.SelectedMeeting is { Status: "Complete" } shownAfterPass && shownAfterPass.Id == interrupted.Id
                    && meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().Where(item => item.Tag is MeetingDocument listed && listed.Id == interrupted.Id).All(item => ReferenceEquals(item.Tag, shownAfterPass)),
                    "after a final pass the History list holds the same finished meeting the page shows, not an older copy that could be saved over it");
                Check(finishingOlder && chipDuringPass == "English, German, Spanish" && Settings.Load().MeetingLanguages == "en" && ChipText("Meeting languages") == "English only" && panel.SelectedLanguages == "en"
                    && ChipText("Meeting audio") == "Audio kept for re-transcribe",
                    "defaults saved under Settings while an older meeting is finished become the Meetings page's choices when the pass ends, and a choice changed on the page before stays");
                Choose("Meeting audio", "Delete the audio once the transcript is final");
                panelArchive.Delete(interrupted.Id);

                // The History page: two meetings that say the searched time, one that does not and one past its retention.
                MeetingDocument Archived(string title, DateTimeOffset ended, params string[] lines)
                {
                    var document = new MeetingDocument
                    {
                        Title = title, Status = "Complete", Started = ended.AddMinutes(-25), Ended = ended, MicrophoneDevice = "Synthetic headset microphone", PlaybackDevice = "Synthetic speakers",
                        // Chunks with sound, as a recorded meeting has; their audio was deleted once the transcript was final.
                        Chunks = [new() { File = "mic-000000.wav", Channel = "mic", Offset = 0, Duration = 1500, Transcribed = true, Peak = .5 }, new() { File = "system-000000.wav", Channel = "system", Offset = 0, Duration = 1500, Transcribed = true, Peak = .5 }],
                        Segments = [.. lines.Select((text, index) => new MeetingSegment { Start = 10 * index + 250, End = 10 * index + 256, Speaker = index % 2 == 0 ? "You" : "Remote speaker", Channel = index % 2 == 0 ? "mic" : "system", Text = text })]
                    };
                    panelArchive.Save(document); return document;
                }
                var design = Archived("Design review", DateTimeOffset.UtcNow.AddMinutes(-5), "Can we do the demo before lunch?", "Lunch is at 12:30, so eleven would be safer.", "Eleven then. I will send the invite.",
                    "Do we need the lab?", "Yes, book room 4 for the demo.", "The train leaves at twelve thirty, I have to go.", "Fine, we continue on Thursday.");
                var supplier = Archived("Supplier call", DateTimeOffset.UtcNow.AddDays(-1), "The parts arrive on Friday.", "Call me back at 1230 about the invoice.", "Will do.");
                var weekly = Archived("Weekly sync", DateTimeOffset.UtcNow.AddDays(-2), "Nothing new this week.");
                var expired = Archived("Old planning", DateTimeOffset.UtcNow.AddDays(-400), "That was long ago.");
                panel.Refresh();
                System.Windows.Controls.RadioButton Tab(string name) => Descendants(host).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == name);
                var transcriptRows = Descendants(host).OfType<System.Windows.Controls.DataGrid>().Single(grid => Named(grid) == "Meeting transcript rows");
                SelectMeeting(design.Id); await Task.Delay(100);
                Check(panel.SelectedMeeting?.Id == design.Id && transcriptRows.Items.Count == 7 && Tab("Transcript").IsChecked == true && !transcriptRows.IsReadOnly
                    && new[] { design, supplier, weekly, expired }.All(document => meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().Any(item => ((MeetingDocument)item.Tag).Id == document.Id))
                    && Named(Listed(design.Id)).StartsWith("Design review, Today ") && Named(Listed(design.Id)).Contains(" · 25 min"),
                    "the History page lists past meetings with date and length and shows the selected one's editable transcript on the Transcript tab");
                var searchBox = Descendants(host).OfType<System.Windows.Controls.TextBox>().Single(box => Named(box) == "Search meetings");
                var onlySelected = Descendants(host).OfType<System.Windows.Controls.CheckBox>().Single(box => Named(box) == "Search only the selected meeting");
                Tab("Notes").IsChecked = true;
                searchBox.Text = "twelve thirty"; await Task.Delay(100);
                bool Highlighted(MeetingSegment segment) => transcriptRows.ItemContainerGenerator.ContainerFromItem(segment) is DataGridRow row && row.Background == WorkspacePages.Theme<System.Windows.Media.Brush>("HitRow");
                // The page's own copies of the rows, loaded from the archive.
                var shownRows = panel.SelectedMeeting!.Segments.OrderBy(segment => segment.Start).ToArray();
                var designHits = shownRows.Where(segment => segment.Text.Contains("12:30") || segment.Text.Contains("twelve thirty")).ToArray();
                Check(meetingList.Items.Count == 2 && Named(Listed(design.Id)).Contains(" · 2 hits") && Named(Listed(supplier.Id)).Contains(" · 1 hit") && panel.SearchHitCount(weekly.Id) == 0
                    && panel.HighlightedRows.OrderBy(segment => segment.Start).SequenceEqual(designHits) && designHits.All(Highlighted) && !Highlighted(shownRows[0]) && Tab("Transcript").IsChecked == true
                    && onlySelected.Visibility == Visibility.Visible,
                    $"a search lists only the meetings that say it with their hit counts, and the transcript highlights the hit rows (listed {string.Join(" | ", meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().Select(Named))}; highlighted {string.Join(", ", panel.HighlightedRows.Select(segment => segment.Start))}; shown {string.Join(", ", designHits.Select(Highlighted))}; first row {Highlighted(shownRows[0])}; transcript tab {Tab("Transcript").IsChecked}; toggle {onlySelected.Visibility})");
                var firstHit = shownRows[1];
                Check(new[] { shownRows[0], firstHit, shownRows[2] }.All(segment => transcriptRows.ItemContainerGenerator.ContainerFromItem(segment) is DataGridRow { IsVisible: true }),
                    "the first hit is in view with the row before and the row after it");
                onlySelected.IsChecked = true; await Task.Delay(50);
                var limited = (Count: meetingList.Items.Count, Supplier: panel.SearchHitCount(supplier.Id), Design: panel.SearchHitCount(design.Id));
                onlySelected.IsChecked = false;
                var unlimited = meetingList.Items.Count;
                searchBox.Text = ""; await Task.Delay(50);
                Check(limited == (1, 0, 2) && unlimited == 2 && meetingList.Items.Count >= 4 && panel.HighlightedRows.Count == 0 && !shownRows.Any(Highlighted) && onlySelected.Visibility == Visibility.Collapsed,
                    "Only this meeting limits the search to the selected meeting, and clearing the search lists every meeting again without highlights");
                searchBox.Text = "twelve thirty"; onlySelected.IsChecked = true; searchBox.Text = ""; await Task.Delay(50);
                Check(onlySelected.IsChecked == false && onlySelected.Visibility == Visibility.Collapsed, "clearing the search also clears Only this meeting, so it never limits a later search while hidden");
                // A row edited on the page and not saved. Unsaved edits are compared with the meeting on disk, so no way of leaving the meeting and
                // coming back makes them look saved.
                var saveEditsButton = Descendants(host).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Save edits");
                bool SaveEditsProminent() => ReferenceEquals(saveEditsButton.Style, WorkspacePages.Theme("Primary"));
                var quietWithoutEdits = !SaveEditsProminent();
                var editedRow = panel.SelectedMeeting!.Segments.OrderBy(segment => segment.Start).First(); var originalText = editedRow.Text;
                editedRow.Text = originalText + " Edited on the page.";
                SelectMeeting(supplier.Id); await Task.Delay(50);
                var askedToLeave = Question() is { } leaveQuestion && Descendants(leaveQuestion).OfType<TextBlock>().Any(block => block.Text == "“Design review” has edits you have not saved. Discard them and open “Supplier call”?");
                if (Question() is not null) Press(Answer("Cancel"));
                await WaitAsync(() => Question() is null, 5);
                var editsKeptAfterCancel = panel.SelectedMeeting?.Id == design.Id && editedRow.Text.EndsWith("Edited on the page.") && meetingList.SelectedItem == Listed(design.Id) && SaveEditsProminent();
                Check(quietWithoutEdits && askedToLeave && editsKeptAfterCancel,
                    "Save edits is quiet without changes and prominent with them; choosing another meeting with unsaved edits asks first, and Cancel keeps the meeting and its edits (issue #53)");
                searchBox.Text = "invoice"; await Task.Delay(50);
                var keptWithEdits = panel.SelectedMeeting?.Id == design.Id && meetingList.Items.Count == 1 && Named(Listed(supplier.Id)).Contains(" · 1 hit")
                    && panel.SelectedNotices.Shown.Any(notice => notice.Key == "history-unsaved-edits" && notice.Text.StartsWith("“Design review” stays open."));
                searchBox.Text = ""; panel.Refresh(); await Task.Delay(50);
                var keptAfterRefresh = panel.SelectedMeeting?.Id == design.Id && panel.SelectedMeeting.Segments.Any(segment => segment.Text.EndsWith("Edited on the page."))
                    && !panelArchive.Load(design.Id).Segments.Any(segment => segment.Text.EndsWith("Edited on the page."));
                searchBox.Text = "invoice"; await Task.Delay(50);
                Click("Save edits"); await Task.Delay(50);
                var savedDuringSearch = panel.SelectedMeeting?.Id == design.Id && panelArchive.Load(design.Id).Segments.Any(segment => segment.Text.EndsWith("Edited on the page.")) && !SaveEditsProminent();
                editedRow.Text = originalText; Click("Save edits"); searchBox.Text = "";
                Check(keptWithEdits && keptAfterRefresh && savedDuringSearch,
                    "a search that leaves out a meeting with unsaved edits keeps it shown and says why, a refresh keeps the edits, and saving during the search keeps the meeting shown (issue #53)");
                editedRow.Text = originalText + " Edited on the page.";
                SelectMeeting(supplier.Id); await Task.Delay(50);
                if (Question() is not null) Press(Answer("Discard edits"));
                await WaitAsync(() => panel.SelectedMeeting?.Id == supplier.Id, 5);
                var leftAfterDiscard = panel.SelectedMeeting?.Id == supplier.Id;
                SelectMeeting(design.Id); await Task.Delay(50);
                var reopenedSaved = Question() is null && panel.SelectedMeeting?.Id == design.Id && panel.SelectedMeeting.Segments.OrderBy(segment => segment.Start).First().Text == originalText && !SaveEditsProminent();
                searchBox.Text = "invoice"; await Task.Delay(50);
                var switchedWithoutEdits = panel.SelectedMeeting?.Id == supplier.Id;
                searchBox.Text = ""; SelectMeeting(design.Id); await Task.Delay(50);
                Check(leftAfterDiscard && reopenedSaved && switchedWithoutEdits,
                    "Discard edits opens the other meeting and reopening shows the saved transcript; without edits a search opens the first meeting with hits (issue #53)");
                var silentHeadset = Archived("Silent headset", DateTimeOffset.UtcNow.AddMinutes(-40), "The parts arrive on Friday.");
                silentHeadset.Chunks[0].Peak = 0; panelArchive.Save(silentHeadset); panel.Refresh();
                SelectMeeting(silentHeadset.Id); Tab("Notes").IsChecked = true; await Task.Delay(50);
                var silentNotes = panel.NotesState.Text;
                Check(silentNotes.StartsWith("The microphone “Synthetic headset microphone” delivered no sound") && !silentNotes.Contains(ExtractiveNotes.Header) && silentNotes.Contains(ExtractiveNotes.SummaryHeading),
                    "the Notes tab drops the header of the rule-based notes also after a line about a silent channel, and keeps that line (issue #53)");
                panelArchive.Delete(silentHeadset.Id); panel.Refresh(); SelectMeeting(design.Id);
                Tab("Notes").IsChecked = true;
                static string Readable(string notes) => string.Concat(MeetingHistory.ReadableNotes(notes).Select(piece => piece.Text));
                static string RuleNotesShown(MeetingDocument document) => Readable(MeetingHistory.RuleNotesOnPage(document));
                var extractiveNotes = panel.NotesState;
                var notesLink = Descendants(host).OfType<System.Windows.Documents.Hyperlink>().Single(link => Named(link) == "Choose how notes are written");
                notesLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
                Check(KeysOf(extractiveNotes.Notices).SequenceEqual([NotesStatus.RuleBasedKey]) && extractiveNotes.Notices[0] is { Kind: NoticeKind.Warning, Text: NotesStatus.RuleNotesCaveat }
                    && Descendants(panel.NotesNotices).Contains(notesLink) && panel.NotesNotices.Toggle is null
                    && extractiveNotes.Text == RuleNotesShown(design) && extractiveNotes.WriteButton is null && extractiveNotes.WrittenBy is null && meetingSettingsOpened == 1 && notesCalls == 0,
                    "the Notes tab shows the rule-based notes at once under one warning card with a link that opens the notes setting, and offers no model by default");
                // Dismissed, the caveat stays away while the page is redrawn and comes back when the meeting is opened again; the notes stay as they are.
                panel.NotesNotices.DismissButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var caveatDismissed = panel.NotesState.Notices.Count == 0 && panel.NotesState.Text == RuleNotesShown(design);
                Tab("Transcript").IsChecked = true; Tab("Notes").IsChecked = true; panel.Refresh();
                var caveatStaysAway = panel.NotesState.Notices.Count == 0;
                SelectMeeting(supplier.Id); SelectMeeting(design.Id); Tab("Notes").IsChecked = true;
                Check(caveatDismissed && caveatStaysAway && KeysOf(panel.NotesState.Notices).SequenceEqual([NotesStatus.RuleBasedKey]),
                    "the x of the rule-based caveat hides it without changing the notes, a redraw keeps it away, and opening the meeting again shows it again");
                Descendants(host).OfType<System.Windows.Controls.RadioButton>().Single(option => Named(option) == "Meeting notes: API").IsChecked = true;
                SaveMeetingPreferences();
                var apiNotes = panel.NotesState;
                const string WriteWithApi = "Write notes with test-model at notes.invalid";
                Check(Settings.Load().MeetingNotesStyle == MeetingNotesStyle.Api && apiNotes.WriteButton == WriteWithApi && KeysOf(apiNotes.Notices).SequenceEqual([NotesStatus.RuleBasedKey]) && notesCalls == 0,
                    "with the API style chosen under Meeting defaults, the button names the model and the host the transcript would go to, and nothing is sent before it is pressed");
                notesAnswer = _ => Task.FromException<string>(new InvalidOperationException("The stub endpoint refused the request."));
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var failedNotes = panel.NotesState;
                Check(notesCalls == 1 && KeysOf(failedNotes.Notices).SequenceEqual([NotesStatus.FailedKey, NotesStatus.RuleBasedKey])
                    && failedNotes.Notices[0] is { Kind: NoticeKind.Error, Title: "Notes could not be written", Text: "The stub endpoint refused the request. The rule-based draft shows." }
                    && failedNotes.Text == RuleNotesShown(design) && panelArchive.Load(design.Id).WrittenNotes is null
                    && messages[^1] == "Writing the notes for “Design review” ended; its Notes tab says how." && panel.NotesNotices.Toggle?.Content as string == "1 more · show all",
                    "a failing model call keeps the rule-based notes and puts an error card with the reason in front of the caveat, piled with “1 more · show all”; the status line names no error, and nothing is stored");
                // The model finds nothing to note, as in a short test meeting: a result, said in one neutral line, not a failure.
                notesAnswer = _ => Task.FromResult("""{"summary": ["A short test of the recording."], "decisions": [], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var nothingFound = panel.NotesState;
                Check(notesCalls == 2 && KeysOf(nothingFound.Notices).SequenceEqual([NotesStatus.NothingFoundKey, NotesStatus.RuleBasedKey])
                    && nothingFound.Notices[0] is { Kind: NoticeKind.Note, Text: "test-model through the API found no decisions, action items, open questions or times in this meeting." }
                    && nothingFound.Text == RuleNotesShown(design) && panelArchive.Load(design.Id).WrittenNotes is null,
                    "an answer without points is a result: a note card says the model found nothing to note, the rule-based notes keep their caveat card, and a summary alone is not stored");
                notesAnswer = async token => { await Task.Delay(Timeout.Infinite, token); return ""; };
                Click(WriteWithApi);
                var runningNotes = panel.NotesState;
                SelectMeeting(supplier.Id);
                var usableWhileWriting = panel.SelectedMeeting?.Id == supplier.Id && !panel.IsRecording && panel.NotesState.Running;
                SelectMeeting(design.Id);
                Press(Descendants(host).OfType<System.Windows.Controls.Button>().Single(button => Named(button) == "Cancel writing notes"));
                await WaitAsync(() => !panel.NotesState.Running, 5);
                Check(notesCalls == 3 && runningNotes.Running && runningNotes.WriteButton is not null && usableWhileWriting
                    && KeysOf(panel.NotesState.Notices).SequenceEqual([NotesStatus.CancelledKey, NotesStatus.RuleBasedKey]) && panel.NotesState.Notices[0].Kind == NoticeKind.Note,
                    "while notes are written the page shows the progress and stays usable, and Cancel stops them with a note card");
                const string FullAnswer = """
                    {"summary": ["The team planned the demo and booked the lab."], "decisions": [{"text": "The demo is at eleven.", "citations": ["04:30"]}],
                     "actionItems": [{"text": "Send the invite.", "owner": "You", "due": null, "citations": ["04:30"]}, {"text": "Book room 4.", "owner": null, "due": null, "citations": ["04:50"]}],
                     "openQuestions": [], "timesMentioned": [{"time": "12:30", "text": "Lunch.", "citations": ["04:20"]}]}
                    """;
                notesAnswer = _ => Task.FromResult(FullAnswer);
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var storedNotes = panelArchive.Load(design.Id).WrittenNotes;
                var writtenNotes = panel.NotesState;
                Check(notesCalls == 4 && storedNotes is { Model: "test-model through the API" } && storedNotes.Text.Contains("The demo is at eleven.") && writtenNotes.Text == Readable(MeetingHistory.WrittenNotesOnPage(storedNotes))
                    && storedNotes.Text.Contains("Written by the model test-model through the API") && !writtenNotes.Text.Contains("Written by")
                    && writtenNotes.WrittenBy!.StartsWith("Written by test-model through the API on ") && writtenNotes.Notices.Count == 0 && !panel.NotesNotices.IsVisible,
                    "notes the model wrote are shown and stored with the meeting together with the model and the time; the Notes tab says once who wrote them");
                Click("Back to rule-based notes");
                var askedRules = Question() is not null;
                if (askedRules) Press(Answer("Cancel"));
                await WaitAsync(() => Question() is null, 5);
                var keptWritten = panelArchive.Load(design.Id).WrittenNotes is not null;
                Click("Back to rule-based notes");
                if (Question() is not null) Press(Answer("Discard written notes"));
                await WaitAsync(() => panel.NotesState.WrittenBy is null, 5);
                Check(askedRules && keptWritten && panelArchive.Load(design.Id).WrittenNotes is null && KeysOf(panel.NotesState.Notices).SequenceEqual([NotesStatus.RuleBasedKey]) && panel.NotesState.Text == RuleNotesShown(design),
                    "Back to rule-based notes asks first and then shows the rule-based notes again with their caveat");
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var writtenAgain = panelArchive.Load(design.Id).WrittenNotes;
                Check(notesCalls == 5 && writtenAgain is not null, "the notes can be written again with the button for this meeting");
                // Writing again fails: the notes written before stay on screen, with one line about the new run above them.
                notesAnswer = _ => Task.FromException<string>(new InvalidOperationException("The stub endpoint refused the request."));
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var failedAgain = panel.NotesState;
                Check(notesCalls == 6 && failedAgain.Text == Readable(MeetingHistory.WrittenNotesOnPage(writtenAgain!)) && failedAgain.WrittenBy is not null
                    && KeysOf(failedAgain.Notices).SequenceEqual([NotesStatus.FailedKey]) && failedAgain.Notices[0].Text == "The stub endpoint refused the request. The notes written before stay."
                    && panelArchive.Load(design.Id).WrittenNotes?.Text == writtenAgain!.Text,
                    "a failed attempt to write notes again keeps showing the notes written before, under one error card with the reason that says they stay, without reopening the meeting");
                // Nothing found on a second run: the notes written before stay too, with the neutral line.
                notesAnswer = _ => Task.FromResult("""{"summary": [], "decisions": [], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var nothingAgain = panel.NotesState;
                Listed(supplier.Id).IsSelected = true; Listed(design.Id).IsSelected = true;
                Tab("Notes").IsChecked = true;
                var keptWithItsMeeting = KeysOf(panel.NotesState.Notices).SequenceEqual([NotesStatus.NothingFoundKey]);
                panel.NotesNotices.DismissButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Listed(supplier.Id).IsSelected = true; Listed(design.Id).IsSelected = true;
                Check(notesCalls == 7 && nothingAgain.Text == failedAgain.Text && KeysOf(nothingAgain.Notices).SequenceEqual([NotesStatus.NothingFoundKey])
                    && nothingAgain.Notices[0].Text.StartsWith("test-model through the API found no decisions")
                    && panelArchive.Load(design.Id).WrittenNotes?.Text == writtenAgain!.Text && panel.NotesState.WrittenBy is not null && keptWithItsMeeting && panel.NotesState.Notices.Count == 0,
                    "a model that finds nothing on a later run keeps the notes written before on screen and stored; the card about the run stays with its meeting while another is opened, and its x ends it");
                // The transcript changed after the notes were written and writing them again fails: the line says both, stale first.
                var originalRow = panel.SelectedMeeting!.Segments[0].Text;
                panel.SelectedMeeting!.Segments[0].Text = "The demo moved to noon."; Click("Save edits");
                notesAnswer = _ => Task.FromException<string>(new InvalidOperationException("The stub endpoint refused the request."));
                Click(WriteWithApi);
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var staleFailed = panel.NotesState;
                panel.SelectedMeeting!.Segments[0].Text = originalRow; Click("Save edits");
                Check(KeysOf(staleFailed.Notices).SequenceEqual([NotesStatus.FailedKey, NotesStatus.StaleKey]) && staleFailed.Notices[1] is { Kind: NoticeKind.Warning, Text: NotesStatus.StaleNotes }
                    && staleFailed.Notices[0].Kind == NoticeKind.Error && staleFailed.WrittenBy is not null && MeetingHistory.WrittenNotesMatchTranscript(panelArchive.Load(design.Id)),
                    "notes written for an older transcript and a failed run to write them again are two cards, the newer failure in front of the stale warning");
                // A row edited on the page and not saved: Write notes asks before it saves the edit, and the notes are written from the saved text only.
                var savedRow = panelArchive.Load(design.Id).Segments[0].Text;
                panel.SelectedMeeting!.Segments[0].Text = "Can we do the demo at noon?";
                Click(WriteWithApi);
                var askedToSave = Question() is { } saveQuestion && Descendants(saveQuestion).OfType<TextBlock>().Any(block => block.Text == "“Design review” has edits you have not saved. Save them and write the notes from the saved transcript?");
                if (Question() is not null) Press(Answer("Cancel"));
                await WaitAsync(() => Question() is null, 5);
                var nothingSentUnsaved = notesCalls == 8 && !panel.NotesState.Running && panelArchive.Load(design.Id).Segments[0].Text == savedRow && panel.SelectedMeeting!.Segments[0].Text == "Can we do the demo at noon?";
                // Refused, so the notes written before stay as written from the saved transcript, which is restored below.
                notesAnswer = _ => Task.FromException<string>(new InvalidOperationException("The stub endpoint refused the request."));
                Click(WriteWithApi);
                if (Question() is not null) Press(Answer("Save and write notes"));
                await WaitAsync(() => notesCalls == 9 && !panel.NotesState.Running, 10);
                Check(askedToSave && nothingSentUnsaved && notesCalls == 9 && notesRequest.Contains("Can we do the demo at noon?") && panelArchive.Load(design.Id).Segments[0].Text == "Can we do the demo at noon?",
                    $"Write notes with unsaved edits asks first; Cancel saves and sends nothing, Save and write notes saves the edits and writes the notes from that text (asked {askedToSave}, calls {notesCalls})");
                panel.SelectedMeeting!.Segments[0].Text = savedRow; var restoredDesign = panelArchive.Load(design.Id); restoredDesign.Segments[0].Text = savedRow; panelArchive.Save(restoredDesign);
                // The local model style with the meeting notes model in place, as its install under Models leaves it; the install itself is
                // checked in the window, where Settings → Models runs it.
                Descendants(host).OfType<System.Windows.Controls.RadioButton>().Single(option => Named(option) == "Meeting notes: Local model").IsChecked = true;
                foreach (var file in new[] { settings.MeetingNotesEnginePath, settings.MeetingNotesModelPath }) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }
                SaveMeetingPreferences();
                SelectMeeting(supplier.Id); Tab("Notes").IsChecked = true;
                notesAnswer = async token => { await Task.Delay(Timeout.Infinite, token); return ""; };
                Click("Write notes with the local model");
                await Task.Delay(50);
                var localProgress = panel.NotesProgressText;
                Press(Descendants(host).OfType<System.Windows.Controls.Button>().Single(button => Named(button) == "Cancel writing notes"));
                await WaitAsync(() => !panel.NotesState.Running, 5);
                Check(localProgress.StartsWith("Writing notes for “Supplier call” with the local model") && localProgress.EndsWith(". You can keep using Private Flow. " + MeetingsPanel.LocalNotesBlockDictation)
                    && MeetingsPanel.LocalNotesBlockDictation.Contains("Cancel on the Dictate page"),
                    "while the local model writes notes, the progress says that a Local polish dictation waits for them, locks the page meanwhile and is cancelled on the Dictate page");
                foreach (var file in new[] { settings.MeetingNotesEnginePath, settings.MeetingNotesModelPath }) File.Delete(file);
                Descendants(host).OfType<System.Windows.Controls.RadioButton>().Single(option => Named(option) == "Meeting notes: API").IsChecked = true;
                SaveMeetingPreferences();
                // Notes being written for an expired meeting: Delete expired meetings refuses and says why, so the notes are saved, not lost.
                SelectMeeting(expired.Id); Tab("Notes").IsChecked = true; await Task.Delay(50);
                var expiredNotesReleased = new TaskCompletionSource<string>();
                notesAnswer = _ => expiredNotesReleased.Task;
                Click(WriteWithApi);
                Click("Delete expired meetings");
                var refusedExpired = Question() is null && panel.NotesState.Running && Directory.Exists(panelArchive.DirectoryFor(expired.Id))
                    && panel.HistoryNotices.Shown.Any(notice => notice is { Key: "notes-being-written", Text: "Wait for the notes of “Old planning” or cancel them on its Notes tab first." });
                // The page's own card is not part of the meeting shown: choosing another meeting keeps it in view.
                SelectMeeting(design.Id); await Task.Delay(50);
                var pageCardKept = panel.HistoryNotices.Shown.Any(notice => notice.Key == "notes-being-written") && panel.HistoryNotices.IsVisible;
                SelectMeeting(expired.Id); Tab("Notes").IsChecked = true;
                expiredNotesReleased.SetResult("""{"summary": ["An old plan."], "decisions": [{"text": "The old plan is done.", "citations": ["04:10"]}], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
                await WaitAsync(() => !panel.NotesState.Running, 10);
                Check(refusedExpired && panelArchive.Load(expired.Id).WrittenNotes?.Text.Contains("The old plan is done.") == true,
                    "Delete expired meetings refuses while notes are written for an expired meeting and says why in a card, so the notes are saved with it");
                Check(pageCardKept && !panel.HistoryNotices.Shown.Any(notice => notice.Key == "notes-being-written"),
                    "the History page's own card stays in view when another meeting is chosen, and the refusal ends when the notes it waited for are done");
                // Notes for one meeting fail while another is selected: the failure stays with its meeting, is pointed to with that meeting's
                // title, and shows when it is selected again, also after choosing a third.
                SelectMeeting(supplier.Id); Tab("Notes").IsChecked = true;
                var supplierReleased = new TaskCompletionSource<string>();
                notesAnswer = _ => supplierReleased.Task;
                Click(WriteWithApi);
                SelectMeeting(design.Id); await Task.Delay(50);
                var designBefore = panel.NotesState.Notices;
                supplierReleased.SetException(new InvalidOperationException("Supplier call's stub endpoint refused the request."));
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var designWithout = panel.NotesState.Notices.SequenceEqual(designBefore) && !panel.NotesState.Notices.Any(notice => notice.Text.StartsWith("Supplier call's"));
                var pointedTo = panel.Board.Pointer;
                SelectMeeting(weekly.Id); SelectMeeting(supplier.Id); await Task.Delay(50);
                Check(designWithout && pointedTo == "“Supplier call”: Notes could not be written. See History."
                    && panel.NotesState.Notices.FirstOrDefault() is { Key: NotesStatus.FailedKey, Kind: NoticeKind.Error } failure && failure.Text.StartsWith("Supplier call's"),
                    $"notes that fail for a meeting while another is selected stay with their meeting: not shown under the other, pointed to with the meeting's title, and shown when it is selected again ({pointedTo})");
                SelectMeeting(design.Id);
                SelectMeeting(design.Id);
                Tab("Transcript").IsChecked = true;
                Click("Delete expired meetings");
                var askedExpired = Question() is not null;
                if (askedExpired) Press(Answer("Cancel"));
                await WaitAsync(() => Question() is null, 5);
                var expiredKept = Directory.Exists(panelArchive.DirectoryFor(expired.Id));
                Click("Delete expired meetings");
                if (Question() is not null) Press(Answer("Delete expired meetings"));
                await WaitAsync(() => !Directory.Exists(panelArchive.DirectoryFor(expired.Id)), 5);
                Check(askedExpired && expiredKept && !Directory.Exists(panelArchive.DirectoryFor(expired.Id)) && new[] { design, supplier, weekly }.All(document => Directory.Exists(panelArchive.DirectoryFor(document.Id)))
                    && !meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().Any(item => ((MeetingDocument)item.Tag).Id == expired.Id),
                    "Delete expired meetings on the History page asks first and then deletes only completed meetings past the retention period");

                // A complete meeting with kept audio and a row edited on the page: a cancelled Re-transcribe keeps the edit shown and unsaved.
                var keptAudio = Saved(panelArchive, new MeetingDocument { Title = "Edited meeting", Status = "Complete", Started = DateTimeOffset.UtcNow.AddHours(-3), Ended = DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(1),
                    Segments = [new() { Start = 1, End = 3, Speaker = "You", Channel = "mic", Text = "This is a private voice test." }] });
                panel.Refresh(); SelectMeeting(keptAudio.Id); await Task.Delay(50);
                panel.SelectedMeeting!.Segments[0].Text = "This is a corrected voice test.";
                Menu("Re-transcribe");
                var askedWithEdits = Question() is { } retranscribeQuestion && Descendants(retranscribeQuestion).OfType<TextBlock>().Any(block => block.Text.EndsWith("including the edits you have not saved."));
                if (Question() is not null) Press(Answer("Re-transcribe"));
                await WaitAsync(() => panel.IsRecording, 5);
                Click("Cancel finishing");
                await WaitAsync(() => !panel.IsRecording, 120);
                Check(askedWithEdits && panel.SelectedMeeting is { } stillEdited && stillEdited.Id == keptAudio.Id && stillEdited.Segments[0].Text == "This is a corrected voice test."
                    && panelArchive.Load(keptAudio.Id).Segments[0].Text == "This is a private voice test." && panel.SelectedNotices.Shown.Any(notice => notice is { Key: "history-edits-kept", Kind: NoticeKind.Note }),
                    "Re-transcribe asks before replacing unsaved edits, and a cancelled pass keeps them shown and unsaved with a notice (issue #53)");
                panelArchive.Delete(keptAudio.Id); panel.Refresh();

                // A meeting wave whose bars rest asks for no redraw.
                var restingWave = new MirroredWaveform(System.Windows.Media.Colors.Blue);
                for (var frame = 0; frame < 30; frame++) restingWave.Advance(1 / 60.0);
                var restingRedraws = restingWave.Redraws;
                restingWave.PushMicrophone(.05f); for (var frame = 0; frame < 6; frame++) restingWave.Advance(1 / 60.0);
                var movingRedraws = restingWave.Redraws;
                for (var frame = 0; frame < 300; frame++) restingWave.Advance(1 / 60.0);
                var settledRedraws = restingWave.Redraws;
                for (var frame = 0; frame < 30; frame++) restingWave.Advance(1 / 60.0);
                Check(restingRedraws == 0 && movingRedraws > 0 && restingWave.Redraws == settledRedraws && restingWave.MicrophoneHeight == 0,
                    $"the meeting wave redraws only while a bar moves, not on every frame while both channels are flat (issue #42; {movingRedraws} redraws while moving)");

                // A meeting from synthetic sources: a microphone that delivers only zeros and system audio with speech.
                speech = ReadSamples(wav);
                SyntheticSource silentMicrophone = new(new float[speech.Length]), playback = new(speech);
                var captureStates = new List<bool>(); panel.CaptureChanged += captureStates.Add;
                var beginning = panel.BeginAsync(active => active.Start([(silentMicrophone, "mic", "Synthetic microphone"), (playback, "system", "Synthetic playback")]));
                // A second start while the pill shows and the devices open starts nothing; once the meeting records, the second click
                // of a double click on Start meeting lands on Stop and finish.
                await panel.BeginAsync(_ => throw new InvalidOperationException("A second meeting started while the first one started."));
                await beginning;
                Click("Stop and finish");
                var recordingEntries = meetingList.Items.OfType<System.Windows.Controls.ListBoxItem>().Where(item => !item.IsEnabled).ToArray();
                Check(recordingEntries.Length == 1 && Named(recordingEntries[0]).Contains("Recording now") && meetingList.IsEnabled
                    && !Descendants(host).OfType<System.Windows.Controls.Button>().Single(button => Named(button) == "More meeting actions").IsEnabled,
                    "the meeting recording now is listed on the History page but cannot be selected; other meetings stay readable while their actions wait");
                Check(panel.IsCapturing && panel.PlaybackWarning is null && !Chip("Meeting microphone").IsEnabled && !Chip("Meeting languages").IsEnabled && !meetingTitle.IsEnabled,
                    $"a double click on Start meeting does not also stop it; while recording, the title and the chips are disabled and a meeting on the Windows default output shows no notice (capturing {panel.IsCapturing}, notice {panel.PlaybackWarning ?? "none"}, last status {messages.LastOrDefault()})");
                var recording = new DispatcherWatchdog(); var buttonPeaks = new WavePeaks(pageWave);
                await WaitAsync(() => panel.ShowsSilenceWarning, 30);
                var warnedWhileRecording = panel.ShowsSilenceWarning;
                await WaitAsync(() => silentMicrophone.Played && playback.Played, 60); await Task.Delay(1500);
                recording.Dispose(); buttonPeaks.Dispose();
                Check(buttonPeaks.PillSeen && pageWave.IsVisible && buttonPeaks.Pill is { System: > .3, Microphone: < .02 } && buttonPeaks.Page is { System: > .3, Microphone: < .02 } && captureStates.SequenceEqual([true]),
                    $"a meeting started with the button shows the pill and the page wave while it records; only the system half reacts to system audio, a silent microphone stays flat (pill {buttonPeaks.Pill}, page {buttonPeaks.Page})");
                var silenceCard = panel.MeetingNotices.Shown.FirstOrDefault();
                var freeWarnings = FreeWarningText(panel);
                Capture(host, Path.Combine(output, "cards-meetings-silent-microphone-host.png"));
                Check(warnedWhileRecording && panel.ShowsSilenceWarning && notifications.Count(text => text.StartsWith("No sound from the microphone “Synthetic microphone”")) == 1
                    && silenceCard is { Kind: NoticeKind.Warning, Title: "No sound from the microphone" } && panel.MeetingNotices.Cards.Count == 1 && freeWarnings.Length == 0,
                    $"a silent microphone is a warning card on the Meetings page while it lasts, with no free yellow text, and is announced once in the tray ({string.Join(" | ", freeWarnings)})");
                Check(recording.LongestGap < ResponsiveBound, $"the interface thread stays responsive while recording with a live preview (longest gap {recording.LongestGap:0.00} s, bound {ResponsiveBound} s)");
                var stopIndex = texts.Count; var watchdog = new DispatcherWatchdog(); var stopClicked = clock.Elapsed.TotalSeconds;
                Click("Stop and finish");
                var pillClosedOnStop = !PillShown() && pageWave.MicrophoneHeight == 0 && pageWave.SystemAudioHeight == 0 && captureStates.SequenceEqual([true, false]);
                await WaitAsync(() => !panel.IsRecording, 600);
                watchdog.Dispose();
                Check(pillClosedOnStop, "Stop and finish closes the pill, flattens the page wave at once and ends the recording state");
                var finishing = texts.Skip(stopIndex).Where(text => text.Text.StartsWith("Finishing the transcript · ")).ToArray();
                var percentages = finishing.Select(text => int.Parse(System.Text.RegularExpressions.Regex.Match(text.Text, @"(\d+)\s*%").Groups[1].Value)).ToArray();
                Check(finishing.Length > 0 && finishing[0].Seconds - stopClicked < 1 && finishing[0].Text.Contains("Stopping capture")
                    && new[] { "Loading the model", "Detecting speech · microphone", "Detecting speech · system audio", "Detecting languages · system audio", "Transcribing · system audio", "Saving the transcript" }
                        .All(step => finishing.Any(text => text.Text.Contains(step)))
                    && percentages.Zip(percentages.Skip(1)).All(pair => pair.Second >= pair.First) && percentages[^1] == 100,
                    $"Stop and finish shows named steps from the first second ({finishing[0].Seconds - stopClicked:0.00} s), with a percentage that never falls and ends at 100 % ({finishing.Length} updates)");
                Check(watchdog.LongestGap < ResponsiveBound, $"the interface thread stays responsive during stop and finish (longest gap {watchdog.LongestGap:0.00} s, bound {ResponsiveBound} s)");
                var silentMeeting = panelArchive.List().First(document => document.MicrophoneDevice == "Synthetic microphone");
                Check(silentMeeting is { Status: "Complete", PlaybackDevice: "Synthetic playback" } && panelArchive.AudioBytes(silentMeeting.Id) == 0 && silentMeeting.Segments.Any(segment => segment.Channel == "system")
                    && messages[^1] == "Meeting saved with a problem; see Meetings." && !panel.ShowsSilenceWarning
                    && panel.MeetingNotices.Shown.Any(notice => notice.Title == "No sound from the microphone" && notice.Text.StartsWith("The microphone “Synthetic microphone” delivered no sound during the whole meeting"))
                    && MeetingArchive.Notes(silentMeeting).StartsWith("The microphone “Synthetic microphone” delivered no sound")
                    && meetingTranscript.Text.Contains("Remote speaker: ") && meetingTranscript.Text.Contains("not delete", StringComparison.OrdinalIgnoreCase),
                    "the meeting stores its capture and playback devices, ends Complete without audio, a warning card and its notes say the microphone stayed silent, the status line says it was saved with a problem instead of a bare success, and the composer shows the final transcript");
                panelArchive.Delete(silentMeeting.Id);

                // Finishing in the background, cancelled with its labelled button, then finished from Unfinished meetings in one action.
                // A device slow to confirm the stop keeps finishing at its first step past the double click guard.
                var spoken = new SyntheticSource(speech, 1500);
                await panel.BeginAsync(active => active.Start([(spoken, "mic", "Synthetic microphone")]));
                await WaitAsync(() => spoken.Played, 60); await Task.Delay(500);
                Click("Stop and finish"); Click("Continue in background");
                var hiddenByDoubleClick = !host.IsVisible;
                await Task.Delay(600);
                Click("Continue in background");
                await WaitAsync(ProgressWindowShown, 5);
                var progressWhileHidden = !host.IsVisible && ProgressWindowShown();
                await WaitAsync(() => indicator.Text.Contains("Detecting speech"), 120);
                Click("Cancel finishing");
                await WaitAsync(() => !panel.IsRecording, 120);
                var unfinished = panelArchive.Unfinished();
                Check(!hiddenByDoubleClick, "a double click on Stop and finish does not also continue in the background");
                Check(progressWhileHidden && !ProgressWindowShown() && unfinished.Count == 1 && unfinished[0].Document.Status == "Interrupted" && unfinished[0].AudioBytes > 0 && panel.ShowsUnfinished
                    && notifications[^1].StartsWith("Finishing cancelled") && unfinished[0].Document.UnfinishedReason == MeetingArchive.FinishingCancelled
                    && panel.UnfinishedText.Contains(MeetingArchive.DescribeUnfinished(unfinished[0].Document)) && panel.UnfinishedText.Contains(MeetingArchive.FinishingCancelled),
                    "Continue in background hides the window and a progress window takes over; Cancel finishing keeps the audio, says so in the tray and offers the meeting under Unfinished meetings with why it ended unfinished");
                host.Show();
                // A second unfinished meeting whose notes are being written: the final pass would save its older copy over the notes.
                var withNotes = Saved(panelArchive, new MeetingDocument { Title = "Unfinished with notes", Status = "Interrupted", Started = DateTimeOffset.UtcNow.AddMinutes(-30),
                    Segments = [new() { Start = 1, End = 3, Speaker = "You", Channel = "mic", Text = "This is a private voice test." }] });
                panel.Refresh(); SelectMeeting(withNotes.Id); Tab("Notes").IsChecked = true; await Task.Delay(50);
                var notesReleased = new TaskCompletionSource<string>();
                notesAnswer = _ => notesReleased.Task;
                Click(WriteWithApi);
                Click("Finish transcripts");
                var refusedWhileWriting = !panel.IsRecording && panel.NotesState.Running
                    && panel.MeetingNotices.Shown.Any(notice => notice is { Key: "notes-being-written", Text: "Wait for the notes of “Unfinished with notes” or cancel them on its Notes tab first." })
                    && panelArchive.Load(withNotes.Id).Status == "Interrupted";
                notesReleased.SetResult("""{"summary": ["A voice test."], "decisions": [{"text": "The voice test stays private.", "citations": ["00:01"]}], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
                await WaitAsync(() => !panel.NotesState.Running, 10);
                var notesBeforePass = panelArchive.Load(withNotes.Id).WrittenNotes;
                Check(refusedWhileWriting && notesBeforePass is not null && notesBeforePass.Text.Contains("The voice test stays private."),
                    "Finish transcripts refuses while notes are written for one of the unfinished meetings and says why in a card, so the pass cannot save over the notes");
                Click("Finish transcripts");
                var writeDuringPass = Descendants(host).OfType<System.Windows.Controls.Button>().First(button => button.Content as string == WriteWithApi);
                var writeRefusedDuringPass = panel.IsRecording && !writeDuringPass.IsEnabled;
                await WaitAsync(() => !panel.IsRecording, 600);
                var finishedWithNotes = panelArchive.Load(withNotes.Id);
                Check(writeRefusedDuringPass && finishedWithNotes.Status == "Complete" && finishedWithNotes.WrittenNotes?.Text == notesBeforePass!.Text && panelArchive.AudioBytes(withNotes.Id) == 0,
                    "while Finish transcripts runs, Write notes waits; the pass keeps the notes written before it and finishes the transcript");
                panelArchive.Delete(withNotes.Id); panel.Refresh();
                var finished = panelArchive.Load(unfinished[0].Document.Id);
                Check(finished.Status == "Complete" && finished.UnfinishedReason == "" && panelArchive.AudioBytes(finished.Id) == 0 && !panel.ShowsUnfinished && finished.Segments.Any(segment => segment.Text.Contains("not delete", StringComparison.OrdinalIgnoreCase)),
                    "Finish transcripts completes an unfinished meeting in one action, clears why it was unfinished and deletes its audio");
                SelectMeeting(finished.Id);
                Border? Question() => Descendants(host).OfType<Border>().FirstOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName);
                System.Windows.Controls.Button Answer(string content) => Descendants(Question()!).OfType<System.Windows.Controls.Button>().First(button => button.Content as string == content);
                void Press(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Menu("Delete meeting");
                var asked = Question() is not null && DockPanel.GetDock(Answer("Cancel")) == Dock.Right && DockPanel.GetDock(Answer("Delete meeting")) == Dock.Left && Answer("Cancel").IsCancel;
                if (asked) Press(Answer("Cancel"));
                await WaitAsync(() => Question() is null, 5);
                var keptAfterCancel = Directory.Exists(panelArchive.DirectoryFor(finished.Id));
                Menu("Delete meeting");
                if (Question() is not null) Press(Answer("Delete meeting"));
                await WaitAsync(() => !Directory.Exists(panelArchive.DirectoryFor(finished.Id)), 5);
                Check(asked && keptAfterCancel && Question() is null && !Directory.Exists(panelArchive.DirectoryFor(finished.Id)),
                    "Delete meeting asks inside the window with Cancel on the right; Cancel keeps the meeting and the action on the left deletes it");
                if (Directory.Exists(panelArchive.DirectoryFor(finished.Id))) panelArchive.Delete(finished.Id);
                // With no meeting selected, the History page's own cards still show, and choosing a meeting keeps them. The card stands in for
                // a failed Delete expired meetings, whose disk error cannot be caused here.
                var noSelection = panel.SelectedMeeting is null;
                panel.HistoryNotices.Report(new("history-expired-not-deleted", NoticeKind.Error, "Expired meetings were not deleted", "Access to the folder was denied."));
                await Task.Delay(50);
                var shownWithoutSelection = panel.HistoryNotices.IsVisible && panel.HistoryNotices.Cards.Count == 1;
                SelectMeeting(design.Id); await Task.Delay(50);
                Check(noSelection && shownWithoutSelection && panel.HistoryNotices.Shown.Any(notice => notice.Key == "history-expired-not-deleted"),
                    "the History page's own error shows while no meeting is selected and stays when one is chosen");
                panel.HistoryNotices.Clear("history-expired-not-deleted");

                // The meeting shortcut with the window hidden: a gated microphone (zeros, then speech) and system audio that plays nothing.
                var refused = panel.PressShortcut(false, false);
                Check(refused.Action == MeetingShortcutAction.Refuse && refused.Reason.StartsWith("Diagnostic") && !panel.IsRecording && !PillShown(),
                    "in diagnostic mode the meeting shortcut starts nothing and gives the reason for the tray");
                host.Hide(); captureStates.Clear();
                SyntheticSource gated = new([.. new float[32000], .. speech[..(16000 * 6)]]), quietPlayback = new(new float[16000 * 8]);
                // The chosen output is gone when the meeting starts: the Windows default is recorded instead, and the notice reaches the tray.
                outputChoice.SelectedIndex = 1;
                var meetingPress = System.Diagnostics.Stopwatch.StartNew(); double devicesOpenAt = 0; var meetingPillBeforeDevices = false;
                var started = panel.PressShortcut(false, false, false, active =>
                {
                    devicesOpenAt = meetingPress.Elapsed.TotalMilliseconds;
                    meetingPillBeforeDevices = System.Windows.Application.Current.Windows.OfType<MeetingHud>().Any(window => window.IsVisible && window.Starting);
                    active.Start([(gated, "mic", "Synthetic microphone"), (quietPlayback, "system", "Synthetic default output")],
                        MeetingCapture.PlaybackFallbackNotice(panel.PlaybackChoice!, "synthetic-default-output", "Synthetic default output"));
                });
                var meetingPillAtPress = PillShown();
                await WaitAsync(() => panel.IsCapturing && !panel.IsStarting, 5);
                var meetingPillRecording = System.Windows.Application.Current.Windows.OfType<MeetingHud>().Any(window => window.IsVisible && !window.Starting);
                var fallbackShown = panel.PlaybackWarning;
                outputChoice.SelectedIndex = 0;
                var shortcutPeaks = new WavePeaks(pageWave);
                await WaitAsync(PillShown, 5);
                var pillWhileHidden = PillShown() && !host.IsVisible && panel.IsCapturing && captureStates.SequenceEqual([true]);
                await WaitAsync(() => gated.Played && quietPlayback.Played, 60); await Task.Delay(300);
                var green = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#3FB950");
                Press(Descendants(host).OfType<System.Windows.Controls.Button>().First(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Green system audio colour"));
                var colourWaited = settings.SystemAudioColor != "#3FB950" && Settings.Load().SystemAudioColor != "#3FB950" && pageWave.SystemColor != green
                    && System.Windows.Application.Current.Windows.OfType<MeetingHud>().Single().Wave.SystemColor != green;
                SaveMeetingPreferences();
                var colourApplied = colourWaited && settings.SystemAudioColor == "#3FB950" && Settings.Load().SystemAudioColor == "#3FB950" && pageWave.SystemColor == green
                    && System.Windows.Application.Current.Windows.OfType<MeetingHud>().Single().Wave.SystemColor == green;
                shortcutPeaks.Dispose();
                var stopped = panel.PressShortcut(false, false, false, _ => throw new InvalidOperationException("The shortcut started a second meeting."));
                var pillClosed = !PillShown() && captureStates.SequenceEqual([true, false]);
                await WaitAsync(ProgressWindowShown, 5);
                var progressWhileFinishing = ProgressWindowShown();
                var refusedWhileFinishing = panel.PressShortcut(false, false, false, _ => throw new InvalidOperationException("The shortcut started a meeting while finishing."));
                await WaitAsync(() => !panel.IsRecording, 600);
                var shortcutMeeting = panelArchive.List().First(document => document.MicrophoneDevice == "Synthetic microphone");
                Check(started.Action == MeetingShortcutAction.Start && pillWhileHidden, "the meeting shortcut starts a meeting with the page's choices and shows the pill without showing the window");
                Check(meetingPillAtPress && meetingPillBeforeDevices && devicesOpenAt < PressToPillBound * 1000 && meetingPillRecording,
                    $"the meeting shortcut shows the pill at the press, dimmed as starting and drawn before the devices open {devicesOpenAt:0} ms after the press (bound {PressToPillBound * 1000:0} ms), and as recording once they do");
                Check(fallbackShown is not null && fallbackShown.Contains("records the Windows default “Synthetic default output” instead") && notifications.Contains(fallbackShown)
                    && !messages.Any(message => message.Contains(fallbackShown)) && shortcutMeeting.PlaybackDevice == "Synthetic default output",
                    "a chosen output device that is gone at meeting start falls back to the Windows default, says so in a card on the page and in the tray but not in the status line, and the meeting stores the device it recorded");
                Check(shortcutPeaks.Pill is { Microphone: > .3, System: < .02 } && shortcutPeaks.Page == (0, 0) && !pageWave.Running,
                    $"a gated microphone moves the upper half of the pill once it delivers speech, system audio that plays nothing stays flat, and the page wave of the hidden window stays still (pill {shortcutPeaks.Pill}, page {shortcutPeaks.Page})");
                Check(colourApplied, "a chosen system audio colour waits for Save preferences, then is stored and applies to the pill and the page wave while recording");
                Check(stopped.Action == MeetingShortcutAction.Stop && pillClosed && progressWhileFinishing && refusedWhileFinishing.Action == MeetingShortcutAction.Refuse
                    && shortcutMeeting.Status == "Complete" && shortcutMeeting.Segments.Any(segment => segment.Channel == "mic") && !host.IsVisible,
                    "pressed again, the meeting shortcut stops and finishes like Stop and finish: the pill closes, progress shows while the window stays hidden, a press while finishing is refused");
                panelArchive.Delete(shortcutMeeting.Id);
                // A starting meeting ends as a recording one does (review of #67). BeginAsync is held at its yield, the pill on screen and no
                // device open, while each event is sent; or the event comes from inside the device open; or the open fails.
                async Task<string> StartingMeeting(string name, Action send, bool fromInsideOpen = false, bool openFails = false)
                {
                    var opens = 0; HeldBackSource? source = null; var documentsBefore = panelArchive.List().Count;
                    var beginning = panel.BeginAsync(active =>
                    {
                        opens++;
                        if (openFails) throw new InvalidOperationException("Stand-in microphone failed to open.");
                        source = new HeldBackSource();
                        active.Start([(source, "mic", "Held-back microphone")]);
                        if (fromInsideOpen) send();
                    });
                    var startingShown = panel.IsStarting && panel.IsCapturing && PillShown() && System.Windows.Application.Current.Windows.OfType<MeetingHud>().Any(window => window.IsVisible && window.Starting);
                    if (!fromInsideOpen && !openFails) send();
                    await beginning; await Task.Delay(150);
                    var ended = !panel.IsRecording && !panel.IsStarting && !PillShown() && panelArchive.List().Count == documentsBefore;
                    var deviceUntouched = fromInsideOpen ? opens == 1 && source is { Started: true, Disposed: true } : openFails ? opens == 1 : opens == 0;
                    return $"{name}: {(startingShown && ended && deviceUntouched ? "ended" : "NOT ended")} (starting shown {startingShown}, opens {opens}, closed {source?.Disposed ?? false}, meetings {panelArchive.List().Count - documentsBefore:+0;-0;0})";
                }
                MeetingShortcutDecision? stopDuringStart = null;
                var startingMeeting = new List<string>
                {
                    await StartingMeeting("meeting shortcut", () => stopDuringStart = panel.PressShortcut(false, false, false, _ => throw new InvalidOperationException("The shortcut started a second meeting."))),
                    await StartingMeeting("Stop; finish later", () => Click("Stop; finish later")),
                    await StartingMeeting("Quit", () => _ = panel.StopOperationsAsync()),
                    await StartingMeeting("meeting shortcut while the devices open", () => panel.PressShortcut(false, false, false, _ => throw new InvalidOperationException("The shortcut started a second meeting.")), fromInsideOpen: true),
                    await StartingMeeting("devices fail to open", () => { }, openFails: true),
                };
                var meetingOpenFailure = panel.MeetingNotices.Notices.Find("meeting-could-not-start");
                var startAgain = Descendants(host).OfType<System.Windows.Controls.Button>().Any(button => button.Content as string == "Start meeting" && button.IsEnabled);
                Check(startingMeeting.All(result => result.Contains(": ended")) && stopDuringStart is { Action: MeetingShortcutAction.Stop } && meetingOpenFailure is { Kind: NoticeKind.Error } && startAgain,
                    $"a starting meeting, which counts as capturing so closing the window is refused, ends without opening a device or leaving a meeting on the meeting shortcut, Stop; finish later and Quit; ended while its devices open they are closed at once and its record removed; a failed open removes the pill with the error card and Start meeting is offered again ({string.Join("; ", startingMeeting)})");
                panel.MeetingNotices.Notices.Clear("meeting-could-not-start");
                Selector("Default meeting languages").SelectedItem = Selector("Default meeting languages").Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "de");
                SaveMeetingPreferences();
                Check(Settings.Load().MeetingLanguages == "de" && panel.SelectedLanguages == "de" && ChipText("Meeting languages") == "German only"
                    && !Descendants(host).OfType<System.Windows.Controls.TextBox>().Any(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Meeting vocabulary"),
                    "Save preferences stores the default languages and the Meetings page starts with them; the Meetings page has no vocabulary of its own");
                panel.Dispose(); host.Close();
            }
            finally { File.Delete(wav); }
            var pipeline = settings.Snapshot(); pipeline.OutputStyle = OutputStyle.LocalPolish;
            var rewritten = await DictationPipeline.ProcessAsync("We should review the build. Please do not delete the file. The voltage is 42 V.", pipeline, CancellationToken.None);
            Check(rewritten.Final.Contains("42") && rewritten.Final.Contains("not", StringComparison.OrdinalIgnoreCase), "local polish preserves critical facts or visibly falls back");
            if (File.Exists(settings.LocalRewriteModelPath)) Check(rewritten.Notice is null, "installed local polish runs successfully");
            var german = await DictationPipeline.ProcessAsync("Wir sollten den Build prüfen. Bitte nicht die Datei löschen. Die Spannung beträgt 42 V.", pipeline, CancellationToken.None);
            Check(german.Final.Contains("42") && german.Final.Contains("nicht", StringComparison.OrdinalIgnoreCase), "German local polish preserves facts or visibly falls back");
            var diagnostic = JsonSerializer.Serialize(HealthChecks.Metadata(settings));
            Check(!diagnostic.Contains(Settings.Root) && !diagnostic.Contains("42 V") && !diagnostic.Contains(settings.RewriteEndpoint), "diagnostics exclude private paths, transcript and endpoint");
            // The meetings the History checks above left, with the notes the stand-in model wrote, for the History screenshots.
            var screenshotArchive = new MeetingArchive(Path.Combine(Settings.Root, "meetings"));
            string[] historyTitles = ["Design review", "Supplier call", "Weekly sync"];
            // A realistic saved state for the screenshots: the default meeting choices, two profiles, names, corrections and a snippet.
            settings.MeetingLanguages = SpeechLanguages.Offered; settings.SystemAudioColor = SystemAudioColors.Default; settings.RetainMeetingAudio = false;
            settings.Vocabulary = "Private Flow,Qwen ; Lumen";
            settings.Profiles = [new() { Name = "Code", Applications = "Code.exe, WindowsTerminal.exe", Language = "en", OutputStyle = OutputStyle.Exact, Technical = true, ShortcutModifiers = 3, ShortcutKey = 0x50 },
                new() { Name = "Mails", Applications = "OUTLOOK.EXE", Language = "de", OutputStyle = OutputStyle.LocalPolish }];
            settings.Corrections = [new() { Original = "private flow", Replacement = "Private Flow", Language = "auto" }, new() { Original = "lumen app", Replacement = "Lumen", Language = "en" }];
            settings.Snippets = [new() { Original = "my signature", Replacement = "Best regards, Alex", Language = "en" }];
            settings.SaveAs(settings);
            var main = new MainWindow(); main.Show(); await Task.Delay(200);
            Check(main.FindName("OutputLocal") is System.Windows.Controls.RadioButton, "local output control initializes");
            // Press to pill (the owner, 2026-11-17: "on first use it takes about one whole second until the pill comes up"): the pill shows, dimmed
            // as starting, before the microphone opens, and leaves the foreground where it was. A synthetic source stands in for the microphone;
            // the dictation is cancelled before anything could be pasted. Twice: the first and a later press of this instance.
            var pressTimes = new List<string>();
            for (var press = 1; press <= 2; press++)
            {
                var foreground = Native.GetForegroundWindow();
                var pressClock = System.Diagnostics.Stopwatch.StartNew();
                double pillAt = 0, recordingAt = 0;
                var pillBeforeMicrophone = false; var foregroundKept = false;
                await main.Dictation.StartAsync(false, null, _ =>
                {
                    var pill = System.Windows.Application.Current.Windows.OfType<RecordingHud>().SingleOrDefault(window => window.IsVisible);
                    pillBeforeMicrophone = pill is { Starting: true } && System.Windows.Automation.AutomationProperties.GetName(pill) == "Private Flow starting";
                    foregroundKept = pill is not null && Native.GetForegroundWindow() == foreground && new System.Windows.Interop.WindowInteropHelper(pill).Handle != foreground;
                    if (press == 1 && pill is not null) Capture(pill, Path.Combine(output, "pill-starting.png"));
                    return new SyntheticSource(press == 1 ? speech : new float[speech.Length]);
                }, stage => { if (stage == "pill") pillAt = pressClock.Elapsed.TotalMilliseconds; else recordingAt = pressClock.Elapsed.TotalMilliseconds; });
                var recordingPill = System.Windows.Application.Current.Windows.OfType<RecordingHud>().SingleOrDefault(window => window.IsVisible);
                await Task.Delay(300);
                if (recordingPill is not null) Capture(recordingPill, Path.Combine(output, press == 1 ? "pill-recording.png" : "pill-recording-silent.png"));
                var listening = recordingPill is { Starting: false } && System.Windows.Automation.AutomationProperties.GetName(recordingPill) == "Private Flow recording" && main.Dictation.IsRecording;
                await main.Dictation.CancelAsync();
                var closed = !System.Windows.Application.Current.Windows.OfType<RecordingHud>().Any(window => window.IsVisible) && !main.Dictation.IsRecording;
                pressTimes.Add($"press {press}: pill drawn {pillAt:0} ms, recording {recordingAt:0} ms");
                Check(pillBeforeMicrophone && foregroundKept && listening && closed && pillAt > 0 && pillAt < PressToPillBound * 1000 && recordingAt >= pillAt,
                    $"a dictation press shows the pill within {PressToPillBound * 1000:0} ms, dimmed as starting before the microphone opens and without taking the foreground, then as recording; cancelled, it closes ({string.Join("; ", pressTimes)})");
            }
            T Element<T>(string name) where T : class => (T)main.FindName(name);
            void SavePreferencesInWindow() => Element<System.Windows.Controls.Button>("SavePreferencesButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            // A starting dictation ends as a recording does (review of #67): each event is sent while the microphone open is held back, from
            // the stage "pill", which comes after the yield and before the start checks whether it was ended; or from inside the open; or
            // the open fails. Nothing may open, start recording or stay on screen.
            var startingCancel = Element<System.Windows.Controls.Button>("CancelButton");
            var startingNotices = Element<NoticeStack>("DictateNotices");
            async Task<string> StartingDictation(string name, Action send, bool fromInsideOpen = false, bool openFails = false, ActivationShortcut? held = null, bool endedByTimer = false)
            {
                var opens = 0; HeldBackSource? source = null; var armed = false;
                IAudioSource Open(string _)
                {
                    opens++;
                    if (openFails) throw new InvalidOperationException("Stand-in microphone failed to open.");
                    source = new HeldBackSource();
                    if (fromInsideOpen) send();
                    return source;
                }
                await main.Dictation.StartAsync(false, held, Open, stage =>
                {
                    if (stage != "pill") return;
                    armed = main.Dictation.IsStarting && main.Dictation.EscapeRegistered && startingCancel.IsEnabled && main.Dictation.IsRecording;
                    if (!fromInsideOpen && !openFails) send();
                });
                await Task.Delay(150);
                var pillGone = !System.Windows.Application.Current.Windows.OfType<RecordingHud>().Any(window => window.IsVisible);
                var ended = !main.Dictation.IsRecording && !main.Dictation.IsStarting && !main.Dictation.EscapeRegistered && !startingCancel.IsEnabled;
                var deviceUntouched = fromInsideOpen ? opens == 1 && source is { Started: false, Disposed: true } : openFails ? opens == 1 : opens == 0;
                var passed = (armed || endedByTimer) && pillGone && ended && deviceUntouched;
                return $"{name}: {(passed ? "ended" : "NOT ended")} (armed {armed}, opens {opens}, started {source?.Started ?? false}, closed {source?.Disposed ?? false}, pill gone {pillGone})";
            }
            var startingDictation = new List<string>
            {
                await StartingDictation("second press", () => main.Dictation.Toggle()),
                await StartingDictation("Escape", () => _ = main.Dictation.CancelAsync()),
                await StartingDictation("Cancel", () => startingCancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent))),
                await StartingDictation("Escape while the microphone opens", () => _ = main.Dictation.CancelAsync(), fromInsideOpen: true),
            };
            var cancelledStatus = Element<TextBlock>("Status").Text;
            // Hold to talk, shown and not saved: the shortcut is not held, so the window's timer (FollowHeldShortcut every 50 ms) reads a release
            // during the yield, usually before the stage, and ends the start as a tap; the stage sends the same check in case it did not.
            var behaviorChoice = Element<System.Windows.Controls.ComboBox>("ShortcutBehaviorChoice");
            behaviorChoice.SelectedItem = Element<System.Windows.Controls.ComboBoxItem>("HoldToTalkOption");
            startingDictation.Add(await StartingDictation("hold-to-talk release", () => main.Dictation.FollowHeldShortcut(), held: new ActivationShortcut(0, 0x77), endedByTimer: true));
            var releasedNote = startingNotices.Notices.Find(DictationController.OutcomeKey)?.Title;
            behaviorChoice.SelectedItem = Element<System.Windows.Controls.ComboBoxItem>("ToggleOption");
            startingDictation.Add(await StartingDictation("microphone fails to open", () => { }, openFails: true));
            var openFailure = startingNotices.Notices.Find(DictationController.FailedKey);
            // A start ended at its pill, then a new one before the first resumes: the first opens nothing, the second records.
            HeldBackSource? second = null; var firstOpens = 0; Task? secondStart = null;
            await main.Dictation.StartAsync(false, null, _ => { firstOpens++; return new HeldBackSource(); }, stage =>
            {
                if (stage != "pill") return;
                _ = main.Dictation.CancelAsync();
                secondStart = main.Dictation.StartAsync(false, null, _ => second = new HeldBackSource());
            });
            if (secondStart is not null) await secondStart;
            var secondRecords = firstOpens == 0 && second is { Started: true, Disposed: false } && main.Dictation.IsRecording && !main.Dictation.IsStarting;
            await main.Dictation.CancelAsync(); await Task.Delay(150);
            startingNotices.Notices.Clear(DictationController.FailedKey); startingNotices.Notices.Clear(DictationController.OutcomeKey);
            Check(startingDictation.All(result => result.Contains(": ended")) && cancelledStatus == "Cancelled. No pending insertion." && releasedNote == "Released too soon"
                && openFailure is { Kind: NoticeKind.Error, Title: "The microphone could not start" } && secondRecords && main.UnsavedPreferenceSections.Count == 0,
                $"a starting dictation, with Escape registered and Cancel enabled before its pill yields, ends without recording on a second press, Escape, Cancel, a hold-to-talk release (a tap) and a cancel while the microphone opens (closed at once); a failed open removes the pill with the error card; a start ended at its pill does not complete a newer one ({string.Join("; ", startingDictation)}; second start records {secondRecords})");
            var navigation = Element<System.Windows.Controls.ListBox>("Navigation");
            var sidebar = Element<System.Windows.Controls.TextBox>("SearchBox");
            Check(navigation.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(["Dictate", "Meetings", "History", "Vocabulary"]), "the sidebar lists Dictate, Meetings, History and Vocabulary");
            Check(Descendants(Element<System.Windows.Controls.Button>("SettingsButton")).OfType<System.Windows.Shapes.Path>().Single().Data == main.FindResource("IconGear"), "the settings button in the sidebar shows the gear");
            Capture(main, Path.Combine(output, "workspace.png"));
            // A dictation result as review-first shows it, for the screenshot.
            var dictationView = (IDictationView)main;
            dictationView.ShowResult("um so the demo is at eleven, no wait, at twelve thirty in room four", "So the demo is at 12:30 in room 4.");
            dictationView.ShowStatus("Ready to review • 1.4s");
            await Task.Delay(150); Capture(main, Path.Combine(output, "dictate-result.png"));
            Check(Element<System.Windows.Controls.TextBox>("Transcript").Text == "So the demo is at 12:30 in room 4.", "the Dictate page shows a result in its composer");
            main.ShowPage("Meetings"); await Task.Delay(150); Capture(main, Path.Combine(output, "meetings.png"));
            var meetingShortcutLink = Descendants(main).OfType<System.Windows.Documents.Hyperlink>().Single(link => System.Windows.Automation.AutomationProperties.GetName(link) == "Set a meeting shortcut");
            meetingShortcutLink.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent)); await Task.Delay(150);
            Check(main.SettingsOpen && main.SettingsSection == MainWindow.ShortcutsTitle, "the link on the Meetings page opens the settings popup at the shortcuts");
            main.CloseSettings(); await Task.Delay(150);
            main.ShowPage("History"); await Task.Delay(150);
            var windowPanel = Descendants(main).OfType<MeetingsPanel>().Single();
            var history = Descendants(main).OfType<System.Windows.Controls.ListBox>().Single(list => System.Windows.Automation.AutomationProperties.GetName(list) == "Past meetings");
            void Open(string title) => history.SelectedItem = history.Items.OfType<System.Windows.Controls.ListBoxItem>().Single(item => ((MeetingDocument)item.Tag).Title == title);
            Open("Design review"); await Task.Delay(150);
            Capture(main, Path.Combine(output, "history.png"));
            var historySearch = Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Search meetings");
            historySearch.Text = "twelve thirty"; await Task.Delay(150);
            Capture(main, Path.Combine(output, "history-search.png"));
            var searchedInWindow = history.Items.Count == 2 && windowPanel.HighlightedRows.Count == 2 && Element<ContentControl>("PageActions").Content is FrameworkElement header && Descendants(header).Contains(historySearch);
            historySearch.Text = "";
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Notes").IsChecked = true;
            Open("Supplier call"); await Task.Delay(150);
            Capture(main, Path.Combine(output, "history-notes.png"));
            var noticeInWindow = windowPanel.NotesState is { WriteButton: "Write notes with test-model at notes.invalid", WrittenBy: null } state && KeysOf(state.Notices).SequenceEqual([NotesStatus.RuleBasedKey]);
            Open("Design review"); await Task.Delay(150);
            Capture(main, Path.Combine(output, "history-model-notes.png"));
            // A citation on the Notes tab reads as time and speaker and leads to its row on the Transcript tab.
            var citations = windowPanel.NotesCitations;
            var citedText = citations.Count == 0 ? "" : new System.Windows.Documents.TextRange(citations[0].ContentStart, citations[0].ContentEnd).Text;
            if (citations.Count > 0) citations[0].RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            await Task.Delay(200);
            Capture(main, Path.Combine(output, "history-citation-opened.png"));
            var transcriptShown = Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Transcript").IsChecked == true;
            Check(citedText == "[04:30 · You]" && transcriptShown && windowPanel.SelectedRow is { Start: 270, Speaker: "You" } && !windowPanel.NotesState.Text.Contains("Design review ·"),
                $"a citation on the Notes tab shows only time and speaker, and pressing it opens the Transcript tab at that row ({citedText})");
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Notes").IsChecked = true; await Task.Delay(100);
            Check(searchedInWindow && noticeInWindow && windowPanel.NotesState.WrittenBy is { } writtenBy && writtenBy.StartsWith("Written by test-model through the API on ") && windowPanel.NotesState.Notices.Count == 0,
                "in the window, the History page searches from its header, shows the notice with rule-based notes and the stored model notes after a restart");
            // The confirmation before deleting a meeting, for the screenshot; Cancel keeps the meeting.
            var moreInWindow = Descendants(main).OfType<System.Windows.Controls.Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "More meeting actions");
            moreInWindow.ContextMenu.Items.OfType<System.Windows.Controls.MenuItem>().First(item => ((TextBlock)item.Header).Text == "Delete meeting").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
            await Task.Delay(200);
            Capture(main, Path.Combine(output, "confirmation.png"));
            var windowQuestion = Descendants(main).OfType<Border>().Single(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName);
            Descendants(windowQuestion).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Delay(150);
            Check(windowPanel.SelectedMeeting?.Title == "Design review" && screenshotArchive.List().Any(document => document.Title == "Design review"), "in the window, Cancel in the confirmation keeps the meeting");
            Open("Supplier call"); await Task.Delay(150);
            Descendants(main).OfType<System.Windows.Documents.Hyperlink>().Single(link => System.Windows.Automation.AutomationProperties.GetName(link) == "Choose how notes are written")
                .RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));
            await Task.Delay(300);
            Capture(main, Path.Combine(output, "settings-meeting-notes.png"));
            Check(main.SettingsOpen && main.SettingsSection == MainWindow.ModelsTitle, "the link with the notice opens the settings popup at Models with the notes setting");
            // The local model style without its model: the install action on the Notes tab and under Models. Nothing is pressed.
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: Local model").IsChecked = true;
            SavePreferencesInWindow();
            main.CloseSettings(); await Task.Delay(150);
            Capture(main, Path.Combine(output, "history-install.png"));
            Check(windowPanel.NotesInstallState.Button == $"Install the meeting notes model ({SpeechRuntime.Gigabytes(MeetingNotesModel.DownloadBytes)} GB)" && windowPanel.NotesState.WriteButton is null && !MeetingNotesModel.IsInstalled(Settings.Load()),
                "in the window, the Notes tab offers to install the meeting notes model with its size while the local model style lacks it");
            // The install runs under Settings → Models like every other one, here with a stand-in for the setup script: it names its size,
            // shows its progress on the bar of the meeting progress, can be cancelled, says why it failed and, once done, turns into the write
            // button. Nothing is downloaded.
            var installLabel = MeetingsPanel.NotesInstallLabel;
            var notesSetups = new List<string>();
            Func<IProgress<string>, CancellationToken, Task<string?>> notesSetup = (_, _) => throw new InvalidOperationException("No install answer is set.");
            main.Workspace.RunSetup = (script, arguments, token, progress) =>
            {
                notesSetups.Add(script + " " + string.Join(" ", arguments.TakeWhile(argument => argument != "-ResultFile")));
                return notesSetup(progress!, token);
            };
            var offered = windowPanel.NotesInstallState;
            Check(offered.Button == installLabel && windowPanel.NotesChoiceInstall.Button.Content as string == installLabel && offered.Description!.Contains("nothing is downloaded before you press it") && offered.Progress is null
                && notesSetups.Count == 0 && !MeetingNotesModel.IsInstalled(main.SharedSettings),
                "with the local model style and no model, the Notes tab and the Local model choice under Models offer the install with its download size, and nothing is downloaded before the press");
            System.Windows.Controls.Button WindowButton(string label) => Descendants(main).OfType<System.Windows.Controls.Button>().First(button => button.IsVisible && button.Content as string == label);
            void PressInWindow(string label) => WindowButton(label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            notesSetup = (progress, _) => { progress.Report("Downloading Qwen3.5-4B-Q4_K_M.gguf..."); return Task.FromResult<string?>(SpeechSetup.FailureMessage("Checksum mismatch; nothing installed.")); };
            PressInWindow(installLabel);
            await WaitAsync(() => main.Workspace.NotesInstallInModels.Failure is not null, 5);
            var installCard = windowPanel.NotesState.Notices.FirstOrDefault();
            var settingsInstallCard = main.Workspace.ModelsNotices.Notices.Find(NotesStatus.InstallFailedKey);
            Check(notesSetups.SequenceEqual(["setup-rewrite.ps1 -Part notes"]) && windowPanel.NotesInstallState.Button == installLabel && KeysOf(windowPanel.NotesState.Notices).SequenceEqual([NotesStatus.InstallFailedKey, NotesStatus.RuleBasedKey])
                && installCard is { Kind: NoticeKind.Error, Title: "The meeting notes model was not installed", Text: "Setup failed: Checksum mismatch; nothing installed." }
                && installCard.Detail!.EndsWith("Check network, available disk space and pinned download availability.")
                && main.Workspace.NotesInstallInModels.Failure == "Setup failed: Checksum mismatch; nothing installed." && settingsInstallCard?.Detail == installCard.Detail && settingsInstallCard.Action?.Label == "Try again"
                && Element<TextBlock>("Status").Text == "Installing the meeting notes model ended; Models and the Notes tab say how.",
                "a failed installation is an error card on the Notes tab, shortened with the whole reason as its detail, and under Models with Try again; the install is offered again");
            var notesFiles = new[] { main.SharedSettings.MeetingNotesEnginePath, main.SharedSettings.MeetingNotesModelPath };
            notesSetup = (progress, _) =>
            {
                progress.Report("Meeting notes model ready.");
                foreach (var file in notesFiles) { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, ""); }
                return Task.FromResult<string?>(null);
            };
            PressInWindow(installLabel);
            await WaitAsync(() => windowPanel.NotesState.WriteButton is not null, 5);
            var installedNotes = windowPanel.NotesState; var installedOffer = windowPanel.NotesInstallState;
            bool Hidden(UIElement element) => Ancestors(element).Prepend(element).OfType<UIElement>().Any(shown => shown.Visibility == Visibility.Collapsed);
            Check(notesSetups.Count == 2 && installedNotes.WriteButton == "Write notes with the local model" && installedOffer.Button is null && installedOffer.Progress is null && KeysOf(installedNotes.Notices).SequenceEqual([NotesStatus.RuleBasedKey])
                && main.Workspace.NotesInstallInModels is { Progress: null, Failure: null } && Hidden(main.Workspace.ModelInstall(ModelCatalogue.NotesModel)) && Hidden(windowPanel.NotesChoiceInstall.Button)
                && Element<TextBlock>("Status").Text == "The meeting notes model is installed. Write notes with it on the Notes tab of a meeting.",
                "once installed, the Notes tab offers to write notes with the local model and Models no longer offers the install");
            foreach (var file in notesFiles) File.Delete(file);
            main.SharedSettings.SaveAs(main.SharedSettings); await Task.Delay(100);
            var releaseNotesInstall = new TaskCompletionSource();
            notesSetup = async (progress, token) =>
            {
                progress.Report("Downloading Qwen3.5-4B-Q4_K_M.gguf..."); progress.Report("Local model download: 42%");
                await releaseNotesInstall.Task.WaitAsync(token);
                progress.Report("Local model download: 30%"); progress.Report("Expanding the runtime...");
                await Task.Delay(Timeout.Infinite, token); return null;
            };
            PressInWindow(installLabel);
            await WaitAsync(() => windowPanel.NotesInstallState.Share == .42, 5); await Task.Delay(200);
            Capture(main, Path.Combine(output, "notes-install-progress.png"));
            var notesTabInstalling = windowPanel.NotesInstallState;
            main.ShowSettings(MainWindow.ModelsTitle); await Task.Delay(150);
            // With room below, so the screenshot shows the whole row around the bar.
            var settingsBar = Descendants(main).OfType<SetupProgressView>().First(view => view.Shown && !Descendants(windowPanel.History).Contains(view));
            settingsBar.BringIntoView(new Rect(0, 0, settingsBar.ActualWidth, settingsBar.ActualHeight + 120)); await Task.Delay(300);
            Capture(main, Path.Combine(output, "settings-install-progress.png"));
            var settingsInstallingInWindow = main.Workspace.NotesInstallInModels;
            releaseNotesInstall.SetResult();
            await WaitAsync(() => windowPanel.NotesInstallState.Progress?.Contains("Expanding the runtime") == true, 5);
            var laterStep = windowPanel.NotesInstallState;
            PressInWindow("Cancel installing");
            await WaitAsync(() => main.Workspace.NotesInstallInModels.Failure is not null, 5);
            main.Workspace.RunSetup = SpeechSetup.RunAsync;
            Check(notesTabInstalling is { Button: "Cancel installing", Share: .42 } && notesTabInstalling.Progress!.StartsWith("Installing the meeting notes model · Downloading Qwen3.5-4B-Q4_K_M.gguf · 42%")
                && settingsInstallingInWindow.Progress == notesTabInstalling.Progress && settingsInstallingInWindow.Share == .42 && main.Workspace.NotesInstallInModels.Failure!.Contains("Installing was cancelled."),
                "in the window, the install shows its step and share on the bar of the meeting progress next to Cancel installing, on the Notes tab and under Models");
            Check(notesTabInstalling.Description is null && laterStep.Progress!.StartsWith("Installing the meeting notes model · Expanding the runtime · 42%") && laterStep.Share == .42
                && windowPanel.NotesState.Notices[0] is { Key: NotesStatus.InstallFailedKey, Text: "Installing was cancelled." } && windowPanel.NotesInstallState.Button == installLabel
                && windowPanel.NotesInstallState.Progress is null && notesSetups.Count == 3,
                "while it installs, both places show the step and its share on the bar of the meeting progress next to Cancel installing; the bar never falls and holds its value through a step without a percentage, and Cancel says so");
            // The Notes tab in its states, with a stand-in for the notes model: rule-based with the caveat, nothing found, and a failed run
            // over notes written before. At most one yellow line in each.
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: API").IsChecked = true;
            SavePreferencesInWindow();
            main.CloseSettings(); await Task.Delay(150);
            Open("Supplier call"); await Task.Delay(150);
            Capture(main, Path.Combine(output, "notes-rule-based.png"));
            Capture(main, Path.Combine(output, "cards-notes-one.png"));
            var ruleBasedInWindow = windowPanel.NotesState;
            windowPanel.CompleteNotes = (_, _, _) => Task.FromResult("""{"summary": ["A short test of the recording."], "decisions": [], "actionItems": [], "openQuestions": [], "timesMentioned": []}""");
            PressInWindow("Write notes with test-model at notes.invalid");
            await WaitAsync(() => !windowPanel.NotesState.Running, 10); await Task.Delay(150);
            Capture(main, Path.Combine(output, "notes-nothing-found.png"));
            var nothingInWindow = windowPanel.NotesState;
            windowPanel.CompleteNotes = (_, _, _) => Task.FromException<string>(new TimeoutException("The API did not answer within 60 s."));
            Open("Design review"); await Task.Delay(150);
            PressInWindow("Write notes with test-model at notes.invalid");
            await WaitAsync(() => !windowPanel.NotesState.Running, 10); await Task.Delay(150);
            Capture(main, Path.Combine(output, "notes-failed-earlier-kept.png"));
            var failedInWindow = windowPanel.NotesState;
            Check(ruleBasedInWindow is { WrittenBy: null } && KeysOf(ruleBasedInWindow.Notices).SequenceEqual([NotesStatus.RuleBasedKey]) && nothingInWindow is { WrittenBy: null }
                && KeysOf(nothingInWindow.Notices).SequenceEqual([NotesStatus.NothingFoundKey, NotesStatus.RuleBasedKey])
                && nothingInWindow.Notices[0] is { Kind: NoticeKind.Note, Text: "test-model through the API found no decisions, action items, open questions or times in this meeting." }
                && failedInWindow.WrittenBy is not null && KeysOf(failedInWindow.Notices).SequenceEqual([NotesStatus.FailedKey])
                && failedInWindow.Notices[0] is { Kind: NoticeKind.Error, Text: "The API did not answer within 60 s. The notes written before stay." } && failedInWindow.Text.Contains("The demo is at eleven."),
                "in the window, the Notes tab shows the rule-based notes under one warning card, says in a note card that the model found nothing, and keeps the notes written before under an error card when a new run fails");
            // Three cards on the Notes tab: notes older than the transcript, a failed run over them and the install of the local model that
            // was cancelled above. Piled with the newest in front, spread out and piled again, dismissed one by one; a redraw keeps a
            // dismissed card away and opening the meeting again brings it back.
            var notesStack = windowPanel.NotesNotices;
            var designShown = windowPanel.SelectedMeeting!; var designFirstRow = designShown.Segments.OrderBy(segment => segment.Start).First(); var designRowText = designFirstRow.Text;
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Transcript").IsChecked = true; await Task.Delay(100);
            designFirstRow.Text = "The demo moved to noon."; PressInWindow("Save edits");
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Notes").IsChecked = true; await Task.Delay(100);
            windowPanel.CompleteNotes = (_, _, _) => Task.FromException<string>(new TimeoutException("The API did not answer within 60 s."));
            PressInWindow("Write notes with test-model at notes.invalid");
            await WaitAsync(() => !windowPanel.NotesState.Running, 10); await Task.Delay(150);
            var staleAndFailed = KeysOf(windowPanel.NotesState.Notices);
            Capture(main, Path.Combine(output, "cards-notes-two-piled.png"));
            main.ShowSettings(MainWindow.ModelsTitle); await Task.Delay(150);
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: Local model").IsChecked = true;
            SavePreferencesInWindow(); main.CloseSettings(); await Task.Delay(150);
            var threePiled = (Keys: KeysOf(windowPanel.NotesState.Notices), Toggle: notesStack.Toggle?.Content as string, notesStack.Cards.Count, Dismiss: notesStack.DismissButtons.Count, notesStack.Spread);
            var pileDepth = notesStack.Children.OfType<Grid>().SingleOrDefault()?.Children.Count;
            Capture(main, Path.Combine(output, "cards-notes-three-piled.png"));
            notesStack.Toggle!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(150);
            var spreadOut = (Toggle: notesStack.Toggle?.Content as string, notesStack.Cards.Count, Dismiss: notesStack.DismissButtons.Count, notesStack.Spread,
                Tabbable: notesStack.Cards.All(card => card.Focusable) && notesStack.DismissButtons.All(button => button.Focusable && System.Windows.Automation.AutomationProperties.GetName(button).StartsWith("Dismiss: ")));
            Capture(main, Path.Combine(output, "cards-notes-spread.png"));
            notesStack.Toggle!.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
            var piledAgain = !notesStack.Spread && notesStack.Cards.Count == 1;
            Check(staleAndFailed.SequenceEqual([NotesStatus.FailedKey, NotesStatus.StaleKey])
                && threePiled.Keys.SequenceEqual([NotesStatus.InstallFailedKey, NotesStatus.FailedKey, NotesStatus.StaleKey]) && (threePiled.Toggle, threePiled.Count, threePiled.Dismiss, threePiled.Spread) == ("2 more · show all", 1, 1, false) && pileDepth == 3
                && spreadOut == ("Pile up", 3, 3, true, true) && piledAgain && System.Windows.Automation.AutomationProperties.GetName(notesStack) == "Notes notices",
                $"in the window, three notices pile with the newest in front, two edges peeking and “2 more · show all”; it spreads them one under the other, each with its x reachable by Tab, and “Pile up” piles them again ({string.Join(", ", threePiled.Keys)})");
            notesStack.DismissButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
            var afterDismiss = (Keys: KeysOf(notesStack.Shown), Toggle: notesStack.Toggle?.Content as string);
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Transcript").IsChecked = true;
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Notes").IsChecked = true; await Task.Delay(100);
            var afterRedraw = KeysOf(notesStack.Shown);
            Open("Supplier call"); await Task.Delay(100); Open("Design review"); await Task.Delay(150);
            var afterReopen = KeysOf(notesStack.Shown);
            Check(afterDismiss.Keys.SequenceEqual([NotesStatus.FailedKey, NotesStatus.StaleKey]) && afterDismiss.Toggle == "1 more · show all" && afterRedraw.SequenceEqual(afterDismiss.Keys)
                && afterReopen.SequenceEqual([NotesStatus.InstallFailedKey, NotesStatus.FailedKey, NotesStatus.StaleKey]) && !MeetingHistory.WrittenNotesMatchTranscript(windowPanel.SelectedMeeting!),
                $"in the window, the x of the front card reveals the next one, the dismissed card stays away when the page is redrawn and returns when the meeting is opened again; the notes stay stale ({string.Join(", ", afterReopen)})");
            main.ShowSettings(MainWindow.ModelsTitle); await Task.Delay(150);
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: API").IsChecked = true;
            SavePreferencesInWindow(); main.CloseSettings(); await Task.Delay(100);
            windowPanel.CompleteNotes = null;
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Transcript").IsChecked = true; await Task.Delay(100);
            windowPanel.SelectedMeeting!.Segments.OrderBy(segment => segment.Start).First().Text = designRowText; PressInWindow("Save edits");
            Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(tab => tab.Content as string == "Transcript").IsChecked = true;
            main.ShowPage("Vocabulary"); await Task.Delay(150); Capture(main, Path.Combine(output, "vocabulary.png"));
            await VerifyVocabularyListAsync(main, output, Check);
            sidebar.Text = "retention";
            var found = navigation.Items.Cast<object>().Select(item => item.ToString()).ToArray();
            navigation.SelectedItem = navigation.Items.Cast<object>().Single(item => item.ToString() == "Meeting defaults");
            await Task.Delay(150);
            Check(found.SequenceEqual(["History", "Meeting defaults"]) && main.SettingsOpen && main.SettingsSection == "Meeting defaults" && sidebar.Text == "" && Element<TextBlock>("PageTitle").Text == "Vocabulary",
                $"a sidebar search finds settings, and choosing one opens the settings popup at its section over the open page ({string.Join(", ", found)})");
            var settingsDialog = Element<Border>("SettingsDialog");
            var behind = ((System.Windows.Controls.Panel)main.Content).Children.OfType<UIElement>().Where(element => !Descendants(element).Contains(settingsDialog)).ToArray();
            Check(WindowOverlay.IsShown(main, MainWindow.SettingsAutomationName) && behind.All(element => element.Effect is System.Windows.Media.Effects.BlurEffect && !element.IsEnabled)
                && Descendants(main).OfType<System.Windows.Controls.ComboBox>().Count(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Microphone") == 1,
                "the settings popup covers the blurred, disabled window, and one microphone selector serves dictation and meetings");
            // A question over the open settings, and the settings closed underneath it, as a tray entry or a finished dictation does.
            var questionOverSettings = ConfirmationDialog.AskAsync(main, "Question", "Asked over the open settings.", "Confirm");
            main.CloseSettings();
            var hostChildren = ((System.Windows.Controls.Panel)main.Content).Children.OfType<UIElement>().ToArray();
            Check(questionOverSettings.IsCompleted && !await questionOverSettings && !main.SettingsOpen && !WindowOverlay.IsShown(main, ConfirmationDialog.AutomationName)
                && hostChildren.All(element => element.Effect is null && element.IsEnabled),
                "closing the settings under an open question cancels the question and leaves the window enabled and unblurred");
            string[] sectionTitles = ["Microphone and speakers", MainWindow.ShortcutsTitle, "Language and pasting", "Text style", MainWindow.ProfilesTitle, "Meeting defaults", MainWindow.ModelsTitle, "Speech recognition", "About and updates"];
            var breadcrumbs = new List<string>();
            foreach (var section in sectionTitles)
            {
                main.ShowSettings(section); await Task.Delay(150);
                if (section == MainWindow.ProfilesTitle)
                {
                    var profileRows = Descendants(main).OfType<System.Windows.Controls.DataGrid>().Single(grid => grid.Columns.Any(column => column.Header as string == "Applications"));
                    profileRows.SelectedIndex = 0; await Task.Delay(150);
                    var profileShortcut = Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Selected profile shortcut");
                    Check(profileShortcut.Text == "Ctrl + Alt + P", $"a profile shortcut is shown with the key names the shortcut settings use ({profileShortcut.Text})");
                }
                breadcrumbs.Add(Element<TextBlock>("SettingsTitle").Text);
                Capture(main, Path.Combine(output, $"settings-{section.ToLowerInvariant().Replace(' ', '-')}.png"));
            }
            var versionLine = Element<TextBlock>("VersionText").Text; var buildLine = Element<TextBlock>("BuildText");
            var buildCommit = SpeechSetup.BuildIdentity.Split('+') is [_, var commit] ? commit : "";
            Check(versionLine.StartsWith($"Version {typeof(App).Assembly.GetName().Version!.ToString(3)} (beta).") && buildCommit.Length >= 7
                && buildLine.Text == $"Build {typeof(App).Assembly.GetName().Version!.ToString(3)}+{buildCommit[..7]}" && buildLine.ToolTip as string == SpeechSetup.BuildIdentity,
                $"About and updates names the version as a beta and the build with its commit, all of the build identity in the tooltip ({versionLine[..Math.Min(22, versionLine.Length)]} | {buildLine.Text} | {buildLine.ToolTip})");
            Check(Element<System.Windows.Controls.ListBox>("SettingsNavigation").Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(sectionTitles) && breadcrumbs.SequenceEqual(sectionTitles),
                $"the settings popup lists its sections by what is set there, and the breadcrumb names the open one ({string.Join(", ", breadcrumbs)})");
            Check(Element<System.Windows.Controls.ComboBox>("PlaybackDevices") is { SelectedItem: MicrophoneDevice { Id: "" } } outputs && outputs.Items.OfType<MicrophoneDevice>().First().Name == "Follow Windows default output"
                && System.Windows.Automation.AutomationProperties.GetName(outputs) == "Playback device",
                "the audio settings offer the meeting output device next to the microphone and follow the Windows default when none was chosen");
            // One value changed in every section of the settings popup: the unsaved state shows at once, Discard restores the stored value
            // and Save preferences stores it. Each value is changed back and saved afterwards.
            var preferenceActions = Element<FrameworkElement>("PreferenceActions"); var unsavedDot = Element<FrameworkElement>("SettingsUnsavedDot");
            void PressLabelled(DependencyObject scope, string label) => Descendants(scope).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == label)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            void PressNamed(string name) => Descendants(main).OfType<System.Windows.Controls.Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == name)
                .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            System.Windows.Controls.Primitives.ToggleButton Switch(string name) => Descendants(main).OfType<System.Windows.Controls.Primitives.ToggleButton>().Single(toggle => System.Windows.Automation.AutomationProperties.GetName(toggle) == name);
            System.Windows.Controls.TextBox FieldNamed(string name) => Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == name);
            var languages = Element<System.Windows.Controls.ComboBox>("Languages");
            void ChooseLanguage(string tag) => languages.SelectedItem = languages.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == tag);
            void ChooseIn(string name, string tag)
            {
                var box = Descendants(main).OfType<System.Windows.Controls.ComboBox>().Single(choice => System.Windows.Automation.AutomationProperties.GetName(choice) == name);
                box.SelectedItem = box.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == tag);
            }
            var profileGrid = Descendants(main).OfType<System.Windows.Controls.DataGrid>().Single(grid => grid.Columns.Any(column => column.Header as string == "Applications"));
            Check(main.PreferenceControlProblem is null, $"every setting of a settings section in the table of stored settings has exactly one control in the window, and no other setting has one ({main.PreferenceControlProblem})");
            var before = Settings.Load();
            var sectionChanges = new (string Section, Action Change, Action Restore, Func<Settings, bool> IsStored)[]
            {
                (PreferenceSections.Audio, () => PressNamed("Green system audio colour"), () => PressNamed("Blue system audio colour"), storedNow => storedNow.SystemAudioColor == "#3FB950"),
                (PreferenceSections.Shortcuts, () => Element<System.Windows.Controls.ComboBox>("ShortcutBehaviorChoice").SelectedIndex = 1, () => Element<System.Windows.Controls.ComboBox>("ShortcutBehaviorChoice").SelectedIndex = 0,
                    storedNow => storedNow.ShortcutBehavior == ShortcutBehavior.HoldToTalk),
                (PreferenceSections.LanguageAndPasting, () => ChooseLanguage("de"), () => ChooseLanguage(before.Language), storedNow => storedNow.Language == "de"),
                (PreferenceSections.TextStyle, () => Element<System.Windows.Controls.TextBox>("RewriteModel").Text = "other-model", () => Element<System.Windows.Controls.TextBox>("RewriteModel").Text = before.RewriteModel,
                    storedNow => storedNow.RewriteModel == "other-model"),
                (PreferenceSections.Profiles, () => { profileGrid.SelectedIndex = 0; Switch("Profile literal mode").IsChecked = true; }, () => { profileGrid.SelectedIndex = 0; Switch("Profile literal mode").IsChecked = false; },
                    storedNow => storedNow.Profiles[0].Literal),
                (PreferenceSections.MeetingDefaults, () => FieldNamed("Meeting retention days").Text = "45", () => FieldNamed("Meeting retention days").Text = before.MeetingRetentionDays.ToString(), storedNow => storedNow.MeetingRetentionDays == 45),
                (PreferenceSections.Models, () => ChooseIn("Local polish model", nameof(LocalPolishModel.MeetingNotesModel)), () => ChooseIn("Local polish model", before.LocalPolishModel.ToString()),
                    storedNow => storedNow.LocalPolishModel == LocalPolishModel.MeetingNotesModel),
                (PreferenceSections.SpeechRecognition, () => Switch("Keep speech model warm").IsChecked = !before.KeepModelWarm, () => Switch("Keep speech model warm").IsChecked = before.KeepModelWarm,
                    storedNow => storedNow.KeepModelWarm != before.KeepModelWarm),
            };
            var sectionResults = new List<string>(); var everySection = true;
            foreach (var (section, change, restore, isStored) in sectionChanges)
            {
                main.ShowSettings(section); await Task.Delay(100);
                change(); await Task.Delay(150);
                var shownUnsaved = preferenceActions.IsVisible && unsavedDot.IsVisible && main.UnsavedPreferenceSections.SequenceEqual([section]) && !isStored(Settings.Load());
                if (section == PreferenceSections.MeetingDefaults) Capture(main, Path.Combine(output, "settings-unsaved-meeting-defaults.png"));
                PressLabelled(preferenceActions, "Discard"); await Task.Delay(150);
                var discarded = main.UnsavedPreferenceSections.Count == 0 && !preferenceActions.IsVisible && !unsavedDot.IsVisible;
                change(); await Task.Delay(150);
                SavePreferencesInWindow(); await Task.Delay(150);
                var saved = isStored(Settings.Load()) && main.UnsavedPreferenceSections.Count == 0 && !preferenceActions.IsVisible;
                restore(); await Task.Delay(150); SavePreferencesInWindow(); await Task.Delay(100);
                var restored = !isStored(Settings.Load()) && main.UnsavedPreferenceSections.Count == 0;
                everySection &= shownUnsaved && discarded && saved && restored;
                sectionResults.Add($"{section}: unsaved {shownUnsaved}, discarded {discarded}, saved {saved}, restored {restored}");
            }
            Check(everySection, "in every settings section a changed value shows Unsaved changes and the dot at once, Discard restores the stored value and Save preferences stores it (" + string.Join("; ", sectionResults) + ")");
            // Leaving the popup with an unsaved change asks first: Escape in the question keeps the popup and the change, Don't save discards and
            // closes, Save stores and closes, and a value that cannot be stored keeps the popup open at its section with the reason.
            var storedLanguage = Settings.Load().Language;
            Border? OpenQuestion() => Descendants(main).OfType<Border>().SingleOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName);
            void RaiseKey(UIElement element, System.Windows.Input.Key key) => element.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.KeyDownEvent });
            main.ShowSettings(PreferenceSections.LanguageAndPasting); await Task.Delay(100);
            ChooseLanguage("es"); await Task.Delay(100);
            var closing = main.CloseSettingsAsync(); await Task.Delay(200);
            var question = OpenQuestion();
            var questionTexts = question is null ? [] : Descendants(question).OfType<TextBlock>().Select(text => text.Text).ToList();
            var saveButton = question is null ? null : Descendants(question).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Save");
            var dontSaveButton = question is null ? null : Descendants(question).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Don't save");
            Capture(main, Path.Combine(output, "settings-save-question.png"));
            var askedBeforeClosing = question is not null && main.SettingsOpen && !closing.IsCompleted && questionTexts.SequenceEqual(["Save your changes?", "Your changes under Language and pasting are not saved yet."])
                && saveButton is { IsDefault: true, IsFocused: true } && saveButton.Style == WorkspacePages.Theme("Primary") && DockPanel.GetDock(saveButton) == Dock.Right
                && dontSaveButton is not null && dontSaveButton.Style == WorkspacePages.Theme("Ghost") && dontSaveButton.Foreground == WorkspacePages.Theme<System.Windows.Media.Brush>("DangerText") && DockPanel.GetDock(dontSaveButton) == Dock.Left;
            if (saveButton is not null) RaiseKey(saveButton, System.Windows.Input.Key.Escape);
            await Task.Delay(150);
            var keptByEscape = closing.IsCompleted && !await closing && main.SettingsOpen && OpenQuestion() is null && main.UnsavedPreferenceSections.SequenceEqual([PreferenceSections.LanguageAndPasting])
                && Settings.Load().Language == storedLanguage;
            Check(askedBeforeClosing && keptByEscape, $"closing Settings with an unsaved change asks “Save your changes?” over the blurred window, naming the section, with Save prominent at the bottom right and focused and a red “Don't save” at the bottom left; Escape returns to Settings with the change pending ({string.Join(" | ", questionTexts)})");
            var searchField = Element<System.Windows.Controls.TextBox>("SettingsSearchBox");
            RaiseKey(searchField, System.Windows.Input.Key.Escape); await Task.Delay(200);
            var askedByEscape = OpenQuestion() is not null;
            if (OpenQuestion() is { } escapeQuestion) PressLabelled(escapeQuestion, "Don't save");
            await Task.Delay(150);
            Check(askedByEscape && !main.SettingsOpen && OpenQuestion() is null && main.UnsavedPreferenceSections.Count == 0 && (string)((ComboBoxItem)languages.SelectedItem).Tag == storedLanguage
                && Settings.Load().Language == storedLanguage && behind.All(element => element.Effect is null && element.IsEnabled),
                "Escape in Settings asks too; Don't save discards the change, closes Settings and restores the window");
            main.ShowSettings(PreferenceSections.LanguageAndPasting); await Task.Delay(100);
            ChooseLanguage("es"); await Task.Delay(100);
            PressNamed("Close settings"); await Task.Delay(200);
            if (OpenQuestion() is { } closeQuestion) PressLabelled(closeQuestion, "Save");
            await Task.Delay(150);
            Check(!main.SettingsOpen && OpenQuestion() is null && Settings.Load().Language == "es" && main.UnsavedPreferenceSections.Count == 0,
                "the close button asks too; Save stores the change and closes Settings");
            ChooseLanguage(storedLanguage); SavePreferencesInWindow(); await Task.Delay(100);
            main.ShowSettings(PreferenceSections.LanguageAndPasting); await Task.Delay(100);
            var storedDays = Settings.Load().MeetingRetentionDays;
            FieldNamed("Meeting retention days").Text = "0"; ChooseLanguage("es"); await Task.Delay(100);
            main.ShowPage("History"); await Task.Delay(200);
            if (OpenQuestion() is { } pageQuestion) PressLabelled(pageQuestion, "Save");
            await Task.Delay(150);
            var refusedCard = main.Workspace.MeetingSettingsNotices.Notices.Find("preferences-not-saved");
            main.Workspace.MeetingSettingsNotices.BringIntoView(); await Task.Delay(150);
            Capture(main, Path.Combine(output, "settings-save-refused.png"));
            Check(main.SettingsOpen && main.SettingsSection == PreferenceSections.MeetingDefaults && refusedCard is { Kind: NoticeKind.Error, Title: "Preferences not saved", Text: "Keep completed meetings for: choose 1 to 3650 days." }
                && main.Workspace.MeetingSettingsNotices.Shown.Any(notice => notice.Key == "preferences-not-saved")
                && Settings.Load() is { } afterRefusal && afterRefusal.MeetingRetentionDays == storedDays && afterRefusal.Language == storedLanguage && Element<TextBlock>("PageTitle").Text != "History"
                && main.UnsavedPreferenceSections.SequenceEqual([PreferenceSections.LanguageAndPasting, PreferenceSections.MeetingDefaults]),
                $"opening a page from outside asks as well; when Save cannot store a value, Settings stays open at its section with the reason on an error card and nothing is stored ({refusedCard?.Text})");
            PressLabelled(preferenceActions, "Discard"); await Task.Delay(100);
            main.CloseSettings(); await Task.Delay(150);
            // Changes made outside Settings, on the Dictate page, show the same unsaved state there.
            main.ShowPage("Dictate"); await Task.Delay(150);
            var autoInsert = Element<System.Windows.Controls.CheckBox>("AutoInsert");
            autoInsert.IsChecked = !autoInsert.IsChecked; await Task.Delay(100);
            Check(!main.SettingsOpen && main.Workspace.MeetingSettingsNotices.Notices.Find("preferences-not-saved") is null && autoInsert.IsChecked == !Settings.Load().AutoInsert && preferenceActions.IsVisible && unsavedDot.IsVisible,
                "a change from the Dictate page is offered with Save preferences and Discard on that page and marked on the settings button");
            Capture(main, Path.Combine(output, "dictate-unsaved.png"));
            PressLabelled(preferenceActions, "Discard");
            Check(autoInsert.IsChecked == Settings.Load().AutoInsert && !preferenceActions.IsVisible && !unsavedDot.IsVisible, "Discard returns the change to the saved preference");
            // The invariant of MainWindow.settings: the object the whole app reads holds exactly what settings.json stores, field by field.
            var notStored = new List<string>();
            void ExpectStored(string step) => notStored.AddRange(Settings.Differences(main.SharedSettings, Settings.Load()).Select(name => $"{step}: {name}"));
            System.Windows.Controls.RadioButton NotesOption(string name) => Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: " + name);
            // (a) The reviewer's case: one retention day, a notes style, a profile switch and keep warm pending, a Save whose write fails, then
            // Discard. A completed meeting five days old must survive Delete expired meetings while 30 days are stored.
            var storedBefore = Settings.Load();
            var fiveDaysOld = new MeetingDocument { Title = "Five days old", Status = "Complete", Started = DateTimeOffset.UtcNow.AddDays(-5).AddMinutes(-1), Ended = DateTimeOffset.UtcNow.AddDays(-5) };
            screenshotArchive.Save(fiveDaysOld);
            main.ShowSettings(PreferenceSections.MeetingDefaults); await Task.Delay(100);
            FieldNamed("Meeting retention days").Text = "1"; NotesOption("Local model").IsChecked = true;
            profileGrid.SelectedIndex = 0; Switch("Profile literal mode").IsChecked = true; Switch("Keep speech model warm").IsChecked = !storedBefore.KeepModelWarm;
            await Task.Delay(150);
            // A folder in place of settings.json makes the write fail; the stored file waits beside it.
            var storedFile = Path.Combine(Settings.Root, "settings.json"); var asideFile = storedFile + ".aside";
            File.Move(storedFile, asideFile); Directory.CreateDirectory(storedFile);
            SavePreferencesInWindow(); await Task.Delay(150);
            var failedSave = (Text: main.Workspace.MeetingSettingsNotices.Notices.Find("preferences-not-saved")?.Text ?? "", main.SettingsOpen, main.UnsavedPreferenceSections.ToList());
            var ownWritesLeft = Directory.EnumerateFiles(Settings.Root).Count(Settings.IsOwnPartial);
            Directory.Delete(storedFile); File.Move(asideFile, storedFile);
            ExpectStored("failed save");
            PressLabelled(preferenceActions, "Discard"); await Task.Delay(150);
            ExpectStored("discard after the failed save");
            main.CloseSettings(); await Task.Delay(150);
            main.ShowPage("History"); await Task.Delay(150);
            PressInWindow("Delete expired meetings"); await Task.Delay(200);
            if (Descendants(main).OfType<Border>().FirstOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName) is { } expiryQuestion)
                PressLabelled(expiryQuestion, "Delete expired meetings");
            await Task.Delay(300);
            var fiveDaysKept = Directory.Exists(screenshotArchive.DirectoryFor(fiveDaysOld.Id));
            if (fiveDaysKept) screenshotArchive.Delete(fiveDaysOld.Id);
            Check(failedSave.Text.StartsWith("Nothing was saved, so nothing changed: ") && failedSave.SettingsOpen && ownWritesLeft == 0
                && failedSave.Item3.SequenceEqual([PreferenceSections.Profiles, PreferenceSections.MeetingDefaults, PreferenceSections.Models, PreferenceSections.SpeechRecognition])
                && fiveDaysKept && Settings.Load().MeetingRetentionDays == storedBefore.MeetingRetentionDays && notStored.Count == 0,
                $"a Save whose write fails changes nothing and keeps the changes unsaved, Discard leaves only stored values in the shared settings, and Delete expired meetings then keeps a five-day-old meeting under the stored {storedBefore.MeetingRetentionDays} days ({failedSave.Text}; {string.Join(", ", notStored)})");
            // (b) Don't save, and Write notes with a pending notes style and API model: the Notes tab still offers the stored destination.
            main.ShowSettings(PreferenceSections.MeetingDefaults); await Task.Delay(100);
            NotesOption("Local model").IsChecked = true; Element<System.Windows.Controls.TextBox>("RewriteModel").Text = "pending-model"; ChooseLanguage("es");
            await Task.Delay(100);
            Open("Supplier call"); await Task.Delay(150);
            var writeWhilePending = windowPanel.NotesState.WriteButton;
            var leaving = main.CloseSettingsAsync(); await Task.Delay(200);
            if (OpenQuestion() is { } unsavedQuestion) PressLabelled(unsavedQuestion, "Don't save");
            var leftWithoutSaving = await leaving;
            ExpectStored("don't save");
            Check(writeWhilePending == "Write notes with test-model at notes.invalid" && leftWithoutSaving && main.UnsavedPreferenceSections.Count == 0 && notStored.Count == 0,
                $"while another notes style and API model are only shown, Write notes offers the stored style and destination; Don't save leaves only stored values ({writeWhilePending}; {string.Join(", ", notStored)})");
            // (d) A profile chosen in the tray, and one whose shortcut starts a dictation, apply to the next dictations on their own copy:
            // nothing becomes unsaved, and Save leaves the global vocabulary, language, style and paste option as stored.
            var globalBefore = Settings.Load();
            var codeProfile = globalBefore.Profiles.Single(profile => profile.Name == "Code");
            bool GlobalKept(Settings stored) => stored.Vocabulary == globalBefore.Vocabulary && stored.Language == globalBefore.Language
                && stored.OutputStyle == globalBefore.OutputStyle && stored.AutoInsert == globalBefore.AutoInsert;
            main.SelectProfile(codeProfile); await Task.Delay(100);
            var trayPick = (Next: main.DictationSettings(), Unsaved: main.UnsavedPreferenceSections.Count, Shown: (string)((ComboBoxItem)languages.SelectedItem).Tag, Chip: main.ActiveProfileShown);
            // The chips show what the next dictation uses, the profile's, read-only and named as the profile's.
            var outputChip = Element<System.Windows.Controls.Button>("OutputChip");
            var chipsOfProfile = (Text: Element<TextBlock>("OutputChipText").Text, outputChip.IsEnabled, Name: System.Windows.Automation.AutomationProperties.GetName(outputChip));
            SavePreferencesInWindow(); await Task.Delay(100);
            var afterTraySave = Settings.Load();
            ExpectStored("tray profile");
            main.EndProfile(); await Task.Delay(100);
            var ended = (main.DictationSettings().Language, main.ActiveProfileShown);
            var chipsAfter = (Text: Element<TextBlock>("OutputChipText").Text, outputChip.IsEnabled);
            var storedStyle = globalBefore.OutputStyle switch { OutputStyle.Exact => "Exact", OutputStyle.Concise => "Concise (API)", OutputStyle.LocalPolish => "Local polish", _ => "Clean" };
            Check(trayPick.Next is { Language: "en", TechnicalMode: true, OutputStyle: OutputStyle.Exact } && trayPick.Unsaved == 0 && trayPick.Shown == globalBefore.Language
                && trayPick.Chip == "Profile Code active" && GlobalKept(afterTraySave) && ended == (globalBefore.Language, null) && notStored.Count == 0
                && chipsOfProfile == ("Exact", false, "Text style of the profile Code: Exact") && chipsAfter == (storedStyle, true),
                $"a profile chosen in the tray applies to the next dictation, named by its chip on Dictate, leaves nothing unsaved and the shown choices as they were; the text style chip shows the profile's style read-only; Save keeps the global vocabulary, language, style and paste option; the chip ends it ({trayPick.Chip}; {chipsOfProfile}; {chipsAfter}; {string.Join(", ", notStored)})");
            // As its shortcut starts one: the profile, then the dictation. A synthetic source stands in for the microphone, and the dictation
            // is cancelled before anything could be pasted.
            main.SelectProfile(codeProfile);
            await main.Dictation.StartAsync(true, null, _ => new SyntheticSource(new float[speech.Length]));
            await WaitAsync(() => main.Dictation.IsRecording, 3);
            var profileDictationRecorded = main.Dictation.IsRecording;
            await main.Dictation.CancelAsync(); await Task.Delay(100);
            var unsavedAfterDictation = main.UnsavedPreferenceSections.Count;
            SavePreferencesInWindow(); await Task.Delay(100);
            var afterDictationSave = Settings.Load();
            ExpectStored("profile dictation");
            main.EndProfile();
            Check(profileDictationRecorded && unsavedAfterDictation == 0 && GlobalKept(afterDictationSave) && notStored.Count == 0,
                $"after a dictation started with a profile, nothing is unsaved and Save keeps the global vocabulary, language, style and paste option ({string.Join(", ", notStored)})");
            // (c) A dictation with a pending language, then a meeting started and finished by the meeting shortcut's path while Settings
            // shows other, unsaved meeting defaults.
            main.ShowSettings(PreferenceSections.MeetingDefaults); await Task.Delay(100);
            ChooseLanguage("de");
            var defaultKeepBox = Descendants(main).OfType<System.Windows.Controls.CheckBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Retain processed audio by default");
            defaultKeepBox.IsChecked = !storedBefore.RetainMeetingAudio;
            var defaultLanguagesBox = Descendants(main).OfType<System.Windows.Controls.ComboBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Default meeting languages");
            defaultLanguagesBox.SelectedItem = defaultLanguagesBox.Items.OfType<ComboBoxItem>().Single(item => (string)item.Tag == "es");
            await Task.Delay(100);
            var dictationUses = main.DictationSettings().Language;
            ExpectStored("dictation snapshot");
            var pendingMeetingPanel = Descendants(main).OfType<MeetingsPanel>().Single();
            void StartPendingMeeting(MeetingCapture active) => active.Start([(new SyntheticSource(speech), "mic", "Pending defaults microphone")]);
            var pendingStarted = pendingMeetingPanel.PressShortcut(false, false, false, StartPendingMeeting);
            await WaitAsync(() => pendingMeetingPanel.IsCapturing && !pendingMeetingPanel.IsStarting, 5);
            ExpectStored("meeting start");
            var pendingAfterStart = main.UnsavedPreferenceSections.ToList();
            var keptPending = defaultKeepBox.IsChecked == !storedBefore.RetainMeetingAudio && (string)((ComboBoxItem)defaultLanguagesBox.SelectedItem).Tag == "es";
            await Task.Delay(1500);
            pendingMeetingPanel.PressShortcut(false, false, false, StartPendingMeeting);
            await WaitAsync(() => !pendingMeetingPanel.IsRecording, 300);
            var startedMeeting = screenshotArchive.List().First(document => document.MicrophoneDevice == "Pending defaults microphone");
            var storedAfterMeeting = Settings.Load();
            screenshotArchive.Delete(startedMeeting.Id);
            Check(pendingStarted.Action == MeetingShortcutAction.Start && dictationUses == "de" && storedAfterMeeting.Language == storedBefore.Language
                && storedAfterMeeting.RetainMeetingAudio == storedBefore.RetainMeetingAudio && storedAfterMeeting.MeetingLanguages == storedBefore.MeetingLanguages
                && startedMeeting.RetainAudio == storedBefore.RetainMeetingAudio && startedMeeting.Languages == storedBefore.MeetingLanguages
                && keptPending && pendingAfterStart.SequenceEqual([PreferenceSections.LanguageAndPasting, PreferenceSections.MeetingDefaults]) && notStored.Count == 0,
                $"a dictation uses the shown language without storing it, and a meeting started meanwhile records with the page's stored choices, stores only those and keeps the unsaved defaults shown as unsaved ({string.Join(", ", pendingAfterStart)}; {string.Join(", ", notStored)})");
            PressLabelled(preferenceActions, "Discard"); await Task.Delay(100);
            main.CloseSettings(); await Task.Delay(150);
            // The Meetings page of the window while a synthetic meeting records and while its transcript is finished, for the screenshots.
            main.ShowPage("Meetings"); await Task.Delay(150);
            var windowMeetings = Descendants(main).OfType<MeetingsPanel>().Single();
            var windowWave = Descendants(windowMeetings).OfType<MirroredWaveform>().Single();
            var windowTranscript = Descendants(windowMeetings).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Meeting transcript");
            // Long enough that both channels still play when the live preview shows, so both halves of the wave move in the screenshot.
            var longSpeech = Enumerable.Repeat(speech, 12).SelectMany(samples => samples).ToArray();
            SyntheticSource screenshotMicrophone = new(longSpeech), screenshotPlayback = new([.. new float[16000], .. longSpeech]);
            // Started while History is shown, as the meeting shortcut does: the fallback card on the Meetings page is pointed to from where the user is.
            main.ShowPage("History"); await Task.Delay(100);
            bool NotesInstallEnabled() => windowMeetings.NotesChoiceInstall.Button.IsEnabled && main.Workspace.ModelInstall(ModelCatalogue.NotesModel).IsEnabled;
            bool NotesInstallDisabled() => !windowMeetings.NotesChoiceInstall.Button.IsEnabled && !main.Workspace.ModelInstall(ModelCatalogue.NotesModel).IsEnabled;
            var notesInstallBeforeMeeting = NotesInstallEnabled();
            await windowMeetings.BeginAsync(active => active.Start([(screenshotMicrophone, "mic", "Synthetic microphone"), (screenshotPlayback, "system", "Synthetic playback")],
                MeetingCapture.PlaybackFallbackNotice("synthetic-headset", "synthetic-playback", "Synthetic playback")));
            await WaitAsync(() => windowTranscript.Text.Length > 0, 60);
            Check(notesInstallBeforeMeeting && windowMeetings.IsCapturing && NotesInstallDisabled(),
                "in the window, the install of the meeting notes model under Models is enabled before a meeting and disabled while it records");
            var fallbackPointer = main.StatusPointerShown; var meetingsMarker = main.MarkerOf("Meetings");
            var statusHidden = !Element<TextBlock>("Status").IsVisible;
            // Transcribe file while the meeting records is refused on Dictate; History is in view, so the status line points there.
            Element<System.Windows.Controls.Button>("ImportButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(100);
            var refusalPointer = main.StatusPointerShown; var dictateMarker = main.MarkerOf("Dictate");
            Capture(main, Path.Combine(output, "cards-status-pointer.png"));
            main.ShowPage("Dictate"); await Task.Delay(100);
            var dictateSeen = main.MarkerOf("Dictate") is null && !(main.StatusPointerShown?.Contains("See Dictate") ?? false);
            Check(fallbackPointer == "System audio from the Windows default. See Meetings." && meetingsMarker is not null && statusHidden
                && refusalPointer == "No file transcribed. See Dictate." && dictateMarker is not null && dictateSeen,
                $"in the window, a warning raised on a page not in view takes the place of the status line with its page and marks that page in the sidebar; seeing the page ends both ({fallbackPointer} | {refusalPointer})");
            main.ShowPage("History"); await Task.Delay(100);
            var hiddenWaveStopped = !windowWave.Running;
            main.ShowPage("Meetings"); await Task.Delay(100);
            var shownWaveRuns = windowWave.Running;
            var screenshotPeaks = new WavePeaks(windowWave);
            // The bars can already be up when sampling starts (a warm speech model shows the preview sooner); sample for a moment anyway.
            await Task.Delay(300);
            await WaitAsync(() => windowWave.MicrophoneHeight > .25 && windowWave.SystemAudioHeight > .25, 10);
            Capture(main, Path.Combine(output, "meetings-recording.png"));
            Capture(main, Path.Combine(output, "cards-meetings-warning.png"));
            var meetingsWarning = windowMeetings.MeetingNotices.Shown.SingleOrDefault();
            var meetingsFree = FreeWarningText(main);
            screenshotPeaks.Dispose();
            Check(meetingsWarning is { Kind: NoticeKind.Warning, Title: "System audio from the Windows default" } && meetingsFree.Length == 0
                && !Element<TextBlock>("Status").Text.Contains("Windows default"),
                $"in the window, a fallback to the Windows default output is a warning card above the composer while the meeting records; no warning text stands outside a card and the status line does not carry it ({string.Join(" | ", meetingsFree)})");
            Check(hiddenWaveStopped && shownWaveRuns, "the page wave stops while another page is shown during a meeting and moves again on the Meetings page (issue #42)");
            // Closing the window is refused while the meeting records.
            main.Close();
            await WaitAsync(() => windowMeetings.MeetingNotices.Notices.Find("meeting-close-refused") is not null && Element<TextBlock>("PageTitle").Text == "Meetings", 5);
            var closeRefused = windowMeetings.MeetingNotices.Notices.Find("meeting-close-refused") is not null;
            Descendants(windowMeetings).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Stop and finish").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await WaitAsync(() => Descendants(windowMeetings).OfType<TextBlock>().Any(block => block.Text.Contains("Detecting speech") || block.Text.Contains("Transcribing")), 120);
            Capture(main, Path.Combine(output, "meetings-finishing.png"));
            await WaitAsync(() => !windowMeetings.IsRecording, 600);
            // The cards end when the meeting does; the wait is bounded, so a stalled interface thread fails the check instead of hiding it.
            await WaitAsync(() => windowMeetings.MeetingNotices.Notices.Find("meeting-close-refused") is null && Element<NoticeStack>("DictateNotices").Notices.Find(DictationController.RefusedKey) is null, 5);
            Capture(main, Path.Combine(output, "meetings-finished.png"));
            Check(closeRefused && windowMeetings.MeetingNotices.Notices.Find("meeting-close-refused") is null && Element<NoticeStack>("DictateNotices").Notices.Find(DictationController.RefusedKey) is null
                && main.MarkerOf("Meetings") is null && main.MarkerOf("Dictate") is null,
                $"closing the window while a meeting records says “A meeting is recording”; once the meeting has ended, that card and the refused file transcription are gone, as their reason is (refused {closeRefused}; Meetings {string.Join(", ", windowMeetings.MeetingNotices.Shown.Select(notice => notice.Key))}; Dictate {string.Join(", ", Element<NoticeStack>("DictateNotices").Shown.Select(notice => notice.Key))}; markers {main.MarkerOf("Meetings")}/{main.MarkerOf("Dictate")}; pointer {main.StatusPointerShown})");
            Check(screenshotPeaks.Page.Microphone > .3 && windowTranscript.Text.Contains("You: ") && !windowMeetings.IsRecording,
                $"in the window, the Meetings page shows the live preview and a moving wave while recording, then the final transcript (page {screenshotPeaks.Page})");
            // The shortcut fields in the settings popup. Each field gets the keyboard focus a click gives it (a click raised in-process does
            // not focus it, as WPF reads the real mouse button state), and each key is routed through WPF to the field as a press is, without
            // SendInput. Modifiers come from the real keyboard state, so the combinations are function keys.
            main.ShowSettings(MainWindow.ShortcutsTitle); await Task.Delay(150);
            var shortcutBox = Element<System.Windows.Controls.TextBox>("ShortcutBox"); var meetingShortcutBox = Element<System.Windows.Controls.TextBox>("MeetingShortcutBox");
            var shortcutFeedback = Element<TextBlock>("ShortcutFeedback");
            // What a field is doing shows as a note card above the fields; the field's own line keeps its help.
            string? Hint(string key) => Element<NoticeStack>("ShortcutsNotices").Notices.Find(key) is { Kind: NoticeKind.Note } card ? card.Text : null;
            bool PressKey(System.Windows.Controls.TextBox field, System.Windows.Input.Key key)
            {
                var focused = field.Focus();
                field.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(field), Environment.TickCount, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                return focused && field.IsEnabled && field.IsVisible;
            }
            // A field that cannot be changed while a dictation runs looks locked and the section says why.
            dictationView.ShowRecording(); await Task.Delay(150);
            var shortcutsNotices = Element<NoticeStack>("ShortcutsNotices");
            bool LockedShown() => shortcutsNotices.Shown.Any(notice => notice is { Key: "shortcuts-locked", Kind: NoticeKind.Note });
            var lockedShown = !shortcutBox.IsEnabled && !meetingShortcutBox.IsEnabled && LockedShown() && !shortcutBox.Focus();
            Capture(main, Path.Combine(output, "settings-shortcuts-locked.png"));
            dictationView.ShowIdle(); await Task.Delay(150);
            Check(lockedShown && shortcutBox.IsEnabled && meetingShortcutBox.IsEnabled && !LockedShown(),
                "while a dictation runs the shortcut fields are locked, look it, and a note card in the Shortcuts section says why; afterwards they take keys again");
            // A registered shortcut pressed in a field arrives as its hotkey, not as keys of the field: the field says what it already is.
            var statusBefore = Element<TextBlock>("Status").Text;
            shortcutBox.Focus(); main.DictationShortcutPressed();
            var ownShortcut = Hint("shortcut-dictation-hint");
            main.ProfileShortcutPressed(Settings.Load().Profiles.Single(profile => profile.Name == "Code"));
            var profileShortcutTaken = shortcutsNotices.Shown.FirstOrDefault() is { } takenCard ? takenCard.Title + " " + takenCard.Text : "";
            Check(ownShortcut == "Ctrl + Alt + Space is the current dictation shortcut."
                && profileShortcutTaken == "Ctrl + Alt + P is already in use It is the shortcut of the profile “Code”. Choose a different combination for dictation."
                && shortcutBox.Text == "Ctrl + Alt + Space" && Element<TextBlock>("Status").Text == statusBefore && !main.Workspace.MeetingActive,
                $"a registered shortcut pressed in the dictation shortcut field says what it is and starts nothing ({ownShortcut} | {profileShortcutTaken})");
            // A key that cannot be a shortcut on its own: a warning card above the fields, while the field's line keeps its help.
            PressKey(shortcutBox, System.Windows.Input.Key.A); await Task.Delay(100);
            var rejected = shortcutsNotices.Shown.FirstOrDefault(); var helpKept = shortcutFeedback.Text.StartsWith("Starts and ends recording.");
            Capture(main, Path.Combine(output, "cards-shortcut-rejected.png"));
            var reachable = PressKey(shortcutBox, System.Windows.Input.Key.LeftCtrl);
            var modifierEcho = Hint("shortcut-dictation-hint"); var unchangedByModifier = shortcutBox.Text == "Ctrl + Alt + Space";
            reachable &= PressKey(shortcutBox, System.Windows.Input.Key.F8);
            var dictationCaptured = shortcutBox.Text == "F8" && Hint("shortcut-dictation-hint")?.StartsWith("Press Save preferences") == true && shortcutFeedback.Text.StartsWith("Starts and ends recording.") && !shortcutsNotices.Shown.Any(notice => notice.Key == "shortcut-dictation-rejected");
            Check(rejected is { Kind: NoticeKind.Warning, Title: "That combination cannot be the shortcut", Text: "A: Add Ctrl, Alt or Win, or use a function key; Shift alone is for typing." } && helpKept,
                $"a combination the dictation shortcut field cannot take is a warning card above the fields, the field's line keeps its help, and a valid combination clears the card ({rejected?.Text})");
            reachable &= PressKey(meetingShortcutBox, System.Windows.Input.Key.F9);
            var meetingCaptured = meetingShortcutBox.Text == "F9";
            var saveInPopup = preferenceActions.IsVisible && Descendants(settingsDialog).Contains(preferenceActions);
            Capture(main, Path.Combine(output, "settings-shortcuts-captured.png"));
            Element<System.Windows.Controls.Button>("SavePreferencesButton").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); await Task.Delay(150);
            var stored = Settings.Load();
            main.CloseSettings(); main.ShowPage("Dictate"); await Task.Delay(150);
            var dictateCaps = System.Windows.Automation.AutomationProperties.GetName(Element<WrapPanel>("ShortcutHint"));
            main.ShowPage("Meetings"); await Task.Delay(150);
            var meetingCaps = System.Windows.Automation.AutomationProperties.GetName(windowMeetings.ShortcutHint);
            // The verification instance registers no shortcut of its own; the stored ones are registered here and released at once.
            var registrable = new GlobalShortcuts(); registrable.Attach(new System.Windows.Interop.WindowInteropHelper(main).Handle);
            var registered = new[] { new ActivationShortcut(stored.ShortcutModifiers, stored.ShortcutKey), new ActivationShortcut(stored.MeetingShortcutModifiers, stored.MeetingShortcutKey) }
                .Select(shortcut => registrable.Register(shortcut, () => { }) is not null).ToArray();
            registrable.Dispose();
            Check(reachable && unchangedByModifier && modifierEcho == "… Keep holding and press the last key of the combination." && dictationCaptured && meetingCaptured && saveInPopup
                && stored is { ShortcutModifiers: 0, ShortcutKey: 0x77, MeetingShortcutModifiers: 0, MeetingShortcutKey: 0x78 } && dictateCaps == "Press F8 to dictate" && meetingCaps == "Press F9 to start a meeting"
                && registered.All(done => done),
                $"in the settings popup, each shortcut field takes the focus and a pressed combination, Save preferences stores both, and Dictate and Meetings show them as key caps; both register on this PC (dictate “{dictateCaps}”, meeting “{meetingCaps}”, registered {string.Join(", ", registered)})");
            main.ShowSettings(MainWindow.ShortcutsTitle); await Task.Delay(150);
            meetingShortcutBox.Focus(); main.DictationShortcutPressed();
            var dictationTakenInMeetingField = shortcutsNotices.Notices.Find("shortcut-meeting-rejected") is { } meetingCard ? meetingCard.Title + " " + meetingCard.Text : "";
            main.MeetingShortcutPressed();
            var ownMeetingShortcut = Hint("shortcut-meeting-hint");
            main.CloseSettings(); await Task.Delay(150);
            Check(dictationTakenInMeetingField == "F8 is already in use It is the dictation shortcut. Choose a different combination for meetings." && ownMeetingShortcut == "F9 is the current meeting shortcut."
                && !main.Workspace.MeetingActive,
                $"in the meeting shortcut field, the dictation shortcut and the meeting shortcut themselves are named instead of doing nothing ({dictationTakenInMeetingField} | {ownMeetingShortcut})");
            // Ctrl + Alt + M as the dictation shortcut, the owner's wish. Real modifiers cannot be held in-process, so the combination is passed
            // to the field as a press would; the warning asks the keyboard layouts of this PC through ToUnicodeEx.
            var germanLayout = WindowsKeyboardLayout.Installed(0x0407);
            Check(germanLayout is not null && new ActivationShortcut(3, 0x4d).AltGrCharacter(germanLayout) == "µ" && new ActivationShortcut(3, 0x51).AltGrCharacter(germanLayout) == "@"
                && new ActivationShortcut(3, 0x45).AltGrCharacter(germanLayout) == "€" && new ActivationShortcut(3, 0x41).AltGrCharacter(germanLayout) is null && ActivationShortcut.Default.AltGrCharacter(germanLayout) is null,
                "Windows' German layout says AltGr types µ with M, @ with Q and € with E, and nothing with A or Space");
            var activeTypesMicro = new ActivationShortcut(3, 0x4d).AltGrCharacter(WindowsKeyboardLayout.Current) == "µ";
            main.ShowSettings(MainWindow.ShortcutsTitle); await Task.Delay(150);
            main.ChooseShortcut(new ActivationShortcut(3, 0x4d)); await Task.Delay(100);
            var shortcutsCards = Element<NoticeStack>("ShortcutsNotices");
            string? AltGrCard() => shortcutsCards.Notices.Find("shortcut-dictation-altgr")?.Text;
            var altGrWarning = AltGrCard(); var fieldNote = Hint("shortcut-dictation-hint");
            var pendingCardStanding = shortcutsCards.Notices.Find("shortcut-dictation-altgr")?.Standing;
            shortcutsCards.BringIntoView(); await Task.Delay(150);
            Capture(main, Path.Combine(output, "settings-shortcut-altgr-warning.png"));
            SavePreferencesInWindow(); await Task.Delay(150);
            var warningAfterSave = AltGrCard(); var hintAfterSave = Hint("shortcut-dictation-hint");
            var savedCard = shortcutsCards.Notices.Find("shortcut-dictation-altgr");
            // As at the next start, the saved combination's card is raised while Settings is closed: no pointer and no dot (the owner, 2026-11-17:
            // "the altgr + m warning shows up again and settings has a yellow dot indicating the warning").
            main.CloseSettings(); main.ShowPage("Dictate"); await Task.Delay(150);
            if (savedCard is not null) { shortcutsCards.Notices.Clear(savedCard.Key); shortcutsCards.Notices.Raise(savedCard); await Task.Delay(100); }
            var quietAtStart = main.MarkerOf(MainWindow.ShortcutsTitle) is null && !(main.StatusPointerShown?.Contains("See " + MainWindow.ShortcutsTitle) ?? false)
                && (savedCard is null || shortcutsCards.Notices.Visible.Contains(savedCard));
            main.ShowSettings(MainWindow.ShortcutsTitle); await Task.Delay(150);
            Check(activeTypesMicro ? pendingCardStanding == false && savedCard is { Standing: true } && quietAtStart : savedCard is null,
                $"the AltGr card of a combination still pending is a warning; once saved it stays under Shortcuts as a standing card, which raised at start with Settings closed points nowhere and marks nothing (pending standing {pendingCardStanding}, saved standing {savedCard?.Standing})");
            const string MicroWarning = "Ctrl + Alt + M also is AltGr + M, which types µ on this keyboard; while Private Flow runs, that character cannot be typed with it.";
            var droppedMessage = MainWindow.DroppedShortcutsMessage([("the profile “Code”", new ActivationShortcut(1, 0x73)), ("the dictation shortcut", new ActivationShortcut(8, 0x4c))]);
            Check(droppedMessage == "Alt + F4 was removed from the profile “Code”: Alt + F4 closes the window in front; it cannot be a shortcut. Win + L was removed from the dictation shortcut: Windows reserves Win + L to lock the PC. Choose another one under Settings.",
                $"a shortcut cleared at start is named with its owner and the reason in the message the status line and the tray show ({droppedMessage})");
            Check(Settings.Load() is { ShortcutModifiers: 3, ShortcutKey: 0x4d } && main.UnsavedPreferenceSections.Count == 0
                && fieldNote == "Press Save preferences to apply. Your previous shortcut stays active until then." && hintAfterSave is null
                && (activeTypesMicro ? altGrWarning == MicroWarning && warningAfterSave == MicroWarning : altGrWarning is null && warningAfterSave is null),
                $"Ctrl + Alt + M is accepted and saved as the dictation shortcut; with a layout where AltGr + M types µ a warning card under Shortcuts says so before and after saving (active layout types µ: {activeTypesMicro}; {altGrWarning})");
            main.ChooseShortcut(ActivationShortcut.Default); SavePreferencesInWindow(); await Task.Delay(100);
            var altGrCardGone = AltGrCard() is null;
            main.CloseSettings(); await Task.Delay(150);
            Check(altGrCardGone, "the AltGr card ends once the dictation shortcut is one AltGr types nothing with");
            // The update notice covers the window with its own name, so the question Quit asks about unsaved preferences shows over it.
            var updateNotice = ConfirmationDialog.ShowNotice(main, "Updating Private Flow", "Stand-in notice; nothing is updated.");
            var questionOverNotice = ConfirmationDialog.AskToSaveAsync(main, "Your changes under Language and pasting are not saved yet.");
            await Task.Delay(150);
            var askedOverNotice = WindowOverlay.IsShown(main, ConfirmationDialog.AutomationName) && !questionOverNotice.IsCompleted;
            if (Descendants(main).OfType<Border>().FirstOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName) is { } overNotice)
                PressLabelled(overNotice, "Don't save");
            var answeredOverNotice = await questionOverNotice;
            updateNotice.Close(); await Task.Delay(100);
            Check(askedOverNotice && answeredOverNotice == UnsavedChoice.DontSave && !WindowOverlay.IsShown(main, ConfirmationDialog.NoticeAutomationName),
                "while the update notice covers the window, the question about unsaved preferences can still be asked over it and answered");
            // Learn correction gives the correction the language of the dictation it came from, also when that language was only shown.
            var correctionsBefore = Settings.Load().Corrections.Count;
            main.ShowPage("Dictate"); await Task.Delay(100);
            ChooseLanguage("de"); ((IDictationView)main).DictationPreferences(); ChooseLanguage(Settings.Load().Language);
            main.ShowPage("Vocabulary"); await Task.Delay(150);
            FieldNamed("Mistaken phrase").Text = "flow privat"; FieldNamed("Corrected phrase").Text = "Private Flow";
            PressInWindow("Learn correction"); await Task.Delay(150);
            var learnt = Settings.Load().Corrections.LastOrDefault();
            ExpectStored("learn correction");
            Check(Settings.Load().Corrections.Count == correctionsBefore + 1 && learnt is { Original: "flow privat", Language: "de" } && notStored.Count == 0,
                $"Learn correction gives the correction the language the last dictation started with, here German shown but not saved, and stores only the corrections ({learnt?.Language}; {string.Join(", ", notStored)})");
            // Speech recognition is not set up while the engine path names no file, and is again once valid paths are entered by hand and saved.
            main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(150);
            var enginePathField = Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Speech engine path");
            var realEngine = enginePathField.Text;
            enginePathField.Text = Path.Combine(output, "missing-whisper-cli.exe"); SavePreferencesInWindow(); await Task.Delay(100);
            var notSetUp = main.Workspace.SpeechNotices.Notices.Find("speech-not-set-up") is { Dismissable: false };
            enginePathField.Text = realEngine; SavePreferencesInWindow(); await Task.Delay(100);
            Check(notSetUp && main.Workspace.SpeechNotices.Notices.Find("speech-not-set-up") is null,
                "“Speech recognition is not set up” follows the files: it shows without an x while the engine path names no file, and goes once valid paths are entered by hand and saved");
            // Local polish setup with a stand-in for the script: the same bar as the meeting progress. Nothing is downloaded.
            var releaseSetup = new TaskCompletionSource();
            main.Workspace.RunSetup = async (_, _, token, progress) =>
            {
                progress?.Report("Downloading Qwen3-4B-Q4_K_M.gguf..."); progress?.Report("Local model download: 64%");
                await releaseSetup.Task.WaitAsync(token); return "Stand-in setup stopped; nothing was installed.";
            };
            PressInWindow("Set up Local polish");
            await WaitAsync(() => Descendants(main).OfType<SetupProgressView>().Any(view => view.Shown && view.Share == .64), 5);
            var polishView = Descendants(main).OfType<SetupProgressView>().FirstOrDefault(view => view.Shown);
            polishView?.BringIntoView(new Rect(0, 0, polishView.ActualWidth, polishView.ActualHeight + 120)); await Task.Delay(300);
            Capture(main, Path.Combine(output, "settings-polish-setup-progress.png"));
            var polishText = polishView?.Text;
            releaseSetup.SetResult();
            await WaitAsync(() => polishView?.Shown != true, 5);
            var speechNotices = main.Workspace.SpeechNotices;
            speechNotices.BringIntoView(new Rect(0, -28, speechNotices.ActualWidth, speechNotices.ActualHeight + 28)); await Task.Delay(300);
            Capture(main, Path.Combine(output, "cards-settings-setup-failure.png"));
            var setupFailure = speechNotices.Shown.FirstOrDefault();
            var settingsFree = FreeWarningText(main);
            main.Workspace.RunSetup = SpeechSetup.RunAsync; main.CloseSettings();
            Check(polishText?.StartsWith("Setting up Local polish · Downloading Qwen3-4B-Q4_K_M.gguf · 64%") == true && polishView?.Shown == false,
                $"Set up Local polish shows the script's step and share on the bar of the meeting progress and hides it when the setup ends ({polishText})");
            Check(setupFailure is { Kind: NoticeKind.Error, Title: "Local polish was not set up", Text: "Stand-in setup stopped; nothing was installed." } && Element<TextBlock>("Status").Text == "Ready" && settingsFree.Length == 0,
                $"a setup that fails is an error card under Speech recognition, the status line goes back to Ready instead of carrying the error, and the section has no free warning text ({string.Join(" | ", settingsFree)})");
            // One owner of settings.json: a setup the app runs hands back what it installed in its result file, and the window stores exactly
            // that like every other save. A save while the setup runs, and a save while its result waits to be stored, both keep the saved
            // paste option, retention and notes style and the installed paths, in the shared settings, the file and a fresh load.
            {
                var beforeSetups = Settings.Load();
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var resultFirst = false; var round = 0;
                string StandInEngine(int number) => Path.Combine(Settings.Root, "runtime", $"stand-in-round-{number}", "llama-server.exe");
                main.Workspace.RunSetup = async (_, arguments, token, _) =>
                {
                    var resultFile = arguments.SkipWhile(argument => argument != "-ResultFile").Skip(1).First();
                    var engine = StandInEngine(++round);
                    var installed = JsonSerializer.Serialize(new { LocalRewriteEnginePath = engine, LocalRewriteModelPath = Path.ChangeExtension(engine, ".gguf") });
                    if (resultFirst) File.WriteAllText(resultFile, installed);
                    await release.Task.WaitAsync(token);
                    if (!resultFirst) File.WriteAllText(resultFile, installed);
                    return null;
                };
                var autoInsertBox = Element<System.Windows.Controls.CheckBox>("AutoInsert");
                void ChangePreferences(bool autoInsert, int days, string notes)
                { autoInsertBox.IsChecked = autoInsert; FieldNamed("Meeting retention days").Text = days.ToString(); NotesOption(notes).IsChecked = true; }
                bool Holds(bool autoInsert, int days, MeetingNotesStyle notes, int number) =>
                    new[] { main.SharedSettings, Settings.Load() }.All(each => each.AutoInsert == autoInsert && each.MeetingRetentionDays == days && each.MeetingNotesStyle == notes
                        && each.LocalRewriteEnginePath == StandInEngine(number)) && Settings.Differences(main.SharedSettings, Settings.Load()).Count == 0;
                bool ResultWaiting() => Directory.EnumerateFiles(Settings.Root, "setup-result-*.json").Any();
                // (1) Saved while the setup runs; its result comes afterwards.
                main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(100);
                PressInWindow("Set up Local polish");
                await WaitAsync(() => main.Workspace.OperationActive, 5);
                main.ShowSettings(PreferenceSections.MeetingDefaults); await Task.Delay(100);
                ChangePreferences(!beforeSetups.AutoInsert, 7, "API"); SavePreferencesInWindow(); await Task.Delay(100);
                release.SetResult();
                await WaitAsync(() => !main.Workspace.OperationActive, 5); await Task.Delay(100);
                var saveDuringSetup = Holds(!beforeSetups.AutoInsert, 7, MeetingNotesStyle.Api, 1) && main.UnsavedPreferenceSections.Count == 0;
                // (2) The result is written and waits to be stored on the window's thread while a save runs there.
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); resultFirst = true;
                main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(100);
                PressInWindow("Set up Local polish");
                await WaitAsync(() => main.Workspace.OperationActive && ResultWaiting(), 5);
                main.ShowSettings(PreferenceSections.MeetingDefaults); await Task.Delay(100);
                ChangePreferences(beforeSetups.AutoInsert, 9, "Rules on this PC");
                release.SetResult(); SavePreferencesInWindow();
                await WaitAsync(() => !main.Workspace.OperationActive, 5); await Task.Delay(100);
                var saveDuringResult = Holds(beforeSetups.AutoInsert, 9, MeetingNotesStyle.Extractive, 2) && main.UnsavedPreferenceSections.Count == 0;
                var resultLeft = ResultWaiting();
                main.Workspace.RunSetup = SpeechSetup.RunAsync;
                main.SharedSettings.SaveAs(beforeSetups); main.Workspace.RefreshSpeechEngine(); await Task.Delay(100);
                PressLabelled(preferenceActions, "Discard"); main.CloseSettings(); await Task.Delay(100);
                Check(saveDuringSetup && saveDuringResult && !resultLeft && Settings.Differences(main.SharedSettings, Settings.Load()).Count == 0,
                    "a setup run by the app writes no settings itself: preferences saved while it runs, and a save while its result waits to be stored, keep the saved paste option, retention and notes style and the installed paths in the shared settings, the file and a fresh load; the result file is removed");
            }
            // Settings → Models with stand-ins for the setup script and empty stand-in model files in the isolated data folder; nothing is downloaded
            // and nothing outside the data folder is written or removed.
            {
                var workspace = main.Workspace;
                var original = Settings.Load();
                ListedModel Listed(string key) => ModelList.Find(key);
                string Touch(string path) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, ""); return path; }
                (string Key, string State, string RunsOn, bool InstallShown, bool RemoveShown) RowOf(string key) => workspace.ModelRowsShown.Single(row => row.Key == key);
                void PressModel(string name) => Descendants(main).OfType<System.Windows.Controls.Button>().First(button => System.Windows.Automation.AutomationProperties.GetName(button) == name)
                    .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                Border? ModelQuestion() => Descendants(main).OfType<Border>().FirstOrDefault(border => System.Windows.Automation.AutomationProperties.GetName(border) == ConfirmationDialog.AutomationName);
                void AnswerQuestion(string label) { if (ModelQuestion() is { } open) Descendants(open).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == label).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }
                // Nothing installed: speech paths that name no file, and no language model in the data folder.
                var nothing = original.Snapshot();
                nothing.EnginePath = Path.Combine(Settings.Root, "missing", "whisper-cli.exe"); nothing.ModelPath = Path.Combine(Settings.Root, "missing", "ggml-base.bin");
                nothing.MeetingModelPath = ""; nothing.VadModelPath = Path.Combine(Settings.Root, "missing", "ggml-silero-v6.2.0.bin");
                // Saved here directly, as a setup script's result would be mirrored, so the speech fields are shown again from the stored values.
                void Store(Settings next) { main.SharedSettings.SaveAs(next); workspace.RefreshSpeechEngine(); }
                var modelsPage = (ScrollViewer)workspace.Models.Content;
                Store(nothing);
                // The install of the notes model cancelled further up stays a card here; dismissed, so the screenshot shows the list.
                workspace.ModelsNotices.Notices.Dismiss(NotesStatus.InstallFailedKey);
                main.ShowSettings(MainWindow.ModelsTitle); modelsPage.ScrollToTop(); await Task.Delay(300);
                Capture(main, Path.Combine(output, "models-nothing-installed.png"));
                var nothingRows = workspace.ModelRowsShown;
                var nvidia = SpeechSetup.HasNvidiaGpu;
                Check(nothingRows.Count == ModelCatalogue.Models.Count && main.UnsavedPreferenceSections.Count == 0 && nothingRows.All(row => row.State == "Not installed" && row.InstallShown && !row.RemoveShown)
                    && nothingRows.All(row => row.RunsOn == ModelList.RunsOn(main.SharedSettings, Listed(row.Key), nvidia))
                    && RowOf(ModelCatalogue.SpeechTurbo).RunsOn.Contains("as Apply under Speech recognition sets up") && RowOf(ModelCatalogue.PolishModel).RunsOn.StartsWith("Runs on: processor"),
                    $"with nothing installed, Models lists all {nothingRows.Count} models of the catalogue as not installed, each with Install and where it would run");
                // "Recommended for this PC": the real detection and the real setup script's -Recommend answer, nothing downloaded.
                await workspace.RefreshRecommendationAsync();
                var direct = await WorkspacePages.ScriptRecommendationAsync([]);
                var shownRecommendation = workspace.RecommendedShown;
                var expectedSpeech = nvidia ? new SpeechRecommendation("cuda12", ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection, false)
                    : new SpeechRecommendation("cpu", ModelCatalogue.SpeechBase, "", ModelCatalogue.VoiceDetection, false);
                Check(direct == expectedSpeech && shownRecommendation.Recommendation?.Speech == direct
                    && shownRecommendation.Recommendation!.Transcription.Select(model => model.Key).SequenceEqual(new[] { direct.Model, direct.MeetingModel, direct.VoiceDetection }.Where(key => key.Length > 0))
                    && shownRecommendation.Facts is { Nvidia: var detectedNvidia, Memory: > 0, LogicalProcessors: > 0 } && detectedNvidia == nvidia && shownRecommendation.FactsText.Contains("Processor: "),
                    $"the card recommends exactly what setup-engine.ps1 with Automatic installs on this PC ({direct}), from facts read on this PC ({shownRecommendation.FactsText.Replace('\n', ' ')})");
                long Bytes(params string[] keys) => keys.Sum(key => ModelCatalogue.Model(key).Bytes);
                var engineBytes = ModelCatalogue.Runtime("whisper-" + direct!.Backend).Bytes;
                var transcriptionBytes = engineBytes + Bytes(new[] { direct.Model, direct.MeetingModel, direct.VoiceDetection }.Where(key => key.Length > 0).ToArray());
                // Below 8 GB of memory no local processing model is recommended (ModelList.LocalProcessingMemory), as on a small test VM.
                var localRecommended = shownRecommendation.Facts is not { Memory: > 0 and < ModelList.LocalProcessingMemory };
                var localBytes = localRecommended ? ModelList.DownloadBytes(Listed(ModelCatalogue.NotesModel), nvidia) : 0;
                Check(shownRecommendation.All == ($"Install recommended ({ModelList.Size(transcriptionBytes + localBytes)})", true)
                    && shownRecommendation.Transcription == ($"Install transcription models ({ModelList.Size(transcriptionBytes)})", true)
                    && shownRecommendation.Local == (localRecommended ? ($"Install local processing models ({ModelList.Size(localBytes)})", true) : ("No local processing model recommended", false)),
                    $"with nothing installed, Install recommended, Install transcription models and Install local processing models are offered with their sizes ({shownRecommendation.All.Label}; {shownRecommendation.Transcription.Label}; {shownRecommendation.Local.Label})");
                Capture(main, Path.Combine(output, "models-recommended-gpu.png"));
                Check(workspace.ModelCards.Where(card => card.Card == "Transcription models").Select(card => card.Key).SequenceEqual([ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechMedium, ModelCatalogue.SpeechBase, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection])
                    && workspace.ModelCards.Where(card => card.Card == "Local processing models").Select(card => card.Key).SequenceEqual([ModelCatalogue.PolishModel, ModelCatalogue.NotesModel]),
                    "the model list stands in two cards: transcription models (speech, final pass, voice detection) and local processing models (Local polish, meeting notes)");
                // Partly installed: the recommended transcription files and the engine build's marker, not the notes model.
                foreach (var key in new[] { direct.Model, direct.MeetingModel, direct.VoiceDetection }.Where(key => key.Length > 0)) Touch(ModelList.OwnFile(Listed(key)));
                var directEngineMarker = Path.Combine(Settings.Root, "runtime", direct.Backend, ".archive-sha256");
                Directory.CreateDirectory(Path.GetDirectoryName(directEngineMarker)!); File.WriteAllText(directEngineMarker, ModelCatalogue.Runtime("whisper-" + direct.Backend).Sha256);
                await workspace.RefreshRecommendationAsync();
                var partly = workspace.RecommendedShown;
                Touch(original.MeetingNotesEnginePath); Touch(original.MeetingNotesModelPath);
                Store(nothing); await Task.Delay(100);
                var complete = workspace.RecommendedShown;
                Check(partly.Transcription == ("Transcription models installed", false) && partly.Local == ($"Install local processing models ({ModelList.Size(localBytes)})", true)
                    && partly.All == ($"Install recommended ({ModelList.Size(localBytes)})", true)
                    && complete.All == ("Recommended models installed", false) && complete.Transcription == ("Transcription models installed", false) && complete.Local == ("Local processing model installed", false),
                    "partly installed, only the missing group and its size are offered; with everything installed, all three say so and are disabled");
                // Install recommended with stand-ins: the transcription group through Apply with Automatic, then the notes model, one after the other.
                foreach (var key in new[] { direct.Model, direct.MeetingModel, direct.VoiceDetection }.Where(key => key.Length > 0)) File.Delete(ModelList.OwnFile(Listed(key)));
                File.Delete(directEngineMarker); File.Delete(original.MeetingNotesEnginePath); File.Delete(original.MeetingNotesModelPath);
                await workspace.RefreshRecommendationAsync();
                var order = new List<string>();
                var releaseTranscription = new TaskCompletionSource();
                workspace.RunSetup = async (script, arguments, token, progress) =>
                {
                    if (script == "setup-rewrite.ps1")
                    {
                        order.Add(script + " " + string.Join(" ", arguments.Take(2))); progress?.Report("Local model download: 50%");
                        Touch(original.MeetingNotesEnginePath); Touch(original.MeetingNotesModelPath);
                        return null;
                    }
                    order.Add(script + " " + string.Join(" ", arguments.Take(4)));
                    progress?.Report("Downloading " + ModelCatalogue.Model(direct.Model).File + "...");
                    await releaseTranscription.Task.WaitAsync(token);
                    foreach (var key in new[] { direct.Model, direct.MeetingModel, direct.VoiceDetection }.Where(key => key.Length > 0)) Touch(ModelList.OwnFile(Listed(key)));
                    File.WriteAllText(directEngineMarker, ModelCatalogue.Runtime("whisper-" + direct.Backend).Sha256);
                    return null;
                };
                PressInWindow($"Install recommended ({ModelList.Size(transcriptionBytes + localBytes)})");
                // The verification's speech paths were entered by hand, so the press first asks before Automatic replaces them.
                await Task.Delay(300);
                var switchQuestion = ModelQuestion() is { } askedSwitch ? Descendants(askedSwitch).OfType<TextBlock>().Select(text => text.Text).ToList() : [];
                var startedBeforeAnswer = order.Count;
                AnswerQuestion("Switch and install");
                await WaitAsync(() => workspace.RecommendedShown.Progress is not null, 5); await Task.Delay(300);
                modelsPage.ScrollToTop(); await Task.Delay(200);
                Capture(main, Path.Combine(output, "models-recommended-installing.png"));
                var during = workspace.RecommendedShown;
                releaseTranscription.SetResult();
                await WaitAsync(() => workspace.RecommendedShown.All.Label == "Recommended models installed", 10);
                var after = workspace.RecommendedShown;
                workspace.RunSetup = SpeechSetup.RunAsync;
                Check(switchQuestion.FirstOrDefault() == "Switch speech recognition to Automatic?" && switchQuestion.Skip(1).FirstOrDefault()?.Contains("the engine and model paths entered by hand") == true && startedBeforeAnswer == 0
                    && order.Count == 2 && order[0] == "setup-engine.ps1 -Backend auto -Model auto" && order[1] == "setup-rewrite.ps1 -Part notes"
                    && during.All == ("Cancel installing", true) && !during.Transcription.Enabled && !during.Local.Enabled
                    && during.Progress!.StartsWith("Installing the recommended models, 1 of 2, transcription · Setting up speech recognition · Downloading")
                    && after.All == ("Recommended models installed", false) && after.Progress is null && Settings.Differences(main.SharedSettings, Settings.Load()).Count == 0,
                    $"with speech paths entered by hand, Install recommended asks in the window, naming what changes, before anything starts; then it runs the transcription group (Apply with Automatic) and the notes model, one at a time with the step named on one bar and Cancel in its place, and ends installed ({string.Join(" | ", switchQuestion)}; {string.Join(" → ", order)}; {during.Progress})");
                File.Delete(directEngineMarker);
                // A PC without an NVIDIA GPU, with a stand-in for the detection: the script's own answer for the processor build.
                workspace.DetectPc = async () => (await PcDetection.DetectAsync()) with { Graphics = "Stand-in graphics without NVIDIA", Nvidia = false, GpuTotal = null, GpuFree = null };
                workspace.RecommendSpeech = () => WorkspacePages.ScriptRecommendationAsync(["-Backend", "cpu"]);
                File.Delete(original.MeetingNotesEnginePath); File.Delete(original.MeetingNotesModelPath);
                await workspace.RefreshRecommendationAsync(); modelsPage.ScrollToTop(); await Task.Delay(300);
                Capture(main, Path.Combine(output, "models-recommended-no-gpu.png"));
                var noGpu = workspace.RecommendedShown;
                workspace.DetectPc = PcDetection.DetectAsync; workspace.RecommendSpeech = () => WorkspacePages.ScriptRecommendationAsync([]);
                await workspace.RefreshRecommendationAsync();
                Check(noGpu.Recommendation?.Speech is { Backend: "cpu", Model: "base", MeetingModel: "" } && noGpu.FactsText.Contains("no NVIDIA GPU, so the processor builds are used")
                    && noGpu.LocalText.Contains("Without an NVIDIA GPU it runs on the processor"),
                    $"without an NVIDIA GPU the card recommends the processor build with base and no final-pass model, as the script decides, and says the meeting notes model runs on the processor ({noGpu.LocalText})");
                foreach (var model in ModelList.All.Where(model => model.Use is ModelUse.Speech or ModelUse.VoiceDetection)) File.Delete(ModelList.OwnFile(model));
                // Everything installed: stand-in files for every model and both llama.cpp builds, and the speech settings pointing at them.
                foreach (var model in ModelList.All.Where(model => model.Use is ModelUse.Speech or ModelUse.VoiceDetection)) Touch(ModelList.OwnFile(model));
                Touch(original.LocalRewriteEnginePath); Touch(original.LocalRewriteModelPath);
                Touch(original.MeetingNotesEnginePath); Touch(original.MeetingNotesModelPath); Touch(Path.Combine(Path.GetDirectoryName(original.MeetingNotesEnginePath)!, "ggml-cuda.dll"));
                // The verified engine build's marker, so the recommendation counts the engine as in place too.
                File.WriteAllText(directEngineMarker, ModelCatalogue.Runtime("whisper-" + direct.Backend).Sha256);
                await workspace.RefreshRecommendationAsync();
                var everything = original.Snapshot();
                everything.ModelPath = ModelList.OwnFile(Listed(ModelCatalogue.SpeechTurbo)); everything.MeetingModelPath = ModelList.OwnFile(Listed(ModelCatalogue.SpeechFinalPass));
                everything.VadModelPath = ModelList.OwnFile(Listed(ModelCatalogue.VoiceDetection));
                Store(everything); modelsPage.ScrollToTop(); await Task.Delay(300);
                Capture(main, Path.Combine(output, "models-everything-installed.png"));
                var everythingRows = workspace.ModelRowsShown;
                Check(workspace.RecommendedShown.All == ("Recommended models installed", false) && everythingRows.All(row => row.State.StartsWith("Installed.") && !row.InstallShown && row.RemoveShown && row.RunsOn == ModelList.RunsOn(main.SharedSettings, Listed(row.Key), nvidia))
                    && RowOf(ModelCatalogue.SpeechTurbo).State == "Installed. Dictation uses it." && RowOf(ModelCatalogue.SpeechFinalPass).State == "Installed. The final pass of meetings and files uses it."
                    && RowOf(ModelCatalogue.SpeechMedium).State == "Installed. Nothing uses it now." && RowOf(ModelCatalogue.NotesModel).RunsOn.StartsWith("Runs on: GPU, as many layers as fit")
                    && RowOf(ModelCatalogue.SpeechTurbo).RunsOn.StartsWith(SpeechSetup.Processor(everything.EnginePath) == "GPU" ? "Runs on: GPU (the CUDA build" : "Runs on: processor"),
                    "with everything installed, each model says it is installed, what uses it and where it runs, from the engine build and the layers configured, and offers Remove");
                // Install: the shared bar while it runs, Cancel installing in its place, then installed.
                File.Delete(ModelList.OwnFile(Listed(ModelCatalogue.SpeechMedium)));
                var releaseInstall = new TaskCompletionSource();
                var setupArguments = new List<string>();
                workspace.RunSetup = async (script, arguments, token, progress) =>
                {
                    setupArguments.Add(script + " " + string.Join(" ", arguments.TakeWhile(argument => argument != "-ResultFile")));
                    progress?.Report("Downloading ggml-medium.bin...");
                    await releaseInstall.Task.WaitAsync(token);
                    Touch(ModelList.OwnFile(Listed(ModelCatalogue.SpeechMedium))); progress?.Report("Whisper medium is downloaded and verified.");
                    return null;
                };
                Store(everything); await Task.Delay(150);
                var typedEngineField = Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Speech engine path");
                var typedEngine = Path.Combine(Settings.Root, "typed", "whisper-cli.exe"); typedEngineField.Text = typedEngine;
                PressModel("Install Whisper medium");
                await WaitAsync(() => workspace.ModelProgress(ModelCatalogue.SpeechMedium).Shown, 5); await Task.Delay(300);
                var mediumProgress = workspace.ModelProgress(ModelCatalogue.SpeechMedium);
                mediumProgress.BringIntoView(new Rect(0, -160, mediumProgress.ActualWidth, mediumProgress.ActualHeight + 320)); await Task.Delay(300);
                Capture(main, Path.Combine(output, "models-install-running.png"));
                var running = (Row: RowOf(ModelCatalogue.SpeechMedium), Text: mediumProgress.Text,
                    Cancel: Descendants(main).OfType<System.Windows.Controls.Button>().Any(button => button.Content as string == "Cancel installing" && System.Windows.Automation.AutomationProperties.GetName(button) == "Cancel installing Whisper medium" && button.IsEnabled),
                    OthersLocked: !Descendants(main).OfType<System.Windows.Controls.Button>().Single(button => System.Windows.Automation.AutomationProperties.GetName(button) == "Remove Whisper base").IsEnabled);
                // One operation at a time: while the stand-in install is held, every other install, removal and Apply is pressed; none starts and
                // none cancels the one that runs.
                void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var others = Descendants(main).OfType<System.Windows.Controls.Button>().Where(button => System.Windows.Automation.AutomationProperties.GetName(button) is { } name
                    && (name.StartsWith("Install ") && name != "Install Whisper medium" || name.StartsWith("Remove ") || name.StartsWith("Install recommended") || name.StartsWith("Install transcription")
                        || name.StartsWith("Install local processing")) || button.Content as string is "Apply" or "Set up Local polish" or "Run check").ToList();
                var othersDisabled = others.All(button => !button.IsEnabled);
                foreach (var button in others) Click(button);
                await Task.Delay(300);
                var gate = (Calls: setupArguments.Count, NotesInstalls: setupArguments.Count(call => call.StartsWith("setup-rewrite.ps1 -Part notes")), StillRunning: workspace.ModelProgress(ModelCatalogue.SpeechMedium).Shown,
                    Pressed: others.Count, NotesRefused: workspace.ModelsNotices.Notices.Find("notes-install-refused")?.Text, Question: ModelQuestion() is not null);
                releaseInstall.SetResult();
                await WaitAsync(() => !workspace.ModelProgress(ModelCatalogue.SpeechMedium).Shown, 5); await Task.Delay(150);
                workspace.ModelsNotices.Notices.Clear("notes-install-refused"); workspace.ModelsNotices.Notices.Clear("model-remove-refused");
                Check(gate is { Calls: 1, NotesInstalls: 0, StillRunning: true, Question: false } && gate.Pressed >= 12 && othersDisabled && gate.NotesRefused?.StartsWith("A setup or a check runs first") == true
                    && setupArguments.SequenceEqual(["setup-engine.ps1 -Download medium"]),
                    $"while one install runs, every other Install, Remove, Install recommended, Apply, Set up Local polish and Run check waits disabled; pressed anyway, none starts and none cancels the one that runs ({gate.Pressed} pressed)");
                var typedKept = typedEngineField.Text == typedEngine && main.UnsavedPreferenceSections.Contains(PreferenceSections.SpeechRecognition);
                PressLabelled(preferenceActions, "Discard"); await Task.Delay(100);
                Check(setupArguments.SequenceEqual(["setup-engine.ps1 -Download medium"]) && running.Row.State == "Installing" && running.Text.StartsWith("Installing Whisper medium · Downloading ggml-medium.bin")
                    && running.Cancel && running.OthersLocked && RowOf(ModelCatalogue.SpeechMedium) is { State: "Installed. Nothing uses it now.", InstallShown: false, RemoveShown: true }
                    && Settings.Differences(main.SharedSettings, Settings.Load()).Count == 0 && typedKept && typedEngineField.Text == main.SharedSettings.EnginePath,
                    $"Install runs the setup script for that one model only, shows its step on the shared bar with Cancel installing in place of Install and the other actions locked, and ends with the model installed and no setting changed; an engine path typed and not saved stays in its field and unsaved until Discard ({running.Text})");
                // Cancel: the setup ends, its partial download goes, the model stays not installed.
                var basePartial = ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase)) + ".partial";
                File.Delete(ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase)));
                workspace.RunSetup = async (_, _, token, progress) => { progress?.Report("Downloading ggml-base.bin..."); Touch(basePartial); await Task.Delay(Timeout.Infinite, token); return null; };
                Store(everything); await Task.Delay(150);
                PressModel("Install Whisper base");
                await WaitAsync(() => workspace.ModelProgress(ModelCatalogue.SpeechBase).Shown && File.Exists(basePartial), 5);
                PressModel("Cancel installing Whisper base");
                await WaitAsync(() => !workspace.ModelProgress(ModelCatalogue.SpeechBase).Shown, 5); await Task.Delay(150);
                Check(!File.Exists(basePartial) && RowOf(ModelCatalogue.SpeechBase) is { State: "Not installed", InstallShown: true, RemoveShown: false }
                    && Element<TextBlock>("Status").Text == "Installing was cancelled. The partial download was removed; files already verified stay.",
                    "Cancel installing stops the setup, removes its partial download and leaves the model not installed");
                // Cancelling Apply removes the partial downloads of the engine build and of a speech model it was fetching.
                var applyPartials = new[] { ModelList.OwnFile(Listed(ModelCatalogue.SpeechFinalPass)) + ".partial", Path.Combine(Settings.Root, "downloads", ModelCatalogue.Runtime("whisper-cuda12").File + ".partial") };
                workspace.RunSetup = async (_, _, token, progress) => { progress?.Report("Downloading ggml-large-v3.bin..."); foreach (var partial in applyPartials) Touch(partial); await Task.Delay(Timeout.Infinite, token); return null; };
                main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(150);
                PressInWindow("Apply");
                await WaitAsync(() => workspace.OperationActive && applyPartials.All(File.Exists), 5);
                await workspace.StopOperationsAsync(); await Task.Delay(150);
                var applyCancelled = Element<TextBlock>("Status").Text;
                main.ShowSettings(MainWindow.ModelsTitle); await Task.Delay(150);
                Check(applyPartials.All(partial => !File.Exists(partial)) && applyCancelled == "Setup cancelled. The partial download was removed; files already verified stay.",
                    $"cancelling Apply removes the partial downloads of the engine build and of the speech model it was fetching ({applyCancelled})");
                // Remove asks first: Cancel keeps the file, Remove deletes it.
                var mediumFile = ModelList.OwnFile(Listed(ModelCatalogue.SpeechMedium));
                PressModel("Remove Whisper medium"); await Task.Delay(200);
                var removeQuestion = ModelQuestion() is { } asked ? Descendants(asked).OfType<TextBlock>().Select(text => text.Text).ToList() : [];
                AnswerQuestion("Cancel"); await Task.Delay(150);
                var keptByCancel = File.Exists(mediumFile);
                PressModel("Remove Whisper medium"); await Task.Delay(200);
                AnswerQuestion("Remove"); await Task.Delay(300);
                Check(removeQuestion.SequenceEqual(["Remove Whisper medium?", "Deletes the model file (0 MB) from this PC. Install downloads it again."]) && keptByCancel && !File.Exists(mediumFile)
                    && RowOf(ModelCatalogue.SpeechMedium) is { State: "Not installed", InstallShown: true, RemoveShown: false } && Element<TextBlock>("Status").Text == "Whisper medium removed; 0 MB freed.",
                    $"Remove asks in the window; Cancel keeps the model and Remove deletes its file ({string.Join(" | ", removeQuestion)})");
                // Refusals: the speech model dictation uses, and any model while a setup runs, each with the reason; nothing is asked or deleted.
                var turboFile = ModelList.OwnFile(Listed(ModelCatalogue.SpeechTurbo));
                PressModel("Remove Whisper large-v3-turbo (8-bit)"); await Task.Delay(150);
                var activeRefused = workspace.ModelsNotices.Notices.Find("model-remove-refused");
                var askedForActive = ModelQuestion() is not null;
                Touch(ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase)));
                var releaseBusy = new TaskCompletionSource();
                workspace.RunSetup = async (_, _, token, _) => { await releaseBusy.Task.WaitAsync(token); return null; };
                PressModel("Install Whisper medium");
                await WaitAsync(() => workspace.ModelProgress(ModelCatalogue.SpeechMedium).Shown, 5);
                PressModel("Remove Whisper base"); await Task.Delay(150);
                var busyRefused = workspace.ModelsNotices.Notices.Find("model-remove-refused");
                releaseBusy.SetResult();
                await WaitAsync(() => !workspace.ModelProgress(ModelCatalogue.SpeechMedium).Shown, 5);
                Check(activeRefused is { Kind: NoticeKind.Warning, Title: "Whisper large-v3-turbo (8-bit) was not removed", Text: "Dictation uses it." } && activeRefused.Detail!.EndsWith("press Apply first.")
                    && !askedForActive && File.Exists(turboFile)
                    && busyRefused is { Text: "A setup or a check is running." } && File.Exists(ModelList.OwnFile(Listed(ModelCatalogue.SpeechBase))),
                    $"Remove refuses the speech model dictation uses and any model while a setup runs, with the reason on a card and without asking ({activeRefused?.Text}; {busyRefused?.Detail ?? busyRefused?.Text})");
                workspace.RunSetup = SpeechSetup.RunAsync;
                // The model for Local polish: a preference of this section, used by the next dictation once saved; the meeting notes model
                // without its files offers its install next to the choice.
                File.Delete(original.MeetingNotesModelPath);
                Store(everything); await Task.Delay(150);
                ChooseIn("Local polish model", nameof(LocalPolishModel.MeetingNotesModel)); await Task.Delay(150);
                var choiceUnsaved = main.UnsavedPreferenceSections.ToList();
                var beforeSave = main.DictationSettings().LocalPolishModel;
                var notesInstallNextToChoice = Descendants(main).OfType<System.Windows.Controls.Button>().Any(button => button.IsVisible && button.Content as string == $"Install ({ModelList.Size(MeetingNotesModel.DownloadBytes)})"
                    && Descendants(main.Workspace.Models.Content).Contains(button) && button != workspace.ModelInstall(ModelCatalogue.NotesModel));
                var polishChoiceBox = Descendants(main).OfType<System.Windows.Controls.ComboBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Local polish model");
                polishChoiceBox.BringIntoView(new Rect(0, -260, polishChoiceBox.ActualWidth, polishChoiceBox.ActualHeight + 400)); await Task.Delay(300);
                Capture(main, Path.Combine(output, "models-polish-choice-missing.png"));
                SavePreferencesInWindow(); await Task.Delay(150);
                var afterSave = (Stored: Settings.Load().LocalPolishModel, Next: main.DictationSettings().LocalPolishModel, main.UnsavedPreferenceSections.Count);
                Check(choiceUnsaved.SequenceEqual([PreferenceSections.Models]) && beforeSave == LocalPolishModel.PolishModel && notesInstallNextToChoice
                    && afterSave == (LocalPolishModel.MeetingNotesModel, LocalPolishModel.MeetingNotesModel, 0),
                    "choosing the meeting notes model for Local polish is an unsaved change of Models that offers the missing model's install next to it; once saved, the next dictation's snapshot uses it");
                ChooseIn("Local polish model", nameof(LocalPolishModel.PolishModel)); SavePreferencesInWindow(); await Task.Delay(100);
                // The Local model notes style without its model offers the install there too.
                var localOption = Descendants(main).OfType<System.Windows.Controls.RadioButton>().Single(option => System.Windows.Automation.AutomationProperties.GetName(option) == "Meeting notes: Local model");
                localOption.IsChecked = true; await Task.Delay(150);
                var notesChoiceInstall = windowPanel.NotesChoiceInstall.Button is { IsVisible: true } offeredButton && offeredButton.Content as string == $"Install the meeting notes model ({SpeechRuntime.Gigabytes(MeetingNotesModel.DownloadBytes)} GB)";
                PressLabelled(preferenceActions, "Discard"); await Task.Delay(100);
                Check(notesChoiceInstall && !windowPanel.NotesChoiceInstall.Button.IsVisible, "the Local model notes style chosen without the meeting notes model says so and offers its install next to it");
                // Local polish as the text style with the meeting notes model chosen and saved for it and missing: 0.5.0's card names that model's
                // size and leads here, where it installs that model. A stand-in replaces the install, so nothing is downloaded.
                main.ShowSettings(MainWindow.ModelsTitle); await Task.Delay(100);
                ChooseIn("Local polish model", nameof(LocalPolishModel.MeetingNotesModel)); SavePreferencesInWindow(); await Task.Delay(100);
                main.ShowSettings(PreferenceSections.TextStyle); await Task.Delay(100);
                Element<System.Windows.Controls.RadioButton>("OutputLocal").IsChecked = true; await Task.Delay(150);
                var notesPolishCard = Element<NoticeStack>("OutputNotices").Notices.Find("local-polish-not-set-up");
                Capture(main, Path.Combine(output, "models-text-style-card.png"));
                var installsFromCard = 0;
                workspace.RunSetup = (script, arguments, _, _) =>
                {
                    if (script == "setup-rewrite.ps1" && arguments.Contains("notes")) installsFromCard++;
                    return Task.FromResult<string?>("Stand-in install stopped; nothing was installed.");
                };
                notesPolishCard?.Action?.Invoke(); await Task.Delay(300);
                var ledHere = main.SettingsSection == MainWindow.ModelsTitle;
                workspace.RunSetup = SpeechSetup.RunAsync;
                // The installed Local polish model chosen and not saved does not reach the next dictation, so the card stays.
                ChooseIn("Local polish model", nameof(LocalPolishModel.PolishModel)); await Task.Delay(150);
                var cardWithUnsavedChoice = Element<NoticeStack>("OutputNotices").Notices.Find("local-polish-not-set-up") is not null
                    && main.DictationSettings().LocalPolishModel == LocalPolishModel.MeetingNotesModel;
                PressLabelled(preferenceActions, "Discard"); await Task.Delay(100);
                var endedByDiscard = Element<NoticeStack>("OutputNotices").Notices.Find("local-polish-not-set-up") is null;
                ChooseIn("Local polish model", nameof(LocalPolishModel.PolishModel)); SavePreferencesInWindow(); await Task.Delay(100);
                Check(notesPolishCard is { Kind: NoticeKind.Warning, Title: "Local polish is not set up", Standing: true } && notesPolishCard.Action?.Label == LocalRewriter.SetupLabel(ModelCatalogue.Model(ModelCatalogue.NotesModel).Bytes)
                    && notesPolishCard.Action.Label == "Set up Local polish (2,740 MB)" && ledHere && installsFromCard == 1 && cardWithUnsavedChoice && endedByDiscard,
                    $"with the meeting notes model saved for Local polish and missing, the Local polish card names that model's size, opens Models and installs it there; choosing the installed Local polish model without saving keeps the card, as the next dictation still uses the stored choice; Discard of the text style ends it ({notesPolishCard?.Action?.Label})");
                // Back to the data folder's own state: the stand-ins go, the original settings return.
                foreach (var model in ModelList.All.Where(model => model.Use is ModelUse.Speech or ModelUse.VoiceDetection)) File.Delete(ModelList.OwnFile(model));
                foreach (var path in new[] { original.LocalRewriteEnginePath, original.LocalRewriteModelPath, original.MeetingNotesEnginePath, original.MeetingNotesModelPath, Path.Combine(Path.GetDirectoryName(original.MeetingNotesEnginePath)!, "ggml-cuda.dll"), directEngineMarker })
                    File.Delete(path);
                main.SharedSettings.SaveAs(original); main.Workspace.RefreshSpeechEngine(); main.CloseSettings(); await Task.Delay(150);
            }
            // Local polish chosen without its model (the owner, 2026-11-17: "using local polish needs to be set up seperately from the local notes model,
            // so it falls back to clean instead"): a card on Dictate, pointed to, and a standing one under Text style offer the setup with its
            // size; pressed, it runs under Speech recognition with its bar. A stand-in replaces the script, so nothing is downloaded.
            var polishModelBefore = Settings.Load().LocalRewriteModelPath;
            if (LocalRewriter.PolishInstalled(Settings.Load()))
            {
                main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(150);
                Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Local polish model").Text = Path.Combine(output, "missing-polish.gguf");
                SavePreferencesInWindow(); await Task.Delay(100);
            }
            main.CloseSettings(); main.ShowPage("Dictate"); await Task.Delay(100);
            var styleBefore = Settings.Load().OutputStyle;
            Element<System.Windows.Controls.RadioButton>("OutputLocal").IsChecked = true; await Task.Delay(150);
            var polishCard = Element<NoticeStack>("DictateNotices").Notices.Find("local-polish-not-set-up");
            Capture(main, Path.Combine(output, "cards-local-polish.png"));
            main.ShowSettings("Text style"); await Task.Delay(150);
            var textStyleCard = Element<NoticeStack>("OutputNotices").Notices.Find("local-polish-not-set-up");
            Capture(main, Path.Combine(output, "settings-text-style-local-polish.png"));
            var releasePolishSetup = new TaskCompletionSource();
            main.Workspace.RunSetup = async (_, _, token, progress) =>
            {
                progress?.Report("Downloading Qwen3-4B-Q4_K_M.gguf..."); progress?.Report("Local model download: 12%");
                await releasePolishSetup.Task.WaitAsync(token); return "Stand-in setup stopped; nothing was installed.";
            };
            polishCard?.Action?.Invoke();
            await WaitAsync(() => Descendants(main).OfType<SetupProgressView>().Any(view => view.Shown && view.Share == .12), 5);
            var setupFromCard = main.SettingsSection == MainWindow.ModelsTitle && Descendants(main).OfType<SetupProgressView>().Any(view => view.Shown && view.Share == .12);
            releasePolishSetup.SetResult();
            await WaitAsync(() => !Descendants(main).OfType<SetupProgressView>().Any(view => view.Shown), 5);
            main.Workspace.RunSetup = SpeechSetup.RunAsync;
            // The stored style again, so closing Settings has nothing unsaved to ask about.
            Element<System.Windows.Controls.RadioButton>(styleBefore switch { OutputStyle.Exact => "OutputExact", OutputStyle.Concise => "OutputConcise", OutputStyle.LocalPolish => "OutputLocal", _ => "OutputClean" }).IsChecked = true;
            await Task.Delay(100); main.CloseSettings(); main.ShowPage("Dictate"); await Task.Delay(100);
            var polishCardsEnded = styleBefore == OutputStyle.LocalPolish || Element<NoticeStack>("DictateNotices").Notices.Find("local-polish-not-set-up") is null && Element<NoticeStack>("OutputNotices").Notices.Find("local-polish-not-set-up") is null;
            Check(polishCard is { Kind: NoticeKind.Warning, Standing: false, Title: "Local polish is not set up", Action.Label: "Set up Local polish (2,497 MB)" }
                && textStyleCard is { Standing: true, Action.Label: "Set up Local polish (2,497 MB)" } && setupFromCard && polishCardsEnded && main.Workspace.ProfilesNotices.Notices.Find("local-polish-not-set-up") is { Standing: true },
                $"Local polish chosen without its model shows a card on Dictate and a standing one under Text style and Profiles per app with “Set up Local polish (2,497 MB)”, which opens Models and runs the setup there with its bar; another style ends the cards (setup from the card {setupFromCard})");
            if (Settings.Load().LocalRewriteModelPath != polishModelBefore)
            {
                main.ShowSettings(MainWindow.SpeechRecognitionTitle); await Task.Delay(150);
                Descendants(main).OfType<System.Windows.Controls.TextBox>().Single(box => System.Windows.Automation.AutomationProperties.GetName(box) == "Local polish model").Text = polishModelBefore;
                SavePreferencesInWindow(); await Task.Delay(100); main.CloseSettings(); await Task.Delay(100);
            }
            main.ShowPage("Dictate"); await Task.Delay(100);
            // The Dictate page with an error card. A real microphone failure needs a microphone, so the card the controller raises then is
            // raised here the same way; a second report of the same condition stays one card.
            main.ShowPage("Dictate"); await Task.Delay(100);
            var dictateNotices = Element<NoticeStack>("DictateNotices");
            for (var report = 0; report < 2; report++)
                dictationView.Notices.Report(new(DictationController.FailedKey, NoticeKind.Error, "The microphone could not start", "Another app is using it exclusively. Close that app or choose another microphone."));
            await Task.Delay(150);
            Capture(main, Path.Combine(output, "cards-dictate-error.png"));
            var dictateCard = dictateNotices.Notices.Find(DictationController.FailedKey); var failedCards = dictateNotices.Shown.Count(notice => notice.Key == DictationController.FailedKey);
            var dictateFree = FreeWarningText(main);
            var liveSetting = dictateNotices.Cards.Count == 1 ? System.Windows.Automation.AutomationProperties.GetLiveSetting(dictateNotices.Cards[0]) : default;
            dictateNotices.DismissButtons[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(dictateCard is { Kind: NoticeKind.Error } && failedCards == 1 && !dictateNotices.Shown.Any(notice => notice.Key == DictationController.FailedKey) && dictateFree.Length == 0
                && liveSetting == System.Windows.Automation.AutomationLiveSetting.Assertive && System.Windows.Automation.AutomationProperties.GetName(dictateNotices) == "Dictate notices",
                $"on the Dictate page an error is one card, announced assertively, the same notice raised twice stays one card and its x hides it ({string.Join(" | ", dictateFree)})");
            foreach (var document in screenshotArchive.List().Where(document => document.MicrophoneDevice == "Synthetic microphone")) screenshotArchive.Delete(document.Id);
            main.Hide();
            foreach (var document in screenshotArchive.List().Where(document => historyTitles.Contains(document.Title))) screenshotArchive.Delete(document.Id);
            Check(App.DiagnosticMode, "diagnostic UI disables microphone, loopback, shortcuts and insertion");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, Metadata = HealthChecks.Metadata(settings) }, new JsonSerializerOptions { WriteIndented = true }));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = ex.Message }));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static async Task GenerateAsync(string wav)
    {
        var script = Path.Combine(Path.GetDirectoryName(wav)!, "generate.ps1");
        try
        {
            await File.WriteAllTextAsync(script, "param([string]$Output)\nAdd-Type -AssemblyName System.Speech\n$taskSynth = New-Object System.Speech.Synthesis.SpeechSynthesizer\n$taskSynth.SetOutputToWaveFile($Output)\n$taskSynth.Speak('This is a private voice test. Please do not delete the file. The number is forty two.')\n$taskSynth.Dispose()");
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-NoProfile", "-File", script, "-Output", wav }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!; await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException("Synthetic generator failed.");
        }
        finally { File.Delete(script); }
    }
    // The Vocabulary page with its saved text "Private Flow,Qwen ; Lumen", typed with uneven separators into the former single field.
    // Keys are routed events raised on the element, never real input.
    private static async Task VerifyVocabularyListAsync(MainWindow main, string output, Action<bool, string> check)
    {
        const string stored = "Private Flow,Qwen ; Lumen";
        T Element<T>(string name) where T : class => (T)main.FindName(name);
        var list = Element<System.Windows.Controls.ListBox>("VocabularyList");
        var entry = Element<System.Windows.Controls.TextBox>("VocabularyEntry");
        var add = Element<System.Windows.Controls.Button>("AddTermsButton");
        var delete = Element<System.Windows.Controls.Button>("DeleteTermsButton");
        var save = Element<System.Windows.Controls.Button>("SavePreferencesButton");
        var hint = Element<TextBlock>("VocabularyHint");
        var count = Element<TextBlock>("VocabularyCount");
        var unsaved = Element<FrameworkElement>("PreferenceActions");
        string[] Terms() => list.Items.Cast<VocabularyRow>().Select(row => row.Text).ToArray();
        VocabularyRow Row(string term) => list.Items.Cast<VocabularyRow>().First(row => row.Text == term);
        void Press(UIElement target, System.Windows.Input.Key key) => target.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), 0, key) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
        void Click(System.Windows.Controls.Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        void Discard() => Descendants(unsaved).OfType<System.Windows.Controls.Button>().Single(button => button.Content as string == "Discard").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        string Name(DependencyObject element) => System.Windows.Automation.AutomationProperties.GetName(element);
        System.Windows.Controls.CheckBox Box(VocabularyRow row) => VisualDescendants((Visual)list.ItemContainerGenerator.ContainerFromItem(row)).OfType<System.Windows.Controls.CheckBox>().Single();
        // A real click on a row's box ticks it and then raises its Click.
        void TickBox(VocabularyRow row) { var box = Box(row); box.IsChecked = true; box.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); }

        check(Terms().SequenceEqual(["Private Flow", "Qwen", "Lumen"]) && count.Text == "3 terms" && !delete.IsEnabled && !unsaved.IsVisible
            && Name(entry) == "New vocabulary term" && Name(add) == "Add to vocabulary" && Name(list) == "Vocabulary" && Name(delete) == "Delete selected"
            && list.SelectionMode == System.Windows.Controls.SelectionMode.Extended,
            "the Vocabulary page lists the saved terms in order with their number, a named entry field, Add, list and a disabled Delete selected");

        var autoInsert = Element<System.Windows.Controls.CheckBox>("AutoInsert");
        autoInsert.IsChecked = !autoInsert.IsChecked; Click(save);
        var untouchedKept = Settings.Load().Vocabulary == stored && main.DictationSettings().Vocabulary == stored;
        autoInsert.IsChecked = !autoInsert.IsChecked; Click(save);
        check(untouchedKept && Settings.Load().Vocabulary == stored && !unsaved.IsVisible, $"Save with another preference changed keeps an untouched vocabulary byte for byte (\"{Settings.Load().Vocabulary}\")");

        entry.Text = "  Zephyr "; Click(add);
        // Keyboard focus can only be checked while the verification window is the active one.
        var focusKept = main.IsActive ? entry.IsKeyboardFocused : (bool?)null;
        var byButton = Terms().Last() == "Zephyr" && entry.Text == "" && focusKept != false;
        entry.Text = "Okafor"; Press(entry, System.Windows.Input.Key.Enter);
        check(byButton && Terms().SequenceEqual(["Private Flow", "Qwen", "Lumen", "Zephyr", "Okafor"]) && entry.Text == "" && count.Text == "5 terms" && unsaved.IsVisible,
            $"Add and Enter append the trimmed entry, clear the field and keep it focused (focus {focusKept?.ToString() ?? "not checked, window inactive"}), and offer Save preferences and Discard");

        entry.Text = "Lindqvist, Priya;okafor\r\nJoaquín, C#/.NET"; Click(add);
        check(Terms().Skip(5).SequenceEqual(["Lindqvist", "Priya", "Joaquín", "C#/.NET"]) && hint.IsVisible && hint.Text == "Added 4. “Okafor” is already in the list.",
            $"a pasted list separated by commas, semicolons and line breaks adds each new term and names the one already listed ({hint.Text})");
        entry.Text = "qwen"; Press(entry, System.Windows.Input.Key.Enter);
        var duplicateNamed = hint.IsVisible && hint.Text == "“Qwen” is already in the list." && Terms().Length == 9;
        entry.Text = "   "; Click(add);
        check(duplicateNamed && Terms().Length == 9 && entry.Text == "   ", "a duplicate in another case is not added and is named; an empty entry does nothing");
        entry.Clear();

        entry.Text = "Claude Code, Codex, Grafana"; Click(add); await Task.Delay(150);
        list.ScrollIntoView(list.Items[0]); list.UpdateLayout();
        list.SelectedItems.Clear();
        // Selected through the list's own selection and through a row's box; no mouse input is sent.
        list.SelectedItems.Add(Row("Qwen"));
        Box(Row("Okafor")).IsChecked = true;
        list.SelectedItems.Add(Row("Codex"));
        await Task.Delay(150);
        Capture(main, Path.Combine(output, "vocabulary-selected.png"));
        check(Terms().Length == 12 && list.SelectedItems.Count == 3 && Box(Row("Qwen")).IsChecked == true && delete.IsEnabled && delete.Content as string == "Delete selected (3)" && Name(delete) == "Delete selected (3)",
            "selecting three of twelve terms marks their boxes and enables Delete selected (3)");
        Press(list, System.Windows.Input.Key.Delete);
        var byKey = Terms().SequenceEqual(["Private Flow", "Lumen", "Zephyr", "Lindqvist", "Priya", "Joaquín", "C#/.NET", "Claude Code", "Grafana"]) && list.SelectedItems.Count == 0 && !delete.IsEnabled;
        list.SelectedItems.Add(Row("Lumen")); list.SelectedItems.Add(Row("Grafana")); Click(delete);
        check(byKey && Terms().SequenceEqual(["Private Flow", "Zephyr", "Lindqvist", "Priya", "Joaquín", "C#/.NET", "Claude Code"]) && count.Text == "7 terms",
            "the Delete key and Delete selected remove exactly the selected terms and keep the order of the rest");
        // Ctrl+A in a list runs the list's own select-all command, which WPF keeps internal; ApplicationCommands.SelectAll is not bound there.
        var selectAll = (System.Windows.Input.RoutedCommand)typeof(System.Windows.Controls.ListBox).GetField("SelectAllCommand", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        selectAll.Execute(null, list);
        var selectedByCommand = list.SelectedItems.Count;
        Click(delete); await Task.Delay(150);
        Capture(main, Path.Combine(output, "vocabulary-empty.png"));
        check(selectedByCommand == 7 && Terms().Length == 0 && count.Text == "No names or terms yet" && !list.IsVisible && unsaved.IsVisible,
            $"Ctrl+A selects every term and deleting them all leaves an empty list ({selectedByCommand} selected, {Terms().Length} left, \"{count.Text}\")");
        Discard();
        check(Terms().SequenceEqual(["Private Flow", "Qwen", "Lumen"]) && !unsaved.IsVisible && !hint.IsVisible && Settings.Load().Vocabulary == stored && main.DictationSettings().Vocabulary == stored,
            "Discard restores the saved list and its stored text exactly");

        entry.Text = "Hollis";
        var pendingMarked = unsaved.IsVisible && main.DictationSettings().Vocabulary == "Private Flow, Qwen, Lumen, Hollis" && Terms().Length == 3;
        Discard();
        check(pendingMarked && entry.Text == "" && !unsaved.IsVisible && main.DictationSettings().Vocabulary == stored,
            "text typed but not added marks the preferences unsaved and reaches a dictation as the single field did; Discard clears it");

        // A chosen profile with an empty vocabulary, as a new profile has: its dictations use that, while the list, the unsaved state and
        // the stored text stay, and Save stores the list.
        main.SelectProfile(main.SharedSettings.Profiles.Single(profile => profile.Name == "Code"));
        var profileApplied = Terms().SequenceEqual(["Private Flow", "Qwen", "Lumen"]) && !unsaved.IsVisible && main.DictationSettings().Vocabulary == "";
        Click(save);
        main.EndProfile();
        check(profileApplied && Settings.Load().Vocabulary == stored && main.DictationSettings().Vocabulary == stored,
            "a chosen profile with an empty vocabulary leaves the list and the unsaved state alone, and Save keeps the stored text exactly");

        // A stored list with duplicate terms, shown as at a start.
        var withDuplicates = main.SharedSettings.Snapshot(); withDuplicates.Vocabulary = "Alpha, Middle, Alpha, Tail, Alpha";
        main.SharedSettings.SaveAs(withDuplicates); Discard(); await Task.Delay(150);
        var loadedWithDuplicates = Terms().SequenceEqual(["Alpha", "Middle", "Alpha", "Tail", "Alpha"]);
        list.SelectedItems.Clear(); list.SelectedItems.Add(list.Items[2]); Click(delete);
        check(loadedWithDuplicates && Terms().SequenceEqual(["Alpha", "Middle", "Tail", "Alpha"]) && list.SelectedItems.Count == 0,
            $"a stored list keeps its duplicate terms, and deleting the second of them removes that row only ({string.Join(", ", Terms())})");
        entry.Text = "alpha"; Click(add);
        var duplicateRefused = Terms().Length == 4 && hint.Text == "“Alpha” is already in the list.";
        await Task.Delay(100);
        list.SelectedItems.Add(list.Items[0]); TickBox((VocabularyRow)list.Items[3]);
        var focusAfterBox = System.Windows.Input.Keyboard.FocusedElement as UIElement;
        var focusOnRow = main.IsActive ? focusAfterBox is System.Windows.Controls.ListBoxItem { Content: VocabularyRow { Text: "Alpha" } } : (bool?)null;
        Press(focusAfterBox ?? (UIElement)list.ItemContainerGenerator.ContainerFromIndex(3), System.Windows.Input.Key.Delete);
        check(duplicateRefused && focusOnRow != false && Terms().SequenceEqual(["Middle", "Tail"]) && list.SelectedItems.Count == 0,
            $"a new duplicate is still refused, a click on a row's box moves the focus to its row (focus {focusOnRow?.ToString() ?? "not checked, window inactive"}), and Delete there removes the two selected Alpha rows only");
        list.SelectedItems.Add(list.Items[0]);
        entry.Text = "typing"; entry.Focus();
        var editingActive = main.IsActive;
        Press(entry, System.Windows.Input.Key.Delete);
        var keptWhileTyping = Terms().Length == 2;
        entry.Clear(); entry.Focus();
        Press(entry, System.Windows.Input.Key.Delete);
        check(!editingActive || keptWhileTyping && Terms().SequenceEqual(["Tail"]),
            $"Delete in the entry field edits its text while it has some and deletes the selected rows once it is empty (checked: {editingActive})");
        Discard();
        var withDuplicatesBack = Terms().SequenceEqual(["Alpha", "Middle", "Alpha", "Tail", "Alpha"]);
        var restored = main.SharedSettings.Snapshot(); restored.Vocabulary = stored; main.SharedSettings.SaveAs(restored); Discard();
        check(withDuplicatesBack && Terms().SequenceEqual(["Private Flow", "Qwen", "Lumen"]) && main.DictationSettings().Vocabulary == stored, "Discard returns to the stored list, duplicates included");

        ((IDictationView)main).ShowRecording();
        var lockedWhileRecording = !entry.IsEnabled && !add.IsEnabled && !list.IsEnabled;
        ((IDictationView)main).ShowIdle();
        check(lockedWhileRecording && entry.IsEnabled && list.IsEnabled, "the vocabulary list is locked while a dictation runs, like the other preferences");

        entry.Text = "Zephyr"; Click(add); entry.Text = "Okafor";
        Click(save);
        check(Settings.Load().Vocabulary == "Private Flow, Qwen, Lumen, Zephyr, Okafor" && Terms().Last() == "Okafor" && entry.Text == "" && !unsaved.IsVisible
            && main.DictationSettings().Vocabulary == "Private Flow, Qwen, Lumen, Zephyr, Okafor",
            "Save adds the text still in the entry field, stores a changed list with \", \" between terms, and a dictation takes it");
        entry.Text = "Lindqvist"; Click(add); Click(save);
        // The single field would have stored the same text after typing ", Lindqvist" at its end.
        check(Settings.Load().Vocabulary == "Private Flow, Qwen, Lumen, Zephyr, Okafor, Lindqvist" && main.DictationSettings().Vocabulary == "Private Flow, Qwen, Lumen, Zephyr, Okafor, Lindqvist",
            "a term added to a saved list is stored as the text the single field held");

        entry.Text = string.Join("\n", Enumerable.Range(1, 194).Select(index => $"Project {index:000}")); Click(add); await Task.Delay(200);
        Capture(main, Path.Combine(output, "vocabulary-many.png"));
        var containers = VisualDescendants(list).OfType<System.Windows.Controls.ListBoxItem>().Count();
        check(Terms().Length == 200 && count.Text == "200 terms" && list.ActualHeight <= 420 && list.ItemContainerGenerator.ContainerFromItem(Row("Project 194")) is System.Windows.Controls.ListBoxItem { IsVisible: true },
            $"two hundred terms scroll within the bounded list and the last added term is shown ({containers} rows created)");
        Discard();
        check(Terms().Length == 6, "Discard drops the unsaved terms");
    }
    // The longest the interface thread may be busy at once while a meeting records, stops and finishes.
    private const double ResponsiveBound = 0.25;
    // The longest from a press to the pill drawn, in seconds, with synthetic sources; the press showed it after about a second before.
    private const double PressToPillBound = 0.25;
    private static async Task WaitAsync(Func<bool> condition, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(100);
    }
    private static bool PillShown() => System.Windows.Application.Current.Windows.OfType<MeetingHud>().Any(window => window.IsVisible);
    private static bool ProgressWindowShown() => System.Windows.Application.Current.Windows.OfType<MeetingProgressWindow>().Any(window => window.IsVisible);
    private static float[] ReadSamples(string path)
    {
        using var reader = new AudioFileReader(path);
        var source = WhisperEngine.Mono16Khz(reader); var samples = new List<float>(); var buffer = new float[16000]; int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
        return samples.ToArray();
    }
    // Measures the longest time the interface thread could not handle input: a timer at input priority every 50 ms.
    private sealed class DispatcherWatchdog : IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer timer = new(System.Windows.Threading.DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(50) };
        private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        private double last;
        public double LongestGap { get; private set; }
        public DispatcherWatchdog()
        {
            timer.Tick += (_, _) => { var now = clock.Elapsed.TotalSeconds; LongestGap = Math.Max(LongestGap, now - last); last = now; };
            timer.Start();
        }
        public void Dispose() => timer.Stop();
    }
    // The tallest bar each half of the meeting pill and of the page wave reached while sampled.
    private sealed class WavePeaks : IDisposable
    {
        private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(20) };
        public (double Microphone, double System) Pill { get; private set; }
        public (double Microphone, double System) Page { get; private set; }
        public bool PillSeen { get; private set; }
        private readonly MirroredWaveform page;
        // Sampled also at the start and at the end: a wait that is over at once would otherwise see no tick at all.
        public WavePeaks(MirroredWaveform page)
        {
            this.page = page;
            timer.Tick += (_, _) => Sample();
            Sample(); timer.Start();
        }
        private void Sample()
        {
            Page = (Math.Max(Page.Microphone, page.MicrophoneHeight), Math.Max(Page.System, page.SystemAudioHeight));
            if (System.Windows.Application.Current.Windows.OfType<MeetingHud>().FirstOrDefault(window => window.IsVisible) is not { } pill) return;
            PillSeen = true;
            Pill = (Math.Max(Pill.Microphone, pill.Wave.MicrophoneHeight), Math.Max(Pill.System, pill.Wave.SystemAudioHeight));
        }
        public void Dispose() { Sample(); timer.Stop(); }
    }
    // A device that records nothing: it says whether it was started and whether it was closed.
    private sealed class HeldBackSource : IAudioSource
    {
        private readonly System.Threading.Channels.Channel<AudioChunk> chunks = System.Threading.Channels.Channel.CreateUnbounded<AudioChunk>();
        public System.Threading.Channels.ChannelReader<AudioChunk> Chunks => chunks.Reader;
        public event Action? Failed { add { } remove { } }
        public double Seconds => 0;
        public bool Started { get; private set; }
        public bool Disposed { get; private set; }
        public bool TryReadLevel(out float level) { level = 0; return false; }
        public void Start() => Started = true;
        public Task StopAsync() { chunks.Writer.TryComplete(); return Task.CompletedTask; }
        public void Dispose() { Disposed = true; chunks.Writer.TryComplete(); }
    }
    // Plays samples as a capture would, ten times faster than real time: chunks of at most 20 s and a level for every 25 ms of audio.
    private sealed class SyntheticSource(float[] samples, int stopDelayMilliseconds = 0) : IAudioSource
    {
        private const int Step = 8000, LevelSamples = 400;
        private readonly System.Threading.Channels.Channel<AudioChunk> chunks = System.Threading.Channels.Channel.CreateUnbounded<AudioChunk>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<float> levels = new();
        private readonly TaskCompletionSource stopRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task feeding = Task.CompletedTask;
        private int position;
        public System.Threading.Channels.ChannelReader<AudioChunk> Chunks => chunks.Reader;
        public event Action? Failed { add { } remove { } }
        public double Seconds => Volatile.Read(ref position) / 16000.0;
        public bool Played { get; private set; }
        public bool TryReadLevel(out float level) => levels.TryDequeue(out level);
        public void Start() => feeding = Task.Run(FeedAsync);
        public async Task StopAsync() { stopRequested.TrySetResult(); await feeding; await Task.Delay(stopDelayMilliseconds); }
        public void Dispose() => stopRequested.TrySetResult();
        private async Task FeedAsync()
        {
            var chunkStart = 0;
            while (position < samples.Length && !stopRequested.Task.IsCompleted)
            {
                var end = Math.Min(samples.Length, position + Step);
                for (var block = position; block + LevelSamples <= end; block += LevelSamples)
                {
                    var squares = 0.0;
                    for (var sample = block; sample < block + LevelSamples; sample++) squares += samples[sample] * samples[sample];
                    levels.Enqueue((float)Math.Sqrt(squares / LevelSamples));
                }
                position = end;
                if (position - chunkStart >= AudioRecorder.ChunkSamples) { chunks.Writer.TryWrite(new(samples[chunkStart..position], chunkStart / 16000.0)); chunkStart = position; }
                await Task.Delay(50);
            }
            Played = true;
            await stopRequested.Task;
            if (position > chunkStart) chunks.Writer.TryWrite(new(samples[chunkStart..position], chunkStart / 16000.0));
            chunks.Writer.TryComplete();
        }
    }
    private static string[] KeysOf(IReadOnlyList<Notice> notices) => [.. notices.Select(notice => notice.Key)];
    // Visible text in a warning or error colour that stands outside a notice card; the "unfinished" badge of a meeting in the History list
    // is a label in its own frame, not a message.
    private static string[] FreeWarningText(DependencyObject root)
    {
        var colours = new[] { "WarningText", "ErrorText", "DangerText" }.Select(key => ((System.Windows.Media.SolidColorBrush)WorkspacePages.Theme<System.Windows.Media.Brush>(key)).Color).ToArray();
        return [.. Descendants(root).OfType<TextBlock>()
            .Where(block => block.IsVisible && block.Text != "unfinished" && block.Foreground is System.Windows.Media.SolidColorBrush brush && colours.Contains(brush.Color)
                && !Ancestors(block).OfType<NoticeStack>().Any())
            .Select(block => block.Text)];
    }
    private static IEnumerable<DependencyObject> Ancestors(DependencyObject element)
    {
        for (var parent = LogicalTreeHelper.GetParent(element); parent is not null; parent = LogicalTreeHelper.GetParent(parent)) yield return parent;
    }
    private static IEnumerable<DependencyObject> VisualDescendants(Visual parent) =>
        Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(parent)).Select(index => VisualTreeHelper.GetChild(parent, index)).OfType<Visual>().SelectMany(child => VisualDescendants(child).Prepend(child));
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) =>
        LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().SelectMany(child => Descendants(child).Prepend(child));
    private static void Capture(Window window, string path)
    {
        // The window's content without the frame, which renders as a white strip, at its layout size: a brush over the whole drawing would
        // shrink the picture when anything draws outside the window.
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var area = new Rect(0, 0, content.ActualWidth, content.ActualHeight);
        var frame = new DrawingVisual();
        using (var drawing = frame.RenderOpen()) { drawing.DrawRectangle(window.Background, null, area); drawing.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, area); }
        var bitmap = new RenderTargetBitmap((int)area.Width, (int)area.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(frame);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
