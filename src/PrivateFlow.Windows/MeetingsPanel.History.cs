using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;
using PrivateFlow.Core;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using DataGrid = System.Windows.Controls.DataGrid;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using RadioButton = System.Windows.Controls.RadioButton;
using MenuItem = System.Windows.Controls.MenuItem;
using Binding = System.Windows.Data.Binding;
using Brush = System.Windows.Media.Brush;

namespace PrivateFlow.Windows;

// The History page: past meetings on the left, the selected one on the right with its transcript and notes, one search over all.
internal sealed partial class MeetingsPanel
{
    // The meetings as last read from disk; the selected one is the object the page edits, so unsaved edits survive a refresh.
    private List<MeetingDocument> documents = [];
    private readonly ListBox list = new() { Margin = new(8, 4, 8, 8) };
    private readonly TextBlock emptyArchive = new() { Style = WorkspacePages.Theme("RowDescription"), Margin = new(18, 12, 18, 0) };
    private readonly TextBox search = new() { Style = WorkspacePages.Theme("PageSearchBox"), Width = 280, Tag = "Search all meetings" };
    private readonly CheckBox searchSelectedOnly = new() { Content = "Only this meeting", Foreground = WorkspacePages.Theme<Brush>("Secondary"), Margin = new(0, 0, 14, 0), Visibility = Visibility.Collapsed };
    private readonly RadioButton transcriptTab = Tab("Transcript"), notesTab = Tab("Notes");
    private readonly TextBlock selectedTitle = new() { FontSize = 17, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock selectedDetails = new() { Style = WorkspacePages.Theme("RowDescription") };
    // The History page's own notices, such as a failed Delete expired meetings, above the meeting shown or the hint to choose one. Notices
    // about one meeting belong to it: the selected meeting's under its heading on both tabs, those about its notes on the Notes tab. They
    // stay with their meeting while another is selected and show again when it is.
    private readonly NoticeStack historyNotices = new("History notices"), selectedNotices = new("Meeting notices") { Margin = new(0, 12, 0, 0) },
        notesNotices = new("Notes notices");
    private readonly Dictionary<string, (NoticeList Meeting, NoticeList Notes)> meetingLists = [];
    private static readonly NoticeList NoMeeting = new();
    private const string UnfinishedKey = "history-unfinished", UnsavedEditsKey = "history-unsaved-edits", EditsKeptKey = "history-edits-kept",
        NotesBeingWrittenKey = "notes-being-written", RowGoneKey = "notes-row-gone";
    private readonly TextBlock noSelection = new() { Style = WorkspacePages.Theme("HintNote"), Text = "Choose a meeting on the left to read and edit its transcript and notes." };
    private readonly DataGrid segments;
    private readonly HashSet<MeetingSegment> hitRows = [];
    private readonly Button saveEdits, export, more, deleteExpired, writeNotes, ruleNotes, cancelNotes;
    private readonly MenuItem retranscribe, deleteMeeting;
    private FrameworkElement selectedPane = null!, transcriptView = null!, notesView = null!;
    private readonly TextBlock notesWrittenBy = new() { Style = WorkspacePages.Theme("RowDescription"), Margin = new(0) };
    private readonly TextBlock notesDestination = new() { Style = WorkspacePages.Theme("RowDescription") }, notesProgress = new() { Style = WorkspacePages.Theme("RowDescription"), Margin = new(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
    private FrameworkElement writeNotesRow = null!, notesProgressRow = null!, writtenByRow = null!;
    // Read-only, with its citations as links to the transcript rows; the text stays selectable for copying.
    private readonly System.Windows.Controls.RichTextBox notesText = new()
    {
        IsReadOnly = true, IsDocumentEnabled = true, Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new(0), Padding = new(12, 14, 12, 14),
        Foreground = WorkspacePages.Theme<Brush>("Ink"), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    // The notes text as the Notes tab shows it, for the verification.
    private string notesShown = "";
    private readonly DispatcherTimer notesTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    // Stands in for the notes model in the verification, also in the window, so it never reaches a real model.
    internal Func<string, string, CancellationToken, Task<string>>? CompleteNotes { get; set; }
    private readonly Action openNotesSettings;
    private CancellationTokenSource? notesCancellation;
    private MeetingDocument? notesMeeting;
    private DateTime notesStarted;
    // How the last press of Write notes for each meeting ended when it stored no notes; it lasts until the next press for that meeting or
    // until its card is dismissed.
    private readonly Dictionary<string, NotesRun> notesRuns = [];
    private bool listing;
    // The search box and the toggle that limits it to the selected meeting, shown in the page header.
    public FrameworkElement HistorySearch { get; }
    internal int SearchHitCount(string meetingId) => hits.TryGetValue(meetingId, out var rows) ? rows.Count : 0;
    private IReadOnlyDictionary<string, IReadOnlyList<MeetingSegment>> hits = new Dictionary<string, IReadOnlyList<MeetingSegment>>();
    private bool Searching => search.Text.Trim().Length > 0;

    private static RadioButton Tab(string name) => new() { Style = WorkspacePages.Theme("Tab"), Content = name, GroupName = "HistoryTab" };
    private static void Show(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private FrameworkElement BuildHistory(out FrameworkElement searchBar)
    {
        AutomationProperties.SetName(search, "Search meetings");
        AutomationProperties.SetName(searchSelectedOnly, "Search only the selected meeting");
        AutomationProperties.SetName(list, "Past meetings");
        AutomationProperties.SetName(notesText, "Meeting notes");
        search.TextChanged += (_, _) =>
        {
            // Hidden, the toggle would limit the next search without showing it.
            if (!Searching) searchSelectedOnly.IsChecked = false;
            Show(searchSelectedOnly, Searching); Search();
        };
        searchSelectedOnly.Checked += (_, _) => Search(); searchSelectedOnly.Unchecked += (_, _) => Search();
        searchBar = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Children = { searchSelectedOnly, search } };
        list.SelectionChanged += (_, _) => { if (!listing && list.SelectedItem is ListBoxItem { Tag: MeetingDocument document }) _ = OpenAsync(document); };

        var left = new DockPanel();
        var retention = new Border { BorderBrush = WorkspacePages.Theme<Brush>("Line"), BorderThickness = new(0, 1, 0, 0), Padding = new(18, 14, 18, 16) };
        retention.Child = WorkspacePages.Stack(
            new TextBlock { Style = WorkspacePages.Theme("RowDescription"), Margin = new(0, 0, 0, 10), Text = "Completed meetings older than the days set under Settings → Meeting defaults can be deleted here. Unfinished meetings are kept until you finish them or delete their audio." },
            deleteExpired);
        deleteExpired.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        DockPanel.SetDock(retention, Dock.Bottom); left.Children.Add(retention);
        DockPanel.SetDock(emptyArchive, Dock.Top); left.Children.Add(emptyArchive);
        left.Children.Add(list);
        var leftPane = new Border { BorderBrush = WorkspacePages.Theme<Brush>("Line"), BorderThickness = new(0, 0, 1, 0), Background = WorkspacePages.Theme<Brush>("Surface"), Child = left };

        var tabs = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        tabs.Children.Add(transcriptTab); tabs.Children.Add(notesTab);
        transcriptTab.IsChecked = true;
        transcriptTab.Checked += (_, _) => ShowTab(); notesTab.Checked += (_, _) => ShowTab();
        var actions = WorkspacePages.Actions(export, more);
        var toolbar = new DockPanel { Margin = new(0, 16, 0, 12) };
        DockPanel.SetDock(actions, Dock.Right); toolbar.Children.Add(actions); toolbar.Children.Add(tabs);
        var heading = WorkspacePages.Stack(selectedTitle, selectedDetails, selectedNotices);

        var editBar = new DockPanel { Margin = new(0, 12, 0, 0) };
        DockPanel.SetDock(saveEdits, Dock.Right); editBar.Children.Add(saveEdits);
        editBar.Children.Add(new TextBlock { Style = WorkspacePages.Theme("RowDescription"), VerticalAlignment = VerticalAlignment.Center, Margin = new(2, 0, 12, 0),
            Text = "Double-click a speaker or a line to edit it. Remote speakers start with one shared label; separate them here when needed." });
        var transcriptDock = new DockPanel();
        DockPanel.SetDock(editBar, Dock.Bottom); transcriptDock.Children.Add(editBar);
        transcriptDock.Children.Add(new Border { Style = WorkspacePages.Theme("Card"), Child = segments });
        transcriptView = transcriptDock;

        var progress = new DockPanel();
        DockPanel.SetDock(cancelNotes, Dock.Right); progress.Children.Add(cancelNotes); progress.Children.Add(notesProgress);
        notesProgressRow = new Border { Style = WorkspacePages.Theme("Card"), Padding = new(16, 10, 10, 10), Margin = new(0, 0, 0, 12), Child = progress, Visibility = Visibility.Collapsed };
        notesDestination.Margin = new(0, 6, 0, 0);
        // While the model installs, its progress stands next to Cancel installing in place of the description.
        var installLine = new DockPanel();
        DockPanel.SetDock(installOnNotes.Button, Dock.Left); installOnNotes.Button.VerticalAlignment = VerticalAlignment.Center;
        installOnNotes.Progress.Margin = new(16, 0, 0, 0); installOnNotes.Progress.VerticalAlignment = VerticalAlignment.Center;
        installLine.Children.Add(installOnNotes.Button); installLine.Children.Add(installOnNotes.Progress);
        writeNotesRow = new StackPanel { Margin = new(0, 0, 0, 14), Children = { writeNotes, installLine, notesDestination } };
        writeNotes.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
        var writtenBy = new DockPanel { Margin = new(0, 0, 0, 10) };
        DockPanel.SetDock(ruleNotes, Dock.Right); writtenBy.Children.Add(ruleNotes); writtenBy.Children.Add(notesWrittenBy);
        notesWrittenBy.VerticalAlignment = VerticalAlignment.Center;
        writtenByRow = writtenBy;
        notesView = new ScrollViewer
        {
            Style = WorkspacePages.Theme("PageScroll"), Visibility = Visibility.Collapsed,
            Content = WorkspacePages.Stack(notesNotices, writtenByRow, writeNotesRow, notesProgressRow, new Border { Style = WorkspacePages.Theme("Card"), Child = notesText })
        };

        var body = new Grid();
        body.Children.Add(transcriptView); body.Children.Add(notesView);
        var right = new DockPanel();
        DockPanel.SetDock(heading, Dock.Top); DockPanel.SetDock(toolbar, Dock.Top);
        right.Children.Add(heading); right.Children.Add(toolbar); right.Children.Add(body);
        selectedPane = right; selectedPane.Visibility = Visibility.Collapsed;
        var meetingArea = new Grid();
        meetingArea.Children.Add(noSelection); meetingArea.Children.Add(selectedPane);
        var rightPane = new DockPanel { Margin = new(28, 20, 28, 20) };
        DockPanel.SetDock(historyNotices, Dock.Top); rightPane.Children.Add(historyNotices); rightPane.Children.Add(meetingArea);
        noSelection.VerticalAlignment = VerticalAlignment.Center;

        var page = new Grid();
        page.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
        page.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(rightPane, 1);
        page.Children.Add(leftPane); page.Children.Add(rightPane);
        notesTimer.Tick += (_, _) => { UpdateNotesProgress(); if (installCancellation is not null) UpdateInstall(); };
        settings.Saved += () => { if (selected is not null) UpdateNotes(); };
        return page;
    }
    private DataGrid TranscriptGrid()
    {
        var grid = WorkspacePages.Grid(new ObservableCollection<MeetingSegment>(), double.NaN);
        grid.CanUserAddRows = false; grid.CanUserDeleteRows = false;
        grid.Columns.Add(new DataGridTextColumn { Header = "Time", Binding = new Binding("Start") { Converter = new ClockTime() }, IsReadOnly = true, Width = 76 });
        grid.Columns.Add(new DataGridTextColumn { Header = "Speaker", Binding = new Binding("Speaker"), Width = 140, ElementStyle = SpeakerStyle() });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Transcript", Binding = new Binding("Text"), Width = new(1, DataGridLengthUnitType.Star),
            ElementStyle = new Style(typeof(TextBlock)) { Setters = { new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } }
        });
        AutomationProperties.SetName(grid, "Meeting transcript rows");
        grid.CellEditEnding += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(UpdateSaveEdits));
        // Rows are recycled while scrolling, so each realized row takes its highlight from the current hits.
        grid.LoadingRow += (_, e) => Highlight(e.Row);
        return grid;
    }
    private static Style SpeakerStyle() => new(typeof(TextBlock))
    {
        Setters = { new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold), new Setter(TextBlock.ForegroundProperty, WorkspacePages.Theme<Brush>("Ink")) }
    };
    private void Highlight(DataGridRow row)
    {
        if (row.Item is MeetingSegment segment && hitRows.Contains(segment)) row.Background = WorkspacePages.Theme<Brush>("HitRow");
        else row.ClearValue(BackgroundProperty);
    }
    // Seconds into the meeting as 04:10, or 1:02:05 past the hour.
    private sealed class ClockTime : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not double seconds) return "";
            var time = TimeSpan.FromSeconds(Math.Floor(seconds));
            return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"mm\:ss");
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    internal void Refresh()
    {
        documents = archive.List().Select(document => document.Id == selected?.Id ? selected : document).ToList();
        if (selected is not null && !documents.Contains(selected)) ClearSelection();
        DropListsOfDeletedMeetings();
        var unfinished = archive.Unfinished(capture?.Document.Id);
        unfinishedSection.Visibility = unfinished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        unfinishedText.Text = unfinished.Count == 0 ? "" : UnfinishedSummary(unfinished) + "\n\n" + string.Join("\n", unfinished.Select(found => MeetingArchive.DescribeUnfinished(found.Document))) +
            "\n\nFinish transcripts runs the final pass over the kept audio and then deletes it, unless a meeting retains its audio. Delete their audio keeps the live preview as the transcript.";
        Search(false);
    }
    // Runs the search over the meetings in memory and lists those it finds; without a search every meeting is listed. A new search
    // opens the transcript at the first hit.
    private void Search(bool scroll = true)
    {
        hits = Searching ? MeetingHistory.HitsByMeeting(documents, search.Text, searchSelectedOnly.IsChecked == true ? selected?.Id ?? "" : null)
            : new Dictionary<string, IReadOnlyList<MeetingSegment>>();
        var shown = Searching ? documents.Where(document => hits.ContainsKey(document.Id)).ToList() : documents;
        listing = true;
        try
        {
            list.Items.Clear();
            foreach (var document in shown) list.Items.Add(Entry(document));
            list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag == selected);
        }
        finally { listing = false; }
        emptyArchive.Text = documents.Count == 0 ? "No meetings yet. Recorded meetings appear here."
            : Searching ? shown.Count == 0 ? "No meeting says that. Try another name, term or time." : shown.Count == 1 ? "1 meeting with hits" : $"{shown.Count} meetings with hits"
            : documents.Count == 1 ? "1 meeting" : $"{documents.Count} meetings";
        // A new search that leaves the shown meeting out opens the first meeting it finds, unless the shown one has unsaved edits. Listing
        // again after a save or a refresh keeps the shown meeting.
        if (scroll && Searching && list.SelectedItem is null && list.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.IsEnabled) is { Tag: MeetingDocument first }
            && !KeepsUnsavedEdits(first)) { Show(first); return; }
        ShowHits(scroll);
    }
    private ListBoxItem Entry(MeetingDocument document)
    {
        var recordingNow = document.Id == capture?.Document.Id;
        var reason = document.Status == "Complete" ? "" : MeetingArchive.UnfinishedReasonText(document);
        var hitCount = SearchHitCount(document.Id);
        var details = recordingNow ? "Recording now" : MeetingHistory.Describe(document, DateTimeOffset.Now) + (hitCount == 0 ? "" : hitCount == 1 ? " · 1 hit" : $" · {hitCount} hits");
        var titleLine = new WrapPanel();
        titleLine.Children.Add(new TextBlock { Text = document.Title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 190 });
        if (document.Status != "Complete" && !recordingNow)
            titleLine.Children.Add(new Border
            {
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5C, 0x4A, 0x12)), BorderThickness = new(1), CornerRadius = new(5),
                Padding = new(5, 0, 5, 1), Margin = new(6, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = "unfinished", FontSize = 11, Foreground = WorkspacePages.Theme<Brush>("WarningText") }
            });
        var content = WorkspacePages.Stack(titleLine, new TextBlock { Text = details, FontSize = 12, Foreground = WorkspacePages.Theme<Brush>("Muted"), Margin = new(0, 2, 0, 0) });
        if (reason.Length > 0 && !recordingNow) content.Children.Add(new TextBlock { Text = reason, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = WorkspacePages.Theme<Brush>("Faint"), Margin = new(0, 2, 0, 0) });
        var item = new ListBoxItem { Content = content, Tag = document, IsEnabled = !recordingNow, Opacity = recordingNow ? .5 : 1, Margin = new(0, 1, 0, 1) };
        AutomationProperties.SetName(item, string.Join(", ", new[] { document.Title, details, document.Status == "Complete" || recordingNow ? "" : "unfinished", reason }.Where(part => part.Length > 0)));
        return item;
    }
    private void Show(MeetingDocument document)
    {
        var lists = ListsOf(document);
        // A meeting opened again shows the cards dismissed while it was shown before, as long as their condition holds.
        if (selected?.Id != document.Id) { lists.Meeting.Reopen(); lists.Notes.Reopen(); }
        selectedNotices.Show(lists.Meeting); notesNotices.Show(lists.Notes);
        selected = document;
        Board.Refresh();
        var index = documents.FindIndex(found => found.Id == document.Id);
        if (index >= 0) documents[index] = document;
        // The list must hold the same object as the page: an older copy selected later would be saved over the newer meeting.
        if (list.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag is MeetingDocument listed && listed.Id == document.Id && !ReferenceEquals(listed, document)) is { } stale)
        {
            listing = true;
            try { list.Items[list.Items.IndexOf(stale)] = Entry(document); }
            finally { listing = false; }
        }
        segments.ItemsSource = new ObservableCollection<MeetingSegment>(document.Segments.OrderBy(segment => segment.Start));
        selectedTitle.Text = document.Title;
        var devices = new[] { document.MicrophoneDevice.Length > 0 ? $"the microphone “{document.MicrophoneDevice}”" : "", document.PlaybackDevice.Length > 0 ? $"the system audio of “{document.PlaybackDevice}”" : "" }.Where(part => part.Length > 0).ToArray();
        selectedDetails.Text = MeetingHistory.Describe(document, DateTimeOffset.Now) + (devices.Length == 0 ? "" : " · recorded " + string.Join(" and ", devices));
        if (document.Status == "Complete") lists.Meeting.Clear(UnfinishedKey);
        else lists.Meeting.Raise(new(UnfinishedKey, NoticeKind.Warning, "Unfinished meeting",
            ("The transcript is the live preview, which can miss parts. " + MeetingArchive.UnfinishedReasonText(document)).Trim(), Standing: true));
        Show(selectedPane, true); Show(noSelection, false);
        SelectListed(document);
        UpdateSaveEdits();
        if (searchSelectedOnly.IsChecked == true && Searching) { Search(); return; }
        ShowHits(true);
        UpdateNotes();
    }
    private void SelectListed(MeetingDocument document)
    {
        listing = true;
        try { list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Tag == document); }
        finally { listing = false; }
    }
    // A meeting chosen in the list. Leaving one with unsaved edits asks first; Cancel keeps it open with its edits, and discarding takes
    // its saved copy back into the list.
    private async Task OpenAsync(MeetingDocument document)
    {
        if (selected is not { } shown || document.Id == shown.Id || !HasUnsavedEdits()) { Show(document); return; }
        SelectListed(shown); UpdateSaveEdits();
        if (!await ConfirmationDialog.AskAsync(this, "Unsaved edits", $"“{shown.Title}” has edits you have not saved. Discard them and open “{document.Title}”?", "Discard edits")
            || !ReferenceEquals(selected, shown)) return;
        try { if (documents.IndexOf(shown) is var index and >= 0) documents[index] = archive.Load(shown.Id); historyNotices.Clear("history-not-read"); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        { historyNotices.Report(new("history-not-read", NoticeKind.Error, "The saved meeting could not be read", ex.Message)); return; }
        Show(documents.FirstOrDefault(found => found.Id == document.Id) ?? document);
        Search(false);
    }
    // True when the shown meeting has unsaved edits and so stays open instead of the meeting a search or a finished operation would show.
    private bool KeepsUnsavedEdits(MeetingDocument next)
    {
        if (selected is null || selected.Id == next.Id || !HasUnsavedEdits()) return false;
        ListsOf(selected).Meeting.Report(new(UnsavedEditsKey, NoticeKind.Warning, "Unsaved edits", $"“{selected.Title}” stays open. Save edits, then open “{next.Title}”."));
        UpdateSaveEdits();
        return true;
    }
    // A pass that did not replace the transcript: the fresh record from disk with the rows edited on the page, still unsaved.
    private void KeepEdits(MeetingDocument fresh, MeetingDocument edited)
    {
        fresh.Segments = edited.Segments;
        Show(fresh);
        ListsOf(fresh).Meeting.Report(new(EditsKeptKey, NoticeKind.Note, "The transcript was not replaced", "Your unsaved edits are still shown; Save edits keeps them."));
    }
    private void ClearSelection()
    {
        selected = null; segments.ItemsSource = new ObservableCollection<MeetingSegment>(); hitRows.Clear();
        selectedNotices.Show(NoMeeting); notesNotices.Show(NoMeeting); Board.Refresh();
        Show(selectedPane, false); Show(noSelection, true);
    }
    // Marks the search hits of the shown meeting and scrolls to the first with its neighbouring rows in view.
    private void ShowHits(bool scroll)
    {
        hitRows.Clear();
        if (selected is not null && hits.TryGetValue(selected.Id, out var rows)) hitRows.UnionWith(rows);
        foreach (var item in segments.Items)
            if (segments.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row) Highlight(row);
        if (!scroll || hitRows.Count == 0) return;
        var ordered = segments.Items.OfType<MeetingSegment>().ToList();
        var first = ordered.FindIndex(hitRows.Contains);
        transcriptTab.IsChecked = true;
        segments.UpdateLayout();
        segments.ScrollIntoView(ordered[Math.Min(first + 1, ordered.Count - 1)]);
        segments.ScrollIntoView(ordered[Math.Max(first - 1, 0)]);
    }
    internal IReadOnlyCollection<MeetingSegment> HighlightedRows => hitRows;
    internal MeetingDocument? SelectedMeeting => selected;
    // What the Notes tab shows, for the verification: null where a part is hidden. Notices are its cards, front first.
    internal (IReadOnlyList<Notice> Notices, string? WrittenBy, string? WriteButton, bool Running, string Text) NotesState =>
        (notesNotices.Shown, Shown(writtenByRow) ? notesWrittenBy.Text : null, Shown(writeNotesRow) && Shown(writeNotes) ? (string)writeNotes.Content : null, Shown(notesProgressRow), notesShown);
    internal NoticeStack NotesNotices => notesNotices;
    internal NoticeStack HistoryNotices => historyNotices;
    internal NoticeStack SelectedNotices => selectedNotices;
    // The notice lists of one meeting, made when it is first shown or has a notice, and dropped with the meeting. They are on screen while
    // the History page shows the meeting; elsewhere a warning or an error of theirs is pointed to with the meeting's title.
    internal (NoticeList Meeting, NoticeList Notes) ListsOf(MeetingDocument document)
    {
        if (meetingLists.TryGetValue(document.Id, out var lists)) return lists;
        lists = (new NoticeList(), new NoticeList());
        var id = document.Id;
        foreach (var list in new[] { lists.Meeting, lists.Notes })
            Board.Add(list, HistoryPage, $"“{document.Title}”", () => selected?.Id == id, () => { if (documents.FirstOrDefault(found => found.Id == id) is { } shown) _ = OpenAsync(shown); });
        // Dismissing how a run ended ends it, so the card does not come back when the meeting is opened again.
        lists.Notes.Dismissed += notice => { if (notice.Key is NotesStatus.FailedKey or NotesStatus.NothingFoundKey or NotesStatus.CancelledKey) notesRuns.Remove(id); };
        meetingLists[id] = lists;
        return lists;
    }
    // Lists of meetings that are gone go with them.
    private void DropListsOfDeletedMeetings()
    {
        foreach (var id in meetingLists.Keys.Where(id => documents.All(document => document.Id != id)).ToArray())
        {
            Board.Remove(meetingLists[id].Meeting); Board.Remove(meetingLists[id].Notes);
            meetingLists.Remove(id); notesRuns.Remove(id);
        }
    }
    // The install action of the meeting notes model on the Notes tab, for the verification: the button is null while it is hidden, and
    // Progress the step and share next to it while it installs.
    internal (string? Button, string? Description, string? Progress, double Share) NotesInstallState =>
        (Shown(writeNotesRow) && Shown(installOnNotes.Button) ? (string)installOnNotes.Button.Content : null, Shown(notesDestination) ? notesDestination.Text : null,
         installOnNotes.Progress.Shown ? installOnNotes.Progress.Text : null, installOnNotes.Progress.Share);
    // The install progress in the model's row under Models, for the verification: null while it is hidden. Failure is the text of its card.
    internal (string? Progress, double Share, string? Failure) SettingsInstallState =>
        (installInSettings.Progress.Shown ? installInSettings.Progress.Text : null, installInSettings.Progress.Share, modelsNotices.Notices.Find(NotesStatus.InstallFailedKey)?.Text);
    internal string NotesProgressText => notesProgress.Text;
    private static bool Shown(UIElement element) => element.Visibility == Visibility.Visible;
    private void ShowTab()
    {
        Show(transcriptView, transcriptTab.IsChecked == true); Show(notesView, notesTab.IsChecked == true);
        if (notesTab.IsChecked == true && selected is not null) UpdateNotes();
    }

    // The notes of the shown meeting: written by a model when the user had them written, otherwise built by rules. A run that stored
    // nothing leaves them as they were.
    private void UpdateNotes()
    {
        if (selected is not { } document) return;
        var written = document.WrittenNotes;
        ShowNotesText(written is not null ? MeetingHistory.WrittenNotesOnPage(written) : MeetingHistory.RuleNotesOnPage(document));
        Show(writtenByRow, written is not null);
        notesWrittenBy.Text = written is null ? "" : $"Written by {written.Model} on {written.Written.ToLocalTime():d MMM yyyy, HH:mm}.";
        // Writing again is the less likely step once a model has written the notes.
        if (written is null) writeNotes.Style = WorkspacePages.Theme("Primary"); else writeNotes.ClearValue(StyleProperty);
        UpdateWriteRow(document);
        UpdateNotesStatus(document);
        var running = notesCancellation is not null;
        ruleNotes.IsEnabled = !IsRecording && !(running && notesMeeting?.Id == document.Id);
        Show(notesProgressRow, running);
        UpdateNotesProgress();
    }
    // Redrawn whenever the notes change; a card dismissed for this meeting stays away until the meeting is opened again or its
    // condition ends.
    private void UpdateNotesStatus(MeetingDocument document)
    {
        var written = document.WrittenNotes;
        var lastRun = notesRuns.GetValueOrDefault(document.Id);
        var missingModel = settings.MeetingNotesStyle == MeetingNotesStyle.LocalModel && !MeetingNotesModel.IsInstalled(settings) && installCancellation is null;
        ListsOf(document).Notes.Sync(NotesStatus.Keys, document.Segments.Count == 0 && written is null ? []
            : NotesStatus.For(written is not null, written is not null && !MeetingHistory.WrittenNotesMatchTranscript(document), lastRun, missingModel ? installFailure : null, openNotesSettings));
    }
    // The button that has a model write the notes and where it sends them, or the install action while the local model is missing.
    private void UpdateWriteRow(MeetingDocument document)
    {
        var style = settings.MeetingNotesStyle;
        writeNotes.Content = style == MeetingNotesStyle.Api ? $"Write notes with {MeetingNotesWriter.Destination(settings)}" : "Write notes with the local model";
        AutomationProperties.SetName(writeNotes, (string)writeNotes.Content);
        var needsInstall = style == MeetingNotesStyle.LocalModel && !MeetingNotesModel.IsInstalled(settings);
        notesDestination.Text = style == MeetingNotesStyle.Api
            ? $"Sends the title, languages, vocabulary and transcript text of this meeting, never audio, to {MeetingNotesWriter.Destination(settings)} when you press it; nothing is sent before."
            : !needsInstall ? "Runs on this PC with the meeting notes model; nothing leaves it."
            : "Runs on this PC; nothing leaves it. The meeting notes model is not installed yet. " + InstallOffer;
        Show(notesDestination, installCancellation is null || !needsInstall);
        Show(writeNotesRow, style != MeetingNotesStyle.Extractive && document.Segments.Count > 0);
        Show(writeNotes, !needsInstall); Show(installOnNotes.Button, needsInstall);
        UpdateInstallRow(installOnNotes);
        writeNotes.IsEnabled = notesCancellation is null && !IsRecording;
    }
    // Each citation reads as its time and speaker and leads to the row on the Transcript tab.
    private void ShowNotesText(string notes)
    {
        var paragraph = new Paragraph { Margin = new(0), LineHeight = 20 };
        void Add(string text)
        {
            var lines = text.Split('\n');
            for (var line = 0; line < lines.Length; line++)
            {
                if (line > 0) paragraph.Inlines.Add(new LineBreak());
                if (lines[line].Length > 0) paragraph.Inlines.Add(new Run(lines[line]));
            }
        }
        var pieces = MeetingHistory.ReadableNotes(notes);
        foreach (var piece in pieces)
        {
            if (piece.SegmentId is not { } row) { Add(piece.Text); continue; }
            var link = new Hyperlink(new Run(piece.Text)) { Foreground = WorkspacePages.Theme<Brush>("AccentHover"), TextDecorations = null, Cursor = System.Windows.Input.Cursors.Hand, ToolTip = "Show this row in the transcript" };
            link.Click += (_, _) => ShowRow(row);
            AutomationProperties.SetName(link, "Show the row " + piece.Text.Trim('[', ']'));
            paragraph.Inlines.Add(link);
        }
        notesText.Document = new FlowDocument(paragraph) { PagePadding = new(0), FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"), FontSize = 13.5 };
        notesShown = string.Concat(pieces.Select(piece => piece.Text));
    }
    // A citation pressed on the Notes tab: the Transcript tab at that row, selected.
    internal void ShowRow(string segmentId)
    {
        if (segments.Items.OfType<MeetingSegment>().FirstOrDefault(segment => segment.Id == segmentId) is not { } row)
        { notesNotices.Report(new(RowGoneKey, NoticeKind.Note, "That row is no longer in the transcript", "The notes cite a row that was edited or removed since.")); return; }
        transcriptTab.IsChecked = true;
        if (selected is not null) ListsOf(selected).Notes.Clear(RowGoneKey);
        segments.SelectedItem = row; segments.UpdateLayout(); segments.ScrollIntoView(row);
    }
    internal IReadOnlyList<Hyperlink> NotesCitations => notesText.Document.Blocks.OfType<Paragraph>().SelectMany(paragraph => paragraph.Inlines.OfType<Hyperlink>()).ToList();
    internal MeetingSegment? SelectedRow => segments.SelectedItem as MeetingSegment;
    private void UpdateNotesProgress()
    {
        if (notesMeeting is null) return;
        var elapsed = (DateTime.UtcNow - notesStarted).TotalSeconds;
        // A dictation disables this page until it is done, so during its wait Cancel here cannot be reached; Cancel on Dictate can.
        notesProgress.Text = $"Writing notes for “{notesMeeting.Title}” with {notesDestinationOfRun}" + (elapsed >= 2 ? $" · {elapsed:0} s" : "") + ". You can keep using Private Flow."
            + (notesDestinationOfRun == MeetingNotesWriter.LocalDestination ? " " + LocalNotesBlockDictation : "");
    }
    private string notesDestinationOfRun = "";
    internal const string LocalNotesBlockDictation = "A Local polish dictation waits until the notes are done and locks this page meanwhile; Cancel on the Dictate page keeps its Clean text unpasted and unlocks the page.";
    // Only this meeting's Write notes press may send its transcript text to the API, never audio; failures keep the extractive notes.
    private async Task WriteNotesAsync()
    {
        if (IsRecording || selected is not { } document || notesCancellation is not null || settings.MeetingNotesStyle == MeetingNotesStyle.Extractive) return;
        // The notes are written from the saved transcript, so edits are saved first, but only when the user agrees.
        if (HasUnsavedEdits())
        {
            if (!await ConfirmAsync("Write notes", $"“{document.Title}” has edits you have not saved. Save them and write the notes from the saved transcript?", "Save and write notes")
                || !ReferenceEquals(selected, document) || notesCancellation is not null || !SaveEdits(quiet: true)) return;
        }
        var copy = JsonSerializer.Deserialize<MeetingDocument>(JsonSerializer.Serialize(document))!;
        var snapshot = settings.Snapshot();
        // The button names where the text goes; preferences applied since it was labelled must not send it elsewhere.
        if (snapshot.MeetingNotesStyle == MeetingNotesStyle.Api && (string)writeNotes.Content != $"Write notes with {MeetingNotesWriter.Destination(snapshot)}")
        {
            UpdateNotes();
            ListsOf(document).Notes.Report(new("notes-destination-changed", NoticeKind.Warning, "The API connection changed", "Check where the button sends the transcript and press it again."));
            return;
        }
        using var cancellation = new CancellationTokenSource();
        notesCancellation = cancellation; notesMeeting = document; notesStarted = DateTime.UtcNow; notesRuns.Remove(document.Id);
        var notesOfRun = ListsOf(document).Notes;
        notesOfRun.Clear("notes-destination-changed"); notesOfRun.Clear("notes-not-saved");
        notesDestinationOfRun = MeetingNotesWriter.Destination(snapshot);
        notesTimer.Start(); UpdateNotes();
        MeetingNotesResult result;
        try { result = await MeetingNotesWriter.WriteAsync(copy, snapshot, cancellation.Token, CompleteNotes); }
        finally
        {
            notesCancellation = null; notesMeeting = null; if (installCancellation is null) notesTimer.Stop();
            // The refusals that waited for these notes no longer hold.
            historyNotices.Clear(NotesBeingWrittenKey); meetingNotices.Clear(NotesBeingWrittenKey);
        }
        if (result.Written)
        {
            var notes = new MeetingWrittenNotes { Text = result.Text, Model = result.Model ?? notesDestinationOfRun, Written = DateTimeOffset.UtcNow, Transcript = MeetingHistory.TranscriptFingerprint(copy) };
            try
            {
                // The file on disk, not the page's copy, so edits not saved meanwhile stay unsaved.
                var stored = archive.Load(document.Id); stored.WrittenNotes = notes; archive.Save(stored);
                document.WrittenNotes = notes;
                foreach (var listed in documents.Where(listed => listed.Id == document.Id)) listed.WrittenNotes = notes;
                report($"Notes written by {notes.Model} for “{document.Title}” and saved with the meeting.");
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
            { notesOfRun.Report(new("notes-not-saved", NoticeKind.Error, "The notes were written but not saved", ex.Message)); }
        }
        else
        {
            // The notes shown stay: those a model wrote before, or the rule-based ones. The Notes tab says how the run ended.
            var run = result.Run ?? new(NotesRunOutcome.Failed, result.FallbackReason ?? "Unknown reason.");
            notesRuns[document.Id] = run;
            report(run.Outcome switch
            {
                NotesRunOutcome.NothingFound => $"{run.Detail} found nothing to note in “{document.Title}”; its notes are unchanged.",
                NotesRunOutcome.Cancelled => $"Writing the notes for “{document.Title}” was cancelled; its notes are unchanged.",
                _ => $"Writing the notes for “{document.Title}” ended; its Notes tab says how."
            });
        }
        // The meeting may no longer be the one shown: its cards are brought up to date anyway, so a failure is pointed to from where the user is.
        UpdateNotesStatus(document);
        if (selected is not null) UpdateNotes();
    }
    private async Task UseRuleNotesAsync()
    {
        if (IsRecording || selected is not { WrittenNotes: { } written } document) return;
        if (!await ConfirmAsync("Go back to the rule-based notes", $"Discard the notes {written.Model} wrote for this meeting? The notes built by rules on this PC show instead; writing them again sends the transcript again.", "Discard written notes")) return;
        try
        {
            var stored = archive.Load(document.Id); stored.WrittenNotes = null; archive.Save(stored);
            document.WrittenNotes = null; ListsOf(document).Notes.Clear("notes-not-discarded"); report("The meeting shows the rule-based notes again.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException)
        { ListsOf(document).Notes.Report(new("notes-not-discarded", NoticeKind.Error, "The written notes were not discarded", ex.Message)); }
        UpdateNotes();
    }
    // Notes being written for one of these meetings would be lost: a final pass saves its own older copy of the meeting over them, and
    // a deletion leaves them nowhere to be saved.
    // The refusal goes to the page the action was pressed on.
    private bool NotesBeingWritten(IEnumerable<MeetingDocument> meetings, NoticeStack page)
    {
        if (notesMeeting is not { } writing || !meetings.Any(document => document.Id == writing.Id)) return false;
        page.Report(new(NotesBeingWrittenKey, NoticeKind.Warning, "Notes are being written", $"Wait for the notes of “{writing.Title}” or cancel them on its Notes tab first."));
        return true;
    }

    // Rows of the shown meeting edited on the page and not saved yet. Only the shown meeting can have them: leaving it asks first.
    private bool HasUnsavedEdits()
    {
        if (selected is null) return false;
        segments.CommitEdit(); segments.CommitEdit(DataGridEditingUnit.Row, true);
        return DiffersFromDisk(selected);
    }
    // Compared with the saved meeting, not with what was shown last; a meeting that cannot be read counts as different.
    private bool DiffersFromDisk(MeetingDocument document)
    {
        try { return MeetingHistory.TranscriptFingerprint(document) != MeetingHistory.TranscriptFingerprint(archive.Load(document.Id)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or JsonException) { return true; }
    }
    // Save edits is the prominent action only while there is something to save.
    private void UpdateSaveEdits()
    {
        if (selected is not null && DiffersFromDisk(selected)) saveEdits.Style = WorkspacePages.Theme("Primary");
        else
        {
            saveEdits.ClearValue(StyleProperty);
            if (selected is not null) { ListsOf(selected).Meeting.Clear(UnsavedEditsKey); ListsOf(selected).Meeting.Clear(EditsKeptKey); }
        }
    }

    // True when the edits are saved or there is nothing to save.
    private bool SaveEdits(bool quiet = false)
    {
        if (IsRecording || selected is null) return false;
        segments.CommitEdit(); segments.CommitEdit(DataGridEditingUnit.Row, true);
        try
        {
            var edited = segments.ItemsSource.Cast<MeetingSegment>().ToList();
            if (edited.Any(s => string.IsNullOrWhiteSpace(s.Speaker) || s.Text is null)) throw new InvalidOperationException("Every segment needs a speaker label and text.");
            selected.Segments = edited;
            archive.Save(selected);
            UpdateSaveEdits();
            ListsOf(selected).Meeting.Clear("history-not-saved");
            if (!quiet) report("Transcript edits saved locally.");
            if (Searching) Search(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ListsOf(selected).Meeting.Report(new("history-not-saved", NoticeKind.Error, "Edits not saved", ex.Message)); return false; }
    }
    private async Task RetryAsync()
    {
        if (IsRecording || selected is null || NotesBeingWritten([selected], historyNotices)) return;
        ListsOf(selected).Meeting.Clear("history-no-audio");
        if (archive.AudioBytes(selected.Id) == 0)
        { ListsOf(selected).Meeting.Report(new("history-no-audio", NoticeKind.Note, "No saved audio for this meeting", "Turn on Retain processed audio to re-transcribe later.")); return; }
        // The pass rewrites the meeting on a worker thread, so it gets its own copy; the one the page shows stays untouched until it is done.
        var document = archive.Load(selected.Id);
        var shown = selected; var edited = HasUnsavedEdits();
        if ((document.Status == "Complete" || edited) && !await ConfirmAsync("Re-transcribe", "Re-transcribe this meeting with the languages chosen on the Meetings page and the shared vocabulary? This replaces the transcript, including "
            + (edited ? "the edits you have not saved." : "edits."), "Re-transcribe")) return;
        var snapshot = settings.Snapshot();
        // A meeting recorded before the shared vocabulary keeps its own terms; the prompt adds the shared ones.
        document.Languages = SelectedLanguages;
        var finished = await RunFinishingAsync(async (reporter, token) => { await MeetingCapture.RetryAsync(archive, document, snapshot, token, reporter); return "Meeting transcribed again locally."; }, document);
        // A cancelled or failed pass leaves the transcript as it was, so the rows edited on the page stay, still unsaved.
        if (finished || !edited) Show(document); else KeepEdits(document, shown);
    }
    private void Export()
    {
        if (IsRecording || selected is null) return;
        var picker = new Microsoft.Win32.SaveFileDialog { Filter = "Transcript|*.txt|Timestamped subtitles|*.srt|Notes with cited rows|*.md|Meeting backup with kept audio|*.zip", FileName = "meeting" };
        if (picker.ShowDialog() != true) return;
        try
        {
            if (!SaveEdits(quiet: true)) return;
            if (picker.FilterIndex == 4) archive.Export(selected.Id, picker.FileName);
            else File.WriteAllText(picker.FileName, picker.FilterIndex switch { 2 => MeetingArchive.ExportSrt(selected.Segments.Select(s => new TranscriptSegment(s.Start, s.End, s.Speaker + ": " + s.Text))), 3 => MeetingHistory.ExportedNotes(selected), _ => MeetingArchive.ExportText(selected) });
            ListsOf(selected).Meeting.Clear("history-export-failed");
            report("Meeting exported. Exported text/audio is private; keep the file in a location you control.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ListsOf(selected).Meeting.Report(new("history-export-failed", NoticeKind.Error, "Export failed", ex.Message)); }
    }
    private async Task DeleteAsync()
    {
        if (IsRecording || selected is not { } document || NotesBeingWritten([document], historyNotices)) return;
        if (!await ConfirmAsync("Delete meeting", "Permanently delete this meeting and its local audio?", "Delete meeting")) return;
        try
        {
            archive.Delete(document.Id); ClearSelection(); transcript.Clear();
            Refresh(); report("Meeting deleted from this PC.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { ListsOf(document).Meeting.Report(new("history-delete-failed", NoticeKind.Error, "The meeting was not deleted", ex.Message)); }
    }
    private async Task DeleteExpiredAsync()
    {
        if (IsRecording || NotesBeingWritten(archive.Expired(settings.MeetingRetentionDays, DateTimeOffset.UtcNow), historyNotices)) return;
        if (!await ConfirmAsync("Delete expired meetings", "Permanently delete completed meetings older than the saved retention period?", "Delete expired meetings")) return;
        try { report($"Deleted {archive.DeleteExpired(settings.MeetingRetentionDays, DateTimeOffset.UtcNow)} expired meetings."); historyNotices.Clear("history-expired-not-deleted"); Refresh(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        { historyNotices.Report(new("history-expired-not-deleted", NoticeKind.Error, "Expired meetings were not deleted", ex.Message)); }
    }
    // Actions that change the archive or start work wait while a meeting records or a transcript is finished; reading and searching do not.
    private void SetHistoryActions(bool enabled)
    {
        saveEdits.IsEnabled = export.IsEnabled = more.IsEnabled = deleteExpired.IsEnabled = enabled;
        segments.IsReadOnly = !enabled;
        if (selected is not null) UpdateNotes();
    }
}
