using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using PrivateFlow.Core;
using Clipboard = System.Windows.Clipboard;
using DataObject = System.Windows.DataObject;

namespace PrivateFlow.Windows;

internal enum InsertionResult { Complete, Unavailable }

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
    internal static FocusTarget CaptureTarget()
    {
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out var process);
        var info = new GuiInfo { Size = (uint)Marshal.SizeOf<GuiInfo>() };
        var control = GetGUIThreadInfo(thread, ref info) ? info.Focus : 0;
        // Any field may receive a paste (browsers, Office, UWP Settings, editors) except a
        // password field and Private Flow itself. UWP content lives in a child process, so the
        // focused element is not required to belong to the foreground window's process.
        var safe = window != 0 && process != Environment.ProcessId;
        string? identity = null;
        if (safe)
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                if (element is not null)
                {
                    identity = string.Join(".", element.GetRuntimeId());
                    if (element.Current.ProcessId == Environment.ProcessId ||
                        element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is true)
                        safe = false;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or UnauthorizedAccessException or InvalidOperationException or COMException) { }
        }
        return new(window, control, process, safe, identity);
    }
    // Bring the window selected at recording start back to the front if something (for
    // example a notification) took focus. Windows may refuse; the caller re-checks.
    internal static void RestoreForeground(nint window)
    {
        if (window != 0 && IsWindow(window) && GetForegroundWindow() != window) SetForegroundWindow(window);
    }
    // Clipboard + Ctrl+V works in browsers, Office, Electron, UWP and terminals, unlike
    // per-character Unicode input. The previous clipboard is restored afterwards.
    internal static async Task<InsertionResult> PasteAsync(string text, Func<bool> stillValid, TimeSpan? restoreDelay = null)
    {
        var previous = SnapshotClipboard();
        if (!SetClipboardText(text)) return InsertionResult.Unavailable;
        // The sequence number identifies our publication; any newer copy, even with equal text, wins.
        var owned = GetClipboardSequenceNumber();
        if (!stillValid()) { RestoreClipboard(previous, owned); return InsertionResult.Unavailable; }
        var keys = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
        var sent = SendInput((uint)keys.Length, keys, Marshal.SizeOf<Input>());
        if (sent != keys.Length) { RestoreClipboard(previous, owned); return InsertionResult.Unavailable; }
        // Targets read the clipboard asynchronously after Ctrl+V; restoring too early pastes
        // the old content. See PasteTiming for the per-application hold.
        await Task.Delay(restoreDelay ?? PasteTiming.RestoreDelay(null));
        RestoreClipboard(previous, owned);
        return InsertionResult.Complete;
    }
    private static Input Key(ushort virtualKey, bool up) =>
        new() { Type = 1, Data = new InputUnion { Key = new KeyInput { VirtualKey = virtualKey, Flags = up ? 2u : 0u } } };
    // Keep dictations out of Win+V history and cloud clipboard sync.
    internal static bool SetClipboardText(string text)
    {
        var data = new DataObject();
        data.SetText(text);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[4]));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        return Retry(() => Clipboard.SetDataObject(data, true));
    }
    internal static uint ClipboardSequence => GetClipboardSequenceNumber();
    internal static DataObject? SnapshotClipboard()
    {
        DataObject? copy = null;
        Retry(() =>
        {
            var current = Clipboard.GetDataObject();
            if (current is null) return;
            copy = new DataObject();
            foreach (var format in current.GetFormats(false))
                try { if (current.GetData(format, false) is { } value) copy.SetData(format, value); }
                catch (Exception ex) when (ex is ExternalException or OutOfMemoryException or InvalidOperationException) { /* Unreadable private format; skip. */ }
        });
        return copy;
    }
    internal static void RestoreClipboard(DataObject? previous, uint owned) => Retry(() =>
    {
        // Re-checked on every attempt: leave the clipboard alone once anyone has published since.
        // A copy landing between this check and the write remains a narrow, unavoidable race.
        if (GetClipboardSequenceNumber() != owned) return;
        if (previous is null || previous.GetFormats(false).Length == 0) Clipboard.Clear();
        else Clipboard.SetDataObject(previous, true);
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
