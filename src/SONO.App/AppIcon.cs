using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SONO.Core.Diagnostics;

namespace SONO.App;

/// <summary>Per-exe icons for the Applications panel rows.
/// The audio engine already knows every session's full executable path, so nothing is
/// searched for: the path goes straight to SHGetFileInfo. (The previous version got only
/// the bare exe name and swept System32 / Program Files / AppData for it on the UI
/// thread — about 2.4 s per app, usually without finding it.) Extraction runs on a worker
/// thread because it can touch a cold disk; the bitmap is handed back on the UI thread.
/// Results, including "no icon", are cached per path.</summary>
public static class AppIcon
{
    // UI-thread only: both dictionaries are touched from Request and from its posted completion
    private static readonly Dictionary<string, Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<Action<Bitmap?>>> _pending = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Small icon for a full exe path. Call on the UI thread; <paramref name="onReady"/>
    /// runs on the UI thread too — synchronously when the icon is cached, otherwise once the
    /// background extraction finishes. It receives null when the file has no usable icon.</summary>
    public static void Request(string exePath, Action<Bitmap?> onReady)
    {
        if (string.IsNullOrWhiteSpace(exePath)) { onReady(null); return; }
        if (_cache.TryGetValue(exePath, out var hit)) { onReady(hit); return; }
        if (_pending.TryGetValue(exePath, out var waiters)) { waiters.Add(onReady); return; }
        _pending[exePath] = new List<Action<Bitmap?>> { onReady };

        Task.Run(() =>
        {
            IconPixels? px = null;
            try { px = LoadPixels(exePath); }
            catch (Exception ex) { Log.Write($"icon '{exePath}': {ex.Message}"); }

            Dispatcher.UIThread.Post(() =>
            {
                Bitmap? bmp = null;
                try { if (px is not null) bmp = ToBitmap(px); }
                catch (Exception ex) { Log.Write($"icon bitmap '{exePath}': {ex.Message}"); }
                _cache[exePath] = bmp;
                if (!_pending.Remove(exePath, out var list)) return;
                foreach (var cb in list)
                {
                    try { cb(bmp); }
                    catch (Exception ex) { Log.Write("icon callback: " + ex.Message); }
                }
            });
        });
    }

    /// <summary>Raw icon image: top-down rows, B-G-R-A byte order, straight (non-premultiplied) alpha.</summary>
    internal sealed record IconPixels(byte[] Bgra, int Width, int Height);

    /// <summary>Extract the shell's small icon for a file. Safe on any thread.</summary>
    internal static IconPixels? LoadPixels(string path)
    {
        var info = new SHFILEINFO { szDisplayName = "", szTypeName = "" };
        var res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try { return PixelsFromHIcon(info.hIcon); }
        finally { DestroyIcon(info.hIcon); }
    }

    internal static unsafe Bitmap ToBitmap(IconPixels px)
    {
        fixed (byte* p = px.Bgra)
        {
            // the Bitmap copies the pixels, so the array may move again after this returns
            return new Bitmap(
                Avalonia.Platform.PixelFormat.Bgra8888,
                Avalonia.Platform.AlphaFormat.Unpremul,
                (IntPtr)p,
                new Avalonia.PixelSize(px.Width, px.Height),
                new Avalonia.Vector(96, 96),
                px.Width * 4);
        }
    }

    // ---- HICON → pixels via GetIconInfo + GetDIBits (no WIC / WinForms dependency) ----

    private static IconPixels? PixelsFromHIcon(IntPtr hIcon)
    {
        var ii = new ICONINFO();
        if (!GetIconInfo(hIcon, ref ii)) return null;
        try
        {
            if (ii.hbmColor == IntPtr.Zero) return null;   // monochrome icon: nothing worth drawing
            if (GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
            int w = bm.bmWidth, h = bm.bmHeight;           // the color bitmap is the icon's real size
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var color = ReadTopDown32(hdc, ii.hbmColor, w, h);
                if (color is null) return null;

                // icons without an alpha channel come back with alpha 0 everywhere: take
                // their transparency from the AND mask instead (white = transparent)
                bool hasAlpha = false;
                for (int i = 3; i < color.Length; i += 4)
                    if (color[i] != 0) { hasAlpha = true; break; }
                if (!hasAlpha)
                {
                    var mask = ii.hbmMask != IntPtr.Zero ? ReadTopDown32(hdc, ii.hbmMask, w, h) : null;
                    for (int i = 0; i < color.Length; i += 4)
                        color[i + 3] = mask is not null && mask[i] != 0 ? (byte)0 : (byte)255;
                }
                return new IconPixels(color, w, h);
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    /// <summary>Read a GDI bitmap as 32-bit pixels in the layout Avalonia wants: asking for a
    /// NEGATIVE height makes GDI return top-down rows, and BI_RGB at 32 bpp is B-G-R-A —
    /// so no row flipping or channel swapping is needed afterwards.</summary>
    private static byte[]? ReadTopDown32(IntPtr hdc, IntPtr hbmp, int w, int h)
    {
        var bmi = new BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = w;
        bmi.bmiHeader.biHeight = -h;
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0;   // BI_RGB
        var bits = new byte[w * h * 4];
        return GetDIBits(hdc, hbmp, 0, (uint)h, bits, ref bmi, 0) == 0 ? null : bits;
    }

    // ---- Win32 ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint fileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;   // BI_RGB at 32 bpp carries no palette
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr h, int cb, out BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
