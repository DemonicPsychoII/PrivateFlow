using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;

namespace PrivateFlow.Windows;

// "Recommended for this PC" at the top of Settings → Models: the facts read from this PC, the transcription setup Automatic installs here
// (setup-engine.ps1 -Recommend, so the card never disagrees with Apply) and the local processing model, with Install recommended and the
// two groups' installs. The groups run the existing installs one after the other; while one runs, the other installs wait disabled.
internal sealed partial class WorkspacePages
{
    // Read the PC and ask the setup script; the verification replaces them, as it does the setup itself.
    internal Func<Task<PcFacts>> DetectPc { get; set; } = PcDetection.DetectAsync;
    internal Func<Task<SpeechRecommendation?>> RecommendSpeech { get; set; } = () => ScriptRecommendationAsync([]);
    internal static async Task<SpeechRecommendation?> ScriptRecommendationAsync(string[] arguments)
    {
        var lines = new List<string>();
        var error = await SpeechSetup.RunAsync("setup-engine.ps1", ["-Recommend", .. arguments], CancellationToken.None, new ProgressCallback<string>(line => { lock (lines) lines.Add(line); }));
        lock (lines) return error is null ? SpeechRecommendation.FromOutput(lines) : null;
    }
    private PcFacts? facts;
    private ModelList.Recommendation? recommendation;
    private bool recommending;
    private CancellationTokenSource? recommendedRun;
    private string? recommendedGroup;
    private (long Done, long Bytes, long Total, string Name) recommendedBand;
    private readonly TextBlock factsText = Description(""), localText = Description(""), diskText = Description(""), switchText = Description("");
    private readonly SetupProgressView recommendedProgress = new() { Margin = new(0, 8, 0, 0) };
    private Button? installAll, installTranscription, installLocal;
    // Shown only where this PC needs something other than the default: no NVIDIA GPU, too little memory, GPU memory or disk space.
    private Border? deviation;
    private const string RecommendedFailedKey = "recommended-install-failed";

