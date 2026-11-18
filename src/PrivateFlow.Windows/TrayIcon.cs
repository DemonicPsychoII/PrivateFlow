using PrivateFlow.Core;
using Forms = System.Windows.Forms;

namespace PrivateFlow.Windows;

internal sealed class TrayIcon : IDisposable
{
    public const string UpdateTitle = "Private Flow update", MeetingsTitle = "Private Flow meetings";
    // Clicking a meetings notification opens the Meetings page.
    public Action? OpenMeetings { get; set; }
    private readonly Forms.NotifyIcon notifyIcon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem updateItem = new("Update available…") { Visible = false };
    private int processingFrame;

    public TrayIcon(Action open, Action update, Action quit)
    {
        notifyIcon = new Forms.NotifyIcon { Icon = BrandArt.TrayIcon(BrandState.Idle), Text = "Private Flow — ready", Visible = true };
        notifyIcon.DoubleClick += (_, _) => open();
        menu.Items.Add("Open Private Flow", null, (_, _) => open());
        updateItem.Click += (_, _) => update();
        menu.Items.Add(updateItem);
        menu.Items.Add("Quit", null, (_, _) => quit());
        notifyIcon.ContextMenuStrip = menu;
        notifyIcon.BalloonTipClicked += (_, _) => { if (notifyIcon.BalloonTipTitle == UpdateTitle) update(); else if (notifyIcon.BalloonTipTitle == MeetingsTitle) OpenMeetings?.Invoke(); };
    }
    // Feature entries sit between Open and the Update and Quit entries, in the order they are added.
    public void AddMenuItem(string text, Action action) =>
        menu.Items.Insert(menu.Items.IndexOf(updateItem), new Forms.ToolStripMenuItem(text, null, (_, _) => action()));
    public void SetProfiles(IEnumerable<DictationProfile> profiles, Action<DictationProfile> select)
    {
        var old = menu.Items.OfType<Forms.ToolStripMenuItem>().FirstOrDefault(item => item.Name == "Profiles");
        if (old is not null) { menu.Items.Remove(old); old.Dispose(); }
        var submenu = new Forms.ToolStripMenuItem("Profiles") { Name = "Profiles" };
        foreach (var profile in profiles) submenu.DropDownItems.Add(profile.Name, null, (_, _) => select(profile));
        menu.Items.Insert(1, submenu);
    }
    public void SetState(BrandState state, string text) { notifyIcon.Icon = BrandArt.TrayIcon(state); notifyIcon.Text = text; processingFrame = 0; }
    // A gently moving tray mark while transcribing; the pill shows the same state.
    public void AnimateProcessing()
    {
        if (++processingFrame % 3 == 0) notifyIcon.Icon = BrandArt.TrayIcon(BrandState.Processing, processingFrame / 3 % 9);
    }
    public void ShowBalloon(int milliseconds, string title, string text, Forms.ToolTipIcon icon) => notifyIcon.ShowBalloonTip(milliseconds, title, text, icon);
    public void ShowUpdateAvailable() => updateItem.Visible = true;
    public void Dispose() { notifyIcon.Dispose(); menu.Dispose(); }
}
