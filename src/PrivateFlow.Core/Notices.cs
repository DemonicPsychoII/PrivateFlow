namespace PrivateFlow.Core;

public enum NoticeKind { Warning, Error, Note }

// A link or button on a notice card, such as "Choose how notes are written". Two actions are the same when their labels are, so a
// page that raises the same notice again with a new delegate does not redraw the card.
public sealed record NoticeAction(string Label, Action Invoke)
{
    public bool Equals(NoticeAction? other) => other is not null && Label == other.Label;
    public override int GetHashCode() => Label.GetHashCode(StringComparison.Ordinal);
}

// A warning, an error or a note about an outcome, shown as a card above the content it concerns. Key names the condition: the same key
// raised again is the same card, with its text brought up to date. Detail is the full text when Text is shortened. A notice that is
// not dismissable asks for a decision the user must take to continue, or states a live condition that ends by itself. Standing marks
// a lasting caveat about the content, such as the rule-based notes, which other pages need not point to.
public sealed record Notice(string Key, NoticeKind Kind, string Title, string Text, string? Detail = null, NoticeAction? Action = null, bool Dismissable = true,
    bool Standing = false);

// A card says a reason in its first sentence; a longer reason ends there and is whole in the card's detail.
public static class NoticeText
{
    private const int ShortReason = 140;
    public static string Short(string reason)
    {
        var end = reason.IndexOf(". ", StringComparison.Ordinal);
        var first = end < 0 ? reason : reason[..(end + 1)];
        return first.Length <= ShortReason ? first : first[..(ShortReason - 1)].TrimEnd() + "…";
    }
    public static bool Shortened(string reason) => Short(reason) != reason;
    // The full reason when the card shortens it, otherwise null.
    public static string? Detail(string reason) => Shortened(reason) ? reason : null;
}

// The notices of one page or of one meeting, newest first. Two sorts:
// - A condition (Raise) lasts until the page clears it. Dismissed, it stays hidden however often it is raised again while it lasts,
//   and shows again once it has ended and come back (Clear, then Raise) or its context is opened again (Reopen).
// - An outcome (Report) of an operation that just ran comes to the front and shows even when the same outcome was dismissed before.
//   Dismissing it ends it; so does the next run of its operation, which clears or reports it.
// Dismissing never changes the condition itself.
public sealed class NoticeList
{
    private readonly List<Notice> notices = [];
    private readonly HashSet<string> dismissed = [], outcomes = [];
    public event Action? Changed;
    // A notice became visible: a new condition, an outcome reported, or a condition that cannot be dismissed shown again.
    public event Action<Notice>? Appeared;
    public event Action<Notice>? Dismissed;
    public IReadOnlyList<Notice> Visible => notices.Where(notice => !dismissed.Contains(notice.Key)).ToArray();
    public Notice? Find(string key) => notices.FirstOrDefault(notice => notice.Key == key);

    // A condition the page shows: new keys go in front, a known key keeps its place and takes the new text.
    public void Raise(Notice notice) => Put(notice, false);
    public void Report(Notice notice)
    {
        notices.RemoveAll(known => known.Key == notice.Key);
        dismissed.Remove(notice.Key);
        Put(notice, true);
    }
    private void Put(Notice notice, bool outcome)
    {
        var index = notices.FindIndex(known => known.Key == notice.Key);
        var appears = index < 0 || (!notice.Dismissable && dismissed.Contains(notice.Key));
        var changed = appears || notices[index] != notice;
        if (index < 0) notices.Insert(0, notice); else notices[index] = notice;
        if (!notice.Dismissable) dismissed.Remove(notice.Key);
        if (outcome) outcomes.Add(notice.Key); else outcomes.Remove(notice.Key);
        if (appears) Appeared?.Invoke(notice);
        if (changed) Changed?.Invoke();
    }
    // The condition has ended, or the outcome is replaced by a new run; when it comes back it shows again.
    public void Clear(string key)
    {
        dismissed.Remove(key); outcomes.Remove(key);
        if (notices.RemoveAll(notice => notice.Key == key) > 0) Changed?.Invoke();
    }
    // The page's current conditions among the keys it owns: those not in current have ended. current is in order, the first in front.
    public void Sync(IEnumerable<string> owned, IReadOnlyList<Notice> current)
    {
        foreach (var key in owned.Where(key => current.All(notice => notice.Key != key))) Clear(key);
        for (var index = current.Count - 1; index >= 0; index--) Raise(current[index]);
    }
    // False for a notice that cannot be dismissed, which stays.
    public bool Dismiss(string key)
    {
        if (Find(key) is not { Dismissable: true } notice || dismissed.Contains(key)) return false;
        if (outcomes.Contains(key)) { outcomes.Remove(key); notices.Remove(notice); }
        else dismissed.Add(key);
        Dismissed?.Invoke(notice);
        Changed?.Invoke();
        return true;
    }
    // The context of the notices was opened again, such as its meeting selected again: dismissed conditions that still hold show again.
    public void Reopen()
    {
        if (dismissed.Count == 0) return;
        dismissed.Clear();
        Changed?.Invoke();
    }
}

