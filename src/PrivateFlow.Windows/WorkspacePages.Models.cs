using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PrivateFlow.Windows;

// Settings → Models: every downloadable model in one list with its size, state, where it runs and its Install and Remove, then which
// model each feature uses. Install and Remove act at once; the choices wait for Save preferences like every other value.
internal sealed partial class WorkspacePages
{
    private readonly NoticeStack modelsNotices = new("Models notices");
    private readonly Action<string> openSettings;
    public WorkspacePage Models { get; }
    // A model list row's parts that follow the settings and the running operations.
    private sealed record ModelRow(ListedModel Model, TextBlock Sizes, TextBlock State, TextBlock RunsOn, Button Install, Button Remove, SetupProgressView Progress);
    private readonly List<ModelRow> modelRows = [];
    // The model whose setup runs now, and every bar and button that shows it, wherever it is offered.
    private string? installingKey;
    private readonly Dictionary<string, List<SetupProgressView>> progressViews = ModelList.All.ToDictionary(model => model.Key, _ => new List<SetupProgressView>());
    private readonly List<(string Key, Button Button, string Label, bool NamedByModel)> installButtons = [];
    // Why the last install of the meeting notes model ended without it: a card here and on the Notes tab until it is installed.
    private string? notesInstallFailure;
    private Action refreshUsedFor = () => { };
    // The local choice rows, which stand in the local processing card and follow whether preferences can change.
    private readonly List<UIElement> choiceRowsInCard = [];
    // An install or a removal ended, so what is installed may have changed.
    public event Action? ModelsChanged;
    private const string ModelInstallFailedKey = "model-install-failed", ModelRemoveRefusedKey = "model-remove-refused", ModelRemoveFailedKey = "model-remove-failed",
        NotesInstallRefusedKey = "notes-install-refused", NotesInstallTitle = "Installing the meeting notes model";

