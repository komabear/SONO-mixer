using System.Runtime.InteropServices;
using SONO.Core.Diagnostics;
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
    private const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

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
                // this runs inside a native window-procedure callback: an exception escaping
                // it takes the whole process down, so a failing handler is logged instead
                try { Pressed?.Invoke(b.ChannelId, b.Slot); }
                catch (Exception ex) { SONO.Core.Diagnostics.Log.Write($"hotkey handler failed: {ex}"); }
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

    /// <summary>Raised after the set of live registrations changed (an apply, or a deferred
    /// retry that succeeded). UI reads <see cref="ErrorFor"/> to flag bindings that are not live.</summary>
    public event Action? RegistrationsChanged;

    private readonly record struct Pending(string ChannelId, HotkeySlot Slot, string Text, uint Mods, uint Vk);

    private readonly Dictionary<int, (string ChannelId, HotkeySlot Slot)> _bindings = new();
    private readonly Dictionary<int, (uint Mods, uint Vk)> _registered = new();
    private readonly Dictionary<(string ChannelId, HotkeySlot Slot), string> _errors = new();
    private readonly List<Pending> _pendingRetry = new();
    /// <summary>The full set last passed to ApplyAll — including bindings that failed — so a
    /// suspend/resume cycle restores exactly that set and its error flags.</summary>
    private List<(string ChannelId, HotkeySlot Slot, string? HotkeyText)> _applied = new();
    private int _nextId = 1;
    private int _generation;   // bumped on every apply/suspend: stale deferred retries drop out
    private bool _suspended;
    private bool _disposed;

    private const int RetryAttempts = 5;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(150);

    /// <summary>Why a binding is not live (taken by another program, duplicated inside SONO,
    /// unsupported key), or null when it is registered or unbound.</summary>
    public string? ErrorFor(string channelId, HotkeySlot slot)
    {
        lock (this) return _errors.GetValueOrDefault((channelId, slot));
    }

    /// <summary>Temporarily unregister all hotkeys (while a capture field is armed, so the
    /// keys reach the field instead of being consumed by the global hotkey system).</summary>
    public void Suspend()
    {
        lock (this)
        {
            if (_suspended) return;
            UnregisterAllLocked();
            _pendingRetry.Clear();
            _generation++;
            _suspended = true;
            SONO.Core.Diagnostics.Log.Write("hotkeys suspended (capture mode)");
        }
    }

    /// <summary>Re-register the suspended set. No-op when a commit already applied a fresh
    /// set (ApplyAll ends suspension) — restoring the snapshot would revert the new binding.</summary>
    public void Resume()
    {
        List<(string ChannelId, HotkeySlot Slot, string? HotkeyText)> restore;
        lock (this)
        {
            if (!_suspended) return;
            restore = _applied;
        }
        ApplyAll(restore);
        SONO.Core.Diagnostics.Log.Write("hotkeys resumed (no commit during capture)");
    }

    // ---- special (non-channel) bindings: output prev/next ----
    public const string OutputPrevChannel = "__output_prev";
    public const string OutputNextChannel = "__output_next";
    public const HotkeySlot OutputPrevSlot = (HotkeySlot)100;
    public const HotkeySlot OutputNextSlot = (HotkeySlot)101;

    /// <summary>Replace ALL registrations with the given (hotkeyText → target) set. Returns
    /// per-binding error messages. Never blocks: a combination Windows reports as already
    /// registered is retried a few times in the background (right after a previous SONO
    /// instance is killed, Windows can take a moment to release its registrations) — the
    /// retry used to be a Thread.Sleep loop on the UI thread, 750 ms per failing binding.</summary>
    public List<string> ApplyAll(IEnumerable<(string ChannelId, HotkeySlot Slot, string? HotkeyText)> bindings)
    {
        var errors = new List<string>();
        lock (this)
        {
            var list = bindings.ToList();
            SONO.Core.Diagnostics.Log.Write($"ApplyAll: hwnd=0x{_hwnd.ToInt64():X} bindings={list.Count} registeredNow={_registered.Count} thread={Environment.CurrentManagedThreadId}");
            _suspended = false;   // an explicit apply always ends suspension
            _applied = list;
            _generation++;
            UnregisterAllLocked();
            _errors.Clear();
            _pendingRetry.Clear();

            var usedCombos = new HashSet<(uint Mods, uint Vk)>();
            foreach (var (channelId, slot, text) in list)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;

                void Fail(string message)
                {
                    _errors[(channelId, slot)] = message;
                    errors.Add(message);
                }

                if (!TryParse(text, out var mods, out var vk, out var err))
                {
                    Fail($"'{text}' can't be used as a global hotkey ({err})");
                    continue;
                }
                // RegisterHotKey refuses a combination twice even inside one process, so a
                // second SONO binding of the same keys can never work — say so directly
                if (!usedCombos.Add((mods, vk)))
                {
                    Fail($"'{text}' is already assigned to another SONO hotkey");
                    SONO.Core.Diagnostics.Log.Write($"hotkey DUPLICATE: '{text}' ({channelId}/{slot}) is bound twice — second binding ignored");
                    continue;
                }
                if (TryRegisterLocked(channelId, slot, text, mods, vk, out int win32)) continue;

                Fail($"'{text}' is in use by another program");
                SONO.Core.Diagnostics.Log.Write($"hotkey FAILED: '{text}' (RegisterHotKey error {win32})");
                if (win32 == ERROR_HOTKEY_ALREADY_REGISTERED)
                    _pendingRetry.Add(new Pending(channelId, slot, text, mods, vk));
            }
            if (_pendingRetry.Count > 0) ScheduleRetry(_generation, 1);
        }
        RegistrationsChanged?.Invoke();
        return errors;
    }

    private bool TryRegisterLocked(string channelId, HotkeySlot slot, string text, uint mods, uint vk, out int win32Error)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods | MOD_NOREPEAT, vk))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }
        win32Error = 0;
        _registered[id] = (mods, vk);
        _bindings[id] = (channelId, slot);
        SONO.Core.Diagnostics.Log.Write($"hotkey registered: '{text}' -> {channelId}/{slot}");
        return true;
    }

    private void UnregisterAllLocked()
    {
        foreach (var id in _registered.Keys.ToList()) UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _bindings.Clear();
    }

    /// <summary>Queue one background retry of the still-failing registrations. Runs on the UI
    /// thread (RegisterHotKey must be called by the thread that owns the sink window).</summary>
    private void ScheduleRetry(int generation, int attempt)
    {
        try
        {
            Avalonia.Threading.DispatcherTimer.RunOnce(() => Retry(generation, attempt), RetryInterval);
        }
        catch (Exception ex) { Log.Write("hotkey retry not scheduled: " + ex.Message); }
    }

    private void Retry(int generation, int attempt)
    {
        bool changed = false;
        lock (this)
        {
            if (_disposed || _suspended || generation != _generation) return;
            for (int i = _pendingRetry.Count - 1; i >= 0; i--)
            {
                var p = _pendingRetry[i];
                if (!TryRegisterLocked(p.ChannelId, p.Slot, p.Text, p.Mods, p.Vk, out _)) continue;
                _errors.Remove((p.ChannelId, p.Slot));
                _pendingRetry.RemoveAt(i);
                changed = true;
            }
            if (_pendingRetry.Count > 0 && attempt < RetryAttempts) ScheduleRetry(generation, attempt + 1);
        }
        if (changed) RegistrationsChanged?.Invoke();
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

    /// <summary>Key name → virtual-key code. This table must cover every name the capture
    /// box (HotkeyBox) can produce; the box also validates against it before committing,
    /// so a combination can never be shown as bound while being unregistrable.</summary>
    private static bool TryParseKey(string key, out uint vk)
    {
        var k = key.ToLowerInvariant();
        vk = k switch
        {
            var s when s.Length == 1 && char.IsAsciiLetterOrDigit(s[0]) => char.ToUpperInvariant(s[0]),
            // numpad digits: "Num7" (what the capture box writes) or "NumPad7" → VK_NUMPAD0..9
            var s when s.Length == 4 && s.StartsWith("num", StringComparison.Ordinal) && char.IsAsciiDigit(s[3]) => (uint)(0x60 + (s[3] - '0')),
            var s when s.Length == 7 && s.StartsWith("numpad", StringComparison.Ordinal) && char.IsAsciiDigit(s[6]) => (uint)(0x60 + (s[6] - '0')),
            "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73, "f5" => 0x74, "f6" => 0x75,
            "f7" => 0x76, "f8" => 0x77, "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
            "f13" => 0x7C, "f14" => 0x7D, "f15" => 0x7E, "f16" => 0x7F, "f17" => 0x80,
            "f18" => 0x81, "f19" => 0x82, "f20" => 0x83, "f21" => 0x84, "f22" => 0x85, "f23" => 0x86, "f24" => 0x87,
            "space" => 0x20, "tab" => 0x09, "enter" or "return" => 0x0D, "backspace" or "back" => 0x08, "esc" or "escape" => 0x1B,
            "insert" => 0x2D, "delete" => 0x2E, "home" => 0x24, "end" => 0x23,
            "pageup" or "prior" => 0x21, "pagedown" or "next" => 0x22,
            "up" => 0x26, "down" => 0x28, "left" => 0x25, "right" => 0x27,
            "pause" => 0x13, "capslock" or "capital" => 0x14, "printscreen" or "snapshot" => 0x2C,
            "apps" => 0x5D, "numlock" => 0x90, "scroll" => 0x91,
            "multiply" => 0x6A, "add" => 0x6B, "subtract" => 0x6D, "decimal" => 0x6E, "divide" => 0x6F,
            // punctuation (US-layout names, as Avalonia reports them)
            "oemsemicolon" or "oem1" => 0xBA, "oemplus" or "+" => 0xBB, "oemcomma" => 0xBC,
            "oemminus" or "-" => 0xBD, "oemperiod" => 0xBE, "oemquestion" or "oem2" => 0xBF,
            "oemtilde" or "oem3" => 0xC0, "oemopenbrackets" or "oem4" => 0xDB, "oempipe" or "oem5" => 0xDC,
            "oemclosebrackets" or "oem6" => 0xDD, "oemquotes" or "oem7" => 0xDE, "oem8" => 0xDF,
            "oembackslash" or "oem102" => 0xE2,
            "abntc1" => 0xC1, "abntc2" => 0xC2,   // ABNT2 (Brazilian) "/?" key and numpad "."
            // browser / launcher keys (need a modifier, like any ordinary key)
            "browserback" => 0xA6, "browserforward" => 0xA7, "browserrefresh" => 0xA8, "browserstop" => 0xA9,
            "browsersearch" => 0xAA, "browserfavorites" => 0xAB, "browserhome" => 0xAC,
            "launchmail" => 0xB4, "selectmedia" => 0xB5, "launchapplication1" => 0xB6, "launchapplication2" => 0xB7,
            // multimedia / consumer keys — legal bare (no modifier)
            "volumeup" => 0xAF, "volumedown" => 0xAE, "volumemute" => 0xAD,
            "medianext" or "medianexttrack" => 0xB0, "mediaprev" or "mediaprevious" or "mediaprevioustrack" => 0xB1,
            "mediastop" => 0xB2, "mediaplay" or "mediaplaypause" => 0xB3,
            _ => 0,
        };
        return vk != 0;
    }

    /// <summary>Consumer-control keys that may be registered without any modifier.</summary>
    private static bool IsMediaVk(uint vk) => vk is >= 0xAD and <= 0xB3;

    public void Dispose()
    {
        lock (this)
        {
            _disposed = true;
            _generation++;
            _pendingRetry.Clear();
            foreach (var id in _registered.Keys) try { UnregisterHotKey(_hwnd, id); } catch (Exception ex) { Log.Write("hotkey unregister failed: " + ex.Message); }
            _registered.Clear();
        }
    }
}
