using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using PrivateFlow.Core;
using Forms = System.Windows.Forms;
using Brush = System.Windows.Media.Brush;

namespace PrivateFlow.Windows;

public partial class MainWindow : Window, IDictationView
{
    // The invariant of the window and of every part sharing this object: it holds exactly what settings.json stores, never a value only
    // shown. Dictation, meetings, Delete expired meetings, Write notes and setup all read it, so unsaved values live only in the controls:
    // ShownPreferences builds them on a copy, a dictation takes such a copy, and Settings.SaveAs takes them over only once the file
    // is written. Every change of this object goes through Settings.SaveAs, also what a setup installed (SpeechSetup.InstallAsync).
    private readonly Settings settings = LoadAsRunningInstance();
    // Claimed before the first load and held for the process's lifetime, once for every window of the process: from then on a setup
    // script run outside the app finds Private Flow running on this data folder and leaves settings.json alone.
    private static System.IO.FileStream? instanceLock;
    private static Settings LoadAsRunningInstance() { instanceLock ??= Settings.ClaimRunningInstance(); return Settings.Load(); }
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly TrayIcon tray;
    private readonly GlobalShortcuts shortcuts = new();
    private readonly MicrophoneDevices devices;
    private readonly DictationController dictation;
    private readonly UpdateController updates;
    private readonly WorkspacePages pages;
    // A page of the main window, or a section of the settings popup. ShowsPreferences places the unsaved-preferences actions in its header.
    // Notices are the page's cards; a failed save of the preferences shows there.
    private sealed record NavigationItem(string Title, string Group, Geometry Icon, FrameworkElement Page, UIElement? Actions, bool ShowsPreferences, string Keywords, bool InSettings = false,
        NoticeStack? Notices = null) : INotifyPropertyChanged
    {
        // The sidebar lists settings sections only as search results, under Settings.
        public string SidebarGroup => InSettings ? "Settings" : "";
        public override string ToString() => Title;
        // The dot next to the entry while the page holds a warning or an error the user has not seen: red for an error, yellow for a warning.
        public Brush? Marker
        {
            get => marker;
            set { if (Equals(marker, value)) return; marker = value; PropertyChanged?.Invoke(this, new(nameof(Marker))); PropertyChanged?.Invoke(this, new(nameof(MarkerStatus))); }
        }
        private Brush? marker;
        public string MarkerStatus => marker is null ? "" : "Has a notice you have not seen";
        public event PropertyChangedEventHandler? PropertyChanged;
        // An entry is itself: lists find their entries by hash, which must not change when the marker does.
        public bool Equals(NavigationItem? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
    }
    // Warnings and errors of pages the user does not see: named in the status line, marked in the sidebar.
    private readonly NoticeBoard board = new();
    private readonly List<NavigationItem> navigationItems = [];
    private NavigationItem? currentPage, currentSection;
    private WindowOverlay? settingsOverlay;
    // Filtering a list moves its selection by itself; only a choice of the user opens another page or section.
    private bool filtering;
    internal const string SettingsAutomationName = "Settings";
    // Settings sections that other places open by name.
    internal const string ShortcutsTitle = PreferenceSections.Shortcuts, ProfilesTitle = PreferenceSections.Profiles, SpeechRecognitionTitle = PreferenceSections.SpeechRecognition, MeetingDefaultsTitle = PreferenceSections.MeetingDefaults,
        ModelsTitle = PreferenceSections.Models;
    private bool contextReady, quitting;
    // While a dictation or a file transcription runs, nothing is saved, so the shortcut that ends it stays registered.
    private bool preferencesLocked;
    private ActivationShortcut activeShortcut, pendingShortcut;
    private int? dictationShortcut;
    // Null while no meeting shortcut is set.
    private ActivationShortcut? activeMeetingShortcut, pendingMeetingShortcut;
    private int? meetingShortcut;
    private const string DictationShortcutHelp = "Starts and ends recording. Click the field and press a combination with Ctrl, Alt or Win, or a function key.";
    private const string MeetingShortcutHelp = "Starts a meeting with the choices on the Meetings page and stops it as Stop and finish does. Not set: click the field and press a combination; Backspace removes it.";
    private readonly List<int> profileShortcuts = [];
    // The names and terms as the Vocabulary page shows them, saved with the other preferences.
    private ObservableCollection<VocabularyRow> vocabularyRows = [];
    // The text the list was loaded from; while the terms are the same, it is kept exactly, separators as typed.
    private string loadedVocabulary = "";
    // The profile chosen in the tray or by its shortcut. It applies to each following dictation on that dictation's own copy until it is
    // ended on the Dictate page or the app quits; it is never a preference, so choosing one leaves nothing unsaved and Save never stores it.
    private string activeProfileId = "";
    // While an update downloads, nothing can be changed that its restart would drop.
    private bool updating;
    // The language the last dictation started with, null before the first; a correction learnt from the Dictate page belongs to it.
    private string? lastDictationLanguage;
    public MainWindow()
    {
        InitializeComponent();
        Settings.RemoveLeftoverWrites();
        foreach (var (stack, page) in new[] { (DictateNotices, "Dictate"), (AudioNotices, "Microphone and speakers"), (ShortcutsNotices, ShortcutsTitle), (DictationNotices, "Language and pasting"),
            (OutputNotices, "Text style"), (AboutNotices, "About and updates") }) board.Add(stack.Notices, page);
        board.Changed += ShowUnseen;
        devices = new(Dispatcher);
        devices.Changed += RefreshDevices;
        settings.Saved += UpdateUnsaved;
        settings.Saved += ShowActiveProfile;
        ownPreferenceControls = OwnPreferenceControls();
        foreach (var control in ownPreferenceControls) control.Show(settings);
        (activeShortcut, activeMeetingShortcut) = (pendingShortcut, pendingMeetingShortcut);
        ShortcutFeedback.Text = DictationShortcutHelp; MeetingShortcutFeedback.Text = MeetingShortcutHelp;
        ShowAltGrWarnings();
        tray = new TrayIcon(OpenWindow, OpenUpdate, Quit);
        tray.AddMenuItem("Recover the last result", RecoverLast);
        tray.AddMenuItem("Meetings", () => OpenPage("Meetings"));
        tray.AddMenuItem("Settings", () => { ShowSettings(); OpenWindow(); });
        dictation = new DictationController(settings, devices, shortcuts, tray, this, Dispatcher);
        updates = new UpdateController(this, tray, offer =>
        {
            UpdateButton.Content = "Update available";
            UpdateButton.ToolTip = $"Update {UpdateController.Describe(offer)}. Download it from {(offer.FromRelease ? "GitHub releases" : "your update server")} and restart.";
        }, message => Status.Text = message, notice => AboutNotices.Report(notice));
        RefreshDevices();
        WhisperEngine.ClearStaleTemporaryFiles();
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)} (beta). Update uses your configured update server or the latest public release and restarts.";
        // Builds of one version differ by the commit after the "+" of the build identity; the tooltip has all of it.
        BuildText.Text = "Build " + (SpeechSetup.BuildIdentity.Split('+') is [var buildVersion, var commit] ? $"{buildVersion}+{commit[..Math.Min(7, commit.Length)]}" : SpeechSetup.BuildIdentity);
        BuildText.ToolTip = SpeechSetup.BuildIdentity; AutomationProperties.SetName(BuildText, "Build " + SpeechSetup.BuildIdentity);
        tray.OpenMeetings = () => OpenPage("Meetings");
        pages = new WorkspacePages(this, settings, Microphones, PlaybackDevices, UpdateUnsaved, () => new(Original.Text, Transcript.Text, null), message => Status.Text = message, board,
            (text, problem) => tray.ShowBalloon(6000, TrayIcon.MeetingsTitle, text, problem ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info), () => OpenPage("Meetings"),
            () => ShowSettings(ShortcutsTitle), title => ShowSettings(title));
        pages.CorrectionLanguage = () => lastDictationLanguage ?? settings.Language;
        pages.NextDictation = DictationPreferences;
        UpdateShortcutHint();
        SystemAudioColorHost.Content = pages.SystemAudioColorChoice;
        // A refused dictation or file transcription goes when the meeting, setup or check it waited for ends.
        pages.OperationEnded += () => { if (!pages.OperationActive) DictateNotices.Clear(DictationController.RefusedKey); };
        pages.SpeechSetUp += () => DictateNotices.Clear(RecommendationFailedKey);
        VocabularyColumn.Children.Add(pages.Libraries.Content);
        pages.PreferenceChanged += UpdateUnsaved;
        pages.ModelsChanged += UpdateUnsaved;
        pages.MeetingCaptureChanged += capturing => SetState(capturing ? BrandState.Recording : BrandState.Idle, capturing ? "Private Flow — recording a meeting" : "Private Flow — ready");
        BuildNavigation();
        ShowUnseen();
        contextReady = true;
        UpdateContext(); UpdateUnsaved();
        if (settings.DroppedShortcuts.Count > 0) ReportDroppedShortcuts();
        if (settings.Unread is { } unread) ReportUnreadableSettings(unread);
        if (settings.MissingRewriteEndpoint)
            DictateNotices.Raise(new(MissingEndpointKey, NoticeKind.Warning, "Configure your API endpoint",
                "This settings file has no API base URL. The built-in endpoint was removed. Set your endpoint under Settings > Text style and save preferences to use Concise or API meeting notes; until then they use their local fallback."));
        if (UpdateCheck.LegacyTokenWithoutServer)
            AboutNotices.Report(new(UpdateSourceChangedKey, NoticeKind.Warning, "Updates now come from public releases", UpdateServer.LegacyNotice));
        SourceInitialized += (_, _) =>
        {
            var window = new WindowInteropHelper(this).Handle;
            shortcuts.Attach(window);
            UseDarkTitleBar(window);
            if (App.DiagnosticMode) { Status.Text = "Diagnostic instance: capture, shortcuts and insertion disabled."; return; }
            if (UpdateCheck.LegacyTokenWithoutServer)
                tray.ShowBalloon(10000, "Private Flow updates", UpdateServer.LegacyNotice, Forms.ToolTipIcon.Warning);
            dictationShortcut = shortcuts.Register(activeShortcut, DictationShortcutPressed);
            if (dictationShortcut is null)
            {
                DictateNotices.Report(new(DictationShortcutTakenKey, NoticeKind.Warning, $"{ShortcutName(activeShortcut)} is used by another app",
                    "The dictation shortcut does not work until you choose another one; Record here still does.", Action: new("Choose another shortcut", () => ShowSettings(ShortcutsTitle))));
                // The window starts hidden, so the status line alone would go unnoticed.
                tray.ShowBalloon(5000, "Private Flow", $"{ShortcutName(activeShortcut)} is used by another app. Open Private Flow to choose another shortcut.", Forms.ToolTipIcon.Warning);
            }
            if (activeMeetingShortcut is { } meeting && (meetingShortcut = shortcuts.Register(meeting, MeetingShortcutPressed)) is null)
            {
                pages.MeetingNotices.Report(new(MeetingShortcutTakenKey, NoticeKind.Warning, $"{ShortcutName(meeting)} is used by another app",
                    "The meeting shortcut does not work until you choose another one; Start meeting here still does.", Action: new("Choose another shortcut", () => ShowSettings(ShortcutsTitle))));
                tray.ShowBalloon(5000, "Private Flow", $"The meeting shortcut {ShortcutName(meeting)} is used by another app. Open Private Flow to choose another one.", Forms.ToolTipIcon.Warning);
            }
            RefreshProfiles();
        };
        Closing += OnClosing;
        // A running meeting capture stays on screen (minimized at most) so it never goes unnoticed; a transcript being finished
        // shows its own progress window instead.
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && !pages.MeetingCapturing) Hide(); ShowWhereTheUserIs(); };
        IsVisibleChanged += (_, _) => ShowWhereTheUserIs();
        ShowWhereTheUserIs();
        timer.Tick += TimerTick;
        timer.Start();
        updates.Start();
        if (!App.DiagnosticMode && UpdateController.InstalledFolder() is not null) _ = FollowSpeechRecommendationAsync();
    }
    private const string NothingRecoveredKey = "dictate-nothing-recovered", RecommendationFailedKey = "speech-recommendation-failed", CopyFailedKey = "dictate-copy-failed", ExportFailedKey = "dictate-export-failed";
    private async Task FollowSpeechRecommendationAsync()
    {
        var (notice, error, changed) = await SpeechSetup.FollowRecommendationAsync(settings);
        UpdateUnsaved(); pages.RefreshSpeechEngine(changed);
        // A failure retries at every start, so it stays as a card instead of a balloon each time.
        if (error is not null)
            DictateNotices.Report(new(RecommendationFailedKey, NoticeKind.Error, "The recommended speech model was not set up", NoticeText.Short(error), NoticeText.Detail(error),
                new("Open Speech recognition", () => ShowSettings(SpeechRecognitionTitle))));
        else
        {
            DictateNotices.Clear(RecommendationFailedKey);
            if (notice is not null) tray.ShowBalloon(5000, "Private Flow", notice, Forms.ToolTipIcon.Info);
        }
    }
    // The offered waits, plus a value entered in settings.json by hand so it is shown as it is.
    private static readonly int[] PolishWaitChoices = [10, 30, 60, 120, 300, 600, 1800];
    private void LoadPolishWait(int seconds)
    {
        PolishWait.Items.Clear();
        foreach (var choice in PolishWaitChoices.Append(seconds).Distinct().Order())
            PolishWait.Items.Add(new ComboBoxItem { Content = choice % 60 == 0 ? $"{choice / 60} min" : $"{choice} s", Tag = choice });
        PolishWait.SelectedItem = PolishWait.Items.Cast<ComboBoxItem>().First(item => (int)item.Tag == seconds);
    }
    private int SelectedPolishWait() => (PolishWait.SelectedItem as ComboBoxItem)?.Tag as int? ?? settings.PolishWaitSeconds;
    private OutputStyle SelectedOutput() => OutputExact.IsChecked == true ? OutputStyle.Exact : OutputConcise.IsChecked == true ? OutputStyle.Concise : OutputLocal.IsChecked == true ? OutputStyle.LocalPolish : OutputStyle.Clean;
    private ShortcutBehavior SelectedShortcutBehavior() => HoldToTalkOption.IsSelected ? ShortcutBehavior.HoldToTalk : ShortcutBehavior.Toggle;
    // The controls of the window's own preferences, in the order they are shown. Language and the devices are locked only while a
    // dictation records (ShowRecording).
    private readonly PreferenceControl[] ownPreferenceControls;
    private PreferenceControl[] OwnPreferenceControls() =>
    [
        new([nameof(Settings.AutoInsert)], source => AutoInsert.IsChecked = source.AutoInsert, target => target.AutoInsert = AutoInsert.IsChecked == true, true, AutoInsert),
        new([nameof(Settings.ShortcutBehavior)], source => ShortcutBehaviorChoice.SelectedItem = source.ShortcutBehavior == ShortcutBehavior.HoldToTalk ? HoldToTalkOption : ToggleOption,
            target => target.ShortcutBehavior = SelectedShortcutBehavior(), true, ShortcutBehaviorChoice),
        new([nameof(Settings.Vocabulary)], source => ShowVocabulary(source.Vocabulary), target => target.Vocabulary = CurrentVocabulary(), true, VocabularyEditor),
        new([nameof(Settings.OutputStyle)], source => (source.OutputStyle switch { OutputStyle.Exact => OutputExact, OutputStyle.Concise => OutputConcise, OutputStyle.LocalPolish => OutputLocal, _ => OutputClean }).IsChecked = true,
            target => target.OutputStyle = SelectedOutput(), true, OutputExact, OutputClean, OutputConcise, OutputLocal),
        new([nameof(Settings.SpokenFormatting)], source => Formatting.IsChecked = source.SpokenFormatting, target => target.SpokenFormatting = Formatting.IsChecked == true, true, Formatting),
        new([nameof(Settings.TechnicalMode)], source => Technical.IsChecked = source.TechnicalMode, target => target.TechnicalMode = Technical.IsChecked == true, true, Technical),
        new([nameof(Settings.LiteralMode)], source => Literal.IsChecked = source.LiteralMode, target => target.LiteralMode = Literal.IsChecked == true, true, Literal),
        new([nameof(Settings.RewriteEndpoint)], source => RewriteEndpoint.Text = source.RewriteEndpoint, target => target.RewriteEndpoint = RewriteEndpoint.Text.Trim(), true, RewriteEndpoint),
        new([nameof(Settings.RewriteModel)], source => RewriteModel.Text = source.RewriteModel, target => target.RewriteModel = RewriteModel.Text.Trim(), true, RewriteModel),
        new([nameof(Settings.RewriteKeyVariable)], source => RewriteKeyVariable.Text = source.RewriteKeyVariable, target => target.RewriteKeyVariable = RewriteKeyVariable.Text.Trim(), true, RewriteKeyVariable),
        new([nameof(Settings.PolishWaitSeconds)], source => LoadPolishWait(source.PolishWaitSeconds), target => target.PolishWaitSeconds = SelectedPolishWait(), true, PolishWait),
        new([nameof(Settings.Language)], source => { foreach (ComboBoxItem item in Languages.Items) if ((string)item.Tag == source.Language) Languages.SelectedItem = item; },
            target => target.Language = (string)((ComboBoxItem)Languages.SelectedItem).Tag, true),
        new([nameof(Settings.MicrophoneId)], source => { if (Microphones.Items.OfType<MicrophoneDevice>().FirstOrDefault(device => device.Id == source.MicrophoneId) is { } microphone) Microphones.SelectedItem = microphone; },
            target => target.MicrophoneId = (Microphones.SelectedItem as MicrophoneDevice)?.Id ?? target.MicrophoneId, true),
        new([nameof(Settings.PlaybackDeviceId)], source => { if (PlaybackDevices.Items.OfType<MicrophoneDevice>().FirstOrDefault(device => device.Id == source.PlaybackDeviceId) is { } output) PlaybackDevices.SelectedItem = output; },
            target => target.PlaybackDeviceId = (PlaybackDevices.SelectedItem as MicrophoneDevice)?.Id ?? target.PlaybackDeviceId, true),
        // The combinations the shortcut fields show; Save preferences registers them before storing them.
        new([nameof(Settings.ShortcutModifiers), nameof(Settings.ShortcutKey)],
            source => { pendingShortcut = new(source.ShortcutModifiers, source.ShortcutKey); ShortcutBox.Text = ShortcutName(pendingShortcut); ShowShortcutHint(DictationHintKey, null); ShortcutsNotices.Clear(DictationRejectedKey); },
            target => { target.ShortcutModifiers = pendingShortcut.Modifiers; target.ShortcutKey = pendingShortcut.Key; }, false, ShortcutBox),
        new([nameof(Settings.MeetingShortcutModifiers), nameof(Settings.MeetingShortcutKey)],
            source => { pendingMeetingShortcut = SavedMeetingShortcut(source); MeetingShortcutBox.Text = MeetingShortcutName(pendingMeetingShortcut); ShowShortcutHint(MeetingHintKey, null); ShortcutsNotices.Clear(MeetingRejectedKey); },
            target => { target.MeetingShortcutModifiers = pendingMeetingShortcut?.Modifiers ?? 0; target.MeetingShortcutKey = pendingMeetingShortcut?.Key ?? 0; }, false, MeetingShortcutBox),
    ];
    private IEnumerable<PreferenceControl> PreferenceControls => ownPreferenceControls.Concat(pages.PreferenceControls);
    // Why the preference controls do not cover the settings of PreferenceSections.Table; null when they do. For the verification.
    internal string? PreferenceControlProblem => PreferenceSections.ControlProblem(PreferenceControls.SelectMany(control => control.Names));
    // The stored preferences with the window's shown dictation choices and the active profile on a copy: what a dictation starting now uses.
    private Settings DictationPreferences()
    {
        var next = ShownDictationChoices();
        if (ActiveProfile() is { } profile) next.ApplyProfile(profile, true);
        return next;
    }
    private Settings ShownDictationChoices()
    {
        var shown = settings.Snapshot();
        foreach (var control in PreferenceControls.Where(control => control.ForDictation)) control.Read(shown);
        return shown;
    }
    private DictationProfile? ActiveProfile() => activeProfileId.Length == 0 ? null : settings.Profiles.FirstOrDefault(profile => profile.Id == activeProfileId);
    // Every preference as the window and the settings popup show it, saved or not; an active profile is not one of them.
    private Settings ShownPreferences()
    {
        var shown = settings.Snapshot();
        foreach (var control in PreferenceControls) control.Read(shown);
        return shown;
    }
    private List<string> UnsavedSections()
    {
        var sections = PreferenceSections.Changed(ShownPreferences(), settings);
        // Text typed into the vocabulary entry and not added yet counts too, also when it only repeats listed terms.
        if (VocabularyTerms.Split(VocabularyEntry.Text).Count > 0 && !sections.Contains(PreferenceSections.Vocabulary)) sections.Add(PreferenceSections.Vocabulary);
        return sections;
    }
    internal IReadOnlyList<string> UnsavedPreferenceSections => UnsavedSections();
    private bool HasUnsavedPreferences() => UnsavedSections().Count > 0;
    private void UpdateUnsaved()
    {
        if (!contextReady) return;
        ShowLocalPolishSetup();
        var unsaved = HasUnsavedPreferences();
        PreferenceActions.Visibility = SettingsUnsavedDot.Visibility = unsaved ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PreferenceChanged(object sender, RoutedEventArgs e) => UpdateUnsaved();
    private const string LocalPolishNotSetUpKey = "local-polish-not-set-up";
    // Local polish chosen, shown or saved, without its model: every such dictation pastes Clean. Said where it is chosen, with the setup
    // one click away; nothing downloads before that click. The Dictate card is the one pointed to; the cards in Settings stand quietly.
    private void ShowLocalPolishSetup()
    {
        // What the next dictation uses: the shown text style, an active profile's, and the stored model choice under Models, as a model
        // chosen there and not saved does not reach a dictation.
        var next = DictationPreferences();
        var model = ModelList.PolishModel(next);
        var installed = ModelList.IsInstalled(next, model);
        var chosen = next.OutputStyle == OutputStyle.LocalPolish;
        var card = new Notice(LocalPolishNotSetUpKey, NoticeKind.Warning, "Local polish is not set up",
            "Dictations in Local polish use Clean until its model is on this PC. It downloads only when you press the button.", Action: new(LocalRewriter.SetupLabel(model.Pinned.Bytes), SetUpLocalPolish));
        foreach (var (stack, chosenThere, standing) in new[] { (DictateNotices, chosen, false), (OutputNotices, chosen, true), (pages.ProfilesNotices, pages.ProfilesChooseLocalPolish, true) })
            if (chosenThere && !installed) stack.Raise(card with { Standing = standing }); else stack.Clear(LocalPolishNotSetUpKey);
    }
    // Opens Models, where the model chosen for Local polish is installed with the bar.
    private void SetUpLocalPolish() { ShowSettings(ModelsTitle); pages.InstallForLocalPolish(ModelList.PolishModel(DictationPreferences())); }
    void IDictationView.SetUpLocalPolish() => SetUpLocalPolish();
    private void ShowVocabulary(string vocabulary)
    {
        loadedVocabulary = vocabulary;
        VocabularyList.ItemsSource = vocabularyRows = new(VocabularyTerms.Split(vocabulary).Select(term => new VocabularyRow(term)));
        VocabularyHint.Visibility = Visibility.Collapsed;
        VocabularyChanged();
    }
    private void VocabularyChanged()
    {
        VocabularyCount.Text = vocabularyRows.Count switch { 0 => "No names or terms yet", 1 => "1 term", var count => $"{count:N0} terms" };
        VocabularyList.Visibility = vocabularyRows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateUnsaved();
    }
    private void AddTermsClick(object sender, RoutedEventArgs e) => AddTerms();
    // The field accepts line breaks so that a pasted list keeps them as separators; Enter adds instead.
    private void VocabularyEntryKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTerms();
    }
    private void VocabularyEntryChanged(object sender, TextChangedEventArgs e) { VocabularyHint.Visibility = Visibility.Collapsed; UpdateUnsaved(); }
    private IEnumerable<string> ListedTerms() => vocabularyRows.Select(row => row.Text);
    // What the page stands for: the loaded text while the terms are those it was loaded from, otherwise the terms joined. Terms
    // still typed in the entry field count, as the single field counted while it was being typed in.
    private string CurrentVocabulary()
    {
        var terms = ListedTerms().Concat(VocabularyTerms.Additions(ListedTerms(), VocabularyEntry.Text).Added).ToList();
        return terms.SequenceEqual(VocabularyTerms.Split(loadedVocabulary)) ? loadedVocabulary.Trim() : VocabularyTerms.Join(terms);
    }
    private void AddTerms()
    {
        var (added, present) = VocabularyTerms.Additions(ListedTerms(), VocabularyEntry.Text);
        if (added.Count == 0 && present.Count == 0) return;
        var rows = added.Select(term => new VocabularyRow(term)).ToList();
        foreach (var row in rows) vocabularyRows.Add(row);
        VocabularyEntry.Clear(); VocabularyEntry.Focus();
        if (present.Count > 0)
        {
            var existing = vocabularyRows.First(row => string.Equals(row.Text, present[0], StringComparison.OrdinalIgnoreCase));
            VocabularyList.ScrollIntoView(existing);
            VocabularyHint.Text = (added.Count > 0 ? $"Added {added.Count}. " : "") + (present.Count == 1 ? $"“{existing.Text}” is already in the list." : $"“{existing.Text}” and {present.Count - 1} more are already in the list.");
            VocabularyHint.Visibility = Visibility.Visible;
        }
        else VocabularyList.ScrollIntoView(rows[^1]);
        VocabularyChanged();
    }
    private void VocabularySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = VocabularyList.SelectedItems.Count;
        DeleteTermsButton.IsEnabled = selected > 0;
        var label = selected > 0 ? $"Delete selected ({selected:N0})" : "Delete selected";
        DeleteTermsButton.Content = label; AutomationProperties.SetName(DeleteTermsButton, label);
    }
    // Delete removes the selected rows wherever the focus is in the editor, except while it edits text typed in the entry field.
    private void VocabularyEditorKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Delete || VocabularyList.SelectedItems.Count == 0 || VocabularyEntry.IsKeyboardFocusWithin && VocabularyEntry.Text.Length > 0) return;
        e.Handled = true;
        DeleteSelectedTerms();
    }
    // A row's box takes no focus of its own, so a click on it moves the focus to its row, where the list's keys apply.
    private void VocabularyBoxClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject box && ItemsControl.ContainerFromElement(VocabularyList, box) is ListBoxItem row) row.Focus();
    }
    private void DeleteTermsClick(object sender, RoutedEventArgs e) => DeleteSelectedTerms();
    // Discard of the unsaved preferences brings deleted terms back, so deleting asks nothing.
    private void DeleteSelectedTerms()
    {
        foreach (var row in VocabularyList.SelectedItems.Cast<VocabularyRow>().ToList()) vocabularyRows.Remove(row);
        VocabularyHint.Visibility = Visibility.Collapsed;
        VocabularyChanged();
        if (vocabularyRows.Count > 0) VocabularyList.Focus(); else VocabularyEntry.Focus();
    }
    private void ContextChanged(object sender, RoutedEventArgs e)
    {
        // A dictation refused for want of a microphone no longer is once one is selected.
        if (Microphones.SelectedIndex >= 0) DictateNotices.Clear(DictationController.NoMicrophoneSelectedKey);
        UpdateContext(); UpdateUnsaved();
    }
    private void DiscardClick(object sender, RoutedEventArgs e) => DiscardPreferences();
    private void DiscardPreferences()
    {
        // The shared settings hold only stored values, so showing them again leaves nothing of the discarded ones behind.
        VocabularyEntry.Clear();
        foreach (var control in PreferenceControls) control.Show(settings);
        ShowAltGrWarnings();
        // A failed save was about the values just discarded.
        foreach (var item in navigationItems) item.Notices?.Clear(PreferencesNotSavedKey);
        UpdateUnsaved(); Status.Text = "Unsaved changes discarded.";
    }
    // Asks whether to save unsaved preferences before they would be lost. False keeps them and stops what was about to happen.
    private async Task<bool> SettleUnsavedAsync()
    {
        pages.CommitEdits();
        var sections = UnsavedSections();
        if (sections.Count == 0) return true;
        switch (await ConfirmationDialog.AskToSaveAsync(this, PreferenceSections.Describe(sections)))
        {
            case UnsavedChoice.Save: return SavePreferences();
            case UnsavedChoice.DontSave: DiscardPreferences(); return true;
            default: return false;
        }
    }
    // Pickers under the composer change the same, still unsaved, preferences as the settings pages.
    private void ChipClick(object sender, RoutedEventArgs e)
    {
        var chip = (System.Windows.Controls.Button)sender;
        IEnumerable<(string, bool, Action)> choices =
            chip == MicrophoneChip ? Microphones.Items.OfType<MicrophoneDevice>().Select(device => (device.Name, ReferenceEquals(Microphones.SelectedItem, device), (Action)(() => Microphones.SelectedItem = device)))
            : chip == LanguageChip ? Languages.Items.OfType<ComboBoxItem>().Select(language => ((string)language.Content, ReferenceEquals(Languages.SelectedItem, language), (Action)(() => Languages.SelectedItem = language)))
            : chip == OutputChip ? new[] { (OutputExact, "Exact"), (OutputClean, "Clean"), (OutputConcise, "Concise (API)"), (OutputLocal, "Local polish") }.Select(choice => (choice.Item2, choice.Item1.IsChecked == true, (Action)(() => choice.Item1.IsChecked = true)))
            : [("Paste into the selected field", AutoInsert.IsChecked == true, () => AutoInsert.IsChecked = true), ("Review first", AutoInsert.IsChecked != true, () => AutoInsert.IsChecked = false)];
        WorkspacePages.Choices(chip, choices);
    }
    // The chips under the composer show what the next dictation uses: the current, possibly unsaved, choices, or while a profile is active
    // its language, style and pasting, read-only and named as the profile's, so a chip never says Clean while text goes to the API.
    private void UpdateContext()
    {
        if (!contextReady) return;
        var next = DictationPreferences(); var profile = ActiveProfile();
        MicrophoneChipText.Text = Microphones.SelectedItem is MicrophoneDevice device ? (device.Id.Length == 0 ? "Default microphone" : device.Name) : "No microphone";
        LanguageChipText.Text = Languages.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == next.Language)?.Content as string ?? "Automatic";
        OutputChipText.Text = next.OutputStyle switch { OutputStyle.Exact => "Exact", OutputStyle.Concise => "Concise (API)", OutputStyle.LocalPolish => "Local polish", _ => "Clean" };
        PasteChipText.Text = next.AutoInsert ? "Auto-paste" : "Review first";
        foreach (var chip in new[] { LanguageChip, OutputChip, PasteChip }) chip.IsEnabled = !preferencesLocked && profile is null;
        // A screen reader announces the current choice with the chip, as the Meetings chips do.
        foreach (var (chip, text) in new[] { (MicrophoneChip, MicrophoneChipText), (LanguageChip, LanguageChipText), (OutputChip, OutputChipText), (PasteChip, PasteChipText) })
            AutomationProperties.SetName(chip, profile is not null && chip != MicrophoneChip ? $"{chip.ToolTip} of the profile {profile.Name}: {text.Text}" : $"{chip.ToolTip}: {text.Text}");
    }
    private void BuildNavigation()
    {
        Geometry Icon(string key) => (Geometry)FindResource(key);
        ((System.Windows.Controls.Panel)SettingsDialog.Parent).Children.Remove(SettingsDialog);
        navigationItems.AddRange([
            new("Dictate", "", Icon("IconMicrophone"), DictatePage, null, true, "record transcript copy export recover clear transcribe file original changes", Notices: DictateNotices),
            new("Meetings", "", Icon("IconMeetings"), pages.Meetings.Content, pages.Meetings.Actions, true, "record start stop title system audio languages retain unfinished wave"),
            new("History", "", Icon("IconRecover"), pages.History.Content, pages.History.Actions, false, "archive past meetings transcript notes model written evidence search hits speaker re-transcribe export delete expired retention"),
            new("Vocabulary", "", Icon("IconBook"), VocabularyPage, pages.Libraries.Actions, true, "names terms spellings corrections aliases snippets teach import export phrases", Notices: pages.LibrariesNotices),
            new(PreferenceSections.Audio, "", Icon("IconMicrophone"), AudioSection, null, true, "audio microphone input output playback device system audio loopback colour color swatch wave", true, AudioNotices),
            new(ShortcutsTitle, "", Icon("IconKeyboard"), ShortcutsSection, null, true, "shortcut hotkey toggle hold to talk meeting profile keys", true, ShortcutsNotices),
            new(PreferenceSections.LanguageAndPasting, "Dictation", Icon("IconSliders"), DictationSection, null, true, "dictation language auto-paste paste insertion formatting spoken technical literal", true, DictationNotices),
            new(PreferenceSections.TextStyle, "Dictation", Icon("IconSparkle"), OutputPage, null, true, "output style exact clean concise local polish api endpoint base url model key variable environment rewrite wait queue meeting notes", true, OutputNotices),
            new(ProfilesTitle, "Dictation", Icon("IconUser"), pages.Profiles.Content, pages.Profiles.Actions, true, "profile shortcut application tray", true, pages.ProfilesNotices),
            new(MeetingDefaultsTitle, "Meetings", Icon("IconMeetings"), pages.MeetingSettings.Content, pages.MeetingSettings.Actions, true, "meetings languages default retain audio retention days keep notes", true, pages.MeetingSettingsNotices),
            new(ModelsTitle, "System", Icon("IconDownload"), pages.Models.Content, pages.Models.Actions, true, "models install remove download size disk gpu processor cpu runs on used for whisper silero qwen llama local polish meeting notes style api rules extractive", true, pages.ModelsNotices),
            new(SpeechRecognitionTitle, "System", Icon("IconPulse"), pages.SpeechEngine.Content, pages.SpeechEngine.Actions, true, "speech recognition engine setup status diagnostics export synthetic check whisper model apply processor gpu cpu silero detection polish paths warm", true, pages.SpeechNotices),
            new("About and updates", "System", Icon("IconInfo"), AboutPage, null, true, "about update version privacy tray quit", true, AboutNotices)]);
        foreach (var item in navigationItems.Where(item => item.Page.Parent is null))
        { item.Page.Visibility = Visibility.Collapsed; (item.InSettings ? SettingsPages : Pages).Children.Add(item.Page); }
        var view = new ListCollectionView(navigationItems);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.SidebarGroup)));
        view.Filter = entry => entry is NavigationItem item && (!item.InSettings || SearchBox.Text.Trim().Length > 0) && Matches(item, SearchBox.Text);
        Navigation.ItemsSource = view;
        var sections = new ListCollectionView(navigationItems.Where(item => item.InSettings).ToList());
        sections.GroupDescriptions.Add(new PropertyGroupDescription(nameof(NavigationItem.Group)));
        sections.Filter = entry => Matches((NavigationItem)entry, SettingsSearchBox.Text);
        SettingsNavigation.ItemsSource = sections;
        Navigation.SelectedItem = navigationItems[0];
        SettingsNavigation.SelectedItem = sections.GetItemAt(0);
    }
    private static bool Matches(NavigationItem item, string search)
    {
        var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.All(word => item.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || item.Keywords.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
    private void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (filtering || Navigation.SelectedItem is not NavigationItem item || ReferenceEquals(item, currentPage)) return;
        if (item.InSettings) { ShowSettings(item); return; }
        if (currentPage is not null) currentPage.Page.Visibility = Visibility.Collapsed;
        currentPage = item;
        item.Page.Visibility = Visibility.Visible;
        PageTitle.Text = item.Title;
        PlaceActions();
        ShowWhereTheUserIs();
    }
    // The unsaved-preferences actions are one element: in the settings popup while it is open, otherwise on the page.
    private void PlaceActions()
    {
        PreferenceSlot.Content = SettingsPreferenceSlot.Content = null;
        var (item, slot) = settingsOverlay is null ? (currentPage, PreferenceSlot) : (currentSection, SettingsPreferenceSlot);
        if (item?.ShowsPreferences == true) slot.Content = PreferenceActions;
        PageActions.Content = currentPage?.Actions;
        SettingsActions.Content = currentSection?.Actions;
    }
    // Opens a page, or the settings popup at the section of that name.
    internal async void ShowPage(string title)
    {
        var item = navigationItems.First(item => item.Title == title);
        if (item.InSettings) { ShowSettings(item); return; }
        if (!await CloseSettingsAsync()) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        Navigation.SelectedItem = item;
    }
    private void OpenPage(string title) { ShowPage(title); OpenWindow(); }
    internal void ShowSettings(string? section = null) =>
        ShowSettings(section is null ? currentSection ?? navigationItems.First(item => item.InSettings) : navigationItems.First(item => item.InSettings && item.Title == section));
    private void ShowSettings(NavigationItem section)
    {
        // The update notice covers the window until the restart.
        if (updating) return;
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        if (SettingsSearchBox.Text.Length > 0) SettingsSearchBox.Clear();
        if (settingsOverlay is null)
        {
            settingsOverlay = new WindowOverlay(this, SettingsDialog, SettingsAutomationName);
            settingsOverlay.Closed += SettingsClosed;
        }
        SettingsNavigation.SelectedItem = section;
        PlaceActions();
        ShowWhereTheUserIs();
        SettingsSearchBox.Focus();
    }
    internal bool SettingsOpen => settingsOverlay is not null;
    // For the verification, which replaces the setup scripts with stand-ins.
    internal WorkspacePages Workspace => pages;
    // For the verification, which starts a dictation with a synthetic source for the microphone.
    internal DictationController Dictation => dictation;
    internal string SettingsSection => currentSection?.Title ?? "";
    internal void CloseSettings() => _ = CloseSettingsAsync();
    // Leaving the popup asks first while preferences are unsaved, so a change made there never stays pending unnoticed. False when it stays open.
    internal async Task<bool> CloseSettingsAsync()
    {
        if (settingsOverlay is null) return true;
        if (!await SettleUnsavedAsync()) return false;
        settingsOverlay?.Close();
        return true;
    }
    // The preferences a dictation starting now would take, read as DictationController reads them.
    internal Settings DictationSettings() => DictationPreferences();
    // For the verification of the invariant on the settings field.
    internal Settings SharedSettings => settings;
    private void SettingsClosed()
    {
        settingsOverlay = null;
        PlaceActions();
        ShowWhereTheUserIs();
    }
    private void SettingsClick(object sender, RoutedEventArgs e) => ShowSettings();
    private void CloseSettingsClick(object sender, RoutedEventArgs e) => CloseSettings();
    private void OpenProfilesClick(object sender, RoutedEventArgs e) => ShowSettings(ProfilesTitle);
    // Bubbling, so an open drop-down or a shortcut field that takes Escape itself keeps the popup open.
    private void SettingsKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseSettings(); e.Handled = true;
    }
    private void SettingsNavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (filtering || SettingsNavigation.SelectedItem is not NavigationItem section || ReferenceEquals(section, currentSection)) return;
        if (currentSection is not null) currentSection.Page.Visibility = Visibility.Collapsed;
        currentSection = section;
        section.Page.Visibility = Visibility.Visible;
        SettingsTitle.Text = section.Title;
        PlaceActions();
        ShowWhereTheUserIs();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => Filter(Navigation, currentPage);
    private void SettingsSearchChanged(object sender, TextChangedEventArgs e) => Filter(SettingsNavigation, currentSection);
    // The open page or section stays open and selected while it matches; otherwise nothing is selected.
    private void Filter(System.Windows.Controls.ListBox list, NavigationItem? open)
    {
        if (list.ItemsSource is not ListCollectionView view) return;
        filtering = true;
        try { view.Refresh(); list.SelectedItem = open is not null && view.Contains(open) ? open : null; }
        finally { filtering = false; }
    }
    private void SearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SearchBox.Clear(); Navigation.Focus(); e.Handled = true; }
        else if (e.Key == Key.Down && Navigation.Items.Count > 0) { (Navigation.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && Navigation.Items.Count > 0 && Navigation.Items[0] is NavigationItem first)
        {
            e.Handled = true;
            if (first.InSettings) { ShowSettings(first); return; }
            SearchBox.Clear(); Navigation.SelectedItem = first;
            (Navigation.ItemContainerGenerator.ContainerFromItem(first) as UIElement)?.Focus();
        }
    }
    private void SettingsSearchKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsSearchBox.Text.Length > 0) { SettingsSearchBox.Clear(); e.Handled = true; }
        else if (e.Key == Key.Down && SettingsNavigation.Items.Count > 0) { (SettingsNavigation.ItemContainerGenerator.ContainerFromIndex(0) as UIElement)?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && SettingsNavigation.Items.Count > 0 && SettingsNavigation.Items[0] is NavigationItem first)
        {
            SettingsSearchBox.Clear(); SettingsNavigation.SelectedItem = first;
            (SettingsNavigation.ItemContainerGenerator.ContainerFromItem(first) as UIElement)?.Focus(); e.Handled = true;
        }
    }
    private void FocusSearchOnSlash(object sender, TextCompositionEventArgs e)
    {
        if (e.Text != "/" || Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        (settingsOverlay is null ? SearchBox : SettingsSearchBox).Focus(); e.Handled = true;
    }
    // The page or settings section on screen, or none while the window is hidden or minimised.
    private void ShowWhereTheUserIs()
    {
        if (!contextReady) return;
        board.Show(!IsVisible || WindowState == WindowState.Minimized ? null : settingsOverlay is not null ? currentSection?.Title : currentPage?.Title);
    }
    // The newest unseen warning or error takes the place of the status line, naming its page, until that page is seen or the notice
    // ends; every page holding one is marked. A success line never stands next to a problem the user has not seen.
    private void ShowUnseen()
    {
        var pointer = board.Pointer;
        StatusPointerText.Text = pointer ?? "";
        StatusPointer.Visibility = pointer is null ? Visibility.Collapsed : Visibility.Visible;
        Status.Visibility = pointer is null ? Visibility.Visible : Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(StatusPointer, pointer ?? "");
        foreach (var item in navigationItems)
            item.Marker = board.Marker(item.Title) switch { NoticeKind.Error => (Brush)FindResource("ErrorText"), NoticeKind.Warning => (Brush)FindResource("WarningText"), _ => null };
        var settingsMarker = navigationItems.Where(item => item.InSettings).Select(item => item.Marker).FirstOrDefault(marker => marker is not null);
        SettingsUnseenDot.Fill = settingsMarker; SettingsUnseenDot.Visibility = settingsMarker is null ? Visibility.Collapsed : Visibility.Visible;
    }
    private void StatusPointerClick(object sender, RoutedEventArgs e) => board.OpenNewest(ShowPage);
    internal string? StatusPointerShown => StatusPointer.Visibility == Visibility.Visible ? StatusPointerText.Text : null;
    internal Brush? MarkerOf(string title) => navigationItems.First(item => item.Title == title).Marker;
    private void SetState(BrandState state, string text)
    {
        tray.SetState(state, text);
        StatusDot.Fill = new SolidColorBrush(state switch { BrandState.Recording => System.Windows.Media.Color.FromRgb(0xF0, 0x52, 0x4F), BrandState.Processing => System.Windows.Media.Color.FromRgb(0xE3, 0xB3, 0x41), _ => System.Windows.Media.Color.FromRgb(0x3F, 0xB9, 0x50) });
    }
    // Match the black window: dark title bar, black caption on Windows 11 (ignored on 10).
    internal static void UseDarkTitleBar(nint handle)
    {
        int on = 1, black = 0x000000, white = 0xFFFFFF, edge = 0x262626;
        DwmSetWindowAttribute(handle, 20, ref on, sizeof(int));
        DwmSetWindowAttribute(handle, 35, ref black, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref white, sizeof(int));
        DwmSetWindowAttribute(handle, 34, ref edge, sizeof(int));
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);
    private void OpenWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    internal void DictationShortcutPressed()
    {
        if (EditingShortcut) ShowRegisteredInField(activeShortcut, DictationOwner);
        else dictation.Toggle(activeShortcut);
    }
    // Capturing a shortcut preference must not activate a registered shortcut itself.
    private bool EditingShortcut => ShortcutBox.IsKeyboardFocusWithin || MeetingShortcutBox.IsKeyboardFocusWithin || pages.EditingProfileShortcut;
    private const string DictationOwner = "the dictation shortcut", MeetingOwner = "the meeting shortcut";
    // What a shortcut field is doing, such as the modifiers held so far or that Save preferences applies the new combination, as a note
    // card above the fields where it is seen; the field's own line keeps its help. Null ends the card.
    private const string DictationHintKey = "shortcut-dictation-hint", MeetingHintKey = "shortcut-meeting-hint";
    private void ShowShortcutHint(string key, string? hint)
    {
        if (hint is null) ShortcutsNotices.Clear(key);
        else ShortcutsNotices.Report(new(key, NoticeKind.Note, key == DictationHintKey ? "Dictation shortcut" : "Meeting shortcut", hint));
    }
    private const string DictationRejectedKey = "shortcut-dictation-rejected", MeetingRejectedKey = "shortcut-meeting-rejected", ShortcutsLockedKey = "shortcuts-locked",
        DictationShortcutTakenKey = "shortcut-taken-dictation", MeetingShortcutTakenKey = "shortcut-taken-meeting";
    // A combination a shortcut field cannot take, or one taken by another shortcut, as a card above the shortcut fields; the field's line
    // keeps its help.
    private void RejectShortcut(string key, string title, string text) => ShortcutsNotices.Report(new(key, NoticeKind.Warning, title, text));
    // A registered shortcut pressed in a shortcut field reaches Private Flow as a hotkey, never as keys of the field, so the field says what
    // the combination already is instead of showing nothing.
    private void ShowRegisteredInField(ActivationShortcut pressed, string owner)
    {
        var name = ShortcutName(pressed);
        if (ShortcutBox.IsKeyboardFocusWithin)
        {
            if (owner == DictationOwner) { ShowShortcutHint(DictationHintKey, $"{name} is the current dictation shortcut."); ShortcutsNotices.Clear(DictationRejectedKey); }
            else RejectShortcut(DictationRejectedKey, $"{name} is already in use", $"It is {owner}. Choose a different combination for dictation.");
        }
        else if (MeetingShortcutBox.IsKeyboardFocusWithin)
        {
            if (owner == MeetingOwner) { ShowShortcutHint(MeetingHintKey, $"{name} is the current meeting shortcut."); ShortcutsNotices.Clear(MeetingRejectedKey); }
            else RejectShortcut(MeetingRejectedKey, $"{name} is already in use", $"It is {owner}. Choose a different combination for meetings.");
        }
        else pages.ProfilesNotices.Report(new(WorkspacePages.ProfileShortcutKey, NoticeKind.Warning, $"{name} is already in use", $"It is {owner}. Choose a different combination for this profile."));
    }
    internal void MeetingShortcutPressed()
    {
        if (quitting) return;
        if (EditingShortcut) { if (activeMeetingShortcut is { } meeting) ShowRegisteredInField(meeting, MeetingOwner); return; }
        if (dictation.IsQueued) { dictation.ShowWaiting(); return; }
        var decision = pages.PressMeetingShortcut(dictation.IsRecording || dictation.IsBusy);
        if (decision.Action == MeetingShortcutAction.Refuse) tray.ShowBalloon(5000, TrayIcon.MeetingsTitle, decision.Reason, Forms.ToolTipIcon.Warning);
    }
    internal void ProfileShortcutPressed(DictationProfile profile)
    {
        ActivationShortcut shortcut = new(profile.ShortcutModifiers, profile.ShortcutKey);
        if (!dictation.IsBusy && !dictation.IsRecording)
        {
            if (EditingShortcut) { ShowRegisteredInField(shortcut, $"the shortcut of the profile “{profile.Name}”"); return; }
            SelectProfile(profile); dictation.Start(true, shortcut);
        }
        // With its shortcut, so a repeated message of a held profile shortcut is told from a press in hold to talk.
        else if (dictation.IsRecording) dictation.Toggle(shortcut);
        else if (!EditingShortcut) dictation.ShowWaiting();
    }
    private void TimerTick(object? sender, EventArgs e)
    {
        WhisperEngine.UnloadIfIdle(); LocalRewriter.UnloadIfIdle();
        if (dictation.IsRecording) { dictation.ShowRecordingProgress(); dictation.FollowHeldShortcut(); }
        else if (dictation.IsBusy) { tray.AnimateProcessing(); dictation.FollowHeldShortcut(); }
        // Announce a found update only between dictations, so the balloon never covers a recording.
        else updates.AnnounceIfNew();
    }
    bool IDictationView.OtherOperationActive => pages.OperationActive;
    bool IDictationView.WindowShowing => IsVisible && WindowState != WindowState.Minimized;
    bool IDictationView.MicrophoneSelected => Microphones.SelectedIndex >= 0;
    Settings IDictationView.DictationPreferences()
    {
        var shown = DictationPreferences();
        lastDictationLanguage = shown.Language;
        return shown;
    }
    NoticeList IDictationView.Notices => DictateNotices.Notices;
    void IDictationView.SpeechNotSetUp() { pages.ReportSpeechNotSetUp(); OpenPage(SpeechRecognitionTitle); }
    void IDictationView.ShowStatus(string message) => Status.Text = message;
    void IDictationView.ShowRecording()
    {
        CancelButton.IsEnabled = true;
        RecordButton.Content = "Stop";
        Microphones.IsEnabled = Languages.IsEnabled = false;
        SetPreferencesEnabled(false);
        SetState(BrandState.Recording, "Private Flow — recording");
    }
    void IDictationView.ShowTranscribing()
    {
        RecordButton.IsEnabled = false;
        SetState(BrandState.Processing, "Private Flow — transcribing");
    }
    void IDictationView.ShowFileTranscription() { SetPreferencesEnabled(false); RecordButton.IsEnabled = false; CancelButton.IsEnabled = true; }
    void IDictationView.ShowIdle()
    {
        RecordButton.Content = "Record";
        RecordButton.IsEnabled = Microphones.Items.Count > 0;
        CancelButton.IsEnabled = false;
        Microphones.IsEnabled = Languages.IsEnabled = true;
        SetPreferencesEnabled(true);
        SetState(BrandState.Idle, "Private Flow — ready");
    }
    void IDictationView.ShowResult(string original, string final) { Original.Text = original; Transcript.Text = final; Diff.Text = TextDifference.Describe(original, final); }
    void IDictationView.OpenPage(string title) => OpenPage(title);
    private void RecordClick(object sender, RoutedEventArgs e) => dictation.Toggle();
    private void CancelClick(object sender, RoutedEventArgs e) => _ = dictation.CancelAsync();
    private void CopyClick(object sender, RoutedEventArgs e)
    {
        try { if (!string.IsNullOrEmpty(Transcript.Text)) { System.Windows.Clipboard.SetText(Transcript.Text); DictateNotices.Clear(CopyFailedKey); Status.Text = "Copied. Paste into the app you choose."; } }
        catch (COMException) { DictateNotices.Report(new(CopyFailedKey, NoticeKind.Error, "Not copied", "The clipboard is busy. Select the text and copy it yourself.")); }
    }
    private void ClearClick(object sender, RoutedEventArgs e) { Transcript.Clear(); Original.Clear(); Diff.Clear(); dictation.ForgetResult(); }
    private void SaveClick(object sender, RoutedEventArgs e) { if (SavePreferences()) Keyboard.ClearFocus(); }
    // Stores every shown preference. False when something stayed unsaved; the reason is then on a card, in the popup at its section.
    private bool SavePreferences()
    {
        pages.CommitEdits(); AddTerms();
        var shown = ShownPreferences();
        var changed = PreferenceSections.Changed(shown, settings);
        (string? Section, string Message)? problem = PreferenceSections.Problem(shown, settings);
        if (preferencesLocked) problem = (null, "Preferences can be saved once the dictation or the file transcription has finished.");
        string? shortcutProblem = null;
        if (problem is null)
        {
            // A shortcut that cannot be registered keeps the previous one and stays unsaved; the other preferences are stored.
            var (dictationBefore, meetingBefore) = (activeShortcut, activeMeetingShortcut);
            var dictationShortcutProblem = TryApplyShortcut(); var meetingShortcutProblem = TryApplyMeetingShortcut();
            shortcutProblem = dictationShortcutProblem ?? meetingShortcutProblem;
            shown.ShortcutModifiers = activeShortcut.Modifiers; shown.ShortcutKey = activeShortcut.Key;
            shown.MeetingShortcutModifiers = activeMeetingShortcut?.Modifiers ?? 0; shown.MeetingShortcutKey = activeMeetingShortcut?.Key ?? 0;
            try
            {
                settings.SaveAs(shown);
                loadedVocabulary = settings.Vocabulary;
                pages.PreferencesStored(changed);
                if (changed.Contains(PreferenceSections.Profiles)) RefreshProfiles();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                RestoreShortcuts(dictationBefore, meetingBefore);
                problem = (null, "Nothing was saved, so nothing changed: " + ex.Message);
                shortcutProblem = null;
            }
            UpdateShortcutHint();
        }
        if (problem is not { } unsaved)
        {
            // Saved now from any page or section: an earlier failure shown elsewhere no longer holds.
            foreach (var item in navigationItems) item.Notices?.Clear(PreferencesNotSavedKey);
            DictateNotices.Clear(SettingsUnreadableKey);
            if (!string.IsNullOrWhiteSpace(settings.RewriteEndpoint)) DictateNotices.Clear(MissingEndpointKey);
            // "Press Save preferences to apply" and the like no longer hold either.
            ShowShortcutHint(DictationHintKey, null); ShowShortcutHint(MeetingHintKey, null);
            ShowAltGrWarnings();
            // A shortcut Windows refused says so on its own card under Shortcuts; everything else is stored.
            if (shortcutProblem is not null) { Status.Text = "Other preferences saved."; if (settingsOverlay is not null) ShowSettings(ShortcutsTitle); return false; }
            Status.Text = settings.ShortcutBehavior == ShortcutBehavior.HoldToTalk ? "Preferences saved. Hold the shortcut while you speak; release it to transcribe." : "Preferences saved. Press the shortcut once to record, again to transcribe.";
            return true;
        }
        if (settingsOverlay is not null && unsaved.Section is { } section) ShowSettings(section);
        PreferenceNotices.Report(new(PreferencesNotSavedKey, NoticeKind.Error, "Preferences not saved", NoticeText.Short(unsaved.Message), NoticeText.Detail(unsaved.Message)));
        return false;
    }
    private const string PreferencesNotSavedKey = "preferences-not-saved";
    // The cards of the page or settings section whose header holds Save preferences.
    private NoticeStack PreferenceNotices => (settingsOverlay is null ? currentPage : currentSection)?.Notices ?? DictateNotices;
    private void SetPreferencesEnabled(bool enabled)
    {
        preferencesLocked = !enabled;
        pages.Enabled = enabled;
        foreach (var element in PreferenceControls.SelectMany(control => control.Locked)) element.IsEnabled = enabled;
        PreferenceActions.IsEnabled = MicrophoneChip.IsEnabled = LanguageChip.IsEnabled = OutputChip.IsEnabled = PasteChip.IsEnabled = ImportButton.IsEnabled = enabled;
        UpdateContext();
        if (enabled) ShortcutsNotices.Clear(ShortcutsLockedKey);
        else ShortcutsNotices.Raise(new(ShortcutsLockedKey, NoticeKind.Note, "Shortcuts are locked", "A dictation or a file transcription runs. The shortcuts can be changed once it has finished."));
    }
    private static List<string> ModifierNames(uint modifiers)
    {
        var names = new List<string>();
        if ((modifiers & 2) != 0) names.Add("Ctrl");
        if ((modifiers & 1) != 0) names.Add("Alt");
        if ((modifiers & 4) != 0) names.Add("Shift");
        if ((modifiers & 8) != 0) names.Add("Win");
        return names;
    }
    internal static string ShortcutName(ActivationShortcut shortcut)
    {
        var names = ModifierNames(shortcut.Modifiers);
        var key = KeyInterop.KeyFromVirtualKey((int)shortcut.Key).ToString();
        if (key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1])) key = key[1..];
        names.Add(key);
        return string.Join(" + ", names);
    }
    // The Dictate and Meetings headings spell their shortcuts as key caps.
    private void UpdateShortcutHint()
    {
        var holding = settings.ShortcutBehavior == ShortcutBehavior.HoldToTalk;
        WorkspacePages.KeyHint(ShortcutHint, holding ? "Hold" : "Press", ShortcutName(activeShortcut).Split(" + "), "to dictate");
        ShortcutNote.Text = (holding ? "Release it" : "Press it again") + " to paste into the field you started in. Esc cancels.";
        pages.ShowMeetingShortcut(activeMeetingShortcut is { } meeting ? ShortcutName(meeting).Split(" + ") : null);
    }
    private static ActivationShortcut PressedShortcut(System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        uint flags = 0;
        if ((modifiers & ModifierKeys.Alt) != 0) flags |= 1;
        if ((modifiers & ModifierKeys.Control) != 0) flags |= 2;
        if ((modifiers & ModifierKeys.Shift) != 0) flags |= 4;
        if ((modifiers & ModifierKeys.Windows) != 0) flags |= 8;
        return new ActivationShortcut(flags, (uint)KeyInterop.VirtualKeyFromKey(key));
    }
    // What a key pressed in a shortcut field says instead of taking it: the modifiers held so far, so every press shows that the field
    // listens, or why the combination cannot be a shortcut. Null when candidate can be taken.
    internal static string? CaptureProblem(System.Windows.Input.KeyEventArgs e, out ActivationShortcut candidate)
    {
        candidate = PressedShortcut(e);
        if (HoldsOnlyModifiers(e))
            return string.Concat(ModifierNames(candidate.Modifiers).Select(name => name + " + ")) + "… Keep holding and press the last key of the combination.";
        return candidate.Problem is { } problem ? $"{ShortcutName(candidate)}: {problem}" : null;
    }
    // The warning for a Ctrl + Alt shortcut whose key types a character with AltGr on this keyboard; null for any other shortcut.
    internal static string? AltGrWarning(ActivationShortcut shortcut) =>
        shortcut.AltGrCharacter(WindowsKeyboardLayout.Current) is { } character
            ? ActivationShortcut.AltGrWarning(ShortcutName(shortcut), ShortcutName(shortcut with { Modifiers = shortcut.Modifiers & 4 }), character) : null;
    // The card of a shortcut field while its combination takes over an AltGr character, or none. A saved combination was chosen
    // knowing this, so its card stays in the section as a standing notice, without a pointer in the status line or a dot.
    internal static Notice? AltGrNotice(string key, ActivationShortcut? shortcut, bool saved = false) =>
        shortcut is { } chosen && AltGrWarning(chosen) is { } warning ? new(key, NoticeKind.Warning, "Takes over an AltGr character", warning, Standing: saved) : null;
    private const string DictationAltGrKey = "shortcut-dictation-altgr", MeetingAltGrKey = "shortcut-meeting-altgr";
    // The AltGr cards follow the combinations the shortcut fields show, saved or not.
    private void ShowAltGrWarnings()
    {
        foreach (var (key, shortcut, saved) in new[] { (DictationAltGrKey, (ActivationShortcut?)pendingShortcut, pendingShortcut == activeShortcut),
            (MeetingAltGrKey, pendingMeetingShortcut, pendingMeetingShortcut == activeMeetingShortcut) })
            if (AltGrNotice(key, shortcut, saved) is { } notice) ShortcutsNotices.Raise(notice); else ShortcutsNotices.Clear(key);
    }
    // Only modifiers held so far: the field shows them as it listens, which is not a rejection.
    internal static bool HoldsOnlyModifiers(System.Windows.Input.KeyEventArgs e) =>
        (e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    private void CaptureShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return; // Keep keyboard navigation out of the capture binding.
        e.Handled = true;
        if (CaptureProblem(e, out var candidate) is { } problem)
        {
            if (HoldsOnlyModifiers(e)) ShowShortcutHint(DictationHintKey, problem);
            else { ShowShortcutHint(DictationHintKey, null); RejectShortcut(DictationRejectedKey, "That combination cannot be the shortcut", problem); }
            return;
        }
        ChooseShortcut(candidate);
    }
    // A combination taken by the dictation shortcut field; the verification passes one, as it cannot hold real modifiers.
    internal void ChooseShortcut(ActivationShortcut candidate)
    {
        if (candidate == pendingMeetingShortcut)
        { ShowShortcutHint(DictationHintKey, null); RejectShortcut(DictationRejectedKey, $"{ShortcutName(candidate)} is already in use", "It is the meeting shortcut. Choose a different combination for dictation."); return; }
        ShortcutsNotices.Clear(DictationRejectedKey);
        pendingShortcut = candidate;
        ShortcutBox.Text = ShortcutName(candidate);
        ShowShortcutHint(DictationHintKey, "Press Save preferences to apply. Your previous shortcut stays active until then.");
        ShowAltGrWarnings();
    }
    private void CaptureMeetingShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        if (e.Key is Key.Back or Key.Delete)
        {
            pendingMeetingShortcut = null; MeetingShortcutBox.Text = MeetingShortcutName(null); ShortcutsNotices.Clear(MeetingRejectedKey);
            ShowShortcutHint(MeetingHintKey, "Press Save preferences to remove the meeting shortcut.");
            return;
        }
        if (CaptureProblem(e, out var candidate) is { } problem)
        {
            if (HoldsOnlyModifiers(e)) ShowShortcutHint(MeetingHintKey, problem);
            else { ShowShortcutHint(MeetingHintKey, null); RejectShortcut(MeetingRejectedKey, "That combination cannot be the shortcut", problem); }
            return;
        }
        if (candidate == pendingShortcut)
        { ShowShortcutHint(MeetingHintKey, null); RejectShortcut(MeetingRejectedKey, $"{ShortcutName(candidate)} is already in use", "It is the dictation shortcut. Choose a different combination for meetings."); return; }
        ShortcutsNotices.Clear(MeetingRejectedKey);
        pendingMeetingShortcut = candidate;
        MeetingShortcutBox.Text = ShortcutName(candidate);
        ShowShortcutHint(MeetingHintKey, "Press Save preferences to apply. The previous meeting shortcut stays active until then.");
        ShowAltGrWarnings();
    }
    private static ActivationShortcut? SavedMeetingShortcut(Settings source) =>
        source.MeetingShortcutKey == 0 ? null : new ActivationShortcut(source.MeetingShortcutModifiers, source.MeetingShortcutKey);
    private static string MeetingShortcutName(ActivationShortcut? shortcut) => shortcut is { } chosen ? ShortcutName(chosen) : "Not set";
    // Null when the meeting shortcut is registered as shown; otherwise why not, also said under its field.
    private string? TryApplyMeetingShortcut()
    {
        if (pendingMeetingShortcut == activeMeetingShortcut && (activeMeetingShortcut is null || meetingShortcut is not null || App.DiagnosticMode)) return null;
        if (pendingMeetingShortcut is { } chosen)
        {
            if (!chosen.IsValid || chosen == activeShortcut)
            {
                const string invalid = "It must be valid and differ from the dictation shortcut. The previous one stays.";
                ShortcutsNotices.Report(new(MeetingRejectedKey, NoticeKind.Error, "Meeting shortcut unchanged", invalid)); return invalid;
            }
            if (!App.DiagnosticMode)
            {
                if (shortcuts.Replace(meetingShortcut, chosen, MeetingShortcutPressed) is not { } replacement)
                {
                    var taken = $"{ShortcutName(chosen)} is used by another app or reserved. The previous one stays.";
                    ShortcutsNotices.Report(new(MeetingRejectedKey, NoticeKind.Error, "Meeting shortcut unchanged", taken)); return taken;
                }
                meetingShortcut = replacement;
            }
        }
        else if (meetingShortcut is { } identifier) { shortcuts.Unregister(identifier); meetingShortcut = null; }
        activeMeetingShortcut = pendingMeetingShortcut;
        ShowShortcutHint(MeetingHintKey, null); ShortcutsNotices.Clear(MeetingRejectedKey); pages.MeetingNotices.Clear(MeetingShortcutTakenKey);
        return null;
    }
    // Null when the dictation shortcut is registered as shown; otherwise why not, also said under its field.
    private string? TryApplyShortcut()
    {
        // The diagnostic instance registers nothing; it takes the shortcut as if it had registered it.
        if (App.DiagnosticMode) { activeShortcut = pendingShortcut; return null; }
        if (pendingShortcut.Problem is { } problem)
        {
            var refused = $"{ShortcutName(pendingShortcut)}: {problem}";
            ShortcutsNotices.Report(new(DictationRejectedKey, NoticeKind.Error, "Dictation shortcut unchanged", refused)); return refused;
        }
        if (pendingShortcut == activeShortcut && dictationShortcut is not null) return null;
        if (shortcuts.Replace(dictationShortcut, pendingShortcut, DictationShortcutPressed) is not { } replacement)
        {
            var taken = $"{ShortcutName(pendingShortcut)} is used by another app or reserved. Your previous shortcut stays.";
            ShortcutsNotices.Report(new(DictationRejectedKey, NoticeKind.Error, "Dictation shortcut unchanged", taken)); return taken;
        }
        dictationShortcut = replacement; ShortcutsNotices.Clear(DictationRejectedKey); DictateNotices.Clear(DictationShortcutTakenKey);
        activeShortcut = pendingShortcut;
        ShowShortcutHint(DictationHintKey, null);
        return null;
    }
    // After a failed write the registered shortcuts return to the stored ones, as every other value stays stored.
    private void RestoreShortcuts(ActivationShortcut dictationBefore, ActivationShortcut? meetingBefore)
    {
        if (!App.DiagnosticMode)
        {
            if (activeShortcut != dictationBefore) dictationShortcut = shortcuts.Replace(dictationShortcut, dictationBefore, DictationShortcutPressed);
            if (activeMeetingShortcut != meetingBefore)
            {
                if (meetingBefore is { } meeting) meetingShortcut = shortcuts.Replace(meetingShortcut, meeting, MeetingShortcutPressed);
                else if (meetingShortcut is { } identifier) { shortcuts.Unregister(identifier); meetingShortcut = null; }
            }
        }
        activeShortcut = dictationBefore; activeMeetingShortcut = meetingBefore;
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (quitting) return;
        e.Cancel = true;
        if (pages.MeetingCapturing)
        {
            pages.RefuseMeetingClose();
            // Show() throws while the window is closing, so the page opens once the refused close is over.
            Dispatcher.BeginInvoke(() => OpenPage("Meetings")); return;
        }
        Hide();
        tray.ShowBalloon(2500, "Private Flow", pages.MeetingActive ? "Finishing the meeting transcript in the background; a small window shows its progress."
            : "Still running in the tray. Double-click to open; use Quit to exit.", Forms.ToolTipIcon.Info);
    }
    private const string UpdateEndedKey = "update-ended-without-restart";
    private void UpdateClick(object sender, RoutedEventArgs e) { AboutNotices.Clear("update-not-started"); AboutNotices.Clear(UpdateEndedKey); StartUpdate(); }
    private void OpenUpdate() { OpenWindow(); StartUpdate(); }
    // The update restarts Private Flow, which would drop unsaved preferences as Quit does: once the update is confirmed, they are
    // saved or discarded, and until the restart a notice covers the window so nothing new can be changed. An update that fails leaves
    // Private Flow running, so the notice closes when the installer ends.
    private async void StartUpdate()
    {
        if (updating || await updates.StartUpdateAsync(SettleUnsavedAsync) is not { } installer) return;
        updating = true;
        if (settingsOverlay is not null) settingsOverlay.Close();
        var notice = ConfirmationDialog.ShowNotice(this, "Updating Private Flow", "The update downloads in its PowerShell window and Private Flow restarts when it is done. Until then nothing can be changed here.");
        installer.EnableRaisingEvents = true;
        installer.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            updating = false; notice.Close();
            AboutNotices.Report(new(UpdateEndedKey, NoticeKind.Warning, "The update ended without a restart", "Private Flow keeps running as before; the PowerShell window of the update says why."));
        });
    }
    private void QuitClick(object sender, RoutedEventArgs e) => Quit();
    private async void Quit()
    {
        if (quitting) return;
        // Quitting would drop unsaved preferences, so it asks first in the window; kept, Private Flow keeps running.
        pages.CommitEdits();
        if (HasUnsavedPreferences()) { OpenWindow(); if (!await SettleUnsavedAsync()) return; }
        if (quitting) return;
        quitting = true;
        updates.Stop();
        await dictation.QuitAsync();
        await pages.StopOperationsAsync();
        while (dictation.IsBusy) await Task.Delay(50);
        timer.Stop();
        shortcuts.Dispose();
        tray.Dispose();
        devices.Dispose(); pages.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
    private void RefreshDevices()
    {
        try
        {
            var microphones = ShowDevices(Microphones, devices.List(), settings.MicrophoneId, "Selected microphone disconnected — reconnect or choose another");
            if (!dictation.IsRecording && !dictation.IsBusy) RecordButton.IsEnabled = microphones > 0;
            if (microphones == 0) DictateNotices.Raise(new(NoMicrophoneKey, NoticeKind.Warning, "No microphone", "Connect one; the list refreshes automatically."));
            else DictateNotices.Clear(NoMicrophoneKey);
            ShowDevices(PlaybackDevices, devices.ListPlayback(), settings.PlaybackDeviceId, "Chosen output not connected — meetings record the Windows default");
            DictateNotices.Clear(AudioDevicesKey);
        }
        catch (COMException) { DictateNotices.Raise(new(AudioDevicesKey, NoticeKind.Error, "Windows audio devices are unavailable", "This is usually temporary; the list refreshes when Windows offers them again.")); }
    }
    private const string NoMicrophoneKey = "dictate-no-microphone", AudioDevicesKey = "dictate-audio-devices";
    // A chosen device that is gone stays listed and selected, so the choice survives until it returns or another is chosen.
    private static int ShowDevices(System.Windows.Controls.ComboBox box, List<MicrophoneDevice> list, string saved, string missing)
    {
        var selected = (box.SelectedItem as MicrophoneDevice)?.Id ?? saved;
        if (selected.Length > 0 && !list.Any(d => d.Id == selected)) list.Add(new(selected, missing));
        box.ItemsSource = list;
        box.SelectedItem = list.FirstOrDefault(d => d.Id == selected);
        if (box.SelectedItem is null && list.Count > 0) box.SelectedIndex = 0;
        return list.Count;
    }
    // A profile chosen explicitly applies to the following dictations; the shown and the stored preferences stay as they are.
    internal void SelectProfile(DictationProfile profile)
    {
        if (dictation.IsBusy || dictation.IsRecording || updating) return;
        activeProfileId = profile.Id; ShowActiveProfile();
        Status.Text = $"Profile {profile.Name} is active for your dictations.";
    }
    internal string? ActiveProfileShown => ProfileChip.Visibility == Visibility.Visible ? ProfileChipText.Text : null;
    // The chip on Dictate names the active profile; a profile deleted or renamed by a save follows.
    private void ShowActiveProfile()
    {
        var profile = ActiveProfile();
        if (profile is null) activeProfileId = "";
        ProfileChip.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
        ProfileChipText.Text = profile is null ? "" : $"Profile {profile.Name} active";
        AutomationProperties.SetName(ProfileChip, profile is null ? "" : $"Profile {profile.Name} active. Press to end it.");
        UpdateContext(); UpdateUnsaved();
    }
    private void EndProfileClick(object sender, RoutedEventArgs e) => EndProfile();
    internal void EndProfile()
    {
        if (activeProfileId.Length == 0) return;
        activeProfileId = ""; ShowActiveProfile();
        Status.Text = "Profile ended. Dictations use your preferences again.";
    }
    internal static string DroppedShortcutsMessage(IEnumerable<(string Owner, ActivationShortcut Shortcut)> dropped) =>
        string.Join(" ", dropped.Select(each => $"{ShortcutName(each.Shortcut)} was removed from {each.Owner}: {each.Shortcut.Problem}")) + " Choose another one under Settings.";
    // Shortcuts Settings.Load cleared because this version refuses them: named once on a card under Shortcuts, which the status line
    // points to, and in the tray, as the window starts hidden; then stored cleared, so the next start names none.
    private const string ShortcutsRemovedKey = "shortcuts-removed-at-start";
    private void ReportDroppedShortcuts()
    {
        var message = DroppedShortcutsMessage(settings.DroppedShortcuts);
        ShortcutsNotices.Report(new(ShortcutsRemovedKey, NoticeKind.Warning, "Shortcuts removed", NoticeText.Short(message), NoticeText.Detail(message)));
        tray.ShowBalloon(10000, "Private Flow", message, Forms.ToolTipIcon.Warning);
        try { settings.SaveAs(settings.Snapshot()); } catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }
    // A settings.json that could not be read was set aside and the app started with the defaults (Settings.Load). Said on Dictate until
    // the next successful Save preferences, which stores the preferences shown then, and in the tray, as the window starts hidden.
    private const string SettingsUnreadableKey = "settings-unreadable";
    private const string MissingEndpointKey = "missing-api-endpoint";
    private const string UpdateSourceChangedKey = "update-source-changed";
    private void ReportUnreadableSettings(Settings.UnreadableFile unread)
    {
        var text = $"Private Flow started with the default preferences. Your file is kept unchanged as {System.IO.Path.GetFileName(unread.KeptAs)} in the data folder; to use it again, correct it, quit Private Flow and rename it to settings.json.";
        DictateNotices.Report(new(SettingsUnreadableKey, NoticeKind.Error, "Your settings could not be read", text, "Reason: " + unread.Reason, Dismissable: false));
        tray.ShowBalloon(10000, "Private Flow", "Your settings could not be read, so Private Flow started with the default preferences. Open it for details.", Forms.ToolTipIcon.Error);
    }
    private void RefreshProfiles()
    {
        RegisterProfileShortcuts();
        tray.SetProfiles(settings.Profiles, SelectProfile);
    }
    private void RegisterProfileShortcuts()
    {
        if (App.DiagnosticMode) return;
        foreach (var identifier in profileShortcuts) shortcuts.Unregister(identifier);
        profileShortcuts.Clear();
        var taken = new List<string>();
        foreach (var profile in settings.Profiles)
        {
            var shortcut = new ActivationShortcut(profile.ShortcutModifiers, profile.ShortcutKey);
            if (!shortcut.IsValid) continue;
            if (shortcuts.Register(shortcut, () => ProfileShortcutPressed(profile)) is { } identifier) profileShortcuts.Add(identifier);
            else taken.Add($"“{profile.Name}” ({ShortcutName(shortcut)})");
        }
        if (taken.Count == 0) pages.ProfilesNotices.Clear("profile-shortcuts-taken");
        else pages.ProfilesNotices.Report(new("profile-shortcuts-taken", NoticeKind.Warning, "Profile shortcuts used by another app",
            $"These do not work until you choose others: {string.Join(", ", taken)}."));
    }
    private void RecoverClick(object sender, RoutedEventArgs e) => RecoverLast();
    private void RecoverLast()
    {
        if (dictation.LastResult is not { } lastResult) { DictateNotices.Report(new(NothingRecoveredKey, NoticeKind.Note, "Nothing to recover", "No result in memory yet.")); OpenPage("Dictate"); return; }
        Original.Text = lastResult.Original; Transcript.Text = lastResult.Final;
        Diff.Text = TextDifference.Describe(lastResult.Original, lastResult.Final);
        dictation.ShowNotices(lastResult);
        DictateNotices.Clear(NothingRecoveredKey);
        Status.Text = "Recovered latest result from memory."; OpenPage("Dictate");
    }
    private async void ImportClick(object sender, RoutedEventArgs e)
    {
        if (pages.OperationActive)
        { DictateNotices.Report(new(DictationController.RefusedKey, NoticeKind.Warning, "No file transcribed", "Finish the meeting, or the setup or check under Speech recognition, first.")); return; }
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Audio files|*.wav;*.mp3;*.m4a;*.wma;*.flac;*.aac|All files|*.*" };
        if (picker.ShowDialog(this) != true) return;
        await dictation.TranscribeFileAsync(picker.FileName);
    }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var segments = dictation.LastFileSegments;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = segments is null ? "Text|*.txt|Markdown|*.md" : "Text|*.txt|Markdown|*.md|Original timestamped subtitles|*.srt", DefaultExt = ".txt", FileName = "transcription" };
        if (picker.ShowDialog(this) != true) return;
        try { System.IO.File.WriteAllText(picker.FileName, picker.FilterIndex == 3 && segments is not null ? MeetingArchive.ExportSrt(segments) : Transcript.Text); DictateNotices.Clear(ExportFailedKey); Status.Text = picker.FilterIndex == 3 ? "Original speech segments exported with timestamps." : "Text exported."; }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { DictateNotices.Report(new(ExportFailedKey, NoticeKind.Error, "Export failed", ex.Message)); }
    }
}

// One row per stored term: a term stored twice is two rows, and deleting one removes only the row selected.
internal sealed class VocabularyRow(string text)
{
    public string Text { get; } = text;
    public override string ToString() => Text;
}
