using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PrivateFlow.Core;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;

namespace PrivateFlow.Windows;

// The warnings, errors and notes of one page as cards above the content they concern. Piled, the newest card is in front with up to two
// more peeking behind it and "N more · show all" under them; that link or the front card spreads them out one under the other, and
// "Pile up" piles them again. The x of a card dismisses it: see NoticeList for when it shows again.
internal sealed class NoticeStack : StackPanel
{
    private const double PeekStep = 9, Gap = 8;
    private bool spread, refocus;
    private string[] shownKeys = [];
    // The list the stack shows; a page whose content changes, such as the selected meeting on History, gives it that content's list.
    public NoticeList Notices { get; private set; } = new();
    public NoticeStack(string name) : this() => AutomationProperties.SetName(this, name);
    // The automation name names the page, such as "Dictate notices".
    public NoticeStack()
    {
        Margin = new(0, 0, 0, 14);
        Follow(Notices);
        Redraw();
    }
    // A notice that is already there when another list is shown is not announced as new.
    public void Show(NoticeList list)
    {
        if (ReferenceEquals(list, Notices)) return;
        Notices.Changed -= Redraw; Notices.Appeared -= BringErrorIntoView;
        Notices = list; Follow(list);
        shownKeys = [.. list.Visible.Select(notice => notice.Key)];
        spread = false;
        Redraw();
    }
    private void Follow(NoticeList list) { list.Changed += Redraw; list.Appeared += BringErrorIntoView; }
    // An error comes into view so it is not missed below the fold, whoever raised it.
    private void BringErrorIntoView(Notice notice) { if (notice.Kind == NoticeKind.Error && IsVisible) BringIntoView(); }
    // For the verification: the cards as shown, front first, and whether they are spread out.
    internal bool Spread => spread;
    internal IReadOnlyList<Notice> Shown => Notices.Visible;
    // The "N more · show all" or "Pile up" link, null while one card or none is shown.
    internal Button? Toggle { get; private set; }
    internal IReadOnlyList<Button> DismissButtons { get; private set; } = [];
    internal IReadOnlyList<FrameworkElement> Cards { get; private set; } = [];

    public void Raise(Notice notice) => Notices.Raise(notice);
    public void Report(Notice notice) => Notices.Report(notice);
    public void Clear(string key) => Notices.Clear(key);
    internal void ToggleSpread() { spread = !spread; Redraw(); }