    private WorkspacePage BuildModels()
    {
        var transcriptionRows = new List<Border>(); var localRows = new List<Border>();
        foreach (var model in ModelList.All)
        {
            var sizes = Description(""); var state = Description(""); var runsOn = Description("");
            foreach (var line in new[] { sizes, state, runsOn }) line.Margin = new(0, 2, 0, 0);
            var (install, progress) = Offer(model, $"Install ({ModelList.Size(ModelList.DownloadBytes(model, SpeechSetup.HasNvidiaGpu))})");
            progress.Margin = new(0, 6, 0, 0);
            var remove = Button("Remove", () => _ = RemoveAsync(model), "Danger");
            AutomationProperties.SetName(remove, "Remove " + model.Pinned.Name);
            modelRows.Add(new(model, sizes, state, runsOn, install, remove, progress));
            var row = Row(Stack(new TextBlock { Text = model.Pinned.Name, Style = Theme("RowTitle") }, Description(model.Purpose), sizes, state, runsOn, progress), Actions(install, remove));
            (model.Use is ModelUse.Polish or ModelUse.Notes ? localRows : transcriptionRows).Add(row);
            modelCards.Add((model.Use is ModelUse.Polish or ModelUse.Notes ? LocalCardTitle : TranscriptionCardTitle, model.Key));
        }
        settings.Saved += RefreshModels;
        var (speechRow, choiceRows) = BuildUsedFor();
        // The choice rows were built in one panel; in the card they stand as its rows.
        var choices = choiceRows.Children.OfType<Border>().ToList();
        choiceRows.Children.Clear(); choiceRowsInCard.AddRange(choices);
        var content = Page(
            modelsNotices,
            Section("Models", true),
            SectionDescription("Every model Private Flow can download, what it is for and where it runs. Install and Remove act at once, outside Save preferences; nothing is downloaded before you press Install. Downloads are pinned and verified, and audio and text stay on this PC."),
            BuildRecommended(),
            Section(TranscriptionCardTitle),
            SectionDescription("Speech recognition with whisper.cpp: the engine build and the models for dictation, the final pass of meetings and files, and voice detection."),
            Card([speechRow, .. transcriptionRows]),
            Section(LocalProcessingTitle),
            SectionDescription("Language models run by llama.cpp on this PC for Local polish and meeting notes. The choices below are preferences: Save preferences stores them, Discard undoes them."),
            Card([.. localRows, .. choices]));
        RefreshModels();
        // The PC is read when the section is first shown, not at start.
        content.IsVisibleChanged += (_, _) => { if (content.IsVisible && recommendation is null && !recommending) _ = RefreshRecommendationAsync(); };
        return new(content, null);
    }
    private const string TranscriptionCardTitle = "Transcription models", LocalCardTitle = "Local processing models", LocalProcessingTitle = LocalCardTitle;
    // Which card holds each model's row, for the verification.
    private readonly List<(string Card, string Key)> modelCards = [];
    internal IReadOnlyList<(string Card, string Key)> ModelCards => modelCards;
    private SetupProgressView NewProgress(string key)
    {
        var view = new SetupProgressView();
        progressViews[key].Add(view);
        return view;
    }
    // An Install button with its bar, wherever a model is offered: a row of the list, next to a choice, on the Notes tab. Pressed while its
    // model installs, the button cancels the install. A screen reader hears the model's name unless the label already says which it is.
    private Button InstallButton(ListedModel model, string label, bool namedByModel = true)
    {
        var button = Button(label, () => PressInstall(model), "Primary");
        installButtons.Add((model.Key, button, label, namedByModel));
        return button;
    }
    private InstallOffer Offer(ListedModel model, string label, bool namedByModel = true) => new(InstallButton(model, label, namedByModel), NewProgress(model.Key));
    // Every Install of a model lands here, also Try again and the Local polish cards.
    private void PressInstall(ListedModel model)
    {
        if (installingKey == model.Key) { busyCancellation?.Cancel(); return; }
        var notes = model.Use == ModelUse.Notes;
        // The notes model is also offered on the Notes tab, away from what keeps it waiting, so a press that cannot start says why.
        if (OperationRunning() is { } other)
        {
            if (notes) modelsNotices.Report(new(NotesInstallRefusedKey, NoticeKind.Warning, "Not installed while another setup runs", $"{other} runs first; install the meeting notes model once it has finished."));
            return;
        }
        if (notes && meetings.IsRecording)
        { modelsNotices.Report(new(NotesInstallRefusedKey, NoticeKind.Warning, "Not installed while a meeting runs", "Finish the meeting before installing the meeting notes model.")); return; }
        var failedTitle = $"{model.Pinned.Name} was not installed";
        switch (model.Use)
        {
            case ModelUse.Notes: _ = InstallNotesModelAsync(); break;
            case ModelUse.Polish: InstallPolish(modelsNotices, ModelInstallFailedKey, failedTitle); break;
            default: _ = SetupAsync("setup-engine.ps1", "Installing " + model.Pinned.Name, model.Key, (modelsNotices, ModelInstallFailedKey, failedTitle), progressViews[model.Key], null, false, "-Download", model.Key); break;
        }
    }
    // The model chosen for Local polish, installed unless it is there or installing; "Set up Local polish" on its cards lands here.
    public void InstallForLocalPolish(ListedModel model)
    {
        if (ModelList.IsInstalled(settings, model) || installingKey == model.Key) return;
        PressInstall(model);
    }
    // Set up Local polish under Speech recognition and Install under Models run this one setup; the failure card stands where it was pressed.
    private void InstallPolish(NoticeStack notices, string failedKey, string failedTitle) =>
        _ = SetupAsync("setup-rewrite.ps1", "Setting up Local polish", ModelCatalogue.PolishModel, (notices, failedKey, failedTitle), progressViews[ModelCatalogue.PolishModel], null, false);
    // The meeting notes model from any of its offers, and as the local processing step of Install recommended.
    private Task<string?> InstallNotesModelAsync(Action<string, double>? step = null, bool partOfRun = false) =>
        SetupAsync("setup-rewrite.ps1", NotesInstallTitle, ModelCatalogue.NotesModel, (modelsNotices, NotesStatus.InstallFailedKey, "The meeting notes model was not installed"),
            progressViews[ModelCatalogue.NotesModel], step, partOfRun, "-Part", "notes");

