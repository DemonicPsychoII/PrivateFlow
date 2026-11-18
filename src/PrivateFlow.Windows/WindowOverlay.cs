using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Effects;

namespace PrivateFlow.Windows;

// Content shown over the app's own window instead of in a separate one. Everything behind it is blurred, dimmed and disabled
// while it is open, so neither clicks nor keys reach it; an overlay opened on top of another restores exactly what it covered.
// Closing one first closes those opened above it, newest first: each restores the state it found, which only holds in that order.
internal sealed class WindowOverlay
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, List<WindowOverlay>> Open = [];
    private readonly Window window;
    private readonly Grid host;
    private readonly Border overlay;
    private readonly (UIElement Element, Effect? Effect, bool IsEnabled)[] behind;
    private readonly IInputElement? previousFocus;
    public event Action? Closed;
    public bool IsOpen { get; private set; } = true;
    public WindowOverlay(DependencyObject source, UIElement content, string automationName)
    {
        window = source as Window ?? Window.GetWindow(source) ?? throw new InvalidOperationException("The overlay needs a window.");
        host = Host(window);
        previousFocus = Keyboard.FocusedElement;
        overlay = new Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0)), Child = content, Focusable = false };
        AutomationProperties.SetName(overlay, automationName);
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        Grid.SetRowSpan(overlay, Math.Max(1, host.RowDefinitions.Count)); Grid.SetColumnSpan(overlay, Math.Max(1, host.ColumnDefinitions.Count));
        System.Windows.Controls.Panel.SetZIndex(overlay, 1000);
        behind = host.Children.OfType<UIElement>().Select(element => (element, (Effect?)element.Effect, element.IsEnabled)).ToArray();
        foreach (var (element, _, _) in behind) { element.Effect = new BlurEffect { Radius = 8 }; element.IsEnabled = false; }
        window.Closed += WindowClosed;
        host.Children.Add(overlay);
        Open.GetOrCreateValue(host).Add(this);
    }
    public static bool IsShown(Window window, string automationName) =>
        window.Content is Grid grid && grid.Children.OfType<Border>().Any(border => AutomationProperties.GetName(border) == automationName);
    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        var stack = Open.GetOrCreateValue(host);
        while (stack.Count > 0 && stack[^1] != this) stack[^1].Close();
        stack.Remove(this);
        foreach (var (element, effect, enabled) in behind) { element.Effect = effect; element.IsEnabled = enabled; }
        // Detached, the same content can be shown again in a new overlay.
        overlay.Child = null;
        host.Children.Remove(overlay); window.Closed -= WindowClosed;
        (previousFocus as UIElement)?.Focus();
        Closed?.Invoke();
    }
    private void WindowClosed(object? sender, EventArgs e) => Close();
    // The grid that fills the window; a window with other content gets one around it.
    private static Grid Host(Window window)
    {
        if (window.Content is Grid grid) return grid;
        var content = window.Content as UIElement; window.Content = null;
        var wrapper = new Grid();
        if (content is not null) wrapper.Children.Add(content);
        window.Content = wrapper;
        return wrapper;
    }
}