    private void Redraw()
    {
        var visible = Notices.Visible;
        if (visible.Count < 2) spread = false;
        Children.Clear();
        var dismissButtons = new List<Button>(); var cards = new List<FrameworkElement>();
        Visibility = visible.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (spread)
            for (var index = 0; index < visible.Count; index++)
            {
                var card = Card(visible[index], dismissButtons, false);
                if (index > 0) card.Margin = new(0, Gap, 0, 0);
                Children.Add(card); cards.Add(card);
            }
        else if (visible.Count > 0)
        {
            // One grid cell: the cards behind take the front card's height, are inset by their depth and peek out under it.
            var peeking = Math.Min(visible.Count - 1, 2);
            var pile = new Grid();
            for (var depth = peeking; depth >= 1; depth--)
                pile.Children.Add(new Border
                {
                    Background = WorkspacePages.Theme<Brush>("NoticeBehind"), BorderBrush = WorkspacePages.Theme<Brush>("EdgeHover"), BorderThickness = new(1),
                    CornerRadius = new(11), Margin = new(depth * 10, depth * PeekStep, depth * 10, (peeking - depth) * PeekStep)
                });
            var front = Card(visible[0], dismissButtons, visible.Count > 1);
            front.Margin = new(0, 0, 0, peeking * PeekStep);
            pile.Children.Add(front); cards.Add(front);
            Children.Add(pile);
        }
        Toggle = null;
        if (visible.Count > 1)
        {
            Toggle = new Button { Style = WorkspacePages.Theme("NoticeToggle"), Content = spread ? "Pile up" : $"{visible.Count - 1} more · show all" };
            AutomationProperties.SetName(Toggle, (string)Toggle.Content);
            Toggle.Click += (_, _) => ToggleSpread();
            Children.Add(Toggle);
        }
        DismissButtons = dismissButtons; Cards = cards;
        Announce(visible);
        if (refocus) { refocus = false; if (cards.FirstOrDefault() is { } first) first.Focus(); }
    }
    // A new error or warning is read out by a screen reader; notes are not.
    private void Announce(IReadOnlyList<Notice> visible)
    {
        var earlier = shownKeys;
        shownKeys = visible.Select(notice => notice.Key).ToArray();
        foreach (var (notice, card) in visible.Zip(Cards))
            if (notice.Kind != NoticeKind.Note && !earlier.Contains(notice.Key))
                UIElementAutomationPeer.CreatePeerForElement(card)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
    private FrameworkElement Card(Notice notice, List<Button> dismissButtons, bool opensPile)
    {
        var (label, edge) = notice.Kind switch
        {
            NoticeKind.Error => ("Error", WorkspacePages.Theme<Brush>("ErrorText")),
            NoticeKind.Note => ("Note", WorkspacePages.Theme<Brush>("NoteText")),
            _ => ("Warning", WorkspacePages.Theme<Brush>("WarningText"))
        };
        var text = new TextBlock { Style = WorkspacePages.Theme("NoticeText") };
        text.Inlines.Add(new Run(notice.Text));
        if (notice.Action is { } action)
        {
            var link = new Hyperlink(new Run(action.Label)) { Foreground = WorkspacePages.Theme<Brush>("NoteText"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand };
            link.Click += (_, _) => action.Invoke();
            AutomationProperties.SetName(link, action.Label);
            text.Inlines.Add(new Run(" ")); text.Inlines.Add(link);
        }
        var content = WorkspacePages.Stack(
            new TextBlock { Text = label.ToUpperInvariant(), Style = WorkspacePages.Theme("NoticeKind"), Foreground = edge },
            new TextBlock { Text = notice.Title, Style = WorkspacePages.Theme("RowTitle"), Margin = new(0, 0, 0, 2) },
            text);
        var grid = new Grid();
        grid.Children.Add(content);
        if (notice.Dismissable)
        {
            var dismiss = new Button
            {
                Style = WorkspacePages.Theme("NoticeDismiss"), ToolTip = "Dismiss",
                Content = new System.Windows.Shapes.Path { Style = WorkspacePages.Theme("Icon"), Data = WorkspacePages.Theme<Geometry>("IconClose")}
            };
            AutomationProperties.SetName(dismiss, "Dismiss: " + notice.Title);
            dismiss.Click += (_, _) => { refocus = dismiss.IsKeyboardFocusWithin; Notices.Dismiss(notice.Key); };
            dismissButtons.Add(dismiss); grid.Children.Add(dismiss);
        }
        var card = new NoticeCard
        {
            Style = WorkspacePages.Theme("NoticeCard"), Child = grid,
            BorderBrush = EdgeBrush(((SolidColorBrush)edge).Color), ToolTip = notice.Detail
        };
        AutomationProperties.SetName(card, $"{label}: {notice.Title}. {notice.Text}");
        if (notice.Detail is not null) AutomationProperties.SetHelpText(card, notice.Detail);
        AutomationProperties.SetLiveSetting(card, notice.Kind == NoticeKind.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        if (opensPile)
        {
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not DependencyObject source || !InsideButtonOrLink(source, card)) ToggleSpread(); };
            card.KeyDown += (_, e) => { if (e.OriginalSource == card && e.Key is Key.Enter or Key.Space) { ToggleSpread(); e.Handled = true; } };
        }
        return card;
    }
    private static bool InsideButtonOrLink(DependencyObject source, DependencyObject card)
    {
        for (var element = source; element is not null && element != card; element = element is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
            if (element is Button or Hyperlink) return true;
        return false;
    }
    // The coloured left edge of a card: the kind's colour over its first 3 px, the card's border colour on the other sides.
    private static LinearGradientBrush EdgeBrush(System.Windows.Media.Color kind)
    {
        var line = ((SolidColorBrush)WorkspacePages.Theme<Brush>("Edge")).Color;
        return new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(3, 0), GradientStops = { new(kind, 0), new(kind, .999), new(line, 1) } };
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this, "NoticeStack");
    // A card reads as one group named by its kind, title and text; Tab reaches it, then its link and its x.
    private sealed class NoticeCard : Border
    {
        public NoticeCard() { Focusable = true; }
        protected override AutomationPeer OnCreateAutomationPeer() => new GroupPeer(this, "Notice");
    }
    private sealed class GroupPeer(FrameworkElement owner, string className) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override string GetClassNameCore() => className;
        protected override bool IsKeyboardFocusableCore() => Owner.Focusable;
    }
}
