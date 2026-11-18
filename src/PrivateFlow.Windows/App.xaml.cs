using System.Threading;
using System.Windows;

namespace PrivateFlow.Windows;

public partial class App : System.Windows.Application
{
    private Mutex? instance;
    internal static bool DiagnosticMode { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        // The interface is English, so its numbers and dates do not follow the region format of Windows: "3.4 GB" and "42%", not
        // "3,4 GB" and "42 %". English rather than the invariant culture, which also writes "42 %".
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        base.OnStartup(e);
        if (e.Args is ["--verify-workspace", var report] && Settings.IsolatedRoot)
        { DiagnosticMode = true; _ = WorkspaceVerification.RunAsync(report); return; }
        if (e.Args is ["--diagnostic-ui"] && Settings.IsolatedRoot)
        {
            DiagnosticMode = true;
            try { var diagnostic = new MainWindow(); MainWindow = diagnostic; diagnostic.Show(); }
            catch (SettingsUnavailableException ex) { Console.Error.WriteLine($"Private Flow cannot read its settings: {ex.Message}"); Shutdown(3); }
            return;
        }
        if (e.Args is ["--speech-worker", var pipe, var engine, var model, var memory])
        {
            _ = SpeechWorker.RunChildAsync(pipe, engine, model, memory);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-overlay" && Settings.IsolatedRoot)
        {
            _ = OverlayVerification.RunAsync(e.Args[1]);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--export-icon")
        {
            // Regenerates assets/PrivateFlow.ico from the vector mark in BrandArt.
            System.IO.File.WriteAllBytes(e.Args[1], BrandArt.IconFile());
            Shutdown(0);
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--verify-insert" && Settings.IsolatedRoot) { _ = InsertionVerification.RunAsync(e.Args[1]); return; }
        if (e.Args.Length == 2 && e.Args[0] == "--insert-fixture" && Settings.IsolatedRoot) { InsertionVerification.ShowFixture(e.Args[1]); return; }
        if (e.Args is ["--transcribe-file", var input, var output] && Settings.IsolatedRoot)
        { _ = TranscribeFileAsync(input, output); return; }
        if (e.Args is ["--evaluate-meeting", var scenarios, var evaluationReport, ..] && e.Args.Length <= 4 && Settings.IsolatedRoot)
        { DiagnosticMode = true; _ = MeetingEvaluation.RunAsync(scenarios, evaluationReport, e.Args.Length == 4 ? e.Args[3] : null); return; }
        if (e.Args is ["--evaluate-dictation", var suite, var dictationReport] && Settings.IsolatedRoot)
        { DiagnosticMode = true; _ = DictationEvaluation.RunAsync(suite, dictationReport); return; }
        if (e.Args is ["--evaluate-recordings", var recordings, var recordingsReport] && Settings.IsolatedRoot)
        { DiagnosticMode = true; _ = MeetingEvaluation.RecordingsAsync(recordings, recordingsReport); return; }
        // The API style sends the meeting's transcript text, so it runs only when asked for by name.
        if (e.Args is ["--write-notes", var meeting, var notesOutput, .. var notesOptions] && notesOptions is [] or ["--api"] && Settings.IsolatedRoot)
        { DiagnosticMode = true; _ = WriteNotesAsync(meeting, notesOutput, notesOptions.Length == 1); return; }
        // A diagnostic or evaluation mode without an isolated data folder ends here instead of opening the app with the owner's settings.
        if (e.Args is ["--verify-workspace" or "--diagnostic-ui" or "--evaluate-meeting" or "--evaluate-dictation" or "--evaluate-recordings" or "--write-notes", ..] && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PRIVATE_FLOW_DATA_DIR"))
            || e.Args is ["--verify-overlay" or "--verify-insert" or "--insert-fixture", _])
        { Shutdown(2); return; }
        instance = new Mutex(true, "Local\\PrivateFlow-" + Environment.UserName, out var first);
        if (!first) { System.Windows.MessageBox.Show("Private Flow is already running. Open it from the system tray."); Shutdown(); return; }
        if (e.Args.Length == 3 && e.Args[0] == "--transcribe-file")
        {
            _ = TranscribeFileAsync(e.Args[1], e.Args[2]);
            return;
        }
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
        if (e.Args is ["--show"]) window.Show();
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
    // Evaluation mode: the notes of one meeting file in every style, one after the other for comparison. A style that cannot run here
    // shows its fallback reason.
    private async Task WriteNotesAsync(string meetingFile, string outputFile, bool withApi)
    {
        try
        {
            var document = System.Text.Json.JsonSerializer.Deserialize<PrivateFlow.Core.MeetingDocument>(await System.IO.File.ReadAllTextAsync(meetingFile))
                ?? throw new System.IO.InvalidDataException(meetingFile);
            var report = new System.Text.StringBuilder();
            foreach (var style in Enum.GetValues<PrivateFlow.Core.MeetingNotesStyle>().Where(style => withApi || style != PrivateFlow.Core.MeetingNotesStyle.Api))
            {
                var settings = Settings.Load(); settings.MeetingNotesStyle = style;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var notes = await MeetingNotesWriter.WriteAsync(document, settings, CancellationToken.None);
                var outcome = $"{timer.Elapsed.TotalSeconds:0.0} s, " + (notes.Written ? $"{notes.Dropped} dropped" : notes.Run is { Outcome: PrivateFlow.Core.NotesRunOutcome.NothingFound } ? "the model found nothing to note"
                    : notes.FallbackReason is { } reason ? "fell back: " + reason : "no model");
                report.Append($"=== {style} notes ({outcome}) ===\n").Append(notes.Notice is { } notice ? $"[Notice] {notice}\n" : "").Append(notes.Text).Append("\n\n");
            }
            await System.IO.File.WriteAllTextAsync(outputFile, report.ToString());
            Shutdown(0);
        }
        catch (Exception ex) { await System.IO.File.WriteAllTextAsync(outputFile, "ERROR: " + ex.Message); Shutdown(1); }
    }
    protected override void OnExit(ExitEventArgs e) { WhisperEngine.Shutdown(); LocalRewriter.Shutdown(); instance?.Dispose(); base.OnExit(e); }
}
