using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace GrupPopup;

/// <summary>Paylaşılan Win32 yardımcıları: acrylic accent, DWM cloak/köşe.</summary>
static class Native
{
    [StructLayout(LayoutKind.Sequential)] struct ACCENTPOLICY { public int State, Flags; public uint Gradient; public int AnimId; }
    [StructLayout(LayoutKind.Sequential)] struct WINCOMPATTRDATA { public int Attr; public IntPtr Data; public int Size; }
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();

    /// <summary>Açık klasör paneli (Başlat klasörü gibi: güçlü bulanıklık, hafif koyu-gri ton). AABBGGRR.</summary>
    public const uint PanelTint = 0x7838363A;

    /// <summary>Pencerenin arkasına acrylic bulanıklık (Windows'un kendi accent politikası).</summary>
    public static void Acrylic(IntPtr h, uint tint)
    {
        var a = new ACCENTPOLICY { State = 4 /* ACCENT_ENABLE_ACRYLICBLURBEHIND */, Flags = 0, Gradient = tint };
        int sz = Marshal.SizeOf<ACCENTPOLICY>();
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(a, p, false);
            var d = new WINCOMPATTRDATA { Attr = 19 /* WCA_ACCENT_POLICY */, Data = p, Size = sz };
            SetWindowCompositionAttribute(h, ref d);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Pencerenin arka plan efektini kapatır. Windhawk "translucent-windows" (Include=*) her pencereye
    /// acrylic veriyor; popup'ın saydam alanı bulanık dikdörtgen olarak görünüyordu.
    /// </summary>
    public static void NoEffect(IntPtr h)
    {
        int none = 1; DwmSetWindowAttribute(h, 38 /* DWMWA_SYSTEMBACKDROP_TYPE */, ref none, 4);
        int ncOff = 1; DwmSetWindowAttribute(h, 2 /* DWMWA_NCRENDERING_POLICY = DISABLED */, ref ncOff, 4); // çerçeve + gölge yok
        int noBorder = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(h, 34 /* DWMWA_BORDER_COLOR = NONE */, ref noBorder, 4); // saydam pencerenin 1 px çerçevesi görünüyordu
        var a = new ACCENTPOLICY { State = 0 /* ACCENT_DISABLED */ };
        int sz = Marshal.SizeOf<ACCENTPOLICY>();
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(a, p, false);
            var d = new WINCOMPATTRDATA { Attr = 19, Data = p, Size = sz };
            SetWindowCompositionAttribute(h, ref d);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>
    /// Yalnız Windhawk translucent-windows'un accent bulanıklığını kapatır; DWM çerçevesine ve köşeye dokunmaz
    /// (Fluent panelde gerçek acrylic DesktopAcrylicBackdrop'tan gelir, köşe/kenarlık DWM'nin).
    /// </summary>
    public static void NoModEffect(IntPtr h)
    {
        var a = new ACCENTPOLICY { State = 0 };
        int sz = Marshal.SizeOf<ACCENTPOLICY>();
        IntPtr p = Marshal.AllocHGlobal(sz);
        try
        {
            Marshal.StructureToPtr(a, p, false);
            var d = new WINCOMPATTRDATA { Attr = 19, Data = p, Size = sz };
            SetWindowCompositionAttribute(h, ref d);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    // ---------- kabuk sürükle-bırak (masaüstünden ikon sürüklemekle aynı mekanizma) ----------
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid bhid, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem ppv);
    [DllImport("shell32.dll")] static extern int SHDoDragDrop(IntPtr hwnd, IntPtr pdata, IntPtr pdsrc, int dwEffect, out int pdwEffect);
    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr r);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDropSource
    {
        [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool fEscapePressed, uint grfKeyState);
        [PreserveSig] int GiveFeedback(uint dwEffect);
    }

    /// <summary>
    /// Bırakma kaynağı: fare tuşunun GERÇEK durumuna bakar. Varsayılan kaynak WinUI penceresinde tuş bırakmayı
    /// görmüyordu (WinUI işaretçi girdisini kendi penceresine yönlendiriyor), sürükleme bitmiyordu.
    /// </summary>
    sealed class DropSource : IDropSource
    {
        public int QueryContinueDrag(bool esc, uint keys)
        {
            if (esc) return 0x00040101;                              // DRAGDROP_S_CANCEL
            if ((GetAsyncKeyState(0x01) & 0x8000) == 0) return 0x00040100; // DRAGDROP_S_DROP
            return 0;
        }
        public int GiveFeedback(uint eff) => 0x00040102;            // DRAGDROP_S_USEDEFAULTCURSORS
    }

    /// <summary>
    /// Dosyayı kabuk sürüklemesiyle sürükler (taşı/kopyala/kısayol). Bırakılan yer Explorer'sa (masaüstü, klasör)
    /// dosyayı oraya kendisi taşır. Fare sol tuşu basılıyken UI iş parçacığından çağrılmalı; bırakılana dek döner.
    /// </summary>
    public static int DragFile(IntPtr hwnd, string path)
    {
        OleInitialize(IntPtr.Zero);
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItem).GUID, out IShellItem item);
        Guid bhid = new("B8C0BD9F-ED24-455c-83E6-D5390C4FE8C4");          // BHID_DataObject
        Guid iidData = new("0000010e-0000-0000-C000-000000000046");       // IDataObject
        item.BindToHandler(IntPtr.Zero, bhid, iidData, out IntPtr data);
        IntPtr src = Marshal.GetComInterfaceForObject(new DropSource(), typeof(IDropSource));
        ReleaseCapture();                                           // WinUI'nin fare yakalamasını bırak
        // WinUI süreci fareyi "pointer" moduna alıyor (EnableMouseInPointer); OLE sürükleme döngüsü WM_MOUSE*
        // beklediği için fare hareketini/bırakmayı hiç görmüyor. Döngüyü ~25 ms'de bir Shift ile (bırakırken taşı demek)
        // uyandır: her uyanışta imleç yerini ve tuş durumunu okur, hedefi günceller, tuş bırakılınca bırakır.
        bool done = false;
        var pulse = new Thread(() =>
        {
            while (!Volatile.Read(ref done))
            {
                keybd_event(0x10, 0, 0, UIntPtr.Zero);
                keybd_event(0x10, 0, 2, UIntPtr.Zero);
                Thread.Sleep(25);
            }
        }) { IsBackground = true };
        pulse.Start();
        try { SHDoDragDrop(hwnd, data, src, 1 | 2 | 4 /* COPY|MOVE|LINK */, out int eff); return eff; }
        finally { Volatile.Write(ref done, true); Marshal.Release(data); Marshal.Release(src); }
    }

