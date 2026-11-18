using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using CheckBox = System.Windows.Controls.CheckBox;
using FontFamily = System.Windows.Media.FontFamily;
using Binding = System.Windows.Data.Binding;

namespace PrivateFlow.Windows;

internal sealed record WorkspacePage(FrameworkElement Content, UIElement? Actions);
// The control of the settings of PreferenceSections.Table it names, whose shown values wait for Save preferences: Show shows a settings
// object's values, Read writes the shown ones into a copy. A dictation's copy takes the shown values of controls ForDictation only. Locked
// are disabled while a dictation or a file transcription runs; the controls of the workspace pages are locked with their page instead.
internal sealed record PreferenceControl(string[] Names, Action<Settings> Show, Action<Settings> Read, bool ForDictation = false, params UIElement[] Locked);

// Corrections and snippets, profiles, meetings, their history and settings, and the speech engine, shown as pages of the main window
// and sections of its settings popup.
internal sealed partial class WorkspacePages : IDisposable
{
    private readonly Window owner;
    private readonly Settings settings;
    private readonly Action setupInstalled;
    private readonly Func<DictationResult> editor;
    private readonly Action<string> report;
    private readonly ObservableCollection<PhraseRule> corrections, snippets;
    private readonly ObservableCollection<DictationProfile> profiles;
    private readonly MeetingsPanel meetings;
    private readonly List<UIElement> pageElements = [];
    private bool busy, enabled = true;
    private CancellationTokenSource? busyCancellation;
    private TextBox? profileShortcutField;
    private DataGrid? profileGrid;
    private readonly List<PreferenceControl> preferenceControls = [];
    // What the sections here do once newly stored preferences are in use, given the sections that changed.
    private readonly List<Action<IReadOnlyList<string>>> preferencesStored = [];
    // A shown value of a section here changed.
    public event Action? PreferenceChanged;
    // The cards of the Vocabulary page's corrections and snippets, of Profiles per app and of Speech recognition.
    private readonly NoticeStack librariesNotices = new("Vocabulary notices") { Margin = new(0, 30, 0, -16) }, profilesNotices = new("Profiles notices"), speechNotices = new("Speech recognition notices");
    internal NoticeStack LibrariesNotices => librariesNotices;
    internal NoticeStack ProfilesNotices => profilesNotices;
    internal NoticeStack SpeechNotices => speechNotices;
    internal NoticeStack MeetingNotices => meetings.MeetingNotices;
    internal NoticeStack MeetingSettingsNotices => meetings.SettingsNotices;
    internal NoticeStack ModelsNotices => modelsNotices;
    public void RefuseMeetingClose() => meetings.RefuseClose();
    private const string ChoiceNotAppliedKey = "speech-choice-not-applied", SpeechNotSetUpKey = "speech-not-set-up", SetupFailedKey = "speech-setup-failed", PolishFailedKey = "polish-setup-failed", CheckFailedKey = "speech-check-failed";
    // Dictation needs the speech engine and model. The card follows whether their files are there, however they got there: Apply, paths
    // entered by hand and saved, or the recommended setup at start. It has no x, as nothing else lets dictation run.
    private static readonly Notice SpeechNotSetUp = new(SpeechNotSetUpKey, NoticeKind.Error, "Speech recognition is not set up",
        "Dictation needs it. Press Apply under Processor and model, or choose the engine and model paths below.", Dismissable: false);
    // A dictation refused for it: shown again, in front.
    public void ReportSpeechNotSetUp() => speechNotices.Report(SpeechNotSetUp);
    private void FollowSpeechSetUp()
    {
        if (File.Exists(settings.EnginePath) && File.Exists(settings.ModelPath)) speechNotices.Clear(SpeechNotSetUpKey);
        else speechNotices.Raise(SpeechNotSetUp);
    }
    // The shortcut field of the selected profile has the keyboard focus, so a registered shortcut pressed there is not taken as a press.
    public bool EditingProfileShortcut => profileShortcutField?.IsKeyboardFocusWithin == true;
    // The language a correction learnt from the Dictate page belongs to: that of the dictation it came from. The window sets it.
    internal Func<string> CorrectionLanguage { get; set; }
    // Runs a setup script; the verification replaces it so nothing is downloaded.
    internal Func<string, IEnumerable<string>, CancellationToken, IProgress<string>?, Task<string?>> RunSetup { get; set; } = SpeechSetup.RunAsync;
    // setupInstalled hears that a setup installed something and its paths are stored. openSettings opens a
    // section of the settings popup by its title.
    public WorkspacePages(Window owner, Settings settings, System.Windows.Controls.ComboBox microphones, System.Windows.Controls.ComboBox playbackDevices, Action setupInstalled, Func<DictationResult> editor,
        Action<string> report, NoticeBoard board, Action<string, bool>? notify = null, Action? openMeetings = null, Action? openShortcutSettings = null, Action<string>? openSettings = null)
    {
        board.Add(librariesNotices.Notices, "Vocabulary"); board.Add(profilesNotices.Notices, MainWindow.ProfilesTitle); board.Add(speechNotices.Notices, MainWindow.SpeechRecognitionTitle);
        board.Add(modelsNotices.Notices, MainWindow.ModelsTitle);
        this.owner = owner; this.settings = settings; this.setupInstalled = setupInstalled; this.editor = editor; this.report = report;
        this.openSettings = openSettings ?? (_ => { });
        CorrectionLanguage = () => settings.Language;
        corrections = new(settings.Corrections.Select(Clone)); snippets = new(settings.Snippets.Select(Clone));
        profiles = new(settings.Profiles.Select(p => JsonSerializer.Deserialize<DictationProfile>(JsonSerializer.Serialize(p))!));
        Libraries = Track(BuildLibraries());
        Profiles = Track(BuildProfiles());
        meetings = new(settings, microphones, playbackDevices, report, notify, openMeetings, openShortcutSettings, () => this.openSettings(MainWindow.ModelsTitle), board: board, modelsNotices: modelsNotices);
        meetings.Ended += () => OperationEnded?.Invoke();
        Meetings = Track(new WorkspacePage(meetings, null));
        History = Track(new WorkspacePage(meetings.History, meetings.HistorySearch));
        MeetingSettings = Track(meetings.MeetingSettings);
        SpeechEngine = Track(BuildSpeechEngine());
        Models = BuildModels();
        preferenceControls.AddRange(meetings.PreferenceControls); preferencesStored.Add(meetings.PreferencesStored);
        meetings.PreferenceChanged += Changed;
    }
    private void Changed() => PreferenceChanged?.Invoke();
    public IReadOnlyList<PreferenceControl> PreferenceControls => preferenceControls;
    public void PreferencesStored(IReadOnlyList<string> changed) { foreach (var stored in preferencesStored) stored(changed); }
    // A profile cell still being edited belongs to the shown values.
    public void CommitEdits() { profileGrid?.CommitEdit(); profileGrid?.CommitEdit(DataGridEditingUnit.Row, true); }
    public WorkspacePage Libraries { get; }
    public WorkspacePage Profiles { get; }
    public WorkspacePage Meetings { get; }
    public WorkspacePage History { get; }
    public WorkspacePage MeetingSettings { get; }
    public FrameworkElement SystemAudioColorChoice => meetings.SystemAudioColorChoice;
    public WorkspacePage SpeechEngine { get; }
    public bool MeetingActive => meetings.IsRecording;
    public bool MeetingCapturing => meetings.IsCapturing;
    public bool OperationActive => meetings.IsRecording || busy;
    public event Action<bool> MeetingCaptureChanged { add => meetings.CaptureChanged += value; remove => meetings.CaptureChanged -= value; }
    public MeetingShortcutDecision PressMeetingShortcut(bool dictationActive) => meetings.PressShortcut(dictationActive, busy);
    public void ShowMeetingShortcut(IReadOnlyList<string>? keys) => meetings.ShowShortcut(keys);
    // Dictation disables these pages; a running setup or check disables them as well.
    public bool Enabled { set { enabled = value; ApplyEnabled(); } }
    public async Task StopOperationsAsync()
    {
        busyCancellation?.Cancel();
        await meetings.StopOperationsAsync();
        while (busy) await Task.Delay(50);
    }
    public void Dispose() => meetings.Dispose();
    // A card on Dictate, under Text style or under Profiles per app runs it with its progress under Speech recognition.
    // A profile shown under Profiles per app, saved or not, writes with Local polish.
    public bool ProfilesChooseLocalPolish => profiles.Any(profile => profile.OutputStyle == OutputStyle.LocalPolish);
    // Setup can change the engine and model in the background; stale fields would be saved back as hand-typed paths. changed names the
    // settings it stored, null for all; the other fields keep what was typed and not saved yet.
    public void RefreshSpeechEngine(IReadOnlyCollection<string>? changed = null) => refreshSpeechEngine(changed);
    private Action<IReadOnlyCollection<string>?> refreshSpeechEngine = _ => { };
    private WorkspacePage Track(WorkspacePage page)
    {
        pageElements.Add(page.Content);
        if (page.Actions is not null) pageElements.Add(page.Actions);
        return page;
    }
    private void ApplyEnabled() { foreach (var element in pageElements) element.IsEnabled = enabled && !busy; RefreshModels(); }
    private void SetBusy(bool value) { busy = value; ApplyEnabled(); if (!value) OperationEnded?.Invoke(); }
    // A meeting, a setup or a check that kept dictation from starting has ended.
    public event Action? OperationEnded;
    // Speech recognition was set up with Apply.
    public event Action? SpeechSetUp;
    private static PhraseRule Clone(PhraseRule rule) => new() { Original = rule.Original, Replacement = rule.Replacement, Language = rule.Language };
    private void Report(string message) => report(message);

