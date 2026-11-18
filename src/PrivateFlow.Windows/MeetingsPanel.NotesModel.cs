using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;

namespace PrivateFlow.Windows;

// The install action of the meeting notes model: on the Notes tab while the Local model style lacks it, and under Settings → Models in
// the model's row and next to the notes style. Nothing is downloaded before the button is pressed; the setup script pins and verifies
// every download.
internal sealed partial class MeetingsPanel
{
    // Why the install failed is a card under Models and on the Notes tab. Label is the button's text while nothing installs.
    // Name is what a screen reader hears for the button while nothing installs; the label by default.
    internal sealed record InstallRow(Button Button, SetupProgressView Progress, string Label, string? Name = null);
    private readonly InstallRow installOnNotes, installInSettings, installForChoice;
    // Every install row under Settings → Models, which all show the one install.
    private readonly List<InstallRow> settingsRows = [];
    // The model's row in the list under Models, and the install next to the Local model notes style there.
    internal InstallRow ModelsListInstall => installInSettings;
    internal InstallRow NotesChoiceInstall => installForChoice;
    internal bool IsInstallingNotesModel => installCancellation is not null;
    // Another install, removal or setup that runs on the Models page and keeps this install waiting; the page sets it.
    internal Func<string?> OtherOperation { get; set; } = () => null;
    // The other operations changed: the install buttons follow without redrawing the rest.
    internal void FollowOtherOperations() { foreach (var row in settingsRows.Append(installOnNotes)) UpdateInstallRow(row); }
    // The install that runs or ran last, and why it ended without the model; for the recommended install, which waits for it.
    internal Task InstallRun => installRun;
    // For Install recommended, which runs its steps itself: starts the install unless one runs, and says how it ended.
    internal async Task<string?> InstallForRunAsync()
    {
        if (installCancellation is not null) return "The meeting notes model is installing already.";
        if (IsRecording) return "Finish the meeting before installing the meeting notes model.";
        installRun = InstallNotesModelAsync();
        await installRun;
        return MeetingNotesModel.IsInstalled(settings) ? null : installFailure ?? "The meeting notes model was not installed.";
    }
    // Cancels a running install; nothing when none runs.
    internal void CancelInstall() => installCancellation?.Cancel();
    internal string? InstallFailure => installFailure;
    // The model was installed, or an install started or ended.
    internal event Action? InstallChanged;
    // Stands in for the setup script in the verification, also in the window, so it never downloads.
    internal Func<IProgress<string>, CancellationToken, Task<string?>> InstallNotesModel { get; set; }
    private CancellationTokenSource? installCancellation;
    // Quit waits for it, so the setup has ended before the app does.
    private Task installRun = Task.CompletedTask;
    private SetupProgress installProgress = new();
    private DateTime installStarted;
    private string? installFailure;
    private const string InstallRefusedKey = "notes-install-refused";

    private static string InstallLabel => $"Install the meeting notes model ({SpeechRuntime.Gigabytes(MeetingNotesModel.DownloadBytes)} GB)";
    private static string InstallOffer =>
        $"Install downloads Qwen3.5-4B and llama.cpp once, about {SpeechRuntime.Gigabytes(MeetingNotesModel.DownloadBytes)} GB, pinned and verified; nothing is downloaded before you press it.";
    private InstallRow NewInstallRow(string label, string? name = null) => new(WorkspacePages.Button(label, PressInstall, "Primary"), new SetupProgressView(), label, name);
    // Another place under Settings → Models that offers the install, such as next to the Local polish choice.
    internal InstallRow AddInstallRow(string label)
    {
        var row = NewInstallRow(label);
        settingsRows.Add(row); UpdateInstallRow(row);
        return row;
    }
    // A model file was removed outside the install.
    internal void RefreshInstall() => UpdateInstall();
    private string InstallProgress()
    {
        var elapsed = (DateTime.UtcNow - installStarted).TotalSeconds;
        return "Installing the meeting notes model · " + installProgress.Text + (elapsed >= 2 ? $" · {elapsed:0} s" : "");
    }
    private void UpdateInstallRow(InstallRow row)
    {
        var running = installCancellation is not null;
        row.Button.Content = running ? "Cancel installing" : row.Label;
        AutomationProperties.SetName(row.Button, row.Name is { } name ? (running ? "Cancel installing " : "Install ") + name : (string)row.Button.Content);
        // While it runs, cancelling is the likely step; the prominent style stays with the install.
        if (running) row.Button.ClearValue(StyleProperty); else row.Button.Style = WorkspacePages.Theme("Primary");
        // A multi-gigabyte download, checksum and extraction wait while a meeting records, as speech setup does.
        // Another setup on the Models page runs first; the button waits visibly.
        row.Button.IsEnabled = running || !IsRecording && OtherOperation() is null;
        if (running) row.Progress.Show(InstallProgress(), installProgress.Share); else row.Progress.Hide();
    }
    // Both places at once; the Notes tab also switches between the install action and the write button.
    private void UpdateInstall()
    {
        var installed = MeetingNotesModel.IsInstalled(settings);
        var running = installCancellation is not null;
        foreach (var row in settingsRows) { Show(row.Button, !installed || running); UpdateInstallRow(row); }
        if (!IsRecording) modelsNotices.Clear(InstallRefusedKey);
        if (installFailure is null || running || installed) modelsNotices.Clear(NotesStatus.InstallFailedKey);
        else modelsNotices.Raise(new(NotesStatus.InstallFailedKey, NoticeKind.Error, "The meeting notes model was not installed", NoticeText.Short(installFailure), NoticeText.Detail(installFailure),
            new("Try again", PressInstall)));
        if (selected is not null) { UpdateWriteRow(selected); UpdateNotesStatus(selected); }
        InstallChanged?.Invoke();
    }
    // Pressed again while it runs, the button cancels the installation.
    internal void PressInstall()
    {
        if (installCancellation is { } running) { running.Cancel(); return; }
        if (OtherOperation() is { } other)
        { modelsNotices.Report(new(InstallRefusedKey, NoticeKind.Warning, "Not installed while another setup runs", $"{other} runs first; install the meeting notes model once it has finished.")); return; }
        if (IsRecording)
        { modelsNotices.Report(new(InstallRefusedKey, NoticeKind.Warning, "Not installed while a meeting runs", "Finish the meeting before installing the meeting notes model.")); return; }
        installRun = InstallNotesModelAsync();
    }
    private async Task InstallNotesModelAsync()
    {
        using var cancellation = new CancellationTokenSource();
        installCancellation = cancellation; installFailure = null; installProgress = new(); installStarted = DateTime.UtcNow;
        notesTimer.Start(); UpdateInstall();
        report("Installing the meeting notes model. You can keep using Private Flow.");
        var progress = installProgress;
        string? error;
        try { error = await InstallNotesModel(new ProgressCallback<string>(line => Dispatcher.BeginInvoke(() => { if (installCancellation is not null) { progress.Report(line); UpdateInstall(); } })), cancellation.Token); }
        catch (OperationCanceledException) { error = "Installing was cancelled. The partial download was removed, so the next try downloads it again; files already verified stay."; }
        catch (Exception ex) { error = $"Setup could not run ({ex.GetType().Name})."; }
        finally { installCancellation = null; if (notesCancellation is null) notesTimer.Stop(); }
        installFailure = error;
        // The reason is in the install row and on the Notes tab; the status line only points there.
        report(error is null ? "The meeting notes model is installed. Write notes with it on the Notes tab of a meeting." : "Installing the meeting notes model ended; Models and the Notes tab say how.");
        UpdateInstall();
    }
}
