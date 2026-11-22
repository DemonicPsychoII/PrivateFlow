using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using PrivateFlow.Core;
using Clipboard = System.Windows.Clipboard;
using Control = System.Windows.Controls.Control;
using RichTextBox = System.Windows.Controls.RichTextBox;
using PrivateFlow.Windows;

namespace PrivateFlow.Verification;

// Explicit diagnostic: pastes only into a disposable fixture in a child process (Private Flow
// never pastes into itself). The fixture moves through a plain field, a rich document field
// and a password field; the parent must paste into the first two, refuse the third and
// restore the clipboard each time.
internal static class InsertionVerification
{
    private static readonly string[] Stages = ["edit", "document", "password"];

    public static async Task RunAsync(string output)
    {
        Process? fixture = null;
        var exit = 1;
        try
        {
            Directory.CreateDirectory(output);
            foreach (var file in Directory.EnumerateFiles(output, "stage-*")) File.Delete(file);
            fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--insert-fixture", output } })
                ?? throw new InvalidOperationException("Fixture did not start.");
            var report = new List<string> { ClipboardOwnership() };
            for (var i = 0; i < Stages.Length; i++)
            {
                await WaitFor(Path.Combine(output, $"stage-{i}.ready"));
                await Task.Delay(200);
                fixture.Refresh();
                Native.RestoreForeground(fixture.MainWindowHandle);
                await Task.Delay(150);
                var start = Native.CaptureTarget();
                if (start.Process != fixture.Id)
                {
                    var holder = Process.GetProcesses().FirstOrDefault(p => p.Id == start.Process)?.ProcessName ?? "unknown";
                    throw new InvalidOperationException($"Fixture lost focus to {holder} before the {Stages[i]} stage; nothing was pasted. Rerun without using the PC.");
                }
                var policy = new DictationPolicy();
                var session = policy.Begin(start);
                const string sentinel = "previous clipboard content";
                Native.SetClipboardText(sentinel);
                var text = $"Private Flow paste check {Stages[i]}";
                bool Valid(FocusTarget current) => policy.CanInsert(session, current, Native.ModifiersDown, text);
                var result = Valid(Native.CaptureTarget()) ? await Native.PasteAsync(text, Valid) : InsertionResult.Unavailable;
                await Task.Delay(300);
                var restored = Clipboard.ContainsText() && Clipboard.GetText() == sentinel;
                File.WriteAllText(Path.Combine(output, $"stage-{i}.done"), "");
                var received = await ReadResult(Path.Combine(output, $"stage-{i}.text"));
                var expectPaste = Stages[i] != "password";
                var pass = restored && (expectPaste
                    ? result == InsertionResult.Complete && received == text
                    : result == InsertionResult.Unavailable && !start.Safe && received.Length == 0);
                report.Add($"{(pass ? "PASS" : "FAIL")} {Stages[i]}: safe={start.Safe} result={result} received=\"{received}\" clipboardRestored={restored}");
            }
            File.WriteAllText(Path.Combine(output, "insert-result.txt"), string.Join(Environment.NewLine, report) + Environment.NewLine);
            exit = report.All(line => line.StartsWith("PASS")) ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(output, "insert-result.txt"), "FAIL " + ex.Message); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            if (fixture is { HasExited: false }) fixture.Kill();
            System.Windows.Application.Current.Shutdown(exit);
        }
    }

    // A copy made during the paste delay that happens to contain the same text must not be
    // overwritten by the restore (review finding on PR #2).
    private static string ClipboardOwnership()
    {
        const string text = "Private Flow ownership check";
        Native.SetClipboardText("previous clipboard content");
        var previous = Native.SnapshotClipboard();
        Native.SetClipboardText(text);
        var owned = Native.ClipboardSequence;
        var newer = new System.Windows.DataObject();
        newer.SetText(text);
        newer.SetData(System.Windows.DataFormats.Html, "<b>newer copy</b>");
        Clipboard.SetDataObject(newer, true);
        Native.RestoreClipboard(previous, owned);
        var kept = Clipboard.ContainsData(System.Windows.DataFormats.Html) && Clipboard.GetText() == text;
        return $"{(kept ? "PASS" : "FAIL")} ownership: newer copy with identical text kept={kept}";
    }

    public static void ShowFixture(string output)
    {
        var edit = new System.Windows.Controls.TextBox { Margin = new Thickness(10) };
        var document = new RichTextBox { Margin = new Thickness(10), Height = 60 };
        var password = new PasswordBox { Margin = new Thickness(10) };
        var panel = new StackPanel();
        panel.Children.Add(edit); panel.Children.Add(document); panel.Children.Add(password);
        var window = new Window { Title = "Private Flow isolated paste fixture", Width = 420, Height = 260, Content = panel, Topmost = true };
        string Read(int stage) => stage switch
        {
            0 => edit.Text,
            1 => new TextRange(document.Document.ContentStart, document.Document.ContentEnd).Text.Trim(),
            _ => password.Password
        };
        window.Loaded += async (_, _) =>
        {
            window.Activate();
            Control[] fields = [edit, document, password];
            for (var stage = 0; stage < fields.Length; stage++)
            {
                window.Activate(); fields[stage].Focus(); Keyboard.Focus(fields[stage]);
                File.WriteAllText(Path.Combine(output, $"stage-{stage}.ready"), "");
                await WaitFor(Path.Combine(output, $"stage-{stage}.done"));
                File.WriteAllText(Path.Combine(output, $"stage-{stage}.text"), Read(stage));
            }
            System.Windows.Application.Current.Shutdown(0);
        };
        window.Show();
    }

    private static async Task WaitFor(string file)
    {
        for (var i = 0; i < 100 && !File.Exists(file); i++) await Task.Delay(50);
        if (!File.Exists(file)) throw new TimeoutException("Timed out waiting for " + Path.GetFileName(file));
    }
    private static async Task<string> ReadResult(string file)
    {
        await WaitFor(file);
        for (var i = 0; i < 20; i++)
            try { return File.ReadAllText(file); } catch (IOException) { await Task.Delay(50); }
        return File.ReadAllText(file);
    }
}
