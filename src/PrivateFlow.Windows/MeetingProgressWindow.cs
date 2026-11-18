using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;
using Brush = System.Windows.Media.Brush;

namespace PrivateFlow.Windows;

// Shows a meeting transcript being finished while the main window is hidden or minimized, so the work never looks like a hang.
// It has a taskbar button with the progress and does not take the focus.
internal sealed class MeetingProgressWindow : Window
{
    private readonly TextBlock step = new() { Style = WorkspacePages.Theme("RowTitle") };
    private readonly System.Windows.Controls.ProgressBar bar = MeetingsPanel.ProgressBar();
    public MeetingProgressWindow(Action open)
    {
        Title = "Private Flow — finishing a meeting"; Width = 400; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        ShowActivated = false; ShowInTaskbar = true; Background = WorkspacePages.Theme<Brush>("Surface");
        TaskbarItemInfo = new TaskbarItemInfo { ProgressState = TaskbarItemProgressState.Normal };
        var heading = new TextBlock { Text = "Finishing the meeting transcript", Style = WorkspacePages.Theme("SectionHeading"), Margin = new(0, 0, 0, 8) };
        var note = WorkspacePages.Description("Private Flow keeps working in the background; this window closes when the transcript is saved.");
        var openButton = WorkspacePages.Button("Open Private Flow", open); openButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left; openButton.Margin = new(0, 12, 0, 0);
        bar.Margin = new(0, 10, 0, 6);
        Content = new Border { Padding = new(20, 16, 20, 18), Child = WorkspacePages.Stack(heading, step, bar, note, openButton) };
        SourceInitialized += (_, _) => MainWindow.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => { Left = SystemParameters.WorkArea.Right - ActualWidth - 16; Top = SystemParameters.WorkArea.Bottom - ActualHeight - 16; };
    }
    public string Step => step.Text;
    public void Report(string text, double share)
    {
        step.Text = text; bar.Value = share;
        TaskbarItemInfo.ProgressValue = share; Title = $"{share:P0} · Private Flow — finishing a meeting";
    }
}