    // Row controls share one right-aligned column so their left edges line up.
    internal const double ControlWidth = 300;
    internal static T Theme<T>(string key) => (T)System.Windows.Application.Current.FindResource(key);
    internal static Style Theme(string key) => Theme<Style>(key);
    internal static ScrollViewer Page(params UIElement[] content)
    {
        var column = new StackPanel { Style = Theme("PageColumn") };
        foreach (var element in content) column.Children.Add(element);
        return new ScrollViewer { Style = Theme("PageScroll"), Content = column };
    }
    internal static TextBlock Section(string title, bool first = false)
    {
        var heading = new TextBlock { Text = title, Style = Theme("SectionHeading") };
        if (first) heading.Margin = new(2, 0, 0, 12);
        return heading;
    }
    internal static TextBlock SectionDescription(string text) => new() { Text = text, Style = Theme("SectionDescription") };
    internal static TextBlock Description(string text) => new() { Text = text, Style = Theme("RowDescription") };
    internal static Border Card(params Border[] rows)
    {
        var stack = new StackPanel();
        foreach (var row in rows) stack.Children.Add(row);
        if (rows.Length > 0) rows[0].BorderThickness = new(0);
        return new Border { Style = Theme("Card"), Child = stack };
    }
    internal static Border Row(string title, string? description, FrameworkElement? control)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, Style = Theme("RowTitle") });
        if (description is not null) text.Children.Add(Description(description));
        return Row(text, control);
    }
    internal static Border Row(UIElement text, FrameworkElement? control)
    {
        var dock = new DockPanel();
        if (control is not null)
        {
            control.Margin = new(24, 0, 0, 0); control.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(control, Dock.Right); dock.Children.Add(control);
        }
        dock.Children.Add(text);
        return new Border { Style = Theme("Row"), Child = dock };
    }
    internal static Border BodyRow(UIElement content, Thickness? padding = null)
    {
        var row = new Border { Style = Theme("Row"), Child = content };
        if (padding is { } value) row.Padding = value;
        return row;
    }
    internal static Border GridRow(DataGrid grid) => BodyRow(grid, new Thickness(0, 2, 0, 0));
    internal static TextBox Field(string name, string text = "", double width = double.NaN)
    {
        var box = new TextBox { Text = text, Width = width }; AutomationProperties.SetName(box, name); return box;
    }
    internal static System.Windows.Controls.ComboBox Choice(string name, string selected, params (string Value, string Label)[] options)
    {
        var box = new System.Windows.Controls.ComboBox { Width = ControlWidth }; AutomationProperties.SetName(box, name);
        foreach (var (value, label) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected) ?? box.Items[0];
        return box;
    }
    internal static StackPanel Stack(params UIElement[] children)
    {
        var stack = new StackPanel();
        foreach (var child in children) stack.Children.Add(child);
        return stack;
    }
    internal static CheckBox Toggle(string name, bool isChecked)
    {
        var toggle = new CheckBox { IsChecked = isChecked }; AutomationProperties.SetName(toggle, name); return toggle;
    }
    internal static Button Button(string name, Action action, string? style = null)
    {
        var button = new Button { Content = name };
        if (style is not null) button.Style = Theme(style);
        button.Click += (_, _) => action();
        return button;
    }
    internal static WrapPanel Buttons(params Button[] buttons)
    {
        var row = new WrapPanel();
        foreach (var button in buttons) { button.Margin = new(0, 3, 8, 3); row.Children.Add(button); }
        return row;
    }
    internal static StackPanel Actions(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        foreach (var button in buttons) { button.Margin = new(8, 0, 0, 0); panel.Children.Add(button); }
        return panel;
    }
    // The large line above a composer, with the shortcut as key caps: "Press [Ctrl] [Alt] [Space] to dictate".
    internal static void KeyHint(WrapPanel hint, string before, IReadOnlyList<string> keys, string after)
    {
        hint.Children.Clear();
        hint.Children.Add(new TextBlock { Text = before, Style = Theme("HintWords") });
        foreach (var key in keys) hint.Children.Add(new Border { Style = Theme("Keycap"), Child = new TextBlock { Text = key, FontSize = 20, FontWeight = FontWeights.SemiBold } });
        if (after.Length > 0) hint.Children.Add(new TextBlock { Text = after, Style = Theme("HintWords") });
        AutomationProperties.SetName(hint, string.Join(" ", new[] { before, string.Join(" + ", keys), after }.Where(part => part.Length > 0)));
    }
    // A chip under a composer: its icon, the current choice and a chevron; Choices opens its menu.
    internal static Button Chip(string icon, string name, TextBlock label)
    {
        var content = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        content.Children.Add(new System.Windows.Shapes.Path { Style = Theme("Icon"), Data = Theme<System.Windows.Media.Geometry>(icon), Margin = new(0, 0, 7, 0) });
        label.MaxWidth = 220; label.TextTrimming = TextTrimming.CharacterEllipsis; content.Children.Add(label);
        content.Children.Add(new System.Windows.Shapes.Path { Style = Theme("ChipChevron") });
        var chip = new Button { Style = Theme("Chip"), Content = content, ToolTip = name };
        AutomationProperties.SetName(chip, name);
        return chip;
    }
    internal static void Choices(FrameworkElement chip, IEnumerable<(string Header, bool Selected, Action Choose)> choices)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = chip, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, VerticalOffset = 4 };
        foreach (var (header, selected, choose) in choices)
        {
            var item = new System.Windows.Controls.MenuItem { Header = new TextBlock { Text = header }, IsChecked = selected };
            item.Click += (_, _) => choose(); menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
    internal static DataGrid Grid(System.Collections.IEnumerable items, double height)
    {
        var grid = new DataGrid
        {
            ItemsSource = items, AutoGenerateColumns = false, Height = height, CanUserAddRows = true, CanUserDeleteRows = true,
            SelectionMode = DataGridSelectionMode.Single
        };
        // Text and check box columns set explicit element styles, which bypass the implicit theme.
        grid.Columns.CollectionChanged += (_, e) =>
        {
            foreach (var column in e.NewItems?.OfType<DataGridColumn>() ?? [])
            {
                if (column is DataGridTextColumn text) text.EditingElementStyle = Theme("CellEditor");
                else if (column is DataGridCheckBoxColumn check) { check.ElementStyle = Theme("CellToggleDisplay"); check.EditingElementStyle = Theme("CellToggle"); }
            }
        };
        return grid;
    }

    private WorkspacePage BuildLibraries()
    {
        var aliases = RulesGrid(corrections); var expansions = RulesGrid(snippets);
        var original = Field("Mistaken phrase", width: ControlWidth); var replacement = Field("Corrected phrase", width: ControlWidth);
        var load = Button("Load from Dictate", () => { var result = editor(); original.Text = result.Original; replacement.Text = result.Final; });
        var learn = Button("Learn correction", () =>
        {
            try { corrections.Add(TextLibraryFile.LearnCorrection(original.Text, replacement.Text, CorrectionLanguage(), corrections, snippets)); SaveLibraries(aliases, expansions); }
            catch (InvalidOperationException ex) { LibraryProblem(NoticeKind.Warning, "Correction not learned", ex.Message); }
        });
        // Shown on the Vocabulary page below the names and terms.
        var content = Stack(
            librariesNotices,
            Section("Corrections and aliases"),
            SectionDescription("Whole spoken phrases are replaced once; longer phrases win and quoted text stays literal. Automatic language applies to English, German and Spanish. Type in the empty last row to add a phrase; select a row and press Delete to remove it."),
            Card(GridRow(aliases)),
            Section("Spoken snippets"),
            SectionDescription("Say the phrase to insert its text. Snippet text bypasses polishing. No commands or scripts run."),
            Card(GridRow(expansions)),
            Section("Teach a correction"),
            SectionDescription("Teach a correction from the text on the Dictate page. Private Flow never reads corrections in other apps."),
            Card(
                Row("Mistaken phrase", "What the transcription wrote.", original),
                Row("Corrected phrase", "The spelling you want instead.", replacement),
                Row("From the last dictation", "Load the text from the Dictate page, then reduce both fields to the changed phrase.", Buttons(load, learn))));
        var actions = Actions(Button("Import", ImportLibraries), Button("Export", ExportLibraries),
            Button("Save corrections and snippets", () => SaveLibraries(aliases, expansions), "Primary"));
        return new(content, actions);
    }
    private static DataGrid RulesGrid(ObservableCollection<PhraseRule> items)
    {
        var grid = Grid(items, 230);
        grid.Columns.Add(new DataGridTextColumn { Header = "Spoken phrase", Binding = new Binding("Original"), Width = new(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "Replacement text", Binding = new Binding("Replacement"), Width = new(2, DataGridLengthUnitType.Star) });
        grid.Columns.Add(LanguageColumn(100));
        return grid;
    }
    // A choice shown by its name on the page and stored by its value, such as "German" for "de".
    private sealed record Named<T>(T Value, string Label);
    private static DataGridComboBoxColumn LanguageColumn(double width) => new()
    {
        Header = "Language", Width = width, SelectedValuePath = "Value", DisplayMemberPath = "Label", SelectedValueBinding = new Binding("Language"),
        ItemsSource = new Named<string>[] { new("auto", "Automatic"), new("en", "English"), new("de", "German"), new("es", "Spanish") }
    };
    private void SaveLibraries(params DataGrid[] grids)
    {
        foreach (var grid in grids) { grid.CommitEdit(); grid.CommitEdit(DataGridEditingUnit.Row, true); }
        try
        {
            TextLibraryValidation.Validate(corrections, snippets);
            var next = settings.Snapshot();
            next.Corrections = corrections.Select(Clone).ToList(); next.Snippets = snippets.Select(Clone).ToList();
            settings.SaveAs(next); librariesNotices.Clear(LibraryProblemKey); Report("Corrections and snippets saved locally.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { LibraryProblem(NoticeKind.Error, "Corrections and snippets not saved", ex.Message); }
    }
    private const string LibraryProblemKey = "libraries-problem";
    private void LibraryProblem(NoticeKind kind, string title, string text)
    {
        librariesNotices.Report(new(LibraryProblemKey, kind, title, NoticeText.Short(text), NoticeText.Detail(text)));
        // The section heading the cards stand above can be far from the button that was pressed.
        librariesNotices.BringIntoView();
    }
    private void ImportLibraries()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Private Flow corrections and snippets|*.json" };
        if (picker.ShowDialog(owner) != true) return;
        try
        {
            TextLibraryFile.CheckLength(new FileInfo(picker.FileName).Length);
            var file = TextLibraryFile.Import(File.ReadAllText(picker.FileName), corrections, snippets);
            foreach (var rule in file.Corrections) corrections.Add(rule);
            foreach (var rule in file.Snippets) snippets.Add(rule);
            librariesNotices.Clear(LibraryProblemKey); Report("Imported for review. Use Save corrections and snippets to apply.");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { LibraryProblem(NoticeKind.Error, "Import failed", ex.Message); }
    }
    private void ExportLibraries()
    {
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Private Flow corrections and snippets|*.json", FileName = "corrections-and-snippets.json" };
        if (picker.ShowDialog(owner) != true) return;
        try { File.WriteAllText(picker.FileName, TextLibraryFile.Export(corrections, snippets)); librariesNotices.Clear(LibraryProblemKey); Report("Corrections and snippets exported. The file contains your text; keep it private."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { LibraryProblem(NoticeKind.Error, "Export failed", ex.Message); }
    }
    internal const string ProfileShortcutKey = "profile-shortcut", ProfileAltGrKey = "profile-shortcut-altgr";
    private WorkspacePage BuildProfiles()
    {
        var grid = Grid(profiles, 240);
        foreach (var (name, width) in new[] { ("Name", 1.0), ("Vocabulary", 1.4), ("Applications", 1.4) }) grid.Columns.Add(new DataGridTextColumn { Header = name, Binding = new Binding(name), Width = new(width, DataGridLengthUnitType.Star) });
        grid.Columns.Add(LanguageColumn(92));
        grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Style", Width = 116, SelectedValuePath = "Value", DisplayMemberPath = "Label", SelectedValueBinding = new Binding("OutputStyle"),
            ItemsSource = new Named<OutputStyle>[] { new(OutputStyle.Exact, "Exact"), new(OutputStyle.Clean, "Clean"), new(OutputStyle.Concise, "Concise"), new(OutputStyle.LocalPolish, "Local polish") }
        });
        profileGrid = grid;
        var shortcut = Field("Selected profile shortcut", width: ControlWidth); shortcut.IsReadOnly = true; shortcut.Tag = "Press a shortcut";
        profileShortcutField = shortcut;
        // While the profile's combination takes over an AltGr character, a card says which.
        void ShowAltGrWarning(ActivationShortcut? chosen)
        {
            if (MainWindow.AltGrNotice(ProfileAltGrKey, chosen) is { } notice) profilesNotices.Raise(notice); else profilesNotices.Clear(ProfileAltGrKey);
        }
        // A cell reaches its row object only once its edit is committed, and a switch once its binding has updated.
        void ChangedLater() => owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(Changed));
        // The selected profile's switches edit the same row object the table shows.
        CheckBox ProfileToggle(string name, string property)
        {
            var toggle = Toggle(name, false);
            toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
            toggle.Checked += (_, _) => ChangedLater(); toggle.Unchecked += (_, _) => ChangedLater();
            return toggle;
        }
        profiles.CollectionChanged += (_, _) => Changed();
        grid.CellEditEnding += (_, _) => ChangedLater();
        var details = Card(
            Row("Shortcut", "Click the field and press a shortcut. Backspace removes it. Save preferences registers the keys.", shortcut),
            Row("Paste into the selected field", "Matching by application keeps an explicit opt-out.", ProfileToggle("Profile pastes automatically", "AutoInsert")),
            Row("Spoken formatting", null, ProfileToggle("Profile spoken formatting", "Formatting")),
            Row("Protect technical text", null, ProfileToggle("Profile protects technical text", "Technical")),
            Row("Literal mode", null, ProfileToggle("Profile literal mode", "Literal")));
        details.IsEnabled = false;
        grid.SelectionChanged += (_, _) =>
        {
            var profile = grid.SelectedItem as DictationProfile;
            details.DataContext = profile; details.IsEnabled = profile is not null;
            shortcut.Text = profile is null ? "" : profile.ShortcutKey == 0 ? "Not set" : MainWindow.ShortcutName(new(profile.ShortcutModifiers, profile.ShortcutKey));
            ShowAltGrWarning(profile is { ShortcutKey: not 0 } ? new(profile.ShortcutModifiers, profile.ShortcutKey) : null);
        };
        shortcut.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Tab) return;
            e.Handled = true;
            if (grid.SelectedItem is not DictationProfile profile) return;
            if (e.Key == System.Windows.Input.Key.Back) { profile.ShortcutKey = profile.ShortcutModifiers = 0; shortcut.Text = "Not set"; profilesNotices.Clear(ProfileShortcutKey); ShowAltGrWarning(null); Changed(); return; }
            if (MainWindow.CaptureProblem(e, out var candidate) is { } problem)
            {
                if (MainWindow.HoldsOnlyModifiers(e)) Report(problem);
                else profilesNotices.Report(new(ProfileShortcutKey, NoticeKind.Warning, "That combination cannot be a shortcut", problem));
                return;
            }
            profilesNotices.Clear(ProfileShortcutKey);
            profile.ShortcutKey = candidate.Key; profile.ShortcutModifiers = candidate.Modifiers; shortcut.Text = MainWindow.ShortcutName(candidate);
            ShowAltGrWarning(candidate); Changed();
        };
        static List<DictationProfile> Copy(IEnumerable<DictationProfile> source) => JsonSerializer.Deserialize<List<DictationProfile>>(JsonSerializer.Serialize(source))!;
        preferenceControls.Add(new([nameof(Settings.Profiles)], source => { profiles.Clear(); foreach (var profile in Copy(source.Profiles)) profiles.Add(profile); }, target => target.Profiles = Copy(profiles)));
        var content = Page(
            profilesNotices,
            Section("Profiles", true),
            SectionDescription("Choose a profile in the tray or with its own shortcut. Application matching uses exact executable names separated by commas, such as Code.exe. Matching never selects Concise, which sends text to the API; choosing a profile explicitly can."),
            Card(GridRow(grid)),
            Section("Selected profile"),
            SectionDescription("Select a profile row to set its shortcut and insertion options."),
            details);
        return new(content, null);
    }
    private WorkspacePage BuildSpeechEngine()
    {
        var report = new TextBox
        {
            Style = Theme("BareTextBox"), Text = HealthChecks.Describe(settings), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Theme<System.Windows.Media.Brush>("Secondary")
        };
        AutomationProperties.SetName(report, "Setup and synthetic check results");
        var enginePath = Field("Speech engine path", settings.EnginePath, ControlWidth); var modelPath = Field("Speech model path", settings.ModelPath, ControlWidth);
        var rewriteEngine = Field("Local polish engine", settings.LocalRewriteEnginePath, ControlWidth); var rewriteModel = Field("Local polish model", settings.LocalRewriteModelPath, ControlWidth);
        var vadModel = Field("Silero model", settings.VadModelPath, ControlWidth);
        var backend = Choice("Speech runs on", settings.SpeechBackend, ("auto", "Automatic (recommended)"), ("cuda12", "GPU (NVIDIA)"), ("cpu", "CPU"));
        if (!SpeechSetup.HasNvidiaGpu) ((ComboBoxItem)backend.Items[1]).IsEnabled = false;
        string Sized(string key, string name) => $"{name}, {ModelList.Size(ModelCatalogue.Model(key).Bytes)}";
        var model = Choice("Speech model", settings.SpeechModel, ("auto", "Automatic (recommended)"), (ModelCatalogue.SpeechTurbo, Sized(ModelCatalogue.SpeechTurbo, "large-v3-turbo")),
            (ModelCatalogue.SpeechMedium, Sized(ModelCatalogue.SpeechMedium, "medium")), (ModelCatalogue.SpeechBase, Sized(ModelCatalogue.SpeechBase, "base")));
        var inUse = Description("");
        var speechProgress = new SetupProgressView { Margin = new(0, 6, 0, 0) }; var polishProgress = new SetupProgressView { Margin = new(0, 6, 0, 0) };
        progressViews[ModelCatalogue.PolishModel].Add(polishProgress);
        static bool Differs(System.Windows.Controls.ComboBox box, string stored) => (box.SelectedItem as ComboBoxItem)?.Tag as string != stored;
        // The processor and model are what Apply sets up, so a choice that differs from the setup in use waits for Apply, not for Save.
        void ShowInUse()
        {
            inUse.Text = $"In use: {SpeechSetup.Processor(settings.EnginePath)}, {SpeechSetup.ModelName(settings.ModelPath)}" +
                (File.Exists(settings.MeetingModelPath) ? $", meetings and files finished with {SpeechSetup.ModelName(settings.MeetingModelPath)}" : "") +
                (settings.SpeechPathsCustom ? ", from paths entered by hand. Apply replaces them." : ".");
            if (Differs(backend, settings.SpeechBackend) || Differs(model, settings.SpeechModel))
                speechNotices.Raise(new(ChoiceNotAppliedKey, NoticeKind.Note, "Not applied yet", "The processor or model chosen above is used once you press Apply; Save preferences does not set it up."));
            else speechNotices.Clear(ChoiceNotAppliedKey);
        }
        backend.SelectionChanged += (_, _) => ShowInUse(); model.SelectionChanged += (_, _) => ShowInUse();
        var vad = Toggle("Use local speech detection (Silero)", settings.SpeechDetection);
        var warm = Toggle("Keep speech model warm", settings.KeepModelWarm);
        // Each of the engine and model paths is read as typed by hand on its own; one after the other they give what UsePathsEnteredByHand
        // gives for both, so a setup that changed one of them refreshes only that field.
        PreferenceControl[] speechControls =
        [
            new([nameof(Settings.SpeechDetection)], source => vad.IsChecked = source.SpeechDetection, target => target.SpeechDetection = vad.IsChecked == true),
            new([nameof(Settings.KeepModelWarm)], source => warm.IsChecked = source.KeepModelWarm, target => target.KeepModelWarm = warm.IsChecked == true),
            new([nameof(Settings.EnginePath)], source => enginePath.Text = source.EnginePath, target => target.UsePathsEnteredByHand(enginePath.Text.Trim(), target.ModelPath)),
            new([nameof(Settings.ModelPath)], source => modelPath.Text = source.ModelPath, target => target.UsePathsEnteredByHand(target.EnginePath, modelPath.Text.Trim())),
            new([nameof(Settings.LocalRewriteEnginePath)], source => rewriteEngine.Text = source.LocalRewriteEnginePath, target => target.LocalRewriteEnginePath = rewriteEngine.Text.Trim()),
            new([nameof(Settings.LocalRewriteModelPath)], source => rewriteModel.Text = source.LocalRewriteModelPath, target => target.LocalRewriteModelPath = rewriteModel.Text.Trim()),
            new([nameof(Settings.VadModelPath)], source => vadModel.Text = source.VadModelPath, target => target.VadModelPath = vadModel.Text.Trim()),
        ];
        void RefreshPaths(IReadOnlyCollection<string>? changed = null)
        {
            bool Refreshed(string name) => changed is null || changed.Contains(name);
            foreach (var (box, name, value) in new[] { (backend, nameof(Settings.SpeechBackend), settings.SpeechBackend), (model, nameof(Settings.SpeechModel), settings.SpeechModel) })
                if (Refreshed(name)) box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == value) ?? box.Items[0];
            foreach (var control in speechControls.Where(control => control.Names.Any(name => Refreshed(name) && PreferenceSections.Installed.Contains(name)))) control.Show(settings);
            FollowSpeechSetUp(); ShowInUse();
        }
        foreach (var field in new[] { enginePath, modelPath, rewriteEngine, rewriteModel, vadModel }) field.TextChanged += (_, _) => Changed();
        foreach (var toggle in new[] { vad, warm }) { toggle.Checked += (_, _) => Changed(); toggle.Unchecked += (_, _) => Changed(); }
        var diagnostics = Button("Export", () =>
        {
            var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Metadata diagnostics|*.json", FileName = "private-flow-diagnostics.json" };
            if (picker.ShowDialog(owner) != true) return;
            try { File.WriteAllText(picker.FileName, JsonSerializer.Serialize(HealthChecks.Metadata(settings), new JsonSerializerOptions { WriteIndented = true })); speechNotices.Clear("speech-export-failed"); Report("Exported metadata only: versions, sizes, backend and configuration flags; no private paths or text."); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { speechNotices.Report(new("speech-export-failed", NoticeKind.Error, "Export failed", ex.Message)); }
        });
        preferenceControls.AddRange(speechControls);
        preferencesStored.Add(changed => { if (changed.Contains(PreferenceSections.SpeechRecognition)) { report.Text = HealthChecks.Describe(settings); FollowSpeechSetUp(); ShowInUse(); } });
        RefreshPaths(); refreshSpeechEngine = changed => { RefreshPaths(changed); report.Text = HealthChecks.Describe(settings); };
        var content = Page(
            speechNotices,
            Section("Status and checks", true),
            Card(
                BodyRow(report, new Thickness(0)),
                Row("Synthetic speech check", "Transcribes generated speech and measures cold and warm start. The microphone stays unused.", Button("Run check", () => _ = CheckAsync(report))),
                Row("Diagnostics", "Exports metadata only: versions, sizes, backend and configuration flags. No private paths or text.", diagnostics)),
            Section("Processor and model"),
            SectionDescription("Automatic follows the recommended setup for this PC. After each update, Private Flow downloads a newly recommended model once in the background. Downloads are pinned and verified; audio never leaves this PC."),
            Card(
                Row("Runs on", "The GPU needs an NVIDIA graphics card and is much faster.", backend),
                Row("Model", $"Larger models recognize names and technical terms better. Use them with the GPU. Automatic on the GPU adds large-v3 ({ModelList.Size(ModelCatalogue.Model(ModelCatalogue.SpeechFinalPass).Bytes)}) for the final pass of meetings and files.", model),
                Row(Stack(new TextBlock { Text = "Apply", Style = Theme("RowTitle") }, inUse, speechProgress), Button("Apply", () => _ = SetupAsync("setup-engine.ps1", "Setting up speech recognition", null,
                    (speechNotices, SetupFailedKey, "Speech recognition was not set up"), [speechProgress], null, false,
                    "-Backend", (string)((ComboBoxItem)backend.SelectedItem).Tag, "-Model", (string)((ComboBoxItem)model.SelectedItem).Tag, "-SetupVersion", SpeechSetup.BuildIdentity)))),
            Section("Local polish"),
            Card(
                Row(Stack(new TextBlock { Text = "Local polish", Style = Theme("RowTitle") },
                    Description($"Optional model for the Local polish style, {ModelList.Size(ModelList.DownloadBytes(ModelList.Find(ModelCatalogue.PolishModel), false))} to download, run on the processor. Downloads only when you click. Every model is also under Models."), polishProgress),
                    Button("Set up Local polish", () => InstallPolish(speechNotices, PolishFailedKey, "Local polish was not set up")))),
            Section("Speech detection and start-up"),
            Card(
                Row("Speech detection", "Uses the local Silero detector to skip silence before recognition.", vad),
                Row("Keep speech model warm", "Starts dictation faster. Unloads after five idle minutes.", warm)),
            Section("Model paths"),
            SectionDescription("Apply fills these in. Change them only to use another local engine or model; updates then leave them alone."),
            Card(
                Row("Speech engine", "whisper-cli.exe", enginePath),
                Row("Speech model", "ggml .bin file", modelPath),
                Row("Local polish engine", "llama-server.exe", rewriteEngine),
                Row("Local polish model", ".gguf file", rewriteModel),
                Row("Silero model", "Speech detection", vadModel)));
        return new(content, null);
    }
    // A setup script run from a row: its steps on the bar of the meeting progress in every view given, with the time it has taken. modelKey
    // names the model of the list under Models that it installs, null for Apply; its row then offers Cancel.
    // step hears the step and share as the views show them. Returns null once installed, otherwise why not.
    private async Task<string?> SetupAsync(string script, string title, string? modelKey, (NoticeStack Stack, string Key, string Title) failure, IReadOnlyList<SetupProgressView> views,
        Action<string, double>? step, bool partOfRun, params string[] arguments)
    {
        // The gate of the Models page: one install, removal, Apply or check at a time; Install recommended runs its own steps through it.
        if (meetings.IsRecording || (partOfRun ? busy || meetings.IsInstallingNotesModel || removing : OperationRunning() is not null)) return "Another setup or a meeting is running.";
        var apply = modelKey is null;
        SetBusy(true); installingKey = modelKey; RefreshModels(); Report(title + "…");
        failure.Stack.Clear(failure.Key);
        using var cancellation = new CancellationTokenSource(); busyCancellation = cancellation;
        var progress = new SetupProgress(); var started = DateTime.UtcNow;
        void Show()
        {
            var elapsed = (DateTime.UtcNow - started).TotalSeconds;
            var text = $"{title} · {progress.Text}" + (elapsed >= 2 ? $" · {elapsed:0} s" : "");
            foreach (var view in views) view.Show(text, progress.Share);
            step?.Invoke(text, progress.Share);
        }
        var active = true;
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => Show();
        Show(); clock.Start();
        try
        {
            // A running worker holds the runtime DLLs that setup may replace; the next dictation restarts it.
            if (apply) WhisperEngine.Shutdown();
            var lines = new ProgressCallback<string>(line => owner.Dispatcher.BeginInvoke(() => { if (active) { progress.Report(line); Show(); } }));
            var (error, changed) = await SpeechSetup.InstallAsync(settings, script, arguments, cancellation.Token, lines, RunSetup);
            if (error is not null)
            { Report("Ready"); failure.Stack.Report(new(failure.Key, NoticeKind.Error, failure.Title, NoticeText.Short(error), NoticeText.Detail(error))); return error; }
            if (apply) SpeechSetUp?.Invoke();
            refreshSpeechEngine(changed); setupInstalled();
            Report(apply ? "Speech recognition is set up; the model paths below are refreshed."
                : modelKey == ModelCatalogue.PolishModel ? "Local polish is set up. Choose it under Text style to use it."
                : $"{ModelCatalogue.Model(modelKey!).Name} is installed." + (ModelList.Find(modelKey!).Use == ModelUse.Speech ? " Choose it under Speech recognition and press Apply to use it." : ""));
            return null;
        }
        catch (OperationCanceledException)
        {
            if (modelKey is not null)
            {
                ModelFiles.RemovePartialDownloads(Settings.Root, ModelCatalogue.Model(modelKey), modelKey == ModelCatalogue.PolishModel ? ModelCatalogue.LlamaRuntimes(false, SpeechSetup.HasNvidiaGpu) : []);
                Report("Installing was cancelled. The partial download was removed; files already verified stay.");
            }
            else
            {
                // Apply fetches the engine build and the speech models setup-engine.ps1 chose; a partial file of any of them is its own.
                foreach (var key in new[] { ModelCatalogue.SpeechTurbo, ModelCatalogue.SpeechMedium, ModelCatalogue.SpeechBase, ModelCatalogue.SpeechFinalPass, ModelCatalogue.VoiceDetection })
                    ModelFiles.RemovePartialDownloads(Settings.Root, ModelCatalogue.Model(key), ModelCatalogue.Runtimes.Where(runtime => runtime.Key.StartsWith("whisper-", StringComparison.Ordinal)));
                Report("Setup cancelled. The partial download was removed; files already verified stay.");
            }
            return "Installing was cancelled.";
        }
        catch (Exception ex)
        {
            var reason = $"Setup failed: {ex.GetType().Name}. Check the data folder access.";
            Report("Ready"); failure.Stack.Report(new(failure.Key, NoticeKind.Error, failure.Title, reason)); return reason;
        }
        finally
        {
            active = false; clock.Stop(); foreach (var view in views) view.Hide(); busyCancellation = null; installingKey = null; SetBusy(false); ModelsChanged?.Invoke();
            // Apply may have put another engine build in place, which only the script can tell.
            if (apply && recommendedRun is null && recommendation is not null) _ = RefreshRecommendationAsync();
        }
    }
    private async Task CheckAsync(TextBox report)
    {
        if (busy || meetings.IsRecording) return; SetBusy(true);
        using var cancellation = new CancellationTokenSource(); busyCancellation = cancellation;
        speechNotices.Clear(CheckFailedKey);
        try { Report("Checking with generated speech; microphone stays unused…"); report.Text = await HealthChecks.SyntheticAsync(settings.Snapshot(), cancellation.Token); Report("Synthetic check complete."); }
        catch (Exception ex)
        { Report("Ready"); speechNotices.Report(new(CheckFailedKey, NoticeKind.Error, "The synthetic check failed", $"{ex.GetType().Name}. Check the setup under Processor and model.")); }
        finally { busyCancellation = null; SetBusy(false); }
    }
}
