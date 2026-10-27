using BarracudaTray.Interop;

namespace BarracudaTray.Ui;

/// <summary>System-wide shortcuts, delivered to a hidden window on the UI thread.</summary>
internal sealed class GlobalHotkeys : NativeWindow, IDisposable
{
    const int WmHotkey = 0x0312;
    const uint ModAlt = 0x1;
    const uint ModControl = 0x2;
    const uint ModShift = 0x4;
    const uint ModNoRepeat = 0x4000;

    readonly Dictionary<int, Action> _actions = [];

    public GlobalHotkeys() => CreateHandle(new CreateParams());

    /// <summary>False when another program already owns the combination.</summary>
    public bool Register(Keys keys, Action action)
    {
        var modifiers = ModNoRepeat
            | ((keys & Keys.Alt) != 0 ? ModAlt : 0)
            | ((keys & Keys.Control) != 0 ? ModControl : 0)
            | ((keys & Keys.Shift) != 0 ? ModShift : 0);
        var id = _actions.Count + 1;
        if (!NativeMethods.RegisterHotKey(Handle, id, modifiers, (uint)(keys & Keys.KeyCode)))
            return false;
        _actions[id] = action;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _actions.TryGetValue((int)m.WParam, out var action))
            action();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys)
            NativeMethods.UnregisterHotKey(Handle, id);
        DestroyHandle();
    }
}
