using System.IO;
using System.Reflection;
using System.Windows;
using Application = System.Windows.Application;
using PrivateFlow.Core;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// The verification host. Every mode but the speech worker runs only with an isolated PRIVATE_FLOW_DATA_DIR outside the app's own data
// folder and otherwise ends with exit code 2, so none of it touches the owner's settings, meetings or devices. A PrivateFlow.dll of
// another build than the one this host was built with ends every mode with exit code 4, so a check never reports on other code.
internal static class Program
{
    internal const int OtherAppBuild = 4;
    internal static readonly string[] InputModes = ["--verify-insert", "--insert-fixture"];

    // A build's identity: the version and the commit it was built from, as its informational version carries them.
    internal static string Identity(Assembly assembly) => assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (Identity(typeof(Program).Assembly) is var built && Identity(typeof(App).Assembly) is var loaded && built != loaded)
        {
            Console.Error.WriteLine($"This verification host was built with Private Flow {built} but loaded PrivateFlow.dll {loaded}; nothing runs (exit code {OtherAppBuild}). Build both again with scripts/build.ps1 -Portable.");
            return OtherAppBuild;
        }
        // As in the app: an English interface whose numbers and dates do not follow the region format of Windows.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var host = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        host.Resources = new ResourceDictionary { Source = new Uri("/PrivateFlow;component/Theme.xaml", UriKind.Relative) };
        host.Startup += (_, _) => { if (!Start(host, arguments)) host.Shutdown(2); };
        host.Exit += (_, _) => { WhisperEngine.Shutdown(); LocalRewriter.Shutdown(); };
        return host.Run();
    }

    private static bool Start(Application host, string[] arguments)
    {
        // The speech engine starts this program again as its worker, as the app starts itself.
        if (arguments is ["--speech-worker", var pipe, var engine, var model, var memory]) { _ = SpeechWorker.RunChildAsync(pipe, engine, model, memory, WorkerFaults.TryActAsync); return true; }
        if (!Settings.IsolatedRoot) return false;
        // The modes that send simulated keys or write the system clipboard, the paste check and its fixture, run only in the disposable
        // test VM; elsewhere they end with exit code 2 and say why.
        if (arguments is [var mode, ..] && InputModes.Contains(mode) && !DisposableTestVm.IsThisComputer) { Console.Error.WriteLine($"{mode}: {DisposableTestVm.Refusal}"); return false; }
        switch (arguments)
        {
            case ["--verify-workspace", var report]:
                App.DiagnosticMode = true; _ = WorkspaceVerification.RunAsync(report); return true;
            case ["--diagnostic-ui"]:
                App.DiagnosticMode = true;
                try { var diagnostic = new MainWindow(); host.MainWindow = diagnostic; diagnostic.Show(); }
                catch (SettingsUnavailableException ex) { Console.Error.WriteLine($"Private Flow cannot read its settings: {ex.Message}"); host.Shutdown(3); }
                return true;
            case ["--verify-overlay", var overlayOutput]:
                _ = OverlayVerification.RunAsync(overlayOutput); return true;
            case ["--verify-insert", var insertOutput]:
                _ = InsertionVerification.RunAsync(insertOutput); return true;
            case ["--insert-fixture", var fixtureOutput]:
                InsertionVerification.ShowFixture(fixtureOutput); return true;
            case ["--evaluate-meeting", var scenarios, var evaluationReport, .. var options] when options.Length <= 1:
                App.DiagnosticMode = true; _ = MeetingEvaluation.RunAsync(scenarios, evaluationReport, options is [var optionsFile] ? optionsFile : null); return true;
            case ["--evaluate-dictation", var suite, var dictationReport]:
                App.DiagnosticMode = true; _ = DictationEvaluation.RunAsync(suite, dictationReport); return true;
            case ["--build-audio-library", var librarySuite, var libraryReport]:
                App.DiagnosticMode = true; _ = AudioLibraryBuilder.RunAsync(librarySuite, libraryReport); return true;
            case ["--verify-imports", var importSpeech, var importReport]:
                App.DiagnosticMode = true; _ = ImportVerification.RunAsync(importSpeech, importReport); return true;
            case ["--evaluate-recordings", var recordings, var recordingsReport]:
                App.DiagnosticMode = true; _ = MeetingEvaluation.RecordingsAsync(recordings, recordingsReport); return true;
            // The API style sends the meeting's transcript text, so it runs only when asked for by name.
            case ["--write-notes", var meeting, var notesOutput, .. var notesOptions] when notesOptions is [] or ["--api"]:
                App.DiagnosticMode = true; _ = WriteNotesAsync(host, meeting, notesOutput, notesOptions.Length == 1); return true;
            case ["--verify-worker-faults", var faultsReport]:
                App.DiagnosticMode = true; _ = WorkerFaults.RunAsync(faultsReport); return true;
            case ["--check-package", var package, var checkReport]:
                _ = PackageCheck.RunAsync(host, package, checkReport); return true;
            default:
                return false;
        }
    }

    // The notes of one meeting file in every style, one after the other for comparison. A style that cannot run here shows its fallback
    // reason.
    private static async Task WriteNotesAsync(Application host, string meetingFile, string outputFile, bool withApi)
    {
        try
        {
            var document = System.Text.Json.JsonSerializer.Deserialize<MeetingDocument>(await File.ReadAllTextAsync(meetingFile))
                ?? throw new InvalidDataException(meetingFile);
            var report = new System.Text.StringBuilder();
            foreach (var style in Enum.GetValues<MeetingNotesStyle>().Where(style => withApi || style != MeetingNotesStyle.Api))
            {
                var settings = Settings.Load(); settings.MeetingNotesStyle = style;
                var timer = System.Diagnostics.Stopwatch.StartNew();
                var notes = await MeetingNotesWriter.WriteAsync(document, settings, CancellationToken.None);
                var outcome = $"{timer.Elapsed.TotalSeconds:0.0} s, " + (notes.Written ? $"{notes.Dropped} dropped" : notes.Run is { Outcome: NotesRunOutcome.NothingFound } ? "the model found nothing to note"
                    : notes.FallbackReason is { } reason ? "fell back: " + reason : "no model");
                report.Append($"=== {style} notes ({outcome}) ===\n").Append(notes.Notice is { } notice ? $"[Notice] {notice}\n" : "").Append(notes.Text).Append("\n\n");
            }
            await File.WriteAllTextAsync(outputFile, report.ToString());
            host.Shutdown(0);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(outputFile, "ERROR: " + ex.Message); host.Shutdown(1); }
    }
}