    // What runs now and keeps a model from being removed, or null.
    // The one gate of the page: an install, a removal, Apply or a check runs, and every other one waits until it has finished.
    private bool removing;
    private string? OperationRunning() =>
        recommendedRun is not null ? "Installing the recommended models" : busy ? "A setup or a check" : removing ? "Removing a model"
        : installingKey == ModelCatalogue.NotesModel ? NotesInstallTitle : null;
    private string? RunningOperation() =>
        !enabled ? "A dictation or a file transcription" : meetings.IsRecording ? "A meeting" : OperationRunning()
        ?? (LocalRewriter.Running is { } task ? (task == LocalRewriter.NotesTask ? "Writing meeting notes" : "Local polish") : null);
    // The stored preferences and what the next dictation would use, which includes a text style chosen but not saved yet.
    internal Func<Settings> NextDictation { get; set; } = () => new();
    private string? RemoveRefusal(ListedModel model) =>
        ModelList.RemoveRefusal(settings, model, RunningOperation()) ?? ModelList.NeededBy(NextDictation(), model);
    private async Task RemoveAsync(ListedModel model)
    {
        var name = model.Pinned.Name;
        modelsNotices.Clear(ModelRemoveFailedKey);
        void Refuse(string reason) => modelsNotices.Report(new(ModelRemoveRefusedKey, NoticeKind.Warning, $"{name} was not removed", NoticeText.Short(reason), NoticeText.Detail(reason)));
        if (RemoveRefusal(model) is { } refusal) { Refuse(refusal); return; }
        modelsNotices.Clear(ModelRemoveRefusedKey);
        var file = ModelList.OwnFile(model);
        var size = ModelList.Size(new FileInfo(file).Length);
        if (!await ConfirmationDialog.AskAsync(owner, $"Remove {name}?", $"Deletes the model file ({size}) from this PC. Install downloads it again.", "Remove")) return;
        // Asked again: something may have started while the question was open.
        if (RemoveRefusal(model) is { } later) { Refuse(later); return; }
        removing = true;
        try
        {
            // A warm local model server holds its file open.
            if (model.Use is ModelUse.Polish or ModelUse.Notes && !await LocalRewriter.StopIdleAsync()) { Refuse("The local model is in use. Remove it once it has finished."); return; }
            // Decided again on the real path right before deleting: a link put in place meanwhile is refused too.
            if (ModelFiles.DeletionRefusal(file, Settings.Root) is { } unsafeFile) { Refuse(unsafeFile); return; }
            File.Delete(file); Report($"{name} removed; {size} freed.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { modelsNotices.Report(new(ModelRemoveFailedKey, NoticeKind.Error, $"{name} was not removed", NoticeText.Short(ex.Message), NoticeText.Detail(ex.Message))); }
        finally { removing = false; OperationEnded?.Invoke(); }
        RefreshModels(); ModelsChanged?.Invoke();
    }

    private void RefreshModels()
    {
        if (modelRows.Count == 0) return;
        var nvidia = SpeechSetup.HasNvidiaGpu;
        var free = enabled && !meetings.IsRecording && OperationRunning() is null;
        // Apply, Set up Local polish and the check under Speech recognition wait too.
        SpeechEngine.Content.IsEnabled = enabled && OperationRunning() is null;
        foreach (var row in modelRows)
        {
            var model = row.Model;
            var installed = ModelList.IsInstalled(settings, model);
            var installing = installingKey == model.Key;
            var onDisk = installed && File.Exists(ModelList.ModelPath(settings, model)) ? new FileInfo(ModelList.ModelPath(settings, model)).Length : model.Pinned.Bytes;
            row.Sizes.Text = $"Download {ModelList.Size(ModelList.DownloadBytes(model, nvidia))} · {ModelList.Size(onDisk)} on disk for the model file";
            row.State.Text = installing ? "Installing" : !installed ? "Not installed"
                : ModelList.NeededBy(settings, model) is { } needed ? "Installed. " + NoticeText.Short(needed) : "Installed. Nothing uses it now.";
            row.RunsOn.Text = ModelList.RunsOn(settings, model, nvidia);
            Show(row.Remove, installed && !installing);
            row.Remove.IsEnabled = free;
            Show(row.Install, !installed || installing);
        }
        foreach (var (key, button, label, namedByModel) in installButtons)
        {
            var installing = installingKey == key;
            button.Content = installing ? "Cancel installing" : label;
            AutomationProperties.SetName(button, !namedByModel ? (string)button.Content : (installing ? "Cancel installing " : "Install ") + ModelCatalogue.Model(key).Name);
            if (installing) button.ClearValue(FrameworkElement.StyleProperty); else button.Style = Theme("Primary");
            button.IsEnabled = installing || free;
        }
        foreach (var row in choiceRowsInCard) row.IsEnabled = enabled;
        if (notesInstallFailure is null || installingKey == ModelCatalogue.NotesModel || MeetingNotesModel.IsInstalled(settings)) modelsNotices.Clear(NotesStatus.InstallFailedKey);
        else modelsNotices.Raise(new(NotesStatus.InstallFailedKey, NoticeKind.Error, "The meeting notes model was not installed", NoticeText.Short(notesInstallFailure), NoticeText.Detail(notesInstallFailure),
            new("Try again", () => PressInstall(ModelList.Find(ModelCatalogue.NotesModel)))));
        if (!meetings.IsRecording) modelsNotices.Clear(NotesInstallRefusedKey);
        meetings.FollowNotesInstall();
        UpdateRecommended();
        refreshUsedFor();
    }
    private static void Show(UIElement element, bool shown) => element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    // The rows as shown, for the verification.
    internal IReadOnlyList<(string Key, string State, string RunsOn, bool InstallShown, bool RemoveShown)> ModelRowsShown =>
        modelRows.Select(row => (row.Model.Key, row.State.Text, row.RunsOn.Text, row.Install.Visibility == Visibility.Visible, row.Remove.Visibility == Visibility.Visible)).ToList();
    internal SetupProgressView ModelProgress(string key) => modelRows.Single(row => row.Model.Key == key).Progress;
    internal Button ModelInstall(string key) => modelRows.Single(row => row.Model.Key == key).Install;
    // The install of the meeting notes model in its row, for the verification: the step while it runs, and the text of its card.
    internal (string? Progress, double Share, string? Failure) NotesInstallInModels =>
        (ModelProgress(ModelCatalogue.NotesModel) is { Shown: true } shown ? shown.Text : null, ModelProgress(ModelCatalogue.NotesModel).Share, modelsNotices.Notices.Find(NotesStatus.InstallFailedKey)?.Text);

    // Which model each feature uses. Speech is chosen with Apply under Speech recognition and only shown here; Local polish, where the meeting
    // notes model runs and how meeting notes are written are preferences.
    private (Border SpeechRow, StackPanel ChoiceRows) BuildUsedFor()
    {
        var speech = Description("");
        var openSpeech = Button("Open Speech recognition", () => openSettings(MainWindow.SpeechRecognitionTitle));
        var polishChoice = Choice("Local polish model", settings.LocalPolishModel.ToString(),
            (nameof(LocalPolishModel.PolishModel), "Local polish model, on the processor"), (nameof(LocalPolishModel.MeetingNotesModel), "Meeting notes model"));
        var notesRunsOn = Choice("Meeting notes model runs on", NotesLayersTag(settings.MeetingNotesGpuLayers), ("-1", "GPU, as many layers as fit"), ("0", "Processor only"));
        // A layer count entered in settings.json by hand stays as it is.
        void OfferStoredLayers(int layers)
        {
            if (layers > 0 && notesRunsOn.Items.OfType<ComboBoxItem>().All(item => (string)item.Tag != layers.ToString()))
                notesRunsOn.Items.Add(new ComboBoxItem { Content = $"GPU, {layers} layers (set in settings.json)", Tag = layers.ToString() });
            notesRunsOn.SelectedItem = notesRunsOn.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == NotesLayersTag(layers));
        }
        OfferStoredLayers(settings.MeetingNotesGpuLayers);
        LocalPolishModel ShownPolishModel() => Enum.Parse<LocalPolishModel>((string)((ComboBoxItem)polishChoice.SelectedItem).Tag);
        int ShownLayers() => int.Parse((string)((ComboBoxItem)notesRunsOn.SelectedItem).Tag);
        // The chosen Local polish model without its files: says so, with its install next to it.
        var polishMissingText = Description("");
        var polishProgress = NewProgress(ModelCatalogue.PolishModel); polishProgress.Margin = new(0, 6, 0, 0);
        var polishInstall = InstallButton(ModelList.Find(ModelCatalogue.PolishModel), $"Install ({ModelList.Size(ModelList.DownloadBytes(ModelList.Find(ModelCatalogue.PolishModel), SpeechSetup.HasNvidiaGpu))})");
        var notesForPolish = Offer(ModelList.Find(ModelCatalogue.NotesModel), $"Install ({ModelList.Size(MeetingNotesModel.DownloadBytes)})", false);
        notesForPolish.Progress.Margin = new(0, 6, 0, 0);
        var polishMissing = Row(Stack(polishMissingText, polishProgress, notesForPolish.Progress), Actions(polishInstall, notesForPolish.Button));
        var notesRunsOnRow = Row(Stack(new TextBlock { Text = "Meeting notes model runs on", Style = Theme("RowTitle") },
            Description("Measured with a synthetic 50-minute meeting of 106 rows on an 8 GB NVIDIA GPU: notes in 28 s on the GPU, 166 s on the processor only, within the time limit of about 18 minutes. " +
                "Local polish with this model: 0.4 s typical on the GPU, 2.5 s on the processor.")), notesRunsOn);
        refreshUsedFor = () =>
        {
            var gpu = SpeechSetup.Processor(settings.EnginePath) == "GPU";
            speech.Text = !File.Exists(settings.EnginePath) || !File.Exists(settings.ModelPath) ? "Not set up yet: choose a processor and a model under Speech recognition and press Apply."
                : $"Dictation: {SpeechSetup.ModelName(settings.ModelPath)} on the {(gpu ? "GPU" : "processor")}" + (settings.SpeechPathsCustom ? ", from a path entered by hand" : "") +
                  $". Final pass of meetings and files: {(File.Exists(settings.MeetingModelPath) ? SpeechSetup.ModelName(settings.MeetingModelPath) : "the dictation model")}. " +
                  $"Runs on: {(settings.SpeechBackend == "auto" ? "Automatic" : gpu ? "GPU" : "processor")}. These are chosen under Speech recognition and set up with Apply.";
            var shownPolish = ModelList.Find(ShownPolishModel() == LocalPolishModel.MeetingNotesModel ? ModelCatalogue.NotesModel : ModelCatalogue.PolishModel);
            var polishInstalled = ModelList.IsInstalled(settings, shownPolish);
            var polishInstalling = installingKey == shownPolish.Key;
            Show(polishMissing, !polishInstalled || polishInstalling);
            polishMissingText.Text = $"The {(shownPolish.Use == ModelUse.Notes ? "meeting notes model" : "Local polish model")} is not installed, so Local polish uses Clean until it is.";
            Show(polishInstall, shownPolish.Use == ModelUse.Polish);
            Show(notesForPolish.Button, shownPolish.Use == ModelUse.Notes && (!polishInstalled || polishInstalling));
            // The CUDA build of llama.cpp is what can choose; the processor build runs on the processor whatever is chosen.
            Show(notesRunsOnRow, File.Exists(settings.MeetingNotesEnginePath) ? ModelList.IsCudaBuild(settings.MeetingNotesEnginePath) : SpeechSetup.HasNvidiaGpu);
        };
        polishChoice.SelectionChanged += (_, _) => { refreshUsedFor(); Changed(); };
        notesRunsOn.SelectionChanged += (_, _) => Changed();
        preferenceControls.AddRange([
            new([nameof(Settings.LocalPolishModel)], source => polishChoice.SelectedItem = polishChoice.Items.OfType<ComboBoxItem>().First(item => (string)item.Tag == source.LocalPolishModel.ToString()),
                target => target.LocalPolishModel = ShownPolishModel()),
            new([nameof(Settings.MeetingNotesGpuLayers)], source => OfferStoredLayers(source.MeetingNotesGpuLayers), target => target.MeetingNotesGpuLayers = ShownLayers())]);
        preferencesStored.Add(_ => RefreshModels());
        var speechRow = Row(Stack(new TextBlock { Text = "Speech recognition in use", Style = Theme("RowTitle") }, speech), openSpeech);
        var notesStyle = BodyRow(Stack(new TextBlock { Text = "Meeting notes", Style = Theme("RowTitle") },
            Description("How the Notes tab on the History page writes a meeting's notes. A model writes them only when you press the button for that meeting. " +
                "The Local polish model (Qwen3-4B, about the same size) is not offered for notes: in its one test, on the processor with an earlier prompt, it cited unrelated rows and missed both pricing decisions of a 53-row meeting and took 165 to 280 s. " +
                "It has not been measured on the GPU or with today's prompt, so whether one download could serve both features that way is open."),
            meetings.NotesStyleChoice));
        return (speechRow, Stack(
                Row(Stack(new TextBlock { Text = "Local polish uses", Style = Theme("RowTitle") },
                    Description("The two models are about the same size, so the meeting notes model lets one download serve notes and polish. Measured with the app's own checks: " +
                        "the meeting notes model polished 33 of 36 test dictations, 0.4 s typical on the GPU, where it holds about 3.1 GB until five idle minutes pass, and 2.5 s on the processor only; " +
                        "the Local polish model polished 20 of 22 in an earlier test with other cases, 0.5 to 1.6 s on the processor. These times are with the model loaded: the first polish after Private Flow starts, " +
                        "or after five idle minutes, loads it first, about 10 s for the Local polish model on the processor. A dictation still waits while meeting notes are written.")), polishChoice),
                polishMissing,
                notesRunsOnRow,
                notesStyle));
    }
    private static string NotesLayersTag(int layers) => Math.Max(-1, layers).ToString();
}

// An Install button with its bar, placed wherever a model is offered; WorkspacePages labels, enables and fills them while its setup runs.
internal sealed record InstallOffer(Button Button, SetupProgressView Progress);
// The install of the meeting notes model as the History page and the notes style choice show it: its offers there, whether it runs and
// why the last one ended without the model. Settings → Models runs it with every other setup.
internal sealed record NotesModelInstall(InstallOffer OnNotesTab, InstallOffer NextToStyle, Func<bool> Running, Func<string?> Failure);
