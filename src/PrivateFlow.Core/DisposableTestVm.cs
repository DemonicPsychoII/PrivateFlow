namespace PrivateFlow.Core;

// A disposable Windows test machine, reset after every run. A test sends simulated keys, pastes into real apps or writes the system
// clipboard only there (AGENTS.md); the desktop checks, the verification host's paste check and the test build's automatic insertion
// ask this guard. The machine's own run setup marks it with a file that names its computer and account as "<computer>\<account>";
// no other PC has that file, and a file copied to another PC or account names the wrong one.
public static class DisposableTestVm
{
    public static string MarkerPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PrivateFlow", "disposable-test-machine.txt");
    public static bool Is(string? marker, string computerName, string account) =>
        marker?.Trim() is { Length: > 0 } named && computerName.Length > 0 && account.Length > 0 && named == computerName + "\\" + account;
    public static bool IsThisComputer => Is(ReadMarker(), Environment.MachineName, Environment.UserName);
    public const string Refusal = "This sends simulated keys and writes the clipboard, which AGENTS.md allows only on a disposable Windows test machine marked by its run setup, not on this PC. Nothing was run.";

    private static string? ReadMarker()
    {
        try { return File.Exists(MarkerPath) ? File.ReadAllText(MarkerPath) : null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
