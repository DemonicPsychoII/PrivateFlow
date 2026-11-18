using System.Windows;
using System.Windows.Controls;

namespace PrivateFlow.Windows;

// A setup's progress as the finishing of a meeting shows its own: the step with its share, and the same bar under it.
internal sealed class SetupProgressView : StackPanel
{
    private readonly TextBlock step = WorkspacePages.Description("");
    private readonly System.Windows.Controls.ProgressBar bar = MeetingsPanel.ProgressBar();
    public SetupProgressView()
    {
        step.Margin = new(0); bar.Margin = new(0, 6, 0, 0);
        Children.Add(step); Children.Add(bar);
        Visibility = Visibility.Collapsed;
        System.Windows.Automation.AutomationProperties.SetName(bar, "Setup progress");
    }
    public string Text => step.Text;
    public double Share => bar.Value;
    public bool Shown => Visibility == Visibility.Visible;
    public void Show(string text, double share)
    {
        step.Text = text; bar.Value = share; Visibility = Visibility.Visible;
        System.Windows.Automation.AutomationProperties.SetHelpText(bar, text);
    }
    public void Hide() => Visibility = Visibility.Collapsed;
}
