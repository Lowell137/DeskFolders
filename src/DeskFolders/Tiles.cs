using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Graphics;

namespace GrupPopup;

/// <summary>
/// Masaüstü klasör kutuları (telefondaki klasör gibi, Windows ikonu değil).
/// - Kutu: üst seviye pencere, arkası Windows acrylic bulanıklığı (accent policy), DWM yuvarlak köşe, 2x2 mini ikon.
///   Altında ad: ayrı katmanlı pencere (blur dikdörtgeni adın arkasına taşmasın).
/// - Masaüstü ikonu gibi davranır: Explorer'ın ikon ızgarasına (LVM_GETITEMSPACING) oturur, dolu hücreye
///   (başka kutu ya da masaüstü ikonu) konamaz, en yakın boş hücreye kayar. Varsayılan kilitli; sağ tık → Kilidi aç.
/// - Z sırası: Progman'ın hemen üstü (uygulamaların altı). 25H2'de Progman'ın içine çocuk koymak çizilmiyor
///   (NOREDIRECTIONBITMAP). Win+D'de masaüstü öne gelince kutular TOPMOST olur, başka uygulama gelince iner.
/// Kendi iş parçacığı ve mesaj döngüsü var.
/// </summary>
static class Tiles
{
    // ---------------- Win32 ----------------
    delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr w, l; public uint time; public POINT pt; public uint priv; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwnd; public uint hover; }
    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biX, biY, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct ACCENTPOLICY { public int State, Flags; public uint Gradient; public int AnimId; }
    [StructLayout(LayoutKind.Sequential)] struct WINCOMPATTRDATA { public int Attr; public IntPtr Data; public int Size; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32.dll")] static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr SetCapture(IntPtr h);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenuW(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr m, uint flags, int x, int y, IntPtr h, IntPtr p);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, IntPtr ps);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, IntPtr ps);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, ref SIZE size, IntPtr hdcSrc, ref POINT ptSrc, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int DrawTextW(IntPtr dc, string s, int n, ref RECT r, uint fmt);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WINCOMPATTRDATA d);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc fn, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(IntPtr dc, uint c);
    [DllImport("gdi32.dll")] static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr n);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr n);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const uint WS_POPUP = 0x80000000;
    const uint WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint WM_PAINT = 0xF, WM_ERASEBKGND = 0x14, WM_TIMER = 0x113, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201,
               WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_CAPTURECHANGED = 0x215, WM_MOUSELEAVE = 0x2A3, WM_MOUSEACTIVATE = 0x21;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    const uint TintNormal = 0x26F4F4F4, TintHover = 0x3CF4F4F4; // AABBGGRR: Başlat klasör plakası gibi açık, yarı saydam

    // ---------------- durum ----------------
    sealed class Tile
    {
        public string Folder = "", Name = "", Sig = "";
        public IntPtr Box, Label;
        public int Col, Row;                        // ızgara hücresi
        public int BX, BY, B, LX, LY, LW, LH;       // ekran pikseli: kutu ve ad
        public double Scale = 1;
        public byte[]? Pix, PixHover, LabelPix, LabelPixHover;
        public bool Hovered, Down, Dragging, Hidden;
        public int CellX, CellY;
        public long RevealAt; public double Alpha = 1;
        public POINT DownPt; public int DownBX, DownBY;
    }

    readonly record struct Grid(IntPtr List, int OX, int OY, int CX, int CY, int Cols, int Rows);

    static readonly Dictionary<IntPtr, Tile> _byHwnd = new();
    static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, string> _folderOf = new();
    static readonly Dictionary<string, Tile> _byName = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int[]> _pos = new(StringComparer.OrdinalIgnoreCase);
    static Grid _grid;
    static string _root = "";
    static IntPtr _inst;
    static bool _raised, _unlocked;
    static WndProc? _proc;
    static WinEventProc? _fgProc;
    static Action<ShowRequest> _show = _ => { };
    /// <summary>Bu pencere bir klasör kutusuysa (kutu ya da adı) klasör yolu.</summary>
    public static string? FolderAt(IntPtr h)
    {
        // UI iş parçacığından çağrılır; kutu sözlüğü başka iş parçacığında değiştiği için ayrı eşzamanlı eşleme
        return _folderOf.TryGetValue(h, out string? f) ? f : null;
    }

    /// <summary>Açık popup penceresi (yoksa 0); PopupHost bağlar.</summary>
    public static Func<IntPtr> PopupHwnd = () => IntPtr.Zero;
    static uint _tid;
    static UIntPtr _fadeTimer;
    static readonly System.Collections.Concurrent.ConcurrentQueue<Action> _ops = new();
    const uint WM_APP_OP = 0x8000 + 31;
    [DllImport("user32.dll")] static extern bool PostThreadMessageW(uint tid, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    static void Post(Action a) { _ops.Enqueue(a); PostThreadMessageW(_tid, WM_APP_OP, IntPtr.Zero, IntPtr.Zero); }
    static string Key(string folder) => Path.GetFileName(folder.TrimEnd('\\', '/'));

    /// <summary>Popup açılınca: Başlat'taki gibi klasör kutusu görünmez olur.</summary>
    public static void Hide(string folder) => Post(() =>
    {
        if (!_byName.TryGetValue(Key(folder), out Tile? t)) return;
        t.Hidden = true; t.RevealAt = 0;
        if (t.Hovered) SetHover(t, false, t.Box);
        ShowWindow(t.Box, 0); ShowWindow(t.Label, 0);
    });

    /// <summary>Popup kapanınca: kayıttaki gibi ~100 ms boş kalır, sonra 3 karede (%30, %80, %100) solarak gelir.</summary>
    public static void Reveal(string folder) => Post(() =>
    {
        if (!_byName.TryGetValue(Key(folder), out Tile? t)) return;
        t.RevealAt = Environment.TickCount64 + 100;
        if (_fadeTimer == UIntPtr.Zero) _fadeTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 15, IntPtr.Zero);
    });

    public static void Refresh() => Post(() => { foreach (Tile t in _byName.Values) t.Sig = ""; Sync(); });

    public static void Rename(string oldName, string newName) => Post(() =>
    {
        if (_pos.Remove(oldName, out int[]? p)) { _pos[newName] = p; SavePos(); }
        if (_byName.TryGetValue(oldName, out Tile? t))
        {
            bool hidden = t.Hidden;
            Destroy(t);
            Sync();
            if (hidden && _byName.TryGetValue(newName, out Tile? n)) { n.Hidden = true; ShowWindow(n.Box, 0); ShowWindow(n.Label, 0); }
        }
    });

    static void FadeTick()
    {
        long now = Environment.TickCount64;
        bool any = false;
        foreach (Tile t in _byName.Values)
        {
            if (t.RevealAt == 0) continue;
            long dt = now - t.RevealAt;
            if (dt < 0) { any = true; continue; }
            double a = dt < 33 ? 0.3 : dt < 67 ? 0.8 : 1.0;
            SetAlpha(t, a);
            if (t.Hidden) { t.Hidden = false; Place(t); }
            if (a >= 1.0) t.RevealAt = 0; else any = true;
        }
        if (!any && _fadeTimer != UIntPtr.Zero) { KillTimer(IntPtr.Zero, _fadeTimer); _fadeTimer = UIntPtr.Zero; }
    }

    static void SetAlpha(Tile t, double a)
    {
        t.Alpha = a;
        SetLayeredWindowAttributes(t.Box, 0, (byte)Math.Round(255 * a), 2 /* LWA_ALPHA */);
        PushLabel(t);
    }

    const string LockKey = "_kilitli";
    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrupPopup");
    static string PosFile => Path.Combine(Dir, "tiles.json");

    public static void Start(string root, Action<ShowRequest> show)
    {
        _root = root; _show = show;
        var t = new Thread(Run) { IsBackground = true, Name = "Tiles" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    static void Run()
    {
        _proc = Proc;
        _inst = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = _inst,
            hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512), // IDC_ARROW: masaüstü ikonu gibi
            lpszClassName = "GrupPopup.Tile",
        };
        RegisterClassExW(ref wc);
        _tid = GetCurrentThreadId();
        LoadPos();
        _raised = IsDesktop(GetForegroundWindow());
        try { Sync(); } catch (Exception e) { Log(e); }
        _fgProc = OnForeground;
        SetWinEventHook(3, 3 /* EVENT_SYSTEM_FOREGROUND */, IntPtr.Zero, _fgProc, 0, 0, 0);
        SetTimer(IntPtr.Zero, UIntPtr.Zero, 2000, IntPtr.Zero);
        while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
        {
            if (m.hwnd == IntPtr.Zero && m.message == WM_APP_OP)
            {
                while (_ops.TryDequeue(out Action? a)) { try { a(); } catch (Exception e) { Log(e); } }
                continue;
            }
            if (m.hwnd == IntPtr.Zero && m.message == WM_TIMER)
            {
                try { if (_fadeTimer != UIntPtr.Zero && (ulong)m.w == (ulong)_fadeTimer) FadeTick(); else Sync(); }
                catch (Exception e) { Log(e); }
                continue;
            }
            TranslateMessage(ref m);
            DispatchMessageW(ref m);
        }
    }

    static void Log(Exception e)
    {
        try { File.AppendAllText(Path.Combine(Dir, "crash.log"), $"{DateTime.Now:s} [tiles] {e}\n\n"); } catch { }
    }

    // ---------------- masaüstü ızgarası ----------------

    static IntPtr DesktopList()
    {
        IntPtr dv = FindWindowExW(FindWindowW("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (dv == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowExW(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
                if ((dv = FindWindowExW(w, IntPtr.Zero, "SHELLDLL_DefView", null)) != IntPtr.Zero) break;
        }
        return dv == IntPtr.Zero ? IntPtr.Zero : FindWindowExW(dv, IntPtr.Zero, "SysListView32", null);
    }

    static Grid ReadGrid()
    {
        IntPtr list = DesktopList();
        int cx = 76, cy = 83;
        var o = new POINT();
        if (list != IntPtr.Zero)
        {
            long sp = (long)SendMessageW(list, 0x1033 /* LVM_GETITEMSPACING */, IntPtr.Zero, IntPtr.Zero);
            if ((sp & 0xFFFF) > 0) { cx = (int)(sp & 0xFFFF); cy = (int)((sp >> 16) & 0xFFFF); }
            ClientToScreen(list, ref o);
        }
        int sw = GetSystemMetrics(78), sh = GetSystemMetrics(79); // sanal ekran
        return new Grid(list, o.X, o.Y, cx, cy, Math.Max(1, sw / cx), Math.Max(1, sh / cy));
    }

    /// <summary>Masaüstü ikonlarının oturduğu hücreler (Explorer belleğinden okunur).</summary>
    static HashSet<(int, int)> IconCells()
    {
        var res = new HashSet<(int, int)>();
        IntPtr list = _grid.List;
        if (list == IntPtr.Zero) return res;
        GetWindowThreadProcessId(list, out uint pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid);
        if (proc == IntPtr.Zero) return res;
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)64, 0x3000, 0x04);
        try
        {
            if (remote == IntPtr.Zero) return res;
            int n = (int)SendMessageW(list, 0x1004 /* LVM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero);
            var rb = new byte[16];
            for (int i = 0; i < n; i++)
            {
                WriteProcessMemory(proc, remote, new byte[16], 16, out _);              // LVIR_BOUNDS
                SendMessageW(list, 0x100E /* LVM_GETITEMRECT */, (IntPtr)i, remote);
                ReadProcessMemory(proc, remote, rb, 16, out _);
                int cxm = (BitConverter.ToInt32(rb, 0) + BitConverter.ToInt32(rb, 8)) / 2;
                int cym = (BitConverter.ToInt32(rb, 4) + BitConverter.ToInt32(rb, 12)) / 2;
                res.Add((cxm / _grid.CX, cym / _grid.CY));
            }
        }
        finally { if (remote != IntPtr.Zero) VirtualFreeEx(proc, remote, UIntPtr.Zero, 0x8000); CloseHandle(proc); }
        return res;
    }

    /// <summary>(col,row)'a en yakın boş hücre (masaüstü ikonu ya da başka kutu yok).</summary>
    static (int, int) NearestFree(int col, int row, Tile? self)
    {
        var busy = IconCells();
        foreach (Tile t in _byName.Values) if (t != self) busy.Add((t.Col, t.Row));
        col = Math.Clamp(col, 0, _grid.Cols - 1); row = Math.Clamp(row, 0, _grid.Rows - 1);
        (int, int) best = (col, row); int bestD = int.MaxValue;
        for (int c = 0; c < _grid.Cols; c++)
            for (int r = 0; r < _grid.Rows; r++)
            {
                if (busy.Contains((c, r))) continue;
                int d = (c - col) * (c - col) * _grid.CX * _grid.CX + (r - row) * (r - row) * _grid.CY * _grid.CY;
                if (d < bestD) { bestD = d; best = (c, r); }
            }
        return best;
    }

    /// <summary>Yeni grup: masaüstü gibi sütun sütun ilk boş hücre.</summary>
    static (int, int) FirstFree()
    {
        var busy = IconCells();
        foreach (Tile t in _byName.Values) busy.Add((t.Col, t.Row));
        for (int c = 0; c < _grid.Cols; c++)
            for (int r = 0; r < _grid.Rows; r++)
                if (!busy.Contains((c, r))) return (c, r);
        return (0, 0);
    }

    // ---------------- senkron ----------------

    static void Sync()
    {
        if (_byName.Values.Any(t => t.Down)) return;   // sürüklerken dokunma

        Grid g = ReadGrid();
        bool gridChanged = g.CX != _grid.CX || g.CY != _grid.CY || g.OX != _grid.OX || g.OY != _grid.OY;
        _grid = g;

        var groups = Directory.Exists(_root)
            ? Directory.GetDirectories(_root)
                .Where(d => !Path.GetFileName(d).StartsWith('_') && (File.GetAttributes(d) & FileAttributes.Hidden) == 0)
                .OrderBy(d => d, StringComparer.CurrentCultureIgnoreCase).ToList()
            : new List<string>();

        foreach (string gone in _byName.Keys.Where(n => !groups.Any(x => Path.GetFileName(x).Equals(n, StringComparison.OrdinalIgnoreCase))).ToList())
            Destroy(_byName[gone]);

        foreach (string folder in groups)
        {
            string name = Path.GetFileName(folder);
            string sig = Icons.Signature(folder);
            if (!_byName.TryGetValue(name, out Tile? t) || !IsWindow(t.Box) || !IsWindow(t.Label))
            {
                if (t != null) Destroy(t);
                t = new Tile { Folder = folder, Name = name };
                if (_pos.TryGetValue(name, out int[]? p) && p.Length == 2) (t.Col, t.Row) = NearestFree(p[0], p[1], t);
                else (t.Col, t.Row) = FirstFree();
                _byName[name] = t;
                Create(t);
                t.Sig = sig;
                Remember(t);
            }
            else if (t.Sig != sig || gridChanged)
            {
                t.Sig = sig;
                Layout(t); Render(t); Place(t);
                InvalidateRect(t.Box, IntPtr.Zero, false);
                PushLabel(t);
            }
        }
    }

    static void Destroy(Tile t)
    {
        if (IsWindow(t.Box)) DestroyWindow(t.Box);
        if (IsWindow(t.Label)) DestroyWindow(t.Label);
        _byHwnd.Remove(t.Box); _byHwnd.Remove(t.Label); _byName.Remove(t.Name);
        _folderOf.TryRemove(t.Box, out _); _folderOf.TryRemove(t.Label, out _);
    }

    static double DpiAt(int x, int y) =>
        GetDpiForMonitor(MonitorFromPoint(new POINT { X = x, Y = y }, 2), 0, out uint dx, out _) == 0 && dx > 0 ? dx / 96.0 : 1.0;

    /// <summary>Hücreden kutu ve ad dikdörtgenleri.</summary>
    static void Layout(Tile t)
    {
        int x = _grid.OX + t.Col * _grid.CX, y = _grid.OY + t.Row * _grid.CY;
        t.Scale = DpiAt(x + _grid.CX / 2, y + _grid.CY / 2);
        double s = t.Scale;
        int textH = (int)Math.Round(18 * s);
        t.B = Math.Min((int)Math.Round(56 * s), Math.Min(_grid.CX - (int)(14 * s), _grid.CY - textH - (int)(9 * s)));
        t.BX = x + (_grid.CX - t.B) / 2;
        t.BY = y + (int)Math.Round(5 * s);
        t.CellX = x; t.CellY = y;
        // Ad katmanı bütün hücreyi kaplar: üzerine gelince Başlat'taki gibi açık vurgu plakası çizer.
        // Ad hücreden biraz taşabilir (masaüstü ikon yazısı gibi), yoksa "Web ve Medya" kesiliyor.
        int extra = (int)Math.Round(14 * s);
        t.LX = x - extra; t.LW = _grid.CX + 2 * extra;
        t.LY = y; t.LH = _grid.CY;
    }

    static void Create(Tile t)
    {
        Layout(t);
        t.Box = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "GrupPopup.Tile", t.Name, WS_POPUP,
            t.BX, t.BY, t.B, t.B, IntPtr.Zero, IntPtr.Zero, _inst, IntPtr.Zero);
        t.Label = CreateWindowExW(WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "GrupPopup.Tile", t.Name + " (ad)", WS_POPUP,
            t.LX, t.LY, t.LW, t.LH, IntPtr.Zero, IntPtr.Zero, _inst, IntPtr.Zero);
        _byHwnd[t.Box] = t; _byHwnd[t.Label] = t;
        _folderOf[t.Box] = t.Folder; _folderOf[t.Label] = t.Folder;
        DragAcceptFiles(t.Box, true); DragAcceptFiles(t.Label, true);   // uygulamayı kutuya bırak → gruba ekle

        int round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(t.Box, 33, ref round, sizeof(int));
        SetLayeredWindowAttributes(t.Box, 0, 255, 2 /* LWA_ALPHA: kapanış sonrası solma için */);
        SetAccent(t.Box, TintNormal);

        Render(t);
        PushLabel(t);
        Place(t);
    }

    /// <summary>Kutunun arkasına acrylic bulanıklık (Windows'un görev çubuğu/menülerde kullandığı accent).</summary>
    static void SetAccent(IntPtr h, uint tint)
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

    // ---------------- z sırası ----------------

    static bool IsDesktop(IntPtr h) { string c = ClassOf(h); return c is "Progman" or "WorkerW"; }
    static string ClassOf(IntPtr h) { var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); return sb.ToString(); }
    static string TitleOf(IntPtr h) { var sb = new StringBuilder(64); GetWindowTextW(h, sb, 64); return sb.ToString(); }

    static IntPtr AboveDesktop()
    {
        IntPtr prog = FindWindowW("Progman", null);
        if (prog == IntPtr.Zero) return IntPtr.Zero;
        IntPtr h = GetWindow(prog, 3 /* GW_HWNDPREV */);
        while (h != IntPtr.Zero && _byHwnd.ContainsKey(h)) h = GetWindow(h, 3);
        return h;
    }

    static void Place(Tile t)
    {
        uint f = SWP_NOACTIVATE | SWP_SHOWWINDOW;
        if (t.Hidden) f = SWP_NOACTIVATE;   // popup açıkken görünmez kalsın
        if (_raised)
        {
            // Popup açıksa onun hemen altına (yenilemede kutular popup'ın üstüne çıkıp onu eziyordu)
            IntPtr pop = PopupHwnd();
            SetWindowPos(t.Box, pop != IntPtr.Zero ? pop : (IntPtr)(-1) /* TOPMOST */, t.BX, t.BY, t.B, t.B, f);
        }
        else
        {
            // Masaüstünün hemen üstündeki pencerenin altına gir. O pencere yükseltilmiş bir sürece aitse Windows
            // reddediyor ve aynı çağrıdaki "göster" de uygulanmıyordu (kutu gizli kalıyordu): sıradakini dene,
            // olmazsa en azından göster.
            IntPtr after = AboveDesktop();
            bool ok = false;
            for (int i = 0; i < 8 && after != IntPtr.Zero && !ok; i++)
            {
                ok = SetWindowPos(t.Box, after, t.BX, t.BY, t.B, t.B, f);
                if (!ok) { after = GetWindow(after, 3 /* GW_HWNDPREV */); while (after != IntPtr.Zero && _byHwnd.ContainsKey(after)) after = GetWindow(after, 3); }
            }
            if (!ok) SetWindowPos(t.Box, IntPtr.Zero, t.BX, t.BY, t.B, t.B, f | SWP_NOZORDER);
        }
        SetWindowPos(t.Label, t.Box, t.LX, t.LY, t.LW, t.LH, f);   // ad/vurgu katmanı kutunun hemen altında
    }

    static void MoveOnly(Tile t)
    {
        SetWindowPos(t.Box, IntPtr.Zero, t.BX, t.BY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(t.Label, IntPtr.Zero, t.LX, t.LY, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    static void OnForeground(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
    {
        try
        {
            if (h == IntPtr.Zero || _byHwnd.ContainsKey(h) || TitleOf(h) == "GrupPopup") return;
            bool desktop = IsDesktop(h);
            if (desktop == _raised) return;
            _raised = desktop;
            foreach (Tile t in _byName.Values)
            {
                if (!desktop)
                {
                    SetWindowPos(t.Box, (IntPtr)(-2) /* NOTOPMOST */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                    SetWindowPos(t.Label, (IntPtr)(-2), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
                }
                Place(t);
            }
        }
        catch (Exception e) { Log(e); }
    }

    // ---------------- pencere mesajları ----------------

    static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (_byHwnd.TryGetValue(h, out Tile? t))
            {
                switch (msg)
                {
                    case WM_MOUSEACTIVATE: return (IntPtr)3; // MA_NOACTIVATE
                    case WM_ERASEBKGND: return (IntPtr)1;
                    case WM_PAINT:
                        if (h == t.Box) { PaintBox(t); return IntPtr.Zero; }
                        break;
                    case WM_LBUTTONDOWN:
                        t.Down = true; t.Dragging = false;
                        GetCursorPos(out t.DownPt); t.DownBX = t.BX; t.DownBY = t.BY;
                        SetCapture(h);
                        return IntPtr.Zero;
                    case WM_MOUSEMOVE:
                        if (!t.Hovered) SetHover(t, true, h);
                        if (t.Down)
                        {
                            GetCursorPos(out POINT p);
                            int dx = p.X - t.DownPt.X, dy = p.Y - t.DownPt.Y;
                            bool far = Math.Abs(dx) > GetSystemMetrics(68) || Math.Abs(dy) > GetSystemMetrics(69);
                            if (!t.Dragging && far)
                            {
                                if (_unlocked) t.Dragging = true;
                                else { t.Down = false; ReleaseCapture(); } // kilitli: taşıma yok, tık da sayılmaz
                            }
                            if (t.Dragging)
                            {
                                int ox = t.DownBX + dx - t.BX, oy = t.DownBY + dy - t.BY;
                                t.BX += ox; t.BY += oy; t.LX += ox; t.LY += oy;
                                MoveOnly(t);
                            }
                        }
                        return IntPtr.Zero;
                    case WM_LBUTTONUP:
                    {
                        bool click = t.Down && !t.Dragging, dragged = t.Down && t.Dragging;
                        t.Down = false; t.Dragging = false;
                        ReleaseCapture();
                        if (dragged) Drop(t);
                        else if (click) Open(t);
                        return IntPtr.Zero;
                    }
                    case WM_CAPTURECHANGED:
                        if (t.Down && t.Dragging) { t.Down = false; t.Dragging = false; Drop(t); }
                        t.Down = false;
                        return IntPtr.Zero;
                    case WM_TIMER:
                        if ((ulong)w == 7 && !t.Down && !CursorInCell(t)) SetHover(t, false, h);
                        return IntPtr.Zero;
                    case 0x233: /* WM_DROPFILES */
                        OnDrop(t, w);
                        return IntPtr.Zero;
                    case WM_RBUTTONUP:
                        Menu(t, h);
                        return IntPtr.Zero;
                }
            }
        }
        catch (Exception e) { Log(e); }
        return DefWindowProcW(h, msg, w, l);
    }

    static void SetHover(Tile t, bool on, IntPtr from)
    {
        if (on) SetTimer(t.Box, (UIntPtr)7, 40, IntPtr.Zero);   // iki pencere var: çıkışı imleç yoklamasıyla anla
        else KillTimer(t.Box, (UIntPtr)7);
        if (t.Hovered == on) return;
        t.Hovered = on;
        SetAccent(t.Box, on ? TintHover : TintNormal);
        InvalidateRect(t.Box, IntPtr.Zero, false);
        PushLabel(t);
    }

    static bool CursorInCell(Tile t)
    {
        GetCursorPos(out POINT p);
        return p.X >= t.CellX && p.X < t.CellX + _grid.CX && p.Y >= t.CellY && p.Y < t.CellY + _grid.CY;
    }

    /// <summary>Bırakılan yerin en yakın boş hücresine otur (masaüstü ikonu gibi).</summary>
    static void Drop(Tile t)
    {
        int cxm = t.BX + t.B / 2 - _grid.OX, cym = t.BY + t.B / 2 - _grid.OY;
        int col = (int)Math.Floor((double)cxm / _grid.CX), row = (int)Math.Floor((double)cym / _grid.CY);
        (t.Col, t.Row) = NearestFree(col, row, t);
        double oldScale = t.Scale;
        Layout(t);
        if (Math.Abs(oldScale - t.Scale) > 0.01) { Render(t); PushLabel(t); InvalidateRect(t.Box, IntPtr.Zero, false); }
        Place(t);
        Remember(t);
    }

    [DllImport("shell32.dll")] static extern void DragAcceptFiles(IntPtr h, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(IntPtr drop, uint i, StringBuilder? sb, uint n);
    [DllImport("shell32.dll")] static extern void DragFinish(IntPtr drop);

    static void OnDrop(Tile t, IntPtr drop)
    {
        var paths = new List<string>();
        uint n = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < n; i++)
        {
            var sb = new StringBuilder(1024);
            DragQueryFileW(drop, i, sb, (uint)sb.Capacity);
            paths.Add(sb.ToString());
        }
        DragFinish(drop);
        // Grup klasörü (kutu) bir kutuya bırakılmışsa yok say
        if (Groups.Add(t.Folder, paths) > 0) { t.Sig = ""; Sync(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);

    /// <summary>Sağ tık → Delete folder: onaydan sonra uygulamalar masaüstüne döner, klasör ve kutusu kalkar.</summary>
    static void DeleteGroup(Tile t)
    {
        int r = MessageBoxW(t.Box, $"Delete the folder \"{t.Name}\"?\nIts apps will be kept in a regular folder on the desktop.", "Delete folder",
            0x4 /* YESNO */ | 0x30 /* WARNING */ | 0x40000 /* TOPMOST */);
        if (r != 6 /* IDYES */) return;
        if (!Groups.Dissolve(t.Folder))
        {
            MessageBoxW(t.Box, "The folder could not be moved to the desktop, so it was kept.", "Delete folder", 0x10 | 0x40000);
            t.Sig = "";
        }
        else { _pos.Remove(t.Name); SavePos(); }
        Sync();
    }

    /// <summary>
    /// Telefondaki gibi: masaüstünde bir uygulama başka bir uygulamanın üstüne bırakıldı → ikisini içeren yeni klasör,
    /// hedef ikonun yerinde; açılır ve adı düzenlemeye hazır gelir.
    /// </summary>
    public static void NewGroupFromApps(string[] apps, PointInt32 at) => Post(() =>
    {
        string folder = Groups.New(_root);
        string name = Path.GetFileName(folder);
        Groups.Add(folder, apps);
        int col = Math.Clamp((at.X - _grid.OX) / Math.Max(1, _grid.CX), 0, _grid.Cols - 1);
        int row = Math.Clamp((at.Y - _grid.OY) / Math.Max(1, _grid.CY), 0, _grid.Rows - 1);
        (col, row) = NearestFree(col, row, null);   // masaüstü ikonu ya da başka kutu olan hücreye konmasın
        _pos[name] = new[] { col, row };
        SavePos();
        Sync();
        if (_byName.TryGetValue(name, out Tile? t))
        {
            GetCursorPos(out POINT p);
            _show(new ShowRequest(t.Folder, new RectInt32(t.BX, t.BY, t.B, (int)(t.B / 0.72)), new PointInt32(p.X, p.Y), Rename: true));
        }
    });

    /// <summary>Sağ tık → Yeni klasör: yanındaki ilk boş hücreye kutu, açılıp adı düzenlemeye hazır.</summary>
    static void NewGroupNear(Tile near)
    {
        string folder = Groups.New(_root);
        string name = Path.GetFileName(folder);
        var (c, r) = NearestFree(near.Col + 1, near.Row, null);
        _pos[name] = new[] { c, r };
        SavePos();
        Sync();
        if (_byName.TryGetValue(name, out Tile? t))
        {
            GetCursorPos(out POINT p);
            _show(new ShowRequest(t.Folder, new RectInt32(t.BX, t.BY, t.B, (int)(t.B / 0.72)), new PointInt32(p.X, p.Y), Rename: true));
        }
    }

    static void Remember(Tile t)
    {
        _pos[t.Name] = new[] { t.Col, t.Row };
        SavePos();
    }

    static void Open(Tile t)
    {
        GetCursorPos(out POINT c);
        // PopupWindow merkezi yüksekliğin %36'sında alır: kutunun ortası olsun.
        var rect = new RectInt32(t.BX, t.BY, t.B, (int)(t.B / 0.72));
        _show(new ShowRequest(t.Folder, rect, new PointInt32(c.X, c.Y)));
    }

    static void Menu(Tile t, IntPtr h)
    {
        IntPtr m = CreatePopupMenu();
        AppendMenuW(m, 0, (UIntPtr)1, "Open");
        AppendMenuW(m, 0, (UIntPtr)2, "Show in File Explorer");
        AppendMenuW(m, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        AppendMenuW(m, _unlocked ? 0 : 0x8u /* MF_CHECKED */, (UIntPtr)3, "Lock positions");
        AppendMenuW(m, 0, (UIntPtr)4, "Refresh");
        AppendMenuW(m, 0x800 /* MF_SEPARATOR */, UIntPtr.Zero, null);
        AppendMenuW(m, 0, (UIntPtr)6, "Delete folder");
        GetCursorPos(out POINT p);
        SetForegroundWindow(h);
        int cmd = TrackPopupMenuEx(m, 0x100 /* TPM_RETURNCMD */ | 0x2 /* TPM_RIGHTBUTTON */, p.X, p.Y, h, IntPtr.Zero);
        DestroyMenu(m);
        switch (cmd)
        {
            case 1: Open(t); break;
            case 2:
                try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + t.Folder + "\"") { UseShellExecute = true }); } catch { }
                break;
            case 3:
                _unlocked = !_unlocked;
                _pos[LockKey] = new[] { _unlocked ? 0 : 1, 0 };
                SavePos();
                break;
            case 6:
                DeleteGroup(t);
                break;
            case 4:
                foreach (Tile x in _byName.Values) x.Sig = "";
                Sync();
                break;
        }
    }

    // ---------------- konum dosyası: { "Grup": [sütun, satır], "_kilit_acik": [0|1, 0] } ----------------

    static void LoadPos()
    {
        try
        {
            if (File.Exists(PosFile))
                _pos = new Dictionary<string, int[]>(JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(PosFile)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        _pos.Remove("_kilit_acik");   // eski anahtar (eskiden varsayılan kilitliydi)
        _unlocked = !(_pos.TryGetValue(LockKey, out int[]? k) && k.Length > 0 && k[0] == 1);
    }

    static void SavePos()
    {
        try { Directory.CreateDirectory(Dir); File.WriteAllText(PosFile, JsonSerializer.Serialize(_pos, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { Log(e); }
    }

    // ---------------- çizim (premultiplied BGRA, üstten aşağı) ----------------

    static void Render(Tile t)
    {
        List<ItemData> items;
        try { items = Icons.GetData(t.Folder); } catch { items = new(); }
        t.Pix = ComposeBox(t, items, false);
        t.PixHover = ComposeBox(t, items, true);
        t.LabelPix = ComposeLabel(t, false);
        t.LabelPixHover = ComposeLabel(t, true);
    }

    static byte[] ComposeBox(Tile t, List<ItemData> items, bool hover)
    {
        int S = t.B;
        double sc = t.Scale;
        var b = new byte[S * S * 4];
        // Başlat klasör ikonu: 2x2, kenar boşluğu ~%11, aralık ~%5 (kayıttan)
        int pad = (int)Math.Round(S * 0.11), gap = (int)Math.Round(S * 0.05);
        int cell = (S - 2 * pad - gap) / 2;
        for (int k = 0; k < 4 && k < items.Count; k++)
        {
            Pix? p = items[k].Pix;
            if (p != null) DrawScaled(b, S, pad + (k % 2) * (cell + gap), pad + (k / 2) * (cell + gap), cell, p);
        }
        return b;
    }

    static void Over(byte[] b, int i, double r, double g, double bl, double a)
    {
        double k = 1 - a;
        b[i] = (byte)Math.Min(255, bl + b[i] * k);
        b[i + 1] = (byte)Math.Min(255, g + b[i + 1] * k);
        b[i + 2] = (byte)Math.Min(255, r + b[i + 2] * k);
        b[i + 3] = (byte)Math.Min(255, a * 255 + b[i + 3] * k);
    }

    /// <summary>96 px ikonu kutu süzgeciyle (alan ortalaması) küçültüp üstüne çizer.</summary>
    static void DrawScaled(byte[] b, int W, int dx0, int dy0, int size, Pix p)
    {
        byte[] src = (byte[])p.Bgra.Clone();
        if (!p.Premultiplied) Icons.EnsurePremultiplied(src);
        for (int y = 0; y < size; y++)
        {
            int sy0 = y * p.H / size, sy1 = Math.Max(sy0 + 1, (y + 1) * p.H / size);
            for (int x = 0; x < size; x++)
            {
                int sx0 = x * p.W / size, sx1 = Math.Max(sx0 + 1, (x + 1) * p.W / size);
                double sb = 0, sg = 0, sr = 0, sa = 0; int n = 0;
                for (int yy = sy0; yy < sy1; yy++)
                    for (int xx = sx0; xx < sx1; xx++)
                    {
                        int j = (yy * p.W + xx) * 4;
                        sb += src[j]; sg += src[j + 1]; sr += src[j + 2]; sa += src[j + 3]; n++;
                    }
                int tx = dx0 + x, ty = dy0 + y, i = (ty * W + tx) * 4;
                if (tx < 0 || ty < 0 || tx >= W || i + 3 >= b.Length) continue;
                Over(b, i, sr / n, sg / n, sb / n, sa / n / 255.0);
            }
        }
    }

    /// <summary>Ad: GDI ile siyah zemine beyaz gri tonlu metin, parlaklık alfa olur; altına yumuşak gölge (masaüstü ikonu yazısı gibi).</summary>
    static byte[] ComposeLabel(Tile t, bool hover)
    {
        int W = t.LW;
        double sc = t.Scale;
        var outp = new byte[W * t.LH * 4];
        int extra = t.CellX - t.LX;
        if (hover) RoundRectFill(outp, W, t.LH, extra + (int)(2 * sc), (int)(1 * sc), _grid.CX - (int)(4 * sc), _grid.CY - (int)(2 * sc), 4 * sc, 0.085);
        int top = t.BY + t.B + (int)Math.Round(3 * sc) - t.LY;
        int lh = Math.Max(1, Math.Min((int)Math.Round(18 * sc), t.LH - top));
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = W, biHeight = -lh, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr oldB = SelectObject(dc, bmp);
        IntPtr font = CreateFontW(-(int)Math.Round(12 * sc), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 4 /* ANTIALIASED_QUALITY */, 0, "Segoe UI");
        IntPtr oldF = SelectObject(dc, font);
        SetTextColor(dc, 0xFFFFFF);
        SetBkMode(dc, 1);
        var r = new RECT { L = 1, T = 0, R = W - 1, B = lh };
        DrawTextW(dc, t.Name, -1, ref r, 0x1 | 0x4 | 0x20 | 0x800 | 0x8000); // CENTER|VCENTER|SINGLELINE|NOPREFIX|END_ELLIPSIS
        GdiFlush();
        var px = new byte[W * lh * 4];
        Marshal.Copy(bits, px, 0, px.Length);
        SelectObject(dc, oldF); DeleteObject(font);
        SelectObject(dc, oldB); DeleteObject(bmp); DeleteDC(dc);

        double Cov(int x, int y) => x < 0 || y < 0 || x >= W || y >= lh ? 0 : px[(y * W + x) * 4 + 1] / 255.0;
        int off = Math.Max(1, (int)Math.Round(sc));
        for (int y = 0; y < lh; y++)
            for (int x = 0; x < W; x++)
            {
                int i = ((top + y) * W + x) * 4;
                double s = 0;
                for (int k = -1; k <= 1; k++) for (int m = -1; m <= 1; m++) s += Cov(x + m, y - off + k);
                double sa = Math.Min(1, s / 9 * 1.6) * 0.75;
                if (sa > 0) Over(outp, i, 0, 0, 0, sa);
                double ta = Cov(x, y);
                if (ta > 0) Over(outp, i, 255 * ta, 255 * ta, 255 * ta, ta);
            }
        return outp;
    }

    static void RoundRectFill(byte[] b, int W, int H, int x0, int y0, int w, int h, double rad, double alpha)
    {
        double cx = x0 + w / 2.0, cy = y0 + h / 2.0, hx = w / 2.0 - rad, hy = h / 2.0 - rad;
        for (int y = Math.Max(0, y0 - 1); y <= Math.Min(H - 1, y0 + h); y++)
            for (int x = Math.Max(0, x0 - 1); x <= Math.Min(W - 1, x0 + w); x++)
            {
                double qx = Math.Abs(x + 0.5 - cx) - hx, qy = Math.Abs(y + 0.5 - cy) - hy;
                double d = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) + Math.Min(Math.Max(qx, qy), 0) - rad;
                double a = alpha * Math.Clamp(0.5 - d, 0, 1);
                if (a > 0) Over(b, (y * W + x) * 4, 255 * a, 255 * a, 255 * a, a);
            }
    }

    static (IntPtr dc, IntPtr bmp, IntPtr old) MakeDib(byte[] px, int w, int h, IntPtr refDc)
    {
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr dc = CreateCompatibleDC(refDc);
        IntPtr bmp = CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        Marshal.Copy(px, 0, bits, Math.Min(px.Length, w * h * 4));
        return (dc, bmp, SelectObject(dc, bmp));
    }

    /// <summary>Kutu: alfa kanallı içerik doğrudan kopyalanır; alfa 0 yerlerde arkadaki acrylic görünür.</summary>
    static void PaintBox(Tile t)
    {
        IntPtr ps = Marshal.AllocHGlobal(128);
        try
        {
            IntPtr hdc = BeginPaint(t.Box, ps);
            byte[]? px = t.Hovered ? t.PixHover : t.Pix;
            if (px != null && hdc != IntPtr.Zero)
            {
                var (dc, bmp, old) = MakeDib(px, t.B, t.B, hdc);
                BitBlt(hdc, 0, 0, t.B, t.B, dc, 0, 0, 0x00CC0020 /* SRCCOPY */);
                SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc);
            }
            EndPaint(t.Box, ps);
        }
        finally { Marshal.FreeHGlobal(ps); }
    }

    static void PushLabel(Tile t)
    {
        byte[]? px = t.Hovered ? t.LabelPixHover : t.LabelPix;
        if (px == null) return;
        IntPtr screen = GetDC(IntPtr.Zero);
        var (dc, bmp, old) = MakeDib(px, t.LW, t.LH, screen);
        var size = new SIZE { cx = t.LW, cy = t.LH };
        var src = new POINT();
        var blend = new BLENDFUNCTION { Op = 0, Flags = 0, Alpha = (byte)Math.Round(255 * t.Alpha), Format = 1 /* AC_SRC_ALPHA */ };
        UpdateLayeredWindow(t.Label, screen, IntPtr.Zero, ref size, dc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
        SelectObject(dc, old); DeleteObject(bmp); DeleteDC(dc); ReleaseDC(IntPtr.Zero, screen);
    }
}
