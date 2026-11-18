using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace PrivateFlow.Windows;

internal enum UnsavedChoice { KeepEditing, Save, DontSave }

// A question shown in the middle of the app's own window instead of a system message box: a prominent button on the right that has
// the focus, a quiet red text button on the far left, and Escape to leave the question unanswered.
internal static class ConfirmationDialog
{
    public const string AutomationName = "Confirmation";
    // The second confirmation before an action that cannot be undone. Cancel is the prominent button, so Enter and Escape both
    // cancel; the action itself needs a deliberate click.
    public static async Task<bool> AskAsync(DependencyObject source, string title, string message, string action) =>
        await ShowAsync(source, title, message, action, "Cancel", "DangerFilled", prominentCancels: true) == Answer.Quiet;
    // Before unsaved preferences are left: here saving is the likely wish, so Save is the prominent button that Enter presses.
    public static async Task<UnsavedChoice> AskToSaveAsync(DependencyObject source, string message) =>
        await ShowAsync(source, "Save your changes?", message, "Don't save", "Save", "Primary", prominentCancels: false) switch
        {
            Answer.Prominent => UnsavedChoice.Save,
            Answer.Quiet => UnsavedChoice.DontSave,
            _ => UnsavedChoice.KeepEditing
        };
    private enum Answer { Dismissed, Quiet, Prominent }
    private static Task<Answer> ShowAsync(DependencyObject source, string title, string message, string quietAction, string prominentAction, string prominentStyle, bool prominentCancels)
    {
        var window = source as Window ?? Window.GetWindow(source) ?? throw new InvalidOperationException("The question needs a window.");
        // One question at a time: a second one, such as the update prompt from the tray, is not asked on top of the first.
        if (WindowOverlay.IsShown(window, AutomationName)) return Task.FromResult(Answer.Dismissed);
        var quiet = new Button { Content = quietAction, Style = WorkspacePages.Theme("Ghost"), Foreground = WorkspacePages.Theme<Brush>("DangerText") };
        var prominent = new Button { Content = prominentAction, Style = WorkspacePages.Theme(prominentStyle), IsDefault = true, IsCancel = prominentCancels, MinWidth = 120 };
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(quiet, Dock.Left); DockPanel.SetDock(prominent, Dock.Right);
        buttons.Children.Add(quiet); buttons.Children.Add(prominent);
        var card = Card(title, message, new Border { Padding = new(24, 14, 24, 14), BorderBrush = WorkspacePages.Theme<Brush>("Line"), BorderThickness = new(0, 1, 0, 0), Child = buttons });
        var answer = new TaskCompletionSource<Answer>();
        var chosen = Answer.Dismissed;
        var overlay = new WindowOverlay(window, card, AutomationName);
        // Closing the window leaves the question unanswered.
        overlay.Closed += () => answer.TrySetResult(chosen);
        quiet.Click += (_, _) => { chosen = Answer.Quiet; overlay.Close(); };
        prominent.Click += (_, _) => { chosen = Answer.Prominent; overlay.Close(); };
        card.KeyDown += (_, e) => { if (e.Key != Key.Escape) return; e.Handled = true; overlay.Close(); };
        prominent.Focus();
        return answer.Task;
    }
    // A notice without buttons over the whole window, such as while an update downloads; the caller closes it. It has its own name, so
    // a question, such as the one Quit asks about unsaved preferences, can still be asked over it.
    public const string NoticeAutomationName = "Notice";
    public static WindowOverlay ShowNotice(Window window, string title, string message) => new(window, Card(title, message, null), NoticeAutomationName);
    private static Border Card(string title, string message, UIElement? footer)
    {
        var heading = new TextBlock { Text = title, Style = WorkspacePages.Theme("SectionHeading"), FontSize = 17, Margin = new(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = WorkspacePages.Theme<Brush>("Secondary"), LineHeight = 20 };
        var content = WorkspacePages.Stack(new Border { Padding = new(24, 22, 24, 22), Child = WorkspacePages.Stack(heading, text) });
        if (footer is not null) content.Children.Add(footer);
        return new Border
        {
            Width = 480, HorizontalAlignment = System.Windows.HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Background = WorkspacePages.Theme<Brush>("Surface"), BorderBrush = WorkspacePages.Theme<Brush>("Edge"), BorderThickness = new(1), CornerRadius = new(14),
            Child = content
        };
    }
}
