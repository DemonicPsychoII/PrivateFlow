using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Shell;
using System.Windows.Threading;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using ListBox = System.Windows.Controls.ListBox;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using MenuItem = System.Windows.Controls.MenuItem;
using Brush = System.Windows.Media.Brush;

namespace PrivateFlow.Windows;

internal sealed partial class MeetingsPanel : DockPanel, IDisposable
{
    private const string IdleText = "Meeting recorder idle — no audio is being captured.";
    private readonly Settings settings;
    private readonly Action<string> report;
    // A tray notification, for messages that must reach the user while the window is hidden; true marks a problem.
    private readonly Action<string, bool> notify;
    private readonly Action open;
    private readonly MeetingArchive archive;
    private readonly MicrophoneDevices devices;
    private readonly TextBox title = MeetingTitle();
    private readonly TextBox transcript = new()
    {
        Style = WorkspacePages.Theme("BareTextBox"), MinHeight = 136, MaxHeight = 320, FontSize = 15, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        Tag = "The live transcript appears here while the meeting records. Stop and finish replaces it with a final pass over the saved audio. Names and terms from the Vocabulary page help spell them right."
    };
    // The microphone and the output device are the shared selections of the audio settings; the chips change them there.
    private readonly System.Windows.Controls.ComboBox microphones, playbackDevices;
    private readonly Action openShortcutSettings;
    private bool recordMicrophone = true, recordSystemAudio = true, keepAudio;
    private string selectedLanguages;
    // The defaults the page's choices last took; a choice still equal to them was not changed on the page and follows newly saved defaults.
    private (bool Keep, string Languages) followedDefaults;
    private readonly TextBlock microphoneChipText = new(), systemAudioChipText = new(), languagesChipText = new(), keepChipText = new();
    private readonly Button microphoneChip, systemAudioChip, languagesChip, keepChip;
    // The meeting shortcut as key caps, or a link to the shortcut settings while none is set; MainWindow fills it.
    public WrapPanel ShortcutHint { get; } = new() { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
    private readonly TextBlock shortcutNote = new() { Style = WorkspacePages.Theme("HintNote"), Margin = new(0, 8, 0, 0) };
    // The defaults in the settings popup; the choices above start with them and a started meeting saves its choices back.
    private readonly System.Windows.Controls.ComboBox defaultLanguages;
    private readonly CheckBox defaultKeep = WorkspacePages.Toggle("Retain processed audio by default", false);
    private readonly Dictionary<MeetingNotesStyle, RadioButton> notesStyles = [];
    private readonly FrameworkElement notesStyleSection;
    private readonly TextBox retentionDays;
    // The marked swatch, stored by Save preferences; the pill and the page wave keep the stored colour until then.
    private string chosenColor;
    private Action markColor = () => { };
    // The defaults, retention, notes style and colour shown in the settings popup, which wait for Save preferences.
    public IReadOnlyList<PreferenceControl> PreferenceControls { get; }
    public event Action? PreferenceChanged;
    private readonly TextBlock indicator = new() { Text = IdleText, Style = WorkspacePages.Theme("RowDescription"), Margin = new(16, 0, 16, 8) };
    // The Meetings page's warnings and errors, above the composer; the History page and Meeting defaults have their own.
    private readonly NoticeStack meetingNotices = new("Meetings notices") { Margin = new(0, 24, 0, 0) }, settingsNotices = new("Meeting defaults notices");
    private const string SilentMicrophoneKey = "meeting-silent-microphone", PlaybackFallbackKey = "meeting-playback-fallback", PreviewStoppedKey = "meeting-preview-stopped",
        CouldNotStartKey = "meeting-could-not-start", EndedKey = "meeting-ended", ModelFallbackKey = "meeting-model-fallback", CaptureStoppedKey = "meeting-capture-stopped";
    // Points to warnings and errors of pages the user does not see; the window shows which page is on screen.
    internal NoticeBoard Board { get; }
    internal const string MeetingsPage = "Meetings", HistoryPage = "History";
    // The cards a meeting or a final pass leaves when it ends, cleared when the next one starts.
    private readonly List<string> outcomeKeys = [];
    private readonly System.Windows.Controls.ProgressBar progress = ProgressBar();
    private readonly TextBlock unfinishedText = WorkspacePages.Description("");
    private readonly StackPanel unfinishedSection = new() { Visibility = Visibility.Collapsed };
    private readonly Button record, interrupt, finishUnfinished, deleteUnfinishedAudio;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MeetingCapture? capture;
    private MeetingDocument? selected;
    private bool processing;
    private Action<string>? cancelFinishing;
    private DateTime relabelled;
    // The record control turns from Start meeting into Stop and finish, and into Continue in background when finishing starts,
    // so the second click of a double click lands on the new action.
    private static readonly TimeSpan RelabelledClickGuard = TimeSpan.FromSeconds(0.5);
    private MeetingProgress currentProgress = new("", 0);
    private DateTime stepStarted;
    private MeetingProgressWindow? progressWindow;
    private bool progressWindowDismissed;
    private readonly MirroredWaveform pageWave;
    private readonly Action<float> microphoneLevel, systemAudioLevel;
    private MeetingHud? hud;
    // The start whose pill shows while its devices do not record yet. Starting counts as capturing: whatever ends a meeting's recording
    // ends the start (StartingCapture).
    private readonly StartingGate starting = new();
    // True when a meeting starts capturing, false when its capture stops; the tray shows the recording state meanwhile.
    public event Action<bool>? CaptureChanged;
    // Starting, recording or finishing a transcript: the archive and the speech worker are in use.
    public bool IsRecording => capture is not null || processing || starting.IsStarting;
    // Starting or capturing audio; the window then stays on screen so the recording never goes unnoticed.
    public bool IsCapturing => (capture is not null || starting.IsStarting) && !processing;
    internal bool IsStarting => starting.IsStarting;
    internal NoticeStack MeetingNotices => meetingNotices;
    internal NoticeStack SettingsNotices => settingsNotices;
    internal bool ShowsSilenceWarning => meetingNotices.Notices.Find(SilentMicrophoneKey) is not null;
    internal string? PlaybackWarning => meetingNotices.Notices.Find(PlaybackFallbackKey)?.Text;
    internal bool ShowsUnfinished => unfinishedSection.Visibility == Visibility.Visible;
    internal string UnfinishedText => unfinishedText.Text;
    // Past meetings with their transcripts and notes, the search and the deletion of expired meetings, shown as the History page.
    public FrameworkElement History { get; }
    // Meeting defaults and retention, shown in the settings popup.
    public WorkspacePage MeetingSettings { get; }
    // How meeting notes are written, shown under Settings → Models with the other choices of a model.
    public FrameworkElement NotesStyleChoice { get; }
    // The system audio colour swatches, shown with the audio settings.
    public FrameworkElement SystemAudioColorChoice { get; }
    // notesInstall is the install of the meeting notes model, which Settings → Models runs. openNotesSettings opens where the notes style
    // is chosen.
    public MeetingsPanel(Settings settings, System.Windows.Controls.ComboBox microphones, System.Windows.Controls.ComboBox playbackDevices, Action<string> report, NotesModelInstall notesInstall,
        Action<string, bool>? notify = null, Action? open = null, Action? openShortcutSettings = null, Action? openNotesSettings = null,
        NoticeBoard? board = null)
    {
        Board = board ?? new();
        Board.Add(meetingNotices.Notices, MeetingsPage); Board.Add(settingsNotices.Notices, MainWindow.MeetingDefaultsTitle); Board.Add(historyNotices.Notices, HistoryPage);
        this.settings = settings; this.microphones = microphones; this.playbackDevices = playbackDevices; this.report = report; this.notify = notify ?? ((_, _) => { });
        this.open = open ?? (() => { if (Window.GetWindow(this) is { } host) { host.Show(); host.WindowState = WindowState.Normal; host.Activate(); } });
        this.openShortcutSettings = openShortcutSettings ?? (() => { });
        this.notesInstall = notesInstall;
        archive = new(Path.Combine(Settings.Root, "meetings")); devices = new(Dispatcher);
        keepAudio = settings.RetainMeetingAudio; defaultKeep.IsChecked = settings.RetainMeetingAudio;
        selectedLanguages = SavedLanguages;
        followedDefaults = (keepAudio, selectedLanguages);
        defaultLanguages = WorkspacePages.Choice("Default meeting languages", SavedLanguages, LanguageChoices);
        retentionDays = WorkspacePages.Field("Meeting retention days", settings.MeetingRetentionDays.ToString(), WorkspacePages.ControlWidth);
        chosenColor = settings.SystemAudioColor;
        void Changed() => PreferenceChanged?.Invoke();
        defaultLanguages.SelectionChanged += (_, _) => Changed(); retentionDays.TextChanged += (_, _) => Changed();
        defaultKeep.Checked += (_, _) => Changed(); defaultKeep.Unchecked += (_, _) => Changed();
        // While a transcript is being finished, the record control continues it in the background and the quiet action cancels it.
        record = WorkspacePages.Button("Start meeting", () =>
        {
            if (DateTime.UtcNow - relabelled < RelabelledClickGuard) return;
            if (processing) ContinueInBackground();
            else if (capture is null && !starting.IsStarting) Start();
            else _ = StopAsync();
        }, "Record");
        interrupt = WorkspacePages.Button("Stop; finish later", () => { if (processing) CancelFinishing(); else _ = StopAsync(true); }, "QuietAction"); interrupt.IsEnabled = false;
        finishUnfinished = WorkspacePages.Button("Finish transcripts", () => _ = FinishUnfinishedAsync(), "Primary");
        deleteUnfinishedAudio = WorkspacePages.Button("Delete their audio", () => _ = DeleteUnfinishedAudioAsync(), "Danger");
        segments = TranscriptGrid();
        saveEdits = WorkspacePages.Button("Save edits", () => SaveEdits(), "Primary");
        export = WorkspacePages.Button("Export", Export);
        retranscribe = new MenuItem { Header = new TextBlock { Text = "Re-transcribe" } };
        retranscribe.Click += (_, _) => _ = RetryAsync();
        deleteMeeting = new MenuItem { Header = new TextBlock { Text = "Delete meeting" }, Foreground = WorkspacePages.Theme<Brush>("DangerText") };
        deleteMeeting.Click += (_, _) => _ = DeleteAsync();
        more = WorkspacePages.Button("", () => { }, "IconButton");
        more.Content = new System.Windows.Shapes.Path { Style = WorkspacePages.Theme("Icon"), Data = WorkspacePages.Theme<System.Windows.Media.Geometry>("IconMore"), StrokeThickness = 2.2 };
        more.ToolTip = "Re-transcribe or delete"; System.Windows.Automation.AutomationProperties.SetName(more, "More meeting actions");
        more.ContextMenu = new System.Windows.Controls.ContextMenu { PlacementTarget = more, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4, Items = { retranscribe, deleteMeeting } };
        more.Click += (_, _) => more.ContextMenu.IsOpen = true;
        deleteExpired = WorkspacePages.Button("Delete expired meetings", () => _ = DeleteExpiredAsync(), "Danger");
        writeNotes = WorkspacePages.Button("Write notes", () => _ = WriteNotesAsync(), "Primary");
        ruleNotes = WorkspacePages.Button("Back to rule-based notes", () => _ = UseRuleNotesAsync());
        cancelNotes = WorkspacePages.Button("Cancel", () => notesCancellation?.Cancel());
        System.Windows.Automation.AutomationProperties.SetName(cancelNotes, "Cancel writing notes");
        // Flat while no meeting records.
        pageWave = new(SystemAudioColor) { Width = 116, Height = 36, Margin = new(8, 0, 0, 0), HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(pageWave, "Meeting levels: microphone upwards, system audio downwards");
        // The page wave moves only while the Meetings page is shown; hidden, it takes no levels, which would pile up unread.
        microphoneLevel = level => { if (pageWave.Running) pageWave.PushMicrophone(level); hud?.Wave.PushMicrophone(level); };
        systemAudioLevel = level => { if (pageWave.Running) pageWave.PushSystemAudio(level); hud?.Wave.PushSystemAudio(level); };
        pageWave.IsVisibleChanged += (_, _) => FollowPageWave();
        System.Windows.Automation.AutomationProperties.SetName(title, "Meeting title");
        System.Windows.Automation.AutomationProperties.SetName(transcript, "Meeting transcript");
        microphoneChip = WorkspacePages.Chip("IconMicrophone", "Meeting microphone", microphoneChipText);
        microphoneChip.Click += (_, _) => WorkspacePages.Choices(microphoneChip, DeviceChoices(microphones, recordMicrophone, "Do not record the microphone", on => recordMicrophone = on));
        systemAudioChip = WorkspacePages.Chip("IconSpeaker", "Meeting system audio", systemAudioChipText);
        systemAudioChip.Click += (_, _) => WorkspacePages.Choices(systemAudioChip, DeviceChoices(playbackDevices, recordSystemAudio, "Do not record system audio", on => recordSystemAudio = on));
        languagesChip = WorkspacePages.Chip("IconGlobe", "Meeting languages", languagesChipText);
        languagesChip.Click += (_, _) => WorkspacePages.Choices(languagesChip, LanguageChoices.Select(choice =>
            (choice.Label, choice.Value == selectedLanguages, (Action)(() => { selectedLanguages = choice.Value; UpdateChips(); }))));
        keepChip = WorkspacePages.Chip("IconAudioFile", "Meeting audio", keepChipText);
        keepChip.Click += (_, _) => WorkspacePages.Choices(keepChip, [
            ("Delete the audio once the transcript is final", !keepAudio, () => { keepAudio = false; UpdateChips(); }),
            ("Keep the audio to re-transcribe later", keepAudio, () => { keepAudio = true; UpdateChips(); })]);
        microphones.SelectionChanged += (_, _) => UpdateChips();
        playbackDevices.SelectionChanged += (_, _) => UpdateChips();
        var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run("Set a meeting shortcut under Settings → Shortcuts"))
        { Foreground = WorkspacePages.Theme<Brush>("AccentHover"), TextDecorations = null };
        link.Click += (_, _) => this.openShortcutSettings();
        System.Windows.Automation.AutomationProperties.SetName(link, "Set a meeting shortcut");
        shortcutNote.Inlines.Add(link);
        ShowShortcut(null);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        interrupt.Margin = new(0, 0, 8, 0); actions.Children.Add(interrupt); actions.Children.Add(record);
        var bottomBar = new DockPanel { Margin = new(8, 0, 8, 8) };
        DockPanel.SetDock(actions, Dock.Right); bottomBar.Children.Add(actions); bottomBar.Children.Add(pageWave);
        progress.Margin = new(16, 0, 16, 10); progress.Visibility = Visibility.Collapsed;
        var status = WorkspacePages.Stack(indicator, progress);
        var composer = new DockPanel();
        DockPanel.SetDock(title, Dock.Top); DockPanel.SetDock(bottomBar, Dock.Bottom); DockPanel.SetDock(status, Dock.Bottom);
        foreach (var part in new UIElement[] { title, bottomBar, status, transcript }) composer.Children.Add(part);
        var chips = new WrapPanel { Margin = new(2, 12, 2, 0) };
        foreach (var chip in new[] { microphoneChip, systemAudioChip, languagesChip, keepChip }) chips.Children.Add(chip);
        unfinishedSection.Children.Add(WorkspacePages.Section("Unfinished meetings"));
        var unfinishedButtons = WorkspacePages.Buttons(finishUnfinished, deleteUnfinishedAudio); unfinishedButtons.Margin = new(0, 10, 0, 0);
        unfinishedText.Margin = new(0);
        unfinishedSection.Children.Add(WorkspacePages.Card(WorkspacePages.BodyRow(WorkspacePages.Stack(unfinishedText, unfinishedButtons))));
        var column = new StackPanel { Style = WorkspacePages.Theme("ComposerColumn") };
        foreach (var part in new UIElement[]
        {
            ShortcutHint, shortcutNote,
            new TextBlock { Style = WorkspacePages.Theme("HintNote"), Text = "Records your microphone and what this PC plays, each on its own channel. Audio stays on this PC, and so do transcripts unless you have a meeting's notes written through the API." },
            meetingNotices, new Border { Style = WorkspacePages.Theme("Composer"), Child = composer }, chips, unfinishedSection
        }) column.Children.Add(part);
        Children.Add(new ScrollViewer { Style = WorkspacePages.Theme("PageScroll"), Content = column });
        UpdateChips();
        History = BuildHistory(out var searchBar); HistorySearch = searchBar;
        var notesOptions = new StackPanel();
        foreach (var (style, name, description) in new[]
        {
            (MeetingNotesStyle.Extractive, "Rules on this PC", "Built at once without a model. Nothing leaves this PC. Usable for action items and times, not for decisions and summaries."),
            (MeetingNotesStyle.LocalModel, "Local model", "Written on this PC when you press the button for a meeting, by the meeting notes model (Qwen3.5-4B, installed on request). Nothing leaves this PC. "),
            (MeetingNotesStyle.Api, "API", "Written by the Concise API connection under Text style when you press the button for a meeting. That meeting's title, languages, vocabulary and transcript text leave this PC, never audio.")
        })
        {
            var option = new RadioButton
            {
                Style = WorkspacePages.Theme("Option"), GroupName = "MeetingNotesStyle", IsChecked = settings.MeetingNotesStyle == style,
                Content = WorkspacePages.Stack(new TextBlock { Text = name, Style = WorkspacePages.Theme("RowTitle") }, WorkspacePages.Description(description))
            };
            System.Windows.Automation.AutomationProperties.SetName(option, "Meeting notes: " + name);
            option.Checked += (_, _) => Changed();
            notesStyles[style] = option; notesOptions.Children.Add(option);
        }
        // Shown under the options while the Local model style is chosen without its model: the same install as everywhere else.
        notesInstall.NextToStyle.Progress.Margin = new(0, 6, 0, 0);
        notesModelMissing = WorkspacePages.Row(WorkspacePages.Stack(WorkspacePages.Description("The meeting notes model is not installed. " + NotesInstallOffer), notesInstall.NextToStyle.Progress),
            notesInstall.NextToStyle.Button);
        foreach (var option in notesStyles.Values) { option.Checked += (_, _) => FollowChoice(); }
        notesOptions.Children.Add(notesModelMissing);
        notesStyleSection = new Border { Style = WorkspacePages.Theme("Card"), Padding = new(4), Child = notesOptions };
        NotesStyleChoice = notesStyleSection;
        FollowChoice();
        this.openNotesSettings = () => { (openNotesSettings ?? (() => { }))(); Dispatcher.BeginInvoke(() => notesStyleSection.BringIntoView(), DispatcherPriority.Loaded); };
        MeetingSettings = new(WorkspacePages.Page(
            settingsNotices,
            WorkspacePages.Section("New meetings", true),
            WorkspacePages.SectionDescription("The Meetings page starts with these choices; a meeting you start there saves its own choices as the new defaults."),
            WorkspacePages.Card(
                WorkspacePages.Row("Languages", "Spoken in your meetings. Each stretch of speech is written in the most likely of them.", defaultLanguages),
                WorkspacePages.Row("Retain processed audio", "Keeps the audio after the final pass, for Re-transcribe. Off deletes it once the transcript is final.", defaultKeep)),
            WorkspacePages.Section("Retention"),
            WorkspacePages.Card(
                WorkspacePages.Row("Keep completed meetings for", "Days, from 1 to 3650. Delete expired meetings on the History page removes older ones. Unfinished meetings are kept until you finish them or delete their audio.", retentionDays)),
            WorkspacePages.Section("Meeting notes"),
            WorkspacePages.Card(WorkspacePages.Row("How notes are written", "Rules, the meeting notes model or the API, and the install of the meeting notes model, are under Models with the other models.",
                WorkspacePages.Button("Open Models", this.openNotesSettings)))),
            null);
        SystemAudioColorChoice = ColorSwatches(Changed);
        PreferenceControls = Controls();
        timer.Tick += (_, _) => Tick();
        try { archive.DeleteLeftoverAudio(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { meetingNotices.Report(new("meeting-leftover-audio", NoticeKind.Error, "Audio left by an earlier run was not deleted", ex.Message)); }
        Refresh();
        if (archive.Unfinished() is { Count: > 0 } unfinished) this.notify(UnfinishedSummary(unfinished) + " Open Meetings to finish or delete it.", true);
    }
    internal static System.Windows.Controls.ProgressBar ProgressBar() => new()
    {
        Height = 6, Minimum = 0, Maximum = 1, BorderThickness = new(0),
        Foreground = WorkspacePages.Theme<Brush>("Accent"), Background = WorkspacePages.Theme<Brush>("Field")
    };
    private static TextBox MeetingTitle()
    {
        var box = new TextBox { Style = WorkspacePages.Theme("BareTextBox"), Text = MeetingVocabulary.DefaultTitle, Tag = "Meeting title", FontSize = 15, FontWeight = FontWeights.SemiBold, Padding = new(16, 14, 16, 0) };
        box.ToolTip = "Shown in the history and in exports";
        return box;
    }
    // The keys of the meeting shortcut, or null while none is set.
    public void ShowShortcut(IReadOnlyList<string>? keys) { shortcutKeys = keys; ShowHint(); }
    private IReadOnlyList<string>? shortcutKeys;
    // The heading says what the page is doing: how to start, that a meeting records and how to stop, or that a transcript is being finished.
    private void ShowHint()
    {
        var idle = capture is null && !processing;
        if (processing) WorkspacePages.KeyHint(ShortcutHint, "Finishing the transcript", [], "");
        else if (!idle) { if (shortcutKeys is null) WorkspacePages.KeyHint(ShortcutHint, "Meeting recording", [], ""); else WorkspacePages.KeyHint(ShortcutHint, "Press", shortcutKeys, "to stop and finish"); }
        else if (shortcutKeys is null) WorkspacePages.KeyHint(ShortcutHint, "Start a meeting below", [], "");
        else WorkspacePages.KeyHint(ShortcutHint, "Press", shortcutKeys, "to start a meeting");
        shortcutNote.Visibility = idle && shortcutKeys is null ? Visibility.Visible : Visibility.Collapsed;
    }
    // The devices of a shared selection, then leaving the channel out.
    private List<(string Header, bool Selected, Action Choose)> DeviceChoices(System.Windows.Controls.ComboBox selection, bool recorded, string leaveOut, Action<bool> recordChannel)
    {
        var choices = selection.Items.OfType<MicrophoneDevice>()
            .Select(device => (device.Name, recorded && ReferenceEquals(selection.SelectedItem, device), (Action)(() => { recordChannel(true); selection.SelectedItem = device; UpdateChips(); }))).ToList();
        choices.Add((leaveOut, !recorded, () => { recordChannel(false); UpdateChips(); }));
        return choices;
    }
    private void UpdateChips()
    {
        microphoneChipText.Text = !recordMicrophone ? "Microphone off"
            : microphones.SelectedItem is MicrophoneDevice microphone ? (microphone.Id.Length == 0 ? "Default microphone" : microphone.Name) : "No microphone";
        systemAudioChipText.Text = !recordSystemAudio ? "System audio off"
            : playbackDevices.SelectedItem is MicrophoneDevice { Id.Length: > 0 } output ? output.Name : "Default output";
        languagesChipText.Text = LanguageChoices.FirstOrDefault(choice => choice.Value == selectedLanguages).Label ?? selectedLanguages;
        keepChipText.Text = keepAudio ? "Audio kept for re-transcribe" : "Audio deleted when final";
        // A screen reader announces the current choice with the chip, as the Dictate chips do.
        foreach (var (chip, name, text) in new[] { (microphoneChip, "Meeting microphone", microphoneChipText), (systemAudioChip, "Meeting system audio", systemAudioChipText),
            (languagesChip, "Meeting languages", languagesChipText), (keepChip, "Meeting audio", keepChipText) })
            System.Windows.Automation.AutomationProperties.SetName(chip, $"{name}: {text.Text}");
    }
    private static string UnfinishedSummary(IReadOnlyList<(MeetingDocument Document, long AudioBytes)> unfinished)
    {
        var bytes = unfinished.Sum(found => found.AudioBytes);
        var size = bytes < 1024 * 1024 ? "Less than 1 MB" : $"{bytes / (1024.0 * 1024):0} MB";
        return unfinished.Count == 1
            ? $"1 meeting is not finished: its transcript is only the live preview, which can miss parts. {size} of its audio is kept on this PC only to finish it."
            : $"{unfinished.Count} meetings are not finished: their transcripts are only the live preview, which can miss parts. {size} of their audio is kept on this PC only to finish them.";
    }
    private static readonly (string Value, string Label)[] LanguageChoices =
    [
        ("en,de,es", "English, German, Spanish"), ("en,es", "English, Spanish"), ("en,de", "English, German"), ("de,es", "German, Spanish"),
        ("en", "English only"), ("de", "German only"), ("es", "Spanish only")
    ];
    private string SavedLanguages => string.Join(",", SpeechLanguages.Parse(settings.MeetingLanguages));
    private static void Select(System.Windows.Controls.ComboBox box, string value) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value) ?? box.Items[0];
    private PreferenceControl[] Controls() =>
    [
        new([nameof(Settings.MeetingLanguages)], source => Select(defaultLanguages, string.Join(",", SpeechLanguages.Parse(source.MeetingLanguages))),
            target => target.MeetingLanguages = (defaultLanguages.SelectedItem as ComboBoxItem)?.Tag as string ?? SpeechLanguages.Offered),
        new([nameof(Settings.RetainMeetingAudio)], source => defaultKeep.IsChecked = source.RetainMeetingAudio, target => target.RetainMeetingAudio = defaultKeep.IsChecked == true),
        // Days that are not a number read as 0, which PreferenceSections.Problem rejects.
        new([nameof(Settings.MeetingRetentionDays)], source => retentionDays.Text = source.MeetingRetentionDays.ToString(),
            target => target.MeetingRetentionDays = int.TryParse(retentionDays.Text, out var days) ? days : 0),
        new([nameof(Settings.MeetingNotesStyle)], source => notesStyles[source.MeetingNotesStyle].IsChecked = true, target => target.MeetingNotesStyle = notesStyles.First(option => option.Value.IsChecked == true).Key),
        new([nameof(Settings.SystemAudioColor)], source => { chosenColor = source.SystemAudioColor; markColor(); }, target => target.SystemAudioColor = chosenColor),
    ];
    // Puts newly stored preferences to use, given the sections that changed.
    public void PreferencesStored(IReadOnlyList<string> changed)
    {
        // While a meeting records or a transcript is finished, the page's choices follow the saved defaults when that ends.
        if (changed.Contains(PreferenceSections.MeetingDefaults) && !IsRecording) FollowDefaults(true);
        // Also while a meeting records.
        pageWave.SystemColor = SystemAudioColor;
        if (hud is not null) hud.Wave.SystemColor = SystemAudioColor;
    }
    // Takes the saved defaults into the page's choices; without force, a choice changed on the page since the last defaults stays.
    private void FollowDefaults(bool force)
    {
        if (force || keepAudio == followedDefaults.Keep) keepAudio = settings.RetainMeetingAudio;
        if (force || selectedLanguages == followedDefaults.Languages) selectedLanguages = SavedLanguages;
        followedDefaults = (settings.RetainMeetingAudio, SavedLanguages);
        UpdateChips();
    }
    private System.Windows.Media.Color SystemAudioColor => (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.SystemAudioColor);
    private WrapPanel ColorSwatches(Action changed)
    {
        var row = new WrapPanel { Width = WorkspacePages.ControlWidth };
        var rings = new List<(System.Windows.Shapes.Ellipse Ring, string Hex)>();
        void Mark() { foreach (var (ring, hex) in rings) ring.Stroke = hex == chosenColor ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Transparent; }
        markColor = Mark;
        foreach (var (name, hex) in SystemAudioColors.Presets)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 22, Height = 22, StrokeThickness = 2,
                Fill = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex))
            };
            rings.Add((ring, hex));
            var swatch = WorkspacePages.Button("", () => { chosenColor = hex; Mark(); changed(); }, "IconButton");
            swatch.Content = ring; swatch.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(swatch, name + " system audio colour");
            row.Children.Add(swatch);
        }
        Mark();
        return row;
    }
    private void Start()
    {
        if (App.DiagnosticMode) { report("Diagnostic instance: microphone and system-audio capture disabled."); return; }
        if (Updating()) { meetingNotices.Report(new(CouldNotStartKey, NoticeKind.Warning, "The meeting did not start", UpdateGate.StartRefusal)); return; }
        _ = BeginAsync(StartCapture);
    }
    // An update runs until Private Flow restarts: Start meeting and the meeting shortcut refuse meanwhile.
    public Func<bool> Updating { get; set; } = () => false;
    private void StartCapture(MeetingCapture active) =>
        active.Start(recordMicrophone ? (microphones.SelectedItem as MicrophoneDevice)?.Id ?? settings.MicrophoneId : null, PlaybackChoice, devices);
    // The output device a meeting started now records: empty for the Windows default, null when system audio is left out.
    internal string? PlaybackChoice => recordSystemAudio ? (playbackDevices.SelectedItem as MicrophoneDevice)?.Id ?? settings.PlaybackDeviceId : null;
    // The meeting shortcut: starts a meeting with the choices on this page without showing the window, or stops it as Stop and finish does.
    public MeetingShortcutDecision PressShortcut(bool dictationActive, bool otherOperation) =>
        PressShortcut(App.DiagnosticMode, dictationActive, otherOperation, StartCapture);
    // The verification passes synthetic sources instead of the devices.
    internal MeetingShortcutDecision PressShortcut(bool diagnostic, bool dictationActive, bool otherOperation, Action<MeetingCapture> startCapture)
    {
        var decision = MeetingShortcut.Decide(diagnostic, IsCapturing, processing, dictationActive, otherOperation, Updating());
        if (decision.Action == MeetingShortcutAction.Start) _ = BeginAsync(startCapture);
        else if (decision.Action == MeetingShortcutAction.Stop) _ = StopAsync();
        return decision;
    }
    private void ShowLevels()
    {
        hud!.Starting = false;
        FollowPageWave();
        System.Windows.Media.CompositionTarget.Rendering += ReadLevels;
        CaptureChanged?.Invoke(true);
    }
    // Closing the window is refused while a meeting records; the card goes when the capture stops.
    public void RefuseClose() => meetingNotices.Report(new(CloseRefusedKey, NoticeKind.Warning, "A meeting is recording", "Stop it here before closing the window."));
    private const string CloseRefusedKey = "meeting-close-refused";
    private void HideLevels()
    {
        meetingNotices.Clear(CloseRefusedKey);
        if (hud is null) return;
        System.Windows.Media.CompositionTarget.Rendering -= ReadLevels;
        hud.Close(); hud = null;
        FollowPageWave();
        CaptureChanged?.Invoke(false);
    }
    private void FollowPageWave()
    {
        if (hud is not null && pageWave.IsVisible) pageWave.Start();
        else { pageWave.Stop(); pageWave.Flatten(); }
    }
    private void ReadLevels(object? sender, EventArgs e) => capture?.ReadLevels(microphoneLevel, systemAudioLevel);
    // Starts a meeting; the verification passes synthetic sources instead of the devices.
    internal async Task BeginAsync(Action<MeetingCapture> startCapture)
    {
        if (IsRecording) return;
        var start = starting.Begin();
        // The pill is the only sign of the meeting shortcut while the window is hidden. Saving the meeting and opening its devices hold
        // this thread, longest at the first meeting, so the pill shows first, dimmed as "starting" until they record, and gets its frame
        // drawn. From the pill on, the start ends as a meeting does: the shortcut, Stop and finish, Stop; finish later and Quit end it.
        MeetingCapture? active = null;
        try
        {
            hud = new MeetingHud(SystemAudioColor) { Starting = true }; hud.Show();
            Relabel("Stop and finish"); interrupt.IsEnabled = true; SetInputs(false);
            await Dispatcher.Yield(DispatcherPriority.Background);
            // Ended before the devices open: no meeting was created.
            if (!starting.Keeps(start)) return;
            // The meeting's own choices become the stored defaults, and only they: the shared settings hold stored values only, and a
            // default changed under Settings but not saved stays shown as unsaved. A default control still showing the stored value
            // follows the new one.
            var meeting = settings.Snapshot();
            meeting.RetainMeetingAudio = keepAudio; meeting.MeetingLanguages = selectedLanguages;
            var keepFollows = defaultKeep.IsChecked == settings.RetainMeetingAudio;
            var languagesFollow = (defaultLanguages.SelectedItem as ComboBoxItem)?.Tag as string == SavedLanguages;
            try { settings.SaveAs(meeting); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (keepFollows) defaultKeep.IsChecked = settings.RetainMeetingAudio;
            if (languagesFollow) Select(defaultLanguages, SavedLanguages);
            followedDefaults = (keepAudio, selectedLanguages);
            // The shared vocabulary joins the prompt when the meeting is transcribed; the meeting itself stores none.
            capture = active = new(archive, meeting, title.Text, selectedLanguages, "");
            var started = active;
            ClearOutcome();
            started.Changed += message => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(capture, started)) report(message); });
            started.PreviewStopped += reason => Dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(capture, started))
                    ReportOutcome(new(PreviewStoppedKey, NoticeKind.Warning, "Live preview stopped", "Recording continues; Stop and finish transcribes the saved audio. " + NoticeText.Short(reason), NoticeText.Detail(reason)));
            });
            started.Previewed += segmentsSoFar => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(capture, started)) { transcript.Text = Lines(segmentsSoFar); transcript.ScrollToEnd(); } });
            // A device that fails or a full disk ends the capture; what was saved is still finished.
            started.Failed += () => Dispatcher.BeginInvoke(async () => { if (ReferenceEquals(capture, started)) await StopAsync(); });
            started.Progressed += step => Dispatcher.BeginInvoke(() => { if (ReferenceEquals(capture, started)) ShowProgress(step); });
            startCapture(started);
            // Ended while the devices opened: they are closed at once and the meeting record they began is removed.
            if (!starting.Keeps(start)) { Discard(started); return; }
            starting.End();
            transcript.Clear();
            ShowLevels();
            Refresh();
            var fallback = started.PlaybackFallback;
            if (fallback is null) meetingNotices.Clear(PlaybackFallbackKey);
            else meetingNotices.Report(new(PlaybackFallbackKey, NoticeKind.Warning, "System audio from the Windows default", fallback));
            timer.Start(); Tick(); report("Meeting capture started explicitly. Local audio is saved about every 20 seconds.");
            // The guard against a double click counts from here: opening the devices and the pill above can take longer than the guard itself.
            relabelled = DateTime.UtcNow;
            if (fallback is not null && !HostShowing) notify(fallback, true);
        }
        catch (Exception ex)
        {
            // A start already ended leaves the page to what followed it.
            if (!starting.Keeps(start) && !ReferenceEquals(capture, active)) { if (active is not null) Discard(active); return; }
            starting.End();
            if (hud is { Starting: true }) { hud.Close(); hud = null; } else HideLevels();
            capture?.Dispose(); capture = null;
            Relabel("Start meeting"); interrupt.IsEnabled = false; SetInputs(true);
            Refresh(); Ended?.Invoke();
            ReportOutcome(new(CouldNotStartKey, NoticeKind.Error, "The meeting could not start", NoticeText.Short(ex.Message), NoticeText.Detail(ex.Message)));
            // Started with the shortcut, the window may be hidden.
            if (!HostShowing) notify("Meeting could not start: " + ex.Message, true);
        }
    }
    // Ends a start before its devices record, as Stop and finish ends a meeting: nothing is recorded or kept, the pill goes.
    private void CancelStart()
    {
        starting.End();
        // Devices opening right now belong to a meeting the start removes once they return (Discard).
        capture = null;
        hud?.Close(); hud = null;
        Relabel("Start meeting"); interrupt.IsEnabled = false; SetInputs(true);
        report("The meeting did not start: it was stopped before recording began, so nothing was recorded.");
        Refresh(); Ended?.Invoke();
    }
    private void Discard(MeetingCapture started)
    {
        started.Dispose();
        try { archive.Delete(started.Document.Id); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
    }
    private void Tick()
    {
        if (processing) { UpdateProgress(); return; }
        if (capture is null) return;
        var devicesText = (capture.Document.MicrophoneDevice.Length > 0 ? $" · microphone “{capture.Document.MicrophoneDevice}”" : "") +
            (capture.Document.PlaybackDevice.Length > 0 ? $" · system audio of “{capture.Document.PlaybackDevice}”" : "");
        indicator.Text = $"● Meeting recording · {capture.Seconds:0} s{devicesText} · Stop and finish ends it";
        if (capture.CheckMicrophone())
            notify($"No sound from the microphone “{capture.Document.MicrophoneDevice}” in this meeting. Check its mute switch or choose another microphone.", true);
        // No x while the silence lasts: dismissed, a muted microphone would go unnoticed for the rest of the meeting. It ends by itself.
        if (capture.MicrophoneSilent)
            meetingNotices.Raise(new(SilentMicrophoneKey, NoticeKind.Warning, "No sound from the microphone",
                "Check its mute switch and input level, or choose another microphone under Settings → Microphone and speakers.", Dismissable: false));
        else meetingNotices.Clear(SilentMicrophoneKey);
    }
    public async Task StopAsync(bool finishLater = false)
    {
        if (starting.IsStarting && StartingCapture.Meeting(StartingEvent.Stop) == StartingOutcome.Cancel) { CancelStart(); return; }
        if (capture is null || processing) return;
        var active = capture;
        HideLevels();
        BeginFinishing(finishLater ? null : active.CancelProcessing, "Stopping capture and saving the last audio");
        const string Kept = "The live preview and the audio are kept; finish it under Unfinished meetings.";
        string confirmation; Exception? failure = null;
        try
        {
            await active.StopAsync(finishLater);
            confirmation = active.Document.Status == "Complete" ? "Meeting saved locally with the final transcript."
                : finishLater ? "Stopped. The transcript is the live preview; the audio is kept to finish it under Unfinished meetings."
                : "Finishing cancelled. " + Kept;
        }
        catch (Exception ex) { failure = ex; confirmation = "The meeting stopped without a final transcript. " + Kept; }
        capture = null; active.Dispose();
        if (failure is not null) ReportOutcome(new(EndedKey, NoticeKind.Error, "Finishing failed", NoticeText.Short(failure.Message) + " " + Kept, NoticeText.Detail(failure.Message)));
        else if (active.Document.Status != "Complete" && !finishLater) ReportOutcome(new(EndedKey, NoticeKind.Note, "Finishing cancelled", Kept));
        if (active.CaptureError is { } error) ReportOutcome(new(CaptureStoppedKey, NoticeKind.Error, "The capture stopped on its own", error));
        var problems = ReportFinished(active.Document);
        // Never a bare success while a problem of this meeting waits on the Meetings page.
        if (failure is null && active.Document.Status == "Complete" && (problems.Length > 0 || active.CaptureError is not null))
            confirmation = "Meeting saved with a problem; see Meetings.";
        // The tray notice of a hidden window says it all, as before.
        var whole = (active.CaptureError is { } stopped ? MeetingArchive.CaptureStopped(stopped) + " " : "")
            + (failure is null ? confirmation + problems : failure.Message + " " + Kept);
        EndFinishing(confirmation, whole, failure is not null || active.CaptureError is not null || active.Document.ModelFallback.Length > 0);
        if (!KeepsUnsavedEdits(active.Document)) Show(active.Document); transcript.Text = Lines(active.Document.Segments);
    }
    // The cards a final pass leaves on the Meetings page for a meeting: the smaller model it fell back to and each channel that delivered
    // no sound. Returns their text for a tray notice.
    private string ReportFinished(MeetingDocument document)
    {
        if (document.ModelFallback.Length > 0)
            ReportOutcome(new(ModelFallbackKey + document.Id, NoticeKind.Warning, $"“{document.Title}” finished with another model", NoticeText.Short(document.ModelFallback), NoticeText.Detail(document.ModelFallback)));
        if (document.Status != "Complete") return Fallback(document);
        foreach (var channel in MeetingArchive.SilentChannels(document))
            ReportOutcome(new($"meeting-silent-{channel}-{document.Id}", NoticeKind.Warning, channel == "mic" ? "No sound from the microphone" : "No sound from the system audio",
                MeetingArchive.DescribeSilence(document, channel)));
        return Fallback(document) + Silence(document);
    }
    private void ReportOutcome(Notice notice) { meetingNotices.Report(notice); outcomeKeys.Add(notice.Key); }
    private void ClearOutcome()
    {
        foreach (var key in outcomeKeys) meetingNotices.Clear(key);
        outcomeKeys.Clear();
    }
    private static string Lines(IEnumerable<MeetingSegment> segments) => string.Join("\n", segments.Select(segment => $"{segment.Speaker}: {segment.Text.Trim()}"));
    private void Relabel(string action) { record.Content = action; relabelled = DateTime.UtcNow; ShowHint(); }
    private static string Fallback(MeetingDocument document) => document.ModelFallback.Length == 0 ? "" : " " + document.ModelFallback;
    private static string Silence(MeetingDocument document) => string.Concat(MeetingArchive.SilentChannels(document).Select(channel => " " + MeetingArchive.DescribeSilence(document, channel)));
    private void BeginFinishing(Action<string>? cancel, string firstStep)
    {
        processing = true; cancelFinishing = cancel; progressWindowDismissed = false;
        currentProgress = new(firstStep, 0); stepStarted = DateTime.UtcNow;
        SetInputs(false); meetingNotices.Clear(SilentMicrophoneKey); meetingNotices.Clear(PlaybackFallbackKey);
        Relabel("Continue in background");
        interrupt.Content = "Cancel finishing"; interrupt.IsEnabled = cancel is not null;
        progress.Value = 0; progress.Visibility = Visibility.Visible;
        timer.Start(); UpdateProgress(); indicator.BringIntoView();
        report("Finishing the transcript; the Meetings page shows its progress.");
    }
    // confirmation goes to the status line; a hidden window gets whole as a tray notice.
    // A meeting that failed to start, or one whose recording and finishing are over.
    public event Action? Ended;
    private void EndFinishing(string confirmation, string whole, bool problem)
    {
        var showing = HostShowing;
        processing = false; cancelFinishing = null; timer.Stop();
        progressWindow?.Close(); progressWindow = null;
        if (Window.GetWindow(this)?.TaskbarItemInfo is { } taskbar) taskbar.ProgressState = TaskbarItemProgressState.None;
        Relabel("Start meeting"); interrupt.Content = "Stop; finish later"; interrupt.IsEnabled = false;
        SetInputs(true); progress.Visibility = Visibility.Collapsed;
        // Defaults saved under Settings while a meeting ran or an older one was finished become the next meeting's choices.
        FollowDefaults(false);
        indicator.Text = IdleText; report(confirmation);
        if (!showing) notify(whole, problem);
        Refresh();
        Ended?.Invoke();
    }
    private void CancelFinishing()
    {
        if (cancelFinishing is null) return;
        cancelFinishing(MeetingArchive.FinishingCancelled); interrupt.IsEnabled = false;
        ShowProgress(new("Cancelling; the audio stays to finish later", currentProgress.Share));
    }
    private void ContinueInBackground()
    {
        Window.GetWindow(this)?.Hide();
        report("Finishing continues in the background; a small window shows its progress.");
        UpdateProgress();
    }
    private bool HostShowing => Window.GetWindow(this) is { IsVisible: true, WindowState: not WindowState.Minimized };
    private void ShowProgress(MeetingProgress step)
    {
        if (!processing) return;
        if (step.Step != currentProgress.Step) stepStarted = DateTime.UtcNow;
        currentProgress = step; UpdateProgress();
    }
    // The step's elapsed time moves even in steps that cannot count their progress, such as loading a model.
    private void UpdateProgress()
    {
        var elapsed = (DateTime.UtcNow - stepStarted).TotalSeconds;
        var text = $"{currentProgress.Step} · {currentProgress.Share:P0}" + (elapsed >= 2 ? $" · {elapsed:0} s" : "");
        indicator.Text = "Finishing the transcript · " + text; progress.Value = currentProgress.Share;
        var host = Window.GetWindow(this);
        if (host is not null)
        {
            host.TaskbarItemInfo ??= new TaskbarItemInfo();
            host.TaskbarItemInfo.ProgressState = TaskbarItemProgressState.Normal; host.TaskbarItemInfo.ProgressValue = currentProgress.Share;
        }
        if (!HostShowing && !progressWindowDismissed)
        {
            if (progressWindow is null)
            {
                progressWindow = new MeetingProgressWindow(open);
                var shown = progressWindow;
                shown.Closed += (_, _) => { if (ReferenceEquals(progressWindow, shown)) { progressWindow = null; progressWindowDismissed = true; } };
            }
            progressWindow.Report(text, currentProgress.Share);
            if (!progressWindow.IsVisible) progressWindow.Show();
        }
        else if (HostShowing && progressWindow is { IsVisible: true }) progressWindow.Hide();
    }
    public async Task StopOperationsAsync()
    {
        // A start in progress ends before any of its devices opens.
        if (starting.IsStarting && StartingCapture.Meeting(StartingEvent.Quit) == StartingOutcome.Cancel) CancelStart();
        notesCancellation?.Cancel();
        cancelFinishing?.Invoke(MeetingArchive.QuitBeforeFinished); capture?.CancelProcessing(MeetingArchive.QuitBeforeFinished);
        await StopAsync(true);
        while (processing) await Task.Delay(50);
    }
    // True when the user confirmed and no recording or finishing started while the question was open.
    private async Task<bool> ConfirmAsync(string heading, string message, string action) => await ConfirmationDialog.AskAsync(this, heading, message, action) && !IsRecording;
    internal string SelectedLanguages => selectedLanguages;
    private void SetInputs(bool enabled)
    {
        microphoneChip.IsEnabled = systemAudioChip.IsEnabled = languagesChip.IsEnabled = keepChip.IsEnabled = title.IsEnabled = finishUnfinished.IsEnabled = deleteUnfinishedAudio.IsEnabled = enabled;
        SetHistoryActions(enabled);
        RecordingChanged?.Invoke();
    }
    // A meeting started or stopped recording or finishing; the installs under Models wait meanwhile.
    public event Action? RecordingChanged;
    // Runs a final pass on the thread pool with the progress, Continue in background and Cancel finishing of a stopped meeting.
    // True when the pass completed; false when it was cancelled or failed. meetings are those the pass finishes, for their cards.
    private async Task<bool> RunFinishingAsync(Func<IProgress<MeetingProgress>, CancellationToken, Task<string>> work, params MeetingDocument[] meetings)
    {
        ClearOutcome();
        using var cancellation = new CancellationTokenSource();
        // The progress and its controls are on the Meetings page; History starts this.
        open();
        BeginFinishing(_ => cancellation.Cancel(), "Preparing the saved audio");
        var reporter = new ProgressCallback<MeetingProgress>(step => Dispatcher.BeginInvoke(() => ShowProgress(step)));
        string confirmation, whole; var completed = false;
        try { whole = confirmation = await Task.Run(() => work(reporter, cancellation.Token)); completed = true; }
        catch (Exception) when (cancellation.IsCancellationRequested)
        {
            whole = confirmation = "Finishing cancelled. The saved audio stays to finish it later.";
            ReportOutcome(new(EndedKey, NoticeKind.Note, "Finishing cancelled", "The saved audio stays to finish it later."));
        }
        catch (Exception ex)
        {
            confirmation = "Finishing stopped without a final transcript; the saved audio stays to try again.";
            whole = "Finishing failed: " + ex.Message + " The saved audio stays to try again.";
            ReportOutcome(new(EndedKey, NoticeKind.Error, "Finishing failed", NoticeText.Short(ex.Message) + " The saved audio stays to try again.", NoticeText.Detail(ex.Message)));
        }
        var anyProblem = false;
        foreach (var meeting in meetings)
            if (ReportFinished(meeting) is { Length: > 0 } problems) { whole += meetings.Length > 1 ? $" {meeting.Title}:{problems}" : problems; anyProblem = true; }
        if (completed && anyProblem) confirmation = confirmation.TrimEnd('.') + ", with a problem; see Meetings.";
        EndFinishing(confirmation, whole, !completed && !cancellation.IsCancellationRequested || meetings.Any(meeting => meeting.ModelFallback.Length > 0));
        return completed;
    }
    // One action for every meeting a cancel, a failure, a crash or Quit left unfinished, each with its own languages and vocabulary.
    private async Task FinishUnfinishedAsync()
    {
        if (IsRecording) return;
        var unfinished = archive.Unfinished();
        if (unfinished.Count == 0) { Refresh(); return; }
        if (NotesBeingWritten(unfinished.Select(found => found.Document), meetingNotices)) return;
        // The pass replaces the transcript of the shown meeting when it is one of them, also rows edited on the page.
        var shown = selected is not null && unfinished.Any(found => found.Document.Id == selected.Id) ? selected : null;
        var edited = shown is not null && HasUnsavedEdits();
        if (edited && !await ConfirmAsync("Finish transcripts", $"Finishing replaces the transcript of “{shown!.Title}”, including the edits you have not saved.", "Finish transcripts")) return;
        var snapshot = settings.Snapshot();
        await RunFinishingAsync(async (reporter, token) =>
        {
            var failures = new List<string>();
            for (var index = 0; index < unfinished.Count; index++)
            {
                var (document, position) = (unfinished[index].Document, index);
                var prefix = unfinished.Count > 1 ? $"Meeting {position + 1} of {unfinished.Count} · " : "";
                try
                {
                    await MeetingCapture.RetryAsync(archive, document, snapshot, token,
                        new ProgressCallback<MeetingProgress>(step => reporter.Report(new(prefix + step.Step, (position + step.Share) / unfinished.Count))));
                }
                catch (Exception ex) when (!token.IsCancellationRequested) { failures.Add($"{document.Title}: {ex.Message}"); }
            }
            if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} of {unfinished.Count} meetings could not be finished ({string.Join("; ", failures)}).");
            return unfinished.Count == 1 ? "The meeting is finished; its final transcript is saved." : $"{unfinished.Count} meetings are finished; their final transcripts are saved.";
        }, [.. unfinished.Select(found => found.Document)]);
        if (shown is null || !ReferenceEquals(selected, shown)) return;
        var fresh = archive.Load(shown.Id);
        if (edited && fresh.Status != "Complete") KeepEdits(fresh, shown); else Show(fresh);
    }
    private async Task DeleteUnfinishedAudioAsync()
    {
        if (IsRecording) return;
        var unfinished = archive.Unfinished();
        if (unfinished.Count == 0) { Refresh(); return; }
        if (!await ConfirmAsync("Delete audio of unfinished meetings",
            UnfinishedSummary(unfinished) + "\n\nDelete this audio? The live preview stays as the transcript, and these meetings cannot be finished afterwards.", "Delete their audio")) return;
        try { foreach (var (document, _) in unfinished) archive.DeleteAudio(document.Id); meetingNotices.Clear("meeting-audio-not-deleted"); report("Audio deleted. The live preview stays as the transcript."); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { meetingNotices.Report(new("meeting-audio-not-deleted", NoticeKind.Error, "The audio was not deleted", ex.Message)); }
        Refresh();
    }
    public void Dispose() { timer.Stop(); notesTimer.Stop(); notesCancellation?.Cancel(); HideLevels(); progressWindow?.Close(); capture?.Dispose(); devices.Dispose(); }
}
