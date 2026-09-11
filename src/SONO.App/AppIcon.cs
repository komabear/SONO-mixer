using System.Runtime.InteropServices;

namespace SONO.App;

/// <summary>Per-exe icon extraction for the Applications panel rows.
/// SHGetFileInfo resolves the real icon (registry DefaultIcon, overlays, etc.).</summary>
public static class AppIcon
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint fileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Small (16px) icon for an exe path or bare exe name; null when unavailable.
    /// Bare names are probed in PATH + common install dirs; results are cached.</summary>
    public static Avalonia.Media.Imaging.Bitmap? Get(string exeName)
    {
        if (_cache.TryGetValue(exeName, out var bmp)) return bmp;
        var bitmap = LoadBitmap(FindPath(exeName));
        _cache[exeName] = bitmap;
        return bitmap;
    }

    private static readonly Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static string? FindPath(string exeName)
    {
        var name = exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exeName : exeName + ".exe";
        if (File.Exists(name)) return Path.GetFullPath(name);

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("SystemRoot") + "\\System32",
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        }.Where(d => !string.IsNullOrEmpty(d));

        foreach (var root in roots)
        {
            // direct hit first
            var direct = Path.Combine(root, name);
            if (File.Exists(direct)) return direct;
            // then one recursive sweep (apps nest: Discord\app-1.0.x\Discord.exe etc.)
            try
            {
                var hit = Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).FirstOrDefault();
                if (hit is not null) return hit;
            }
            catch { /* access denied on some subtrees — skip */ }
        }
        return null;
    }

    private static Avalonia.Media.Imaging.Bitmap? LoadBitmap(string? path)
    {
        if (path is null) return null;
        var info = new SHFILEINFO { szDisplayName = "" };
        var res = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_SMALLICON);
        if (res == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            using var bs = System.IO.MemoryStream.Null;
            var bmp = FromHIcon(info.hIcon);
            return bmp;
        }
        finally { DestroyIcon(info.hIcon); }
    }

    private static unsafe Avalonia.Media.Imaging.Bitmap? FromHIcon(IntPtr hIcon)
    {
        try
        {
            // HICON → BITMAP → pixels → Avalonia bitmap (no WIC/WinForms dependency)
            if (GetIconBitmapInfo(hIcon, out var bmi, out var colorBytes, out var xorBytes))
            {
                int w = bmi.biWidth, h = bmi.biHeight / 2;   // XOR mask is 2× height (color + mask)
                var pixels = new byte[w * h * 4];
                // rows are bottom-up BGRA
                for (int y = 0; y < h; y++)
                {
                    int srcRow = (h - 1 - y) * w * 4;
                    int dstRow = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        pixels[dstRow + 0] = xorBytes[srcRow + 2]; // R
                        pixels[dstRow + 1] = xorBytes[srcRow + 1]; // G
                        pixels[dstRow + 2] = xorBytes[srcRow + 0]; // B
                        pixels[dstRow + 3] = xorBytes[srcRow + 3]; // A
                    }
                }
                fixed (byte* ptr = pixels)
                {
                    return new Avalonia.Media.Imaging.Bitmap(
                        Avalonia.Platform.PixelFormat.Bgra8888,
                        Avalonia.Platform.AlphaFormat.Unpremul,
                        (IntPtr)ptr,
                        new Avalonia.PixelSize(w, h),
                        new Avalonia.Vector(96, 96),
                        w * 4);
                }
            }
        }
        catch { }
        return null;
    }

    // ---- raw HICON → BITMAP via GetIconInfo + GetDIBits ----
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

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
        public uint bmiColors;   // we ask for BI_RGB 32bpp → no palette needed
    }

    [DllImport("user32.dll")]
    private static extern bool GetIconInfo(IntPtr hIcon, ref ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        IntPtr bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        byte[] bits, ref BITMAPINFO bmi, uint usage);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private static bool GetIconBitmapInfo(IntPtr hIcon, out BITMAPINFOHEADER bmi, out byte[] color, out byte[] mask)
    {
        bmi = default;
        color = []; mask = [];
        var ii = new ICONINFO();
        if (!GetIconInfo(hIcon, ref ii)) return false;
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            try
            {
                var bmiC = new BITMAPINFO();
                bmiC.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                bmiC.bmiHeader.biBitCount = 0;   // let GDI fill the layout
                if (GetDIBits(hdc, ii.hbmColor, 0, 0, IntPtr.Zero, ref bmiC, 0) == 0) return false;
                bmi = bmiC.bmiHeader;
                int h = Math.Abs(bmi.biHeight);
                color = new byte[bmi.biSizeImage == 0 ? w_bytes(bmi) : bmi.biSizeImage];
                bmiC.bmiHeader.biCompression = 0;   // BI_RGB
                if (GetDIBits(hdc, ii.hbmColor, 0, (uint)h, color, ref bmiC, 0) == 0) return false;
                // mask (1bpp) — needed only where alpha is 0; grab for completeness
                var bmiM = new BITMAPINFO();
                bmiM.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
                mask = new byte[(((bmi.biWidth + 31) / 32) * 4) * h];
                GetDIBits(hdc, ii.hbmMask, 0, (uint)h, mask, ref bmiM, 0);
                return true;
            }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
        finally { DeleteObject(ii.hbmMask); DeleteObject(ii.hbmColor); }
    }

    private static int w_bytes(BITMAPINFOHEADER bmi) => Math.Abs(bmi.biHeight) * bmi.biWidth * 4;
}
