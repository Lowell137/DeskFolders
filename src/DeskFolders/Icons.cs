using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace GrupPopup;

/// <summary>Ham BGRA piksel verisi (UI thread'inden bağımsız, arka planda üretilebilir).</summary>
sealed record Pix(byte[] Bgra, int W, int H) { public bool Premultiplied { get; set; } }

/// <summary>Arka planda okunan öğe verisi: ad, yol, piksel.</summary>
sealed record ItemData(string Name, string Path, Pix? Pix);

/// <summary>Ekranda gösterilen öğe: ad, yol, ImageSource.</summary>
sealed record Item(string Name, string Path, ImageSource? Icon);

/// <summary>Kısayol/dosya/klasör ikonlarını System.Drawing olmadan, ham BGRA piksel olarak okur ve önbellekler.</summary>
static class Icons
{
    // ---- Win32 / COM ----
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, ref BITMAP bm);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] phicon, uint[] piconid, uint n, uint flags);

    // ---- piksel okuma ----
    static Pix? FromShell(string path, int px)
    {
        try
        {
            Guid g = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref g, out IShellItemImageFactory f);
            if (f.GetImage(new SIZE { cx = px, cy = px }, 0x4 /* yalnız ikon */, out IntPtr hb) != 0 || hb == IntPtr.Zero) return null;
            BITMAP bm = new();
            GetObject(hb, Marshal.SizeOf<BITMAP>(), ref bm);
            if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero) { DeleteObject(hb); return null; }
            // Kabuğun verdiği DIB bazen alttan üste (Flow, Crest baş aşağı çıkıyordu): satırları elle kopyalama,
            // GetDIBits'e üstten alta iste (Dib, biHeight = -h).
            byte[] buf = Dib(hb, bm.bmWidth, bm.bmHeight);
            DeleteObject(hb);
            return new Pix(buf, bm.bmWidth, bm.bmHeight);
        }
        catch { return null; }
    }

    static Pix? FromIconFile(string file, int idx, int px)
    {
        IntPtr hIcon = IntPtr.Zero;
        try
        {
            IntPtr[] h = new IntPtr[1]; uint[] id = new uint[1];
            uint n = PrivateExtractIcons(file, idx, px, px, h, id, 1, 0);
            if (n == 0 || n == 0xFFFFFFFF || h[0] == IntPtr.Zero) return null;
            hIcon = h[0];
            if (!GetIconInfo(hIcon, out ICONINFO ii)) return null;
            try
            {
                BITMAP bm = new();
                GetObject(ii.hbmColor, Marshal.SizeOf<BITMAP>(), ref bm);
                int w = bm.bmWidth, ht = bm.bmHeight;
                byte[] color = Dib(ii.hbmColor, w, ht);
                bool anyAlpha = false;
                for (int i = 3; i < color.Length; i += 4) if (color[i] != 0) { anyAlpha = true; break; }
                if (!anyAlpha)
                {
                    byte[] mask = Dib(ii.hbmMask, w, ht);
                    for (int i = 0; i < color.Length; i += 4) color[i + 3] = (byte)(mask[i] == 0 ? 255 : 0);
                }
                return new Pix(color, w, ht);
            }
            finally { DeleteObject(ii.hbmColor); DeleteObject(ii.hbmMask); }
        }
        catch { return null; }
        finally { if (hIcon != IntPtr.Zero) DestroyIcon(hIcon); }
    }

    static byte[] Dib(IntPtr hbm, int w, int h)
    {
        byte[] bits = new byte[w * h * 4];
        BITMAPINFO bmi = new() { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = GetDC(IntPtr.Zero);
        GetDIBits(dc, hbm, 0, (uint)h, bits, ref bmi, 0);
        ReleaseDC(IntPtr.Zero, dc);
        return bits;
    }

    // COM'u `dynamic` olmadan (ilk kullanımı yavaş) yansıma ile çağır.
    static object? Get(object o, string prop) => o.GetType().InvokeMember(prop, BindingFlags.GetProperty, null, o, null);

    static Pix? Resolve(string path, int px)
    {
        string? target = null, iconFile = null; int iconIdx = 0;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell")!;
                object sh = Activator.CreateInstance(t)!;
                object l = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path })!;
                target = Get(l, "TargetPath") as string;
                string? il = Get(l, "IconLocation") as string;
                if (!string.IsNullOrEmpty(il))
                {
                    int c = il.LastIndexOf(',');
                    string file = c >= 0 ? il[..c] : il;
                    if (c >= 0) int.TryParse(il[(c + 1)..], out iconIdx);
                    file = Environment.ExpandEnvironmentVariables(file.Trim().Trim('"'));
                    if (file.Length > 0 && File.Exists(file)) iconFile = file;
                }
            }
            catch { }
        }
        Pix? p = null;
        if (iconFile != null) p = FromIconFile(iconFile, iconIdx, px);
        if (p == null && !string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target))) p = FromShell(target, px);
        return p ?? FromShell(path, px);
    }

    /// <summary>WriteableBitmap premultiplied BGRA ister; kaynak düz alfa ise çarp, zaten çarpılıysa dokunma.</summary>
    internal static void EnsurePremultiplied(byte[] b)
    {
        bool straight = false;
        for (int i = 0; i < b.Length && !straight; i += 4)
        {
            byte a = b[i + 3];
            if (a < 255 && (b[i] > a || b[i + 1] > a || b[i + 2] > a)) straight = true;
        }
        if (!straight) return;
        for (int i = 0; i < b.Length; i += 4)
        {
            int a = b[i + 3];
            b[i] = (byte)(b[i] * a / 255); b[i + 1] = (byte)(b[i + 1] * a / 255); b[i + 2] = (byte)(b[i + 2] * a / 255);
        }
    }

    /// <summary>UI thread'inde çağrılmalı.</summary>
    public static ImageSource? ToSource(Pix? p)
    {
        if (p == null) return null;
        if (!p.Premultiplied) { EnsurePremultiplied(p.Bgra); p.Premultiplied = true; }
        var wb = new WriteableBitmap(p.W, p.H);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(p.Bgra, 0, p.Bgra.Length);
        wb.Invalidate();
        return wb;
    }

    // ---- klasör verisi + önbellek ----
    static readonly ConcurrentDictionary<string, (string Sig, List<ItemData> Data)> _cache = new(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<string> Entries(string folder) =>
        Order.Sort(folder, Directory.GetFileSystemEntries(folder)
            .Where(f =>
            {
                string n = System.IO.Path.GetFileName(f);
                return !n.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                    && (File.GetAttributes(f) & FileAttributes.Hidden) == 0;
            }));

    /// <summary>İçerik imzası: ad + son yazma zamanı. Değişmediyse önbellek geçerli.</summary>
    public static string Signature(string folder) =>
        string.Join("|", Entries(folder).Select(f => System.IO.Path.GetFileName(f) + "@" + File.GetLastWriteTimeUtc(f).Ticks));

    /// <summary>Arka plan thread'inde de çağrılabilir (UI nesnesi üretmez).</summary>
    public static List<ItemData> GetData(string folder)
    {
        string sig = Signature(folder);
        if (_cache.TryGetValue(folder, out var c) && c.Sig == sig) return c.Data;
        var res = new List<ItemData>();
        foreach (string f in Entries(folder))
        {
            string n = System.IO.Path.GetFileName(f);
            string name = Directory.Exists(f) ? n : System.IO.Path.GetFileNameWithoutExtension(f);
            res.Add(new ItemData(name, f, Resolve(f, 96)));
        }
        _cache[folder] = (sig, res);
        return res;
    }

    public static void Invalidate(string folder) => _cache.TryRemove(folder, out _);

    /// <summary>Kardeş grup klasörlerinin (ve alt klasörlerinin) verisini önceden hazırla.</summary>
    public static void Prewarm(string root)
    {
        try
        {
            foreach (string d in Directory.GetDirectories(root))
                if (!System.IO.Path.GetFileName(d).StartsWith('_')) Warm(d, 0);
        }
        catch { }
    }

    static void Warm(string folder, int depth)
    {
        try
        {
            foreach (ItemData d in GetData(folder))
                if (depth < 6 && Directory.Exists(d.Path)) Warm(d.Path, depth + 1);
        }
        catch { }
    }
}
