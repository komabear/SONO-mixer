using System.Runtime.InteropServices;
using SONO.Core.Audio;

namespace SONO.App;

/// <summary>
/// Global hotkeys via RegisterHotKey on a hidden message-only window, so they work
/// even when SONO has no focus. Re-registered wholesale whenever bindings change.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    private sealed class NativeWindow : System.Windows.Forms.NativeWindow
    {
        public Action<int>? OnHotkey;
        public NativeWindow() => CreateHandle(new CreateParams());
        protected override void WndProc(ref Message m)
        {
            const int WM_HOTKEY = 0x0312;
            if (m.Msg == WM_HOTKEY) OnHotkey?.Invoke(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    private readonly NativeWindow _window = new();
    private readonly Dictionary<int, (uint Mods, uint Vk)> _registered = new();
    private int _nextId = 1;

    /// <summary>(channelId, slot) per registered hotkey.</summary>
    public event Action<string, HotkeySlot>? Pressed;

    public HotkeyManager() => _window.OnHotkey += id =>
    {
        if (_bindings.TryGetValue(id, out var b)) Pressed?.Invoke(b.ChannelId, b.Slot);
    };

    private readonly Dictionary<int, (string ChannelId, HotkeySlot Slot)> _bindings = new();

    /// <summary>Replace ALL registrations with the given (hotkeyText → target) set. Returns per-binding error messages.</summary>
    public List<string> ApplyAll(IEnumerable<(string ChannelId, HotkeySlot Slot, string? HotkeyText)> bindings)
    {
        var errors = new List<string>();
        foreach (var id in _registered.Keys.ToList()) { UnregisterHotKey(_window.Handle, id); }
        _registered.Clear();
        _bindings.Clear();

        foreach (var (channelId, slot, text) in bindings)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (!TryParse(text, out var mods, out var vk, out var err)) { errors.Add($"'{text}': {err}"); continue; }

            int id = _nextId++;
            if (RegisterHotKey(_window.Handle, id, mods | MOD_NOREPEAT, vk))
            {
                _registered[id] = (mods, vk);
                _bindings[id] = (channelId, slot);
            }
            else errors.Add($"'{text}' is taken by another program");
        }
        return errors;
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
        if (key is null || seen == 0) { error = "need at least one modifier (Ctrl/Alt/Shift/Win) + a key"; return false; }
        if (!TryParseKey(key, out vk)) { error = $"unknown key '{key}'"; return false; }
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
            _ => 0,
        };
        return vk != 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys) try { UnregisterHotKey(_window.Handle, id); } catch { }
        _registered.Clear();
    }
}
