using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using PrivateFlow.Core;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace PrivateFlow.Windows;

// ClipboardUnreadable: the previous clipboard could not be saved completely, so nothing was pasted and the clipboard was left as it was.
internal enum InsertionResult { Complete, Unavailable, ClipboardUnreadable }

internal static class Native
{
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(nint window, int id);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiInfo
    {
        public uint Size, Flags; public nint Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KeyInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyInput Key; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    internal static bool Down(int key) => GetAsyncKeyState(key) < 0;
    internal static bool ModifiersDown => new[] { 0x10, 0x11, 0x12, 0x5b, 0x5c }.Any(Down);
    // RegisterHotKey reports only the press; the release is read from the key state. The Ctrl and Alt codes cover both sides, so AltGr,
    // which Windows reports as left Ctrl with right Alt, holds a Ctrl + Alt shortcut too.
    internal static bool Held(ActivationShortcut shortcut) => Down((int)shortcut.Key)
        && ((shortcut.Modifiers & 1) == 0 || Down(0x12)) && ((shortcut.Modifiers & 2) == 0 || Down(0x11))
        && ((shortcut.Modifiers & 4) == 0 || Down(0x10)) && ((shortcut.Modifiers & 8) == 0 || Down(0x5b) || Down(0x5c));
    // The foreground window, its focused control and its process, read at once and without asking the target anything; the password
    // state is not read here.
    internal static FocusTarget CurrentFocus()
    {
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out var process);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        var control = GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
        return new(window, control, process, process == Environment.ProcessId, PasswordField.Unknown);
    }
    internal static FocusTarget CaptureTarget()
    {
        var before = CurrentFocus();
        var (window, control, process, own) = (before.Window, before.Control, before.Process, before.OwnWindow);
        // Any field may receive a paste (browsers, Office, UWP Settings, editors) except a
        // password field and Private Flow itself. UWP content lives in a child process, so the
        // focused element is not required to belong to the foreground window's process.
        var automation = PasswordField.Unknown;
        string? identity = null;
        if (window != 0 && !own)
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                if (element is not null)
                {
                    identity = ElementIdentity(element);
                    own = element.Current.ProcessId == Environment.ProcessId;
                    // Without its default, a property the element does not report comes back as NotSupported: unknown, not "no".
                    automation = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) switch { true => PasswordField.Yes, false => PasswordField.No, _ => PasswordField.Unknown };
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or UnauthorizedAccessException or InvalidOperationException or COMException) { }
        }
        // The focused window's own answer where it is an edit control; a password field by either source is one.
        var (edit, timedOut) = EditControlPassword(control);
        var password = window != 0 && !own ? PasswordFields.Combine(automation, edit, timedOut) : PasswordField.Unknown;
        // Read again after the probes: answers about a field that has lost the focus meanwhile count for nothing.
        return PasswordFields.Settle(new(window, control, process, own, password, identity), CurrentFocus());
    }
    private static string ElementIdentity(AutomationElement element) => string.Join(".", element.GetRuntimeId());
    // The focused accessibility element's identity, or null when UI Automation gives none, as CaptureTarget reads it.
    private static string? FocusedElement()
    {
        try { return AutomationElement.FocusedElement is { } element ? ElementIdentity(element) : null; }
        catch (Exception ex) when (ex is ElementNotAvailableException or UnauthorizedAccessException or InvalidOperationException or COMException) { return null; }
    }
    // ES_PASSWORD and EM_GETPASSWORDCHAR (0x00D2) of a Win32 edit control, asked with a 200 ms timeout so a hung target cannot hold the
    // dictation or widen the time between the two readings of the focus; any other window cannot say.
    private static (PasswordField Password, bool TimedOut) EditControlPassword(nint control)
    {
        if (control == 0) return (PasswordField.Unknown, false);
        var name = new System.Text.StringBuilder(256);
        if (GetClassName(control, name, name.Capacity) == 0 || !PasswordFields.IsEditControl(name.ToString())) return (PasswordField.Unknown, false);
        var style = (long)GetWindowLongPtr(control, -16);
        int? character = SendMessageTimeout(control, 0x00D2, 0, 0, 0x0002, 200, out var answer) != 0 ? (int)answer : null;
        return (PasswordFields.FromEditControl(name.ToString(), style, character), character is null);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder name, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")] private static extern nint SendMessageTimeout(nint window, uint message, nint wParam, nint lParam, uint flags, uint timeout, out nint result);
    // Bring the window selected at recording start back to the front if something (for
    // example a notification) took focus. Windows may refuse; the caller re-checks.
    internal static void RestoreForeground(nint window)
    {
        if (window != 0 && IsWindow(window) && GetForegroundWindow() != window) SetForegroundWindow(window);
    }
    // Clipboard + Ctrl+V works in browsers, Office, Electron, UWP and terminals, unlike
    // per-character Unicode input. The previous clipboard is restored afterwards.
    // stillValid decides on the target classified now (CaptureTarget); right before Ctrl+V the focus is read once more and must be that
    // same window, control, process and accessibility element.
    internal static async Task<InsertionResult> PasteAsync(string text, Func<FocusTarget, bool> stillValid, TimeSpan? restoreDelay = null)
    {
        // A clipboard that could not be saved completely is never replaced: the paste would lose what it held.
        var (previous, saved) = StableClipboardSnapshot();
        if (!ClipboardSnapshot.AllowsPaste(previous.Copy)) return InsertionResult.ClipboardUnreadable;
        // A copy made after the snapshot is newer than what the restore would bring back, so the dictation does not replace it, also not
        // on a retry of the write.
        // The sequence number identifies our publication; any newer copy, even with equal text, wins.
        var (written, owned) = SetClipboardText(text, saved);
        if (written is null) return InsertionResult.ClipboardUnreadable;
        if (written == false) return InsertionResult.Unavailable;
        var target = CaptureTarget();
        // The accessibility element first: asking for it can wait on the target, so the native focus and the modifier keys, which
        // stillValid reads, are read after it, right before Ctrl+V.
        var element = FocusedElement();
        // Not pasted: the previous clipboard comes back. A copy made since the dictation was put there stays, and ClipboardUnreadable
        // keeps the caller from putting the dictation over it.
        InsertionResult NotPasted()
        {
            if (GetClipboardSequenceNumber() != owned) return InsertionResult.ClipboardUnreadable;
            RestoreClipboard(previous, owned);
            return InsertionResult.Unavailable;
        }
        if (!DictationPolicy.SameElement(target, CurrentFocus(), element) || !stillValid(target)) return NotPasted();
        // A copy made while the focus was read would be pasted in place of the dictation.
        if (GetClipboardSequenceNumber() != owned) return InsertionResult.ClipboardUnreadable;
        var keys = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (sent != keys.Length) return NotPasted();
        // Targets read the clipboard asynchronously after Ctrl+V; restoring too early pastes
        // the old content. See PasteTiming for the per-application hold.
        await Task.Delay(restoreDelay ?? PasteTiming.RestoreDelay(null));
        RestoreClipboard(previous, owned);
        return InsertionResult.Complete;
    }
    private static Input Key(ushort virtualKey, bool up) =>
        new() { Type = 1, Data = new InputUnion { Key = new KeyInput { VirtualKey = virtualKey, Flags = up ? 2u : 0u } } };
    private const string DictationFormat = "ExcludeClipboardContentFromMonitorProcessing";
    // Keep dictations out of Win+V history and cloud clipboard sync.
    internal static bool SetClipboardText(string text) => SetClipboardText(text, null).Written == true;
    // With unchanged, null instead of writing once the clipboard's sequence number is no longer that one: someone copied meanwhile.
    // Owned is the sequence number of this publication, read right after the write and kept only while the clipboard still holds the
    // dictation's own format: a copy landing right after the write is then not taken for the dictation, pasted and replaced by the restore.
    private static (bool? Written, uint Owned) SetClipboardText(string text, uint? unchanged)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData(DictationFormat, new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        var changed = false; var owned = 0u;
        var written = Retry(() =>
        {
            if (unchanged is { } sequence && GetClipboardSequenceNumber() != sequence) { changed = true; return; }
            Clipboard.SetDataObject(data, true);
            if (unchanged is null) return;
            owned = GetClipboardSequenceNumber();
            // A clipboard that cannot be read right now counts as still ours: the write succeeded, and writing again would only repeat it.
            bool ours;
            try { ours = Clipboard.ContainsData(DictationFormat); } catch (ExternalException) { ours = true; }
            if (!ours || GetClipboardSequenceNumber() != owned) changed = true;
        });
        return (changed ? null : written, owned);
    }
    internal static uint ClipboardSequence => GetClipboardSequenceNumber();
    // A snapshot with the sequence number it holds: a copy made while it was read changes the number, and the snapshot is read again. Its
    // own delayed rendering may change the number once; a clipboard that keeps changing counts as unreadable and is left as it is.
    private static ((ClipboardCopy Copy, DataObject? Data) Snapshot, uint Sequence) StableClipboardSnapshot()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = GetClipboardSequenceNumber();
            var snapshot = SnapshotClipboard();
            if (GetClipboardSequenceNumber() == before) return (snapshot, before);
        }
        return ((ClipboardCopy.Incomplete, null), 0);
    }
    // The clipboard before a paste: empty, saved with every format it offered, or incomplete when it stayed locked through the retries or a
    // format could not be read or read as no data (ClipboardSnapshot.Classify).
    internal static (ClipboardCopy Copy, DataObject? Data) SnapshotClipboard()
    {
        DataObject? copy = null;
        var formatsRead = new List<bool>();
        var opened = Retry(() =>
        {
            copy = null; formatsRead.Clear();
            var current = Clipboard.GetDataObject();
            if (current is null) return;
            copy = new DataObject();
            foreach (var format in current.GetFormats(false))
            {
                var read = false;
                try { if (current.GetData(format, false) is { } value) { copy.SetData(format, value); read = true; } }
                catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or InvalidOperationException) { }
                formatsRead.Add(read);
            }
        });
        return (ClipboardSnapshot.Classify(opened, formatsRead), copy);
    }
    internal static void RestoreClipboard((ClipboardCopy Copy, DataObject? Data) previous, uint owned) => Retry(() =>
    {
        // Re-checked on every attempt: leave the clipboard alone once anyone has published since.
        // A copy landing between this check and the write remains a narrow, unavoidable race.
        if (GetClipboardSequenceNumber() != owned) return;
        if (previous.Copy == ClipboardCopy.Empty || previous.Data is null) Clipboard.Clear();
        else Clipboard.SetDataObject(previous.Data, true);
    });
    private static bool Retry(Action action)
    {
        // Other apps briefly hold the clipboard open; retry instead of failing the paste.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { action(); return true; }
            catch (ExternalException) { Thread.Sleep(30); }
        }
        return false;
    }
}