    public static void Round(IntPtr h) { int v = 2; DwmSetWindowAttribute(h, 33 /* CORNER_PREFERENCE */, ref v, 4); }
    public static void Cloak(IntPtr h, bool on) { int v = on ? 1 : 0; DwmSetWindowAttribute(h, 13 /* DWMWA_CLOAK */, ref v, 4); }
}

/// <summary>
/// Panelden dışarı sürüklenen uygulamanın imleci izleyen ikonu. Katmanlı, tıklamayı geçiren (WindowFromPoint onu
/// atlar) en üstteki küçük pencere. Windows'un OLE sürüklemesi WinUI sürecinde fare olaylarını görmediği için
/// sürükleme elle yapılıyor.
/// </summary>
sealed class DragGhost
{
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biX, biY, biClrUsed, biClrImportant; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);

    static WndProc? _proc;
    readonly IntPtr _hwnd;
    byte[] _px = Array.Empty<byte>();
    int _w, _h;

    public DragGhost()
    {
        _proc ??= (h, m, w, l) => DefWindowProcW(h, m, w, l);
        var wc = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc), hInstance = GetModuleHandleW(null), lpszClassName = "GrupPopup.DragGhost" };
        RegisterClassExW(ref wc);
        _hwnd = CreateWindowExW(0x80000 | 0x80 | 0x08000000 | 0x20 | 0x8 /* LAYERED|TOOLWINDOW|NOACTIVATE|TRANSPARENT|TOPMOST */,
            "GrupPopup.DragGhost", "", 0x80000000, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
    }

    /// <summary>Premultiplied BGRA kaynaktan size x size ikon hazırla (alan ortalamasıyla küçült, %85 opak).</summary>
    public void SetImage(Pix? p, int size)
    {
        _w = _h = size;
        _px = new byte[size * size * 4];
        if (p == null) return;
        byte[] src = (byte[])p.Bgra.Clone();
        if (!p.Premultiplied) Icons.EnsurePremultiplied(src);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int sx0 = x * p.W / size, sx1 = Math.Max(sx0 + 1, (x + 1) * p.W / size);
                int sy0 = y * p.H / size, sy1 = Math.Max(sy0 + 1, (y + 1) * p.H / size);
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                    for (int xx = sx0; xx < sx1; xx++) { int j = (yy * p.W + xx) * 4; b += src[j]; g += src[j + 1]; r += src[j + 2]; a += src[j + 3]; n++; }
                int o = (y * size + x) * 4;
                _px[o] = (byte)(b / n * 85 / 100); _px[o + 1] = (byte)(g / n * 85 / 100); _px[o + 2] = (byte)(r / n * 85 / 100); _px[o + 3] = (byte)(a / n * 85 / 100);
            }
    }

    public void MoveTo(int cx, int cy)
    {
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = _w, biHeight = -_h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero), dc = CreateCompatibleDC(screen);
        IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(_px, 0, bits, _px.Length);
        IntPtr old = SelectObject(dc, bmp);
        var dst = new POINT { X = cx - _w / 2, Y = cy - _h / 2 };
        var size = new SIZE { cx = _w, cy = _h };
        var src = new POINT();
        var blend = new BLENDFUNCTION { Alpha = 255, Format = 1 };
        UpdateLayeredWindow(_hwnd, screen, ref dst, ref size, dc, ref src, 0, ref blend, 2);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);
        SetWindowPos(_hwnd, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40);
    }

    public void Hide() => ShowWindow(_hwnd, 0);
}

/// <summary>
/// WinUI penceresini tamamen saydam yapar (sistem arka planı = saydam fırça); bulanıklığı Native.Acrylic verir.
/// Böylece gerçek panel ile açılış/kapanış hayaleti birebir aynı acrylic'i kullanır.
/// </summary>
sealed class TransparentBackdrop : SystemBackdrop
{
    [StructLayout(LayoutKind.Sequential)] struct DispatcherQueueOptions { public int dwSize, threadType, apartmentType; }
    [DllImport("CoreMessaging.dll")] static extern int CreateDispatcherQueueController(DispatcherQueueOptions o, out IntPtr ctrl);
    static Windows.UI.Composition.Compositor? _comp;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
            CreateDispatcherQueueController(new DispatcherQueueOptions { dwSize = 12, threadType = 2, apartmentType = 2 }, out _);
        _comp ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _comp.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }
}

