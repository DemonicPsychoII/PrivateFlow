using System.Windows.Interop;
using PrivateFlow.Core;

namespace PrivateFlow.Windows;

// Owns the window's system-wide shortcuts, so each feature registers its own with a handler.
internal sealed class GlobalShortcuts : IDisposable
{
    private const int HotkeyMessage = 0x312;
    // MOD_NOREPEAT: a held key fires once, not on every auto-repeat.
    private const uint NoRepeat = 0x4000;
    private readonly Dictionary<int, Action> handlers = [];
    private nint window;
    private HwndSource? source;
    private int lastIdentifier;

    public void Attach(nint handle)
    {
        window = handle;
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WindowMessage);
    }
    public int? Register(ActivationShortcut shortcut, Action handler)
    {
        if (window == 0) return null;
        // Identifiers are not reused right away: Windows keeps an already queued WM_HOTKEY after
        // unregistering, and it must not reach a shortcut registered since. Applications may use 1 to 0xBFFF.
        int identifier;
        do { lastIdentifier = lastIdentifier % 0xBFFF + 1; identifier = lastIdentifier; } while (handlers.ContainsKey(identifier));
        if (!Native.RegisterHotKey(window, identifier, NoRepeat | shortcut.Modifiers, shortcut.Key)) return null;
        handlers[identifier] = handler;
        return identifier;
    }
    // Registers the new shortcut before removing the old one, so a conflict never loses the old binding.
    public int? Replace(int? identifier, ActivationShortcut shortcut, Action handler)
    {
        var replacement = Register(shortcut, handler);
        if (replacement is not null && identifier is { } previous) Unregister(previous);
        return replacement;
    }
    public void Unregister(int identifier)
    {
        if (handlers.Remove(identifier)) Native.UnregisterHotKey(window, identifier);
    }
    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == HotkeyMessage && handlers.TryGetValue((int)wParam, out var handler)) { handler(); handled = true; }
        return 0;
    }
    public void Dispose()
    {
        foreach (var identifier in handlers.Keys) Native.UnregisterHotKey(window, identifier);
        handlers.Clear();
        source?.RemoveHook(WindowMessage);
    }
}
