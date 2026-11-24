using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PrivateFlow.Windows;

// status hears neutral progress; problem hears why an update could not start, for a card on About and updates.
internal sealed class UpdateController(Window owner, TrayIcon tray, Action<UpdateOffer> offered, Action<string> status, Action<PrivateFlow.Core.Notice> problem)
{
    // Checked shortly after start, then once a day, because each check wakes the update server; each new build is announced once.
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMinutes(2) };
    private UpdateOffer? offer;
    private string? announcedCommit;
    private bool checking, stopped, asking;

    public void Start()
    {
        if (InstalledFolder() is null) return;
        timer.Tick += async (_, _) => { timer.Interval = TimeSpan.FromHours(24); await CheckAsync(); };
        timer.Start();
    }
    public void Stop() { stopped = true; timer.Stop(); }
    public void AnnounceIfNew()
    {
        if (offer is null || offer.Commit == announcedCommit) return;
        announcedCommit = offer.Commit;
        tray.ShowBalloon(8000, TrayIcon.UpdateTitle, $"An update is available ({Describe(offer)}). Click to review and install.", Forms.ToolTipIcon.Info);
    }
    private async Task CheckAsync()
    {
        if (checking || stopped) return;
        checking = true;
        try { offer = await UpdateCheck.FindAsync(CancellationToken.None) ?? offer; }
        finally { checking = false; }
        if (offer is null) return;
        offered(offer);
        tray.ShowUpdateAvailable();
    }
    // Asks first; only when the update will really start does beforeInstalling run, which can still stop it by returning false.
    // Returns the running installer, or null when nothing started.
    public async Task<Process?> StartUpdateAsync(Func<Task<bool>> beforeInstalling)
    {
        if (InstalledFolder() is not { } app)
        { problem(new("update-not-installed", PrivateFlow.Core.NoticeKind.Note, "This copy does not update itself", "Update works only for a copy installed with scripts\\install.ps1.")); return null; }
        var script = System.IO.Path.Combine(app, "scripts", "install.ps1");
        // A manual update checks first, so the dialog names the exact release or build and the installer is told it; a check that finds
        // nothing, or fails, starts nothing. The offer stays as the dialog shows it, whatever a check finds while the dialog is open.
        if (asking) return null;
        asking = true;
        UpdateOffer? offer;
        bool confirmed;
        try
        {
            status("Checking for the update…");
            (offer, var upToDate) = await UpdateCheck.CheckAsync(CancellationToken.None);
            if (offer is null)
            {
                problem(upToDate
                    ? new("update-not-started", PrivateFlow.Core.NoticeKind.Note, "Private Flow is up to date", $"Version {typeof(UpdateController).Assembly.GetName().Version?.ToString(3)} is the newest. Nothing was started.")
                    : new("update-not-started", PrivateFlow.Core.NoticeKind.Warning, "The update did not start",
                        "The check could not reach the update source or found no usable release. Nothing was started; try again later."));
                status("Ready");
                return null;
            }
            this.offer = offer; offered(offer);
            var news = $"Update {Describe(offer)}\n{(offer.FromRelease ? "Release" : "Build")} {offer.Commit}" +
                (offer.Changes.Count == 0 ? "\n\n" : ":\n" + string.Join("\n", offer.Changes.Take(8).Select(change => "• " + change)) +
                 (offer.Changes.Count > 8 ? "\n• …" : "") + "\n\n");
            // The restarted app brings Automatic speech choices to the new recommendation itself.
            confirmed = await ConfirmationDialog.AskAsync(owner, "Update Private Flow",
            news + (offer.FromRelease ? "Install this public release from GitHub? " : "Install this build from your update server? ") +
            "Private Flow restarts once it is downloaded. If the recommended speech model changed, " +
            "it downloads in the background afterwards unless you chose a model under Settings → Speech recognition.", "Install update");
        }
        finally { asking = false; }
        // Not null: a check without an offer returned above.
        var fromRelease = offer!.FromRelease;
        if (!confirmed || !await beforeInstalling()) return null;
        // The installer renames the install folder, so it runs from a temp copy outside it. It stops
        // this app only after the download and engine setup succeed, so a failed update leaves it
        // running; its window stays open on failure so the error is readable.
        var copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"PrivateFlow-update-{Guid.NewGuid():N}.ps1");
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        // The release or build the user confirmed: the installer refuses whatever else it finds, such as a newer release or a stale offer.
        var expected = offer.FromRelease ? $" -ExpectedRelease {Quote(offer.Commit)}" : $" -ExpectedCommit {Quote(offer.Commit)}";
        Process? installer;
        try
        {
            System.IO.File.Copy(script, copy);
            installer = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                    $"try {{ & {Quote(copy)} {(fromRelease ? "-FromRelease" : "-FromCloud")}{expected} -Destination {Quote(app)} }} " +
                    $"catch {{ Write-Host $_ -ForegroundColor Red; Read-Host 'Update failed. Press Enter to close' }} " +
                    $"finally {{ Remove-Item -LiteralPath {Quote(copy)} -ErrorAction SilentlyContinue }}" },
                WorkingDirectory = System.IO.Path.GetTempPath(),
                UseShellExecute = false
            });
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { problem(new("update-not-started", PrivateFlow.Core.NoticeKind.Error, "The update did not start", ex.Message)); return null; }
        status("Updating in the PowerShell window. Private Flow restarts when the download is done.");
        return installer;
    }
    private static string Changes(int count) => count == 1 ? "1 new commit" : $"{count} new commits";
    public static string Describe(UpdateOffer offer) =>
        offer.FromRelease ? $"version {offer.Version}" : $"version {offer.Version}, commit {offer.Commit[..7]}" + (offer.Behind is { } behind ? $", {Changes(behind)}" : "");
    // install.ps1 only replaces folders carrying its marker, so dev builds cannot update themselves.
    internal static string? InstalledFolder()
    {
        var app = AppContext.BaseDirectory.TrimEnd('\\');
        return System.IO.File.Exists(System.IO.Path.Combine(app, ".private-flow-install")) &&
            System.IO.File.Exists(System.IO.Path.Combine(app, "scripts", "install.ps1")) ? app : null;
    }
}
