using System.Runtime.InteropServices;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>
/// Global hotkeys via RegisterHotKey on a message-only Win32 window (HWND_MESSAGE),
/// so they work even when SONO has no focus. Re-registered wholesale whenever bindings change.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    // ---- minimal message-only window (no WinForms) ----
    private const int WM_HOTKEY = 0x0312;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, ushort classAtom, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    private readonly WndProc _proc;
    private IntPtr _hwnd;
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

    private IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_HOTKEY)
        {
            var id = wp.ToInt32();
            if (_bindings.TryGetValue(id, out var b))
            {
                SONO.Core.Diagnostics.Log.Write($"hotkey fired: {b.ChannelId}/{b.Slot}");
                Pressed?.Invoke(b.ChannelId, b.Slot);
            }
        }
        return DefWindowProc(hWnd, msg, wp, lp);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

    public HotkeyManager()
    {
        _proc = Hook;   // field keeps the delegate alive for the app lifetime
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = "SONOHotkeySink",
        };
        var atom = RegisterClass(ref wc);
        if (atom == 0)
        {
            SONO.Core.Diagnostics.Log.Write($"hotkey class register failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        // parent = HWND_MESSAGE (-3): message-only window, invisible, receives WM_HOTKEY
        _hwnd = CreateWindowEx(0, atom, "SONO hotkey sink", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            SONO.Core.Diagnostics.Log.Write($"hotkey sink create failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>(channelId, slot) per registered hotkey.</summary>
    public event Action<string, HotkeySlot>? Pressed;

    private readonly Dictionary<int, (string ChannelId, HotkeySlot Slot)> _bindings = new();
    private readonly Dictionary<int, (uint Mods, uint Vk)> _registered = new();
    private int _nextId = 1;
    private List<(string ChannelId, HotkeySlot Slot, string? HotkeyText)>? _lastBindings;
    private bool _suspended;

    /// <summary>Temporarily unregister all hotkeys (while a capture field is armed, so the
    /// keys reach the field instead of being consumed by the global hotkey system).</summary>
    public void Suspend()
    {
        lock (this)
        {
            if (_suspended) return;
            _lastBindings = _bindings.Select(b => (b.Value.ChannelId, b.Value.Slot, _textById.GetValueOrDefault(b.Key))).ToList();
            foreach (var id in _registered.Keys.ToList()) { UnregisterHotKey(_hwnd, id); }
            _registered.Clear();
            _bindings.Clear();
            _textById.Clear();
            _suspended = true;
            SONO.Core.Diagnostics.Log.Write("hotkeys suspended (capture mode)");
        }
    }

    /// <summary>Re-register the suspended set. No-op when a commit already applied a fresh
    /// set (ApplyAll ends suspension) — restoring the snapshot would revert the new binding.</summary>
    public void Resume()
    {
        lock (this)
        {
            if (!_suspended) return;
            var restore = _lastBindings!;
            _lastBindings = null;
            _suspended = false;
            ApplyAll(restore);
            SONO.Core.Diagnostics.Log.Write("hotkeys resumed (no commit during capture)");
        }
    }

    private readonly Dictionary<int, string> _textById = new();

    /// <summary>Replace ALL registrations with the given (hotkeyText → target) set. Returns per-binding error messages.</summary>
    public List<string> ApplyAll(IEnumerable<(string ChannelId, HotkeySlot Slot, string? HotkeyText)> bindings)
    {
        lock (this)
        {
            _suspended = false;   // an explicit apply always ends suspension
            _lastBindings = null;
            var errors = new List<string>();
            foreach (var id in _registered.Keys.ToList()) { UnregisterHotKey(_hwnd, id); }
            _registered.Clear();
            _bindings.Clear();

            foreach (var (channelId, slot, text) in bindings)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (!TryParse(text, out var mods, out var vk, out var err)) { errors.Add($"'{text}': {err}"); continue; }

                int id = _nextId++;
                if (RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
                {
                    _registered[id] = (mods, vk);
                    _bindings[id] = (channelId, slot);
                    _textById[id] = text;
                    SONO.Core.Diagnostics.Log.Write($"hotkey registered: '{text}' -> {channelId}/{slot}");
                }
                else
                {
                    errors.Add($"'{text}' is taken by another program");
                    SONO.Core.Diagnostics.Log.Write($"hotkey FAILED: '{text}' (RegisterHotKey error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
                }
            }
            return errors;
        }
    }

    public static bool TryParse(string text, out uint mods, out uint vk, out string error)
    {
        mods = 0; vk = 0; error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "empty"; return false; }

        uint seen = 0;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": seen |= MOD_CONTROL; break;
                case "alt": seen |= MOD_ALT; break;
                case "shift": seen |= MOD_SHIFT; break;
                case "win" or "windows": seen |= MOD_WIN; break;
                default:
                    if (key is not null) { error = "two non-modifier keys"; return false; }
                    key = raw;
                    break;
            }
        }
        if (key is null) { error = "no key pressed"; return false; }
        if (!TryParseKey(key, out vk)) { error = $"unknown key '{key}'"; return false; }
        // multimedia keys are the exception: they may be bound bare, everything else needs a modifier
        if (seen == 0 && !IsMediaVk(vk)) { error = "need at least one modifier (Ctrl/Alt/Shift/Win) — media keys may be bare"; return false; }
        mods = seen;
        return true;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        var k = key.ToLowerInvariant();
        vk = k switch
        {
            var s when s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) => char.ToUpperInvariant(s[0]),
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74, "f6" => 0x75,
            "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            "f13" => 0x7C, "f14" => 0x7D, "f15" => 0x7E, "f16" => 0x7F, "f17" => 0x80,
            "f18" => 0x81, "f19" => 0x82, "f20" => 0x83, "f21" => 0x84, "f22" => 0x85, "f23" => 0x86, "f24" => 0x87,
            "space" => 0x20, "tab" => 0x09, "enter" => 0x0D, "backspace" => 0x08, "esc" or "escape" => 0x1B,
            "insert" => 0x2D, "delete" => 0x2E, "home" => 0x24, "end" => 0x23, "pageup" => 0x21, "pagedown" => 0x22,
            "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
            "oemplus" or "+" => 0xBB, "oemminus" or "-" => 0xBD,
            // multimedia / consumer keys — legal bare (no modifier)
            "volumeup" => 0xAF, "volumedown" => 0xAE, "volumemute" => 0xAD,
            "medianext" => 0xB0, "mediaprev" or "mediaprevious" => 0xB1,
            "mediastop" => 0xB2, "mediaplay" or "mediaplaypause" => 0xB3,
            _ => 0,
        };
        return vk != 0;
    }

    /// <summary>Consumer-control keys that may be registered without any modifier.</summary>
    private static bool IsMediaVk(uint vk) => vk is >= 0xAD and <= 0xB3;

    public void Dispose()
    {
        foreach (var id in _registered.Keys) try { UnregisterHotKey(_hwnd, id); } catch { }
        _registered.Clear();
    }
}