    private UIElement BuildRecommended()
    {
        deviation = BodyRow(Stack(switchText, localText, diskText));
        installAll = Button("Install recommended", () => _ = InstallRecommendedAsync("all"), "Primary");
        installTranscription = Button("Install transcription models", () => _ = InstallRecommendedAsync("transcription"));
        installLocal = Button("Install local processing models", () => _ = InstallRecommendedAsync("local"));
        installTranscription.ToolTip = "Sets up speech recognition with Automatic, as Apply does: it installs the engine and models below and switches dictation to them.";
        diskText.Foreground = Theme<System.Windows.Media.Brush>("WarningText");
        factsText.ToolTip = "Read from this PC only; nothing is sent anywhere.";
        UpdateRecommended();
        return Stack(
            Section("Recommended for this PC"),
            Card(
                BodyRow(Stack(factsText, Description("Read from this PC only; nothing is sent anywhere. Install recommended sets up what Automatic under Speech recognition installs here, and the meeting notes model for meeting notes and Local polish."))),
                deviation,
                BodyRow(Stack(Buttons(installAll, installTranscription, installLocal), recommendedProgress))));
    }
    // Reads the PC and the script's answer again, after an install or a removal and when the section is first shown.
    internal async Task RefreshRecommendationAsync()
    {
        recommending = true;
        try
        {
            facts = await DetectPc();
            if (await RecommendSpeech() is { } speech) recommendation = ModelList.Recommend(settings, speech, facts);
        }
        finally { recommending = false; }
        UpdateRecommended();
    }
    private void UpdateRecommended()
    {
        if (installAll is null || installTranscription is null || installLocal is null) return;
        // What is installed changes without asking the script again; whether the engine build is in place comes from its last answer.
        if (recommendation is { } known && facts is { } detected) recommendation = ModelList.Recommend(settings, known.Speech, detected);
        if (facts is { } pc)
            factsText.Text = $"Graphics: {pc.Graphics}" + (pc.Nvidia
                    ? (pc.GpuTotal is { } total ? $", {SpeechRuntime.Gigabytes(total)} GB of memory, {SpeechRuntime.Gigabytes(pc.GpuFree ?? 0)} GB free now" : "") + "; an NVIDIA GPU the CUDA builds use"
                    : "; no NVIDIA GPU, so the processor builds are used") +
                $"\nProcessor: {pc.Processor}, {pc.LogicalProcessors} logical processors · Memory: {SpeechRuntime.Gigabytes(pc.Memory)} GB · Free on {pc.Drive} {ModelList.Size(pc.DiskFree)}";
        else factsText.Text = "Reading this PC…";
        var running = recommendedRun is not null;
        var free = enabled && !busy && installingKey is null && !meetings.IsRecording;
        if (recommendation is not { } recommended)
        {
            localText.Text = recommending || facts is null ? "Working out the recommendation…" : "The setup script gave no recommendation; see Speech recognition.";
            Show(localText, true); if (deviation is not null) Show(deviation, true);
            foreach (var button in new[] { installAll, installTranscription, installLocal }) button.IsEnabled = false;
            return;
        }
        var speech = recommended.Speech;
        localText.Text = recommended.LocalNote ?? "";
        Show(localText, recommended.LocalNote is not null);
        diskText.Text = recommended.DiskNote ?? "";
        Show(diskText, recommended.DiskNote is not null);
        // A speech setup chosen by hand is said before the press; installing the transcription models asks before it switches it.
        var handChosen = recommended.TranscriptionComplete ? null : HandChosenSpeech();
        switchText.Text = handChosen is null ? "" : $"Speech recognition uses {handChosen}. Installing the transcription models switches it to Automatic; Private Flow asks first.";
        Show(switchText, handChosen is not null);
        if (deviation is not null) Show(deviation, recommended.LocalNote is not null || recommended.DiskNote is not null || handChosen is not null);
        var blocked = recommended.DiskNote is not null && facts is { } disk && recommended.TranscriptionBytes * ModelList.DiskMargin > disk.DiskFree;
        void Set(Button button, string group, string label, bool complete, string done)
        {
            var mine = running && recommendedGroup == group;
            button.Content = mine ? "Cancel installing" : complete ? done : label;
            AutomationProperties.SetName(button, (string)button.Content);
            button.IsEnabled = mine || !running && free && !complete && !blocked;
        }
        Set(installAll, "all", $"Install recommended ({ModelList.Size(recommended.TotalBytes)})", recommended.TotalBytes == 0, "Recommended models installed");
        Set(installTranscription, "transcription", $"Install transcription models ({ModelList.Size(recommended.TranscriptionBytes)})", recommended.TranscriptionComplete, "Transcription models installed");
        Set(installLocal, "local", $"Install local processing models ({ModelList.Size(recommended.LocalBytes)})", recommended.LocalComplete,
            recommended.Local.Count == 0 ? "No local processing model recommended" : "Local processing model installed");
    }
    // The bar of the whole run: each group's share placed by its download size, so it never moves backwards from one group to the next.
    private void ShowRecommendedStep(string step, double share)
    {
        var (done, bytes, total, name) = recommendedBand;
        recommendedProgress.Show($"Installing the recommended models, {name} · {step}", total == 0 ? share : (done + share * bytes) / total);
    }
    // Pressed again while its run goes on, the button cancels the step that runs and the rest of the run.
    private async Task InstallRecommendedAsync(string group)
    {
        if (recommendedRun is { } running)
        {
            if (recommendedGroup != group) return;
            running.Cancel(); busyCancellation?.Cancel();
            return;
        }
        if (recommendation is not { } recommended || OperationRunning() is not null || meetings.IsRecording) return;
        if (group != "local" && !recommended.TranscriptionComplete && HandChosenSpeech() is { } chosen
            && !await ConfirmationDialog.AskAsync(owner, "Switch speech recognition to Automatic?",
                $"Installing the transcription models sets up Automatic, as Apply does: speech recognition switches from {chosen} to {Describe(recommended.Speech)}.", "Switch and install"))
            return;
        if (OperationRunning() is not null) return;
        modelsNotices.Clear(RecommendedFailedKey);
        var steps = new List<(string Name, long Bytes, Func<Task<string?>> Install)>();
        if (group != "local" && !recommended.TranscriptionComplete)
            steps.Add(("transcription", recommended.TranscriptionBytes, () => SetupAsync("setup-engine.ps1", "Setting up speech recognition", null,
                (modelsNotices, RecommendedFailedKey, "The recommended transcription models were not installed"), [], ShowRecommendedStep, true,
                "-Backend", "auto", "-Model", "auto", "-SetupVersion", SpeechSetup.BuildIdentity)));
        // The step's name already says which model installs, so its bar shows the step without the install's title.
        if (group != "transcription" && !recommended.LocalComplete)
            steps.Add(("local processing", recommended.LocalBytes, async () =>
                await InstallNotesModelAsync((text, share) => ShowRecommendedStep(text.Replace(NotesInstallTitle + " · ", ""), share), true)
                ?? (MeetingNotesModel.IsInstalled(settings) ? null : "The meeting notes model was not installed.")));
        using var run = new CancellationTokenSource();
        recommendedRun = run; recommendedGroup = group;
        var total = steps.Sum(step => step.Bytes); long done = 0;
        string? failure = null; var failedStep = "";
        try
        {
            for (var index = 0; index < steps.Count && !run.IsCancellationRequested; index++)
            {
                var (name, bytes, install) = steps[index];
                recommendedBand = (done, bytes, total, $"{index + 1} of {steps.Count}, {name}");
                ShowRecommendedStep("starting", 0); UpdateRecommended();
                if (await install() is { } error) { failure = error; failedStep = name; break; }
                done += bytes;
            }
        }
        finally { recommendedRun = null; recommendedGroup = null; recommendedProgress.Hide(); }
        // A failed install of the notes model has its own card with Try again on this page; a failed transcription setup gets one here.
        if (failure is not null && !run.IsCancellationRequested && failedStep == "transcription")
            modelsNotices.Report(new(RecommendedFailedKey, NoticeKind.Error, "The recommended transcription models were not installed", NoticeText.Short(failure), NoticeText.Detail(failure),
                new("Try again", () => _ = InstallRecommendedAsync(group))));
        if (run.IsCancellationRequested) Report("Installing the recommended models was cancelled. Files already verified stay.");
        else if (failure is null) Report("The recommended models are installed.");
        await RefreshRecommendationAsync();
        RefreshModels();
    }
    // The speech setup the user chose instead of Automatic, as a phrase, or null while Automatic is in use.
    private string? HandChosenSpeech() =>
        settings.SpeechPathsCustom ? $"the engine and model paths entered by hand ({SpeechSetup.ModelName(settings.ModelPath)})"
        : settings.SpeechBackend != "auto" || settings.SpeechModel != "auto"
            ? $"{(settings.SpeechModel == "auto" ? "the recommended model" : settings.SpeechModel)} on {(settings.SpeechBackend == "auto" ? "the recommended processor" : settings.SpeechBackend == "cuda12" ? "the GPU" : "the processor")}, chosen under Speech recognition"
            : null;
    private static string Describe(SpeechRecommendation speech) =>
        $"{ModelCatalogue.Model(speech.Model).Name} on the {(speech.Backend == "cuda12" ? "GPU" : "processor")}" + (speech.MeetingModel.Length > 0 ? $" with {ModelCatalogue.Model(speech.MeetingModel).Name} for the final pass" : "");
    // The card's state as shown, for the verification.
    internal (ModelList.Recommendation? Recommendation, PcFacts? Facts, string FactsText, (string Label, bool Enabled) All, (string Label, bool Enabled) Transcription, (string Label, bool Enabled) Local, string LocalText, string? Progress) RecommendedShown =>
        (recommendation, facts, factsText.Text, ((string)installAll!.Content, installAll.IsEnabled), ((string)installTranscription!.Content, installTranscription.IsEnabled),
         ((string)installLocal!.Content, installLocal.IsEnabled), localText.Text, recommendedProgress.Shown ? recommendedProgress.Text : null);
}