// Where the user is told about warnings and errors on pages they do not see. Each list belongs to a page (or settings section) and
// maybe to a context on it, such as one meeting of the History page that is on screen only while it is selected. A warning or an
// error that appears while its list is not on screen is unseen until the list is shown or the notice ends: the page is marked, and
// the newest unseen notice is named with its page. Notes and standing caveats are never unseen.
public sealed class NoticeBoard
{
    private sealed record Entry(NoticeList List, string Page, string? Context, Func<bool> Active, Action? Reveal);
    private readonly List<Entry> entries = [];
    private readonly Dictionary<(Entry Entry, string Key), long> unseen = [];
    private long sequence;
    private string? showing;
    public event Action? Changed;

    // active says whether the list's context is the one its page shows, such as the selected meeting; a page's own list is always.
    // reveal shows that context once the page is open.
    public void Add(NoticeList list, string page, string? context = null, Func<bool>? active = null, Action? reveal = null)
    {
        var entry = new Entry(list, page, context, active ?? (() => true), reveal);
        entries.Add(entry);
        list.Appeared += notice => Appeared(entry, notice);
        list.Changed += () => Prune(entry);
    }
    public void Remove(NoticeList list)
    {
        foreach (var entry in entries.Where(entry => entry.List == list).ToArray())
        {
            entries.Remove(entry);
            foreach (var key in unseen.Keys.Where(key => key.Entry == entry).ToArray()) unseen.Remove(key);
        }
        Changed?.Invoke();
    }
    // The page the user sees now, or null while the window is hidden or minimised. Shown, its lists' notices count as seen.
    public void Show(string? page) { showing = page; Refresh(); }
    // The context shown on the page changed, such as another meeting selected.
    public void Refresh()
    {
        var seen = unseen.Keys.Where(key => OnScreen(key.Entry)).ToArray();
        foreach (var key in seen) unseen.Remove(key);
        if (seen.Length > 0) Changed?.Invoke();
    }
    private bool OnScreen(Entry entry) => entry.Page == showing && entry.Active();
    private void Appeared(Entry entry, Notice notice)
    {
        if (notice.Kind == NoticeKind.Note || notice.Standing || OnScreen(entry)) return;
        unseen[(entry, notice.Key)] = ++sequence;
        Changed?.Invoke();
    }
    // A notice that ended or was dismissed is no longer unseen.
    private void Prune(Entry entry)
    {
        var visible = entry.List.Visible.Select(notice => notice.Key).ToHashSet();
        var ended = unseen.Keys.Where(key => key.Entry == entry && !visible.Contains(key.Key)).ToArray();
        foreach (var key in ended) unseen.Remove(key);
        if (ended.Length > 0) Changed?.Invoke();
    }
    // Error when the page holds an unseen error, Warning for an unseen warning, null for none.
    public NoticeKind? Marker(string page)
    {
        var kinds = unseen.Keys.Where(key => key.Entry.Page == page).Select(key => key.Entry.List.Find(key.Key)?.Kind).ToArray();
        return kinds.Contains(NoticeKind.Error) ? NoticeKind.Error : kinds.Contains(NoticeKind.Warning) ? NoticeKind.Warning : null;
    }
    // The newest unseen notice with its page and context, for the status line.
    public (string Page, string? Context, Notice Notice)? Newest =>
        unseen.OrderByDescending(pair => pair.Value).Select(pair => pair.Key.Entry.List.Find(pair.Key.Key) is { } notice ? (pair.Key.Entry.Page, pair.Key.Entry.Context, notice) : ((string, string?, Notice)?)null)
            .FirstOrDefault(found => found is not null);
    // "“Design review”: Notes could not be written. See History."
    public string? Pointer => Newest is { } newest ? $"{(newest.Context is null ? "" : newest.Context + ": ")}{newest.Notice.Title.TrimEnd('.')}. See {newest.Page}." : null;
    // Opens the page of the newest unseen notice, then its context.
    public void OpenNewest(Action<string> openPage)
    {
        var newest = unseen.OrderByDescending(pair => pair.Value).Select(pair => pair.Key.Entry).FirstOrDefault();
        if (newest is null) return;
        openPage(newest.Page); newest.Reveal?.Invoke();
    }
    public bool Unseen(NoticeList list, string key) => unseen.Keys.Any(found => found.Entry.List == list && found.Key == key);
}
