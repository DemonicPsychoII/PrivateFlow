namespace PrivateFlow.Core;

// The installer stops Private Flow once the update is downloaded, so an update and a recording exclude each other: the update does
// not start while a dictation, a meeting or a setup runs, and nothing starts recording while it runs.
public static class UpdateGate
{
    public const string StartRefusal = "Private Flow is being updated and restarts when the download is done. Start it again after the restart.";
    // Quit while the update runs, and a start while the installer runs (scripts\install.ps1 holds InstallerMutex until it starts the app).
    public const string Installing = "An update is installing. Private Flow restarts when it is done.";
    public const string InstallerMutexPrefix = "Local\\PrivateFlow-Install-";

    // Why the update cannot start now, or null.
    public static string? UpdateRefusal(bool dictationActive, bool meetingActive, bool otherOperation) =>
        dictationActive ? "A dictation is running. Update once it has finished; the update restarts Private Flow."
        : meetingActive ? "A meeting is recording or being finished. Update once it is saved; the update restarts Private Flow."
        : otherOperation ? "A setup, check or model removal is running under Settings. Update once it has finished."
        : null;
}
