using System.Runtime.InteropServices;

namespace SONO.App;

/// <summary>
/// Native Win32 tray icon: Shell_NotifyIcon + a hidden helper window for the callback
/// message, TrackPopupMenu for the right-click menu (native menus, matches WinForms look).
/// Replaces H.NotifyIcon.Avalonia (package no longer exists on nuget.org).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    /// <summary>Stable identity: lets Windows remember the user's show/hide choice for SONO
    /// across rebuilds and reinstalls (instead of treating each exe as a new icon).</summary>
    private static readonly Guid IconGuid = new("8f2a6d9c-4b7e-4c3a-9e5d-1a2b3c4d5e70");

    private const uint WM_CALLBACK = 0x8000 + 0x534F;   // WM_APP + 'SO'
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_CONTEXTMENU = 0x007B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4, NIF_GUID = 0x20;
    private const uint NOTIFYICON_VERSION_4 = 4;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, ushort classAtom, string windowName,
        int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);
    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_DISABLED = 2, MF_GRAYED = 1, MF_CHECKED = 8;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;

    [StructLayout(LayoutKind.Sequential)]
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
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp);

    private static TrayIcon? _current;   // WndProc is static → route to the live instance

    private readonly WndProc _proc;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private IntPtr _hMenu;
    private readonly List<Action> _menuActions = new();
    private bool _menuOpen;

    /// <summary>Left-click on the icon.</summary>
    public event Action? Click;
    /// <summary>Right-click / context-menu requested.</summary>
    public event Action? MenuRequested;

    public string Tooltip { set => SetTooltip(value); }

    public TrayIcon()
    {
        if (_current is not null) throw new InvalidOperationException("Only one TrayIcon supported");
        _current = this;
        _proc = StaticProc;
        var wc = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            lpszClassName = "SONOTraySink",
        };
        var atom = RegisterClass(ref wc);
        SONO.Core.Diagnostics.Log.Write($"tray: class atom={atom} err={Marshal.GetLastWin32Error()}");
        if (atom == 0)
        {
            SONO.Core.Diagnostics.Log.Write($"tray class register failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _hwnd = CreateWindowEx(0, atom, "SONO tray sink", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            SONO.Core.Diagnostics.Log.Write($"tray sink create failed: {Marshal.GetLastWin32Error()}");
            return;
        }
        _hIcon = LoadIconFromExe();
        SONO.Core.Diagnostics.Log.Write($"tray: hIcon={_hIcon}");
        var nid = NewNid();
        var added = Shell_NotifyIcon(NIM_ADD, ref nid);
        SONO.Core.Diagnostics.Log.Write($"tray: NIM_ADD={added} err={Marshal.GetLastWin32Error()}");
        if (!added)
            SONO.Core.Diagnostics.Log.Write("tray icon add failed");
        else
        {
            nid.uTimeoutOrVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref nid);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex, int cxIcon, int cyIcon,
        [Out] IntPtr[] phicon, [Out] uint[] piconid, uint nIcons, uint fInitIn);
    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSMICON = 49;
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string szFile, int nIconIndex,
        [Out] IntPtr[]? phiconLarge, [Out] IntPtr[]? phiconSmall, uint nIcons);
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    private static IntPtr LoadIconFromExe()
    {
        try
        {
            // prefer the shipped sono.ico at EXACT tray metrics (small-icon system size)
            var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "sono.ico");
            if (System.IO.File.Exists(ico))
            {
                int cx = GetSystemMetrics(SM_CXSMICON);
                if (cx <= 0) cx = 16;
                var handles = new IntPtr[1];
                var ids = new uint[1];
                if (PrivateExtractIcons(ico, 0, cx, cx, handles, ids, 1, 0) > 0 && handles[0] != IntPtr.Zero)
                {
                    SONO.Core.Diagnostics.Log.Write($"tray: icon loaded from Assets at {cx}px");
                    return handles[0];
                }
            }
            // fallback: first icon of our own exe
            var path = Environment.ProcessPath;
            if (path is not null)
            {
                var small = new IntPtr[1];
                if (ExtractIconEx(path, 0, null, small, 1) > 0 && small[0] != IntPtr.Zero)
                    return small[0];
            }
        }
        catch { }
        return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
    }

    private NOTIFYICONDATA NewNid() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID,
        uCallbackMessage = WM_CALLBACK,
        hIcon = _hIcon,
        szTip = "SONO Mixer",
        guidItem = IconGuid,
    };

    private void SetTooltip(string text)
    {
        var nid = NewNid();
        nid.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref nid);
    }

    private IntPtr StaticProc(IntPtr hWnd, uint msg, IntPtr wp, IntPtr lp)
    {
        if (msg == WM_CALLBACK)
        {
            // VERSION_4: lParam = event (Win32 word), wParam = (x, y)
            var evt = (uint)(lp.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONUP || lp.ToInt64() == WM_LBUTTONUP)
                Click?.Invoke();
            else if (evt == WM_RBUTTONUP || evt == WM_CONTEXTMENU)
                MenuRequested?.Invoke();
            return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wp, lp);
    }

    /// <summary>Show the native context menu (blocks until an item is chosen, like WinForms).</summary>
    public void PopupMenu(IReadOnlyList<(string Text, Action? Action, bool Checked, bool Disabled, bool SeparatorBefore)> items)
    {
        if (_hwnd == IntPtr.Zero) return;
        _hMenu = CreatePopupMenu();
        _menuActions.Clear();
        uint id = 1;
        foreach (var it in items)
        {
            if (it.SeparatorBefore) AppendMenu(_hMenu, MF_SEPARATOR, 0, null);
            var flags = MF_STRING;
            if (it.Checked) flags |= MF_CHECKED;
            if (it.Disabled) flags |= MF_GRAYED | MF_DISABLED;
            _menuActions.Add(it.Action);
            AppendMenu(_hMenu, flags, id++, it.Text);
        }
        SetMenuDefaultItem(_hMenu, 1, 1);   // bold first item ("Open SONO")

        GetCursorPos(out var pt);
        SetForegroundWindow(_hwnd);
        _menuOpen = true;
        try
        {
            var cmd = TrackPopupMenu(_hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            if (cmd > 0 && cmd <= _menuActions.Count) _menuActions[(int)cmd - 1]?.Invoke();
        }
        finally
        {
            _menuOpen = false;
            DestroyMenu(_hMenu);
            _hMenu = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            var nid = NewNid();
            try { Shell_NotifyIcon(NIM_DELETE, ref nid); } catch { }
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_hIcon != IntPtr.Zero) { try { DestroyIcon(_hIcon); } catch { } _hIcon = IntPtr.Zero; }
        if (ReferenceEquals(_current, this)) _current = null;
    }
}