// A keyboard layout of this PC: by default the one active in Private Flow, which is the one the user types the shortcut with.
internal sealed class WindowsKeyboardLayout(nint layout = 0) : IKeyboardLayout
{
    public static readonly WindowsKeyboardLayout Current = new();
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);
    [DllImport("user32.dll")] private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint virtualKey, uint scanCode, byte[] keyState, [Out] char[] text, int size, uint flags, nint layout);
    // An installed layout of a language, such as 0x0407 for German; null when none is installed.
    public static WindowsKeyboardLayout? Installed(ushort language)
    {
        var layouts = new nint[GetKeyboardLayoutList(0, null)];
        GetKeyboardLayoutList(layouts.Length, layouts);
        return layouts.Where(handle => (ushort)handle == language).Select(handle => new WindowsKeyboardLayout(handle)).FirstOrDefault();
    }
    public string Typed(uint virtualKey, bool shift, bool altGr)
    {
        var active = layout != 0 ? layout : GetKeyboardLayout(0);
        var state = new byte[256];
        if (shift) state[0x10] = state[0xa0] = 0x80;
        // AltGr arrives as left Ctrl with right Alt.
        if (altGr) state[0x11] = state[0xa2] = state[0x12] = state[0xa5] = 0x80;
        var text = new char[8];
        // Flag 4 leaves the keyboard state alone, so a dead key asked about here does not change the next character the user types.
        var count = ToUnicodeEx(virtualKey, MapVirtualKeyEx(virtualKey, 0, active), state, text, text.Length, 4, active);
        // A dead key (-1) still types its accent.
        return count < 0 ? new string(text, 0, 1) : new string(text, 0, count);
    }
}
