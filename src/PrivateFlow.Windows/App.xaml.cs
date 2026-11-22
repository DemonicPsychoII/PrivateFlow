using System.Threading;
using System.Windows;

namespace PrivateFlow.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    // The diagnostic instance of the verification host (src/PrivateFlow.Verification): capture, shortcuts and insertion stay off. The app
    // itself never sets it.
    internal static bool DiagnosticMode { get; set; }
    // The test build's audio library in place of the microphone; null in normal use.
    internal static LibraryAudio? LibraryAudio { get; private set; }
    // The verification and evaluation modes are in PrivateFlow.Verification.exe, which is built beside the package and not part of it.
    private static readonly string[] VerificationSwitches = ["--verify-workspace", "--diagnostic-ui", "--verify-overlay", "--verify-insert", "--insert-fixture",
        "--evaluate-meeting", "--evaluate-dictation", "--evaluate-recordings", "--write-notes", "--build-audio-library", "--verify-imports", "--verify-worker-faults"];
    protected override void OnStartup(StartupEventArgs e)
    {
        // The interface is English, so its numbers and dates do not follow the region format of Windows: "3.4 GB" and "42%", not
        // "3,4 GB" and "42 %". English rather than the invariant culture, which also writes "42 %".
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);
        if (e.Args is ["--speech-worker", var pipe, var engine, var model, var memory])
        {
            _ = SpeechWorker.RunChildAsync(pipe, engine, model, memory);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
        {
            // Regenerates assets/PrivateFlow.ico from the vector mark in BrandArt.
            System.IO.File.WriteAllBytes(e.Args[1], BrandArt.IconFile());
            Shutdown(0);
            return;
        }
        // Transcribes one file into a text file, only with an isolated data folder and without taking the instance lock.
        if (e.Args is ["--transcribe-file", ..])
        {
            if (e.Args is [_, var input, var output] && Settings.IsolatedRoot) _ = TranscribeFileAsync(input, output); else Shutdown(2);
            return;
        }
        // A verification or evaluation switch ends here instead of opening the app.
        if (e.Args is [var mode, ..] && VerificationSwitches.Contains(mode)) { Shutdown(2); return; }
        // The test build: the whole app, with an audio library in place of the microphone. Without an isolated data folder it does
        // not start at all, so it never runs with the owner's settings or microphone.
        var startArguments = e.Args;
        if (e.Args is ["--library-audio", ..])
        {
            if (e.Args is not [_, var library, "--seed", var seedText, .. var rest] || rest is not ([] or ["--show"]) || !int.TryParse(seedText, out var seed) || seed < 0 || !Settings.IsolatedRoot)
            { Shutdown(2); return; }
            try { LibraryAudio = LibraryAudio.Load(library, seed); }
            catch (System.IO.InvalidDataException) { Shutdown(3); return; }
            startArguments = rest;
        }
        // A copy started while the installer runs would be stopped by it without saving, also in the middle of a meeting. Only the
        // installation of the user's own data folder holds the mutex, so isolated test copies never wait for each other.
        if (!Settings.IsolatedRoot && Mutex.TryOpenExisting(PrivateFlow.Core.UpdateGate.InstallerMutexPrefix + Environment.UserName, out var installer))
        {
            installer.Dispose();
            System.Windows.MessageBox.Show(PrivateFlow.Core.UpdateGate.Installing);
            Shutdown();
            return;
        }
        instance = new Mutex(true, "Local\\PrivateFlow-" + Environment.UserName, out var first);
        if (!first) { System.Windows.MessageBox.Show("Private Flow is already running. Open it from the system tray."); Shutdown(); return; }
        MainWindow window;
        while (true)
        {
            try { window = new MainWindow(); break; }
            catch (SettingsUnavailableException ex)
            {
                // Nothing starts with the defaults over settings that are only in use or not accessible now, and nothing is written.
                if (System.Windows.MessageBox.Show($"Private Flow cannot read its settings. {ex.Message}\n\nNothing was changed. Close the program using settings.json and press Yes to try again, or No to quit.",
                    "Private Flow", MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes) { Shutdown(3); return; }
            }
        }
        MainWindow = window;
        // Start in the tray. The handle is still created so the shortcut registers and works
        // while hidden. --show opens the window for the UI smoke test, which needs it on screen.
        if (startArguments is ["--show"]) window.Show();
        else new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, WarmUpPress);
    }
    // The first shortcut press after start waited for what Windows and WPF prepare on first use: UI Automation, which reads the field
    // the press starts in, and the pill's window. Prepared once here, out of sight; the microphone stays closed until a press.
    internal static void WarmUpPress()
    {
        PillWindow.WarmUp(new RecordingHud()); PillWindow.WarmUp(new MeetingHud(System.Windows.Media.Colors.White));
        _ = Task.Run(Native.CaptureTarget);
    }
    private async Task TranscribeFileAsync(string input, string output)
    {
        try
        {
            var settings = Settings.Load();
            var result = await WhisperEngine.TranscribeFileAsync(input, settings, CancellationToken.None);
            System.IO.File.WriteAllText(output, result);
            Shutdown(0);
        }
        catch (Exception ex) { System.IO.File.WriteAllText(output, "ERROR: " + ex.Message); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { WhisperEngine.Shutdown(); LocalRewriter.Shutdown(); instance?.Dispose(); base.OnExit(e); }
}
