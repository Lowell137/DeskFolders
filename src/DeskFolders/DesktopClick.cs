using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics;

namespace GrupPopup;

/// <summary>
/// Düşük seviye fare kancası (UI iş parçacığında):
/// - Açık popup'ın dışına basmak popup'ı kapatır.
/// - Grup adlı eski Windows ikonuna tek tık popup'ı açar.
/// - Telefondaki gibi: masaüstündeki bir uygulama ikonunu sürükleyip başka bir uygulama ikonunun üstüne bırakmak
///   ikisini içeren yeni klasör açar. Explorer bu bırakmayı "uygulamayı bu dosyayla aç" diye işleyeceği için bırakma
///   yutulur, Explorer'ın sürüklemesi Esc ile iptal edilir, sonra fare bırakma olayı işaretli olarak yeniden gönderilir.
/// </summary>
static class DesktopClick
{
    delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, HookProc fn, IntPtr mod, uint thread);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr n);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr n);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    const int WH_MOUSE_LL = 14;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207;
    static readonly IntPtr Marker = (IntPtr)0x47525550; // "GRUP": kendi gönderdiğimiz olaylar kancadan geçsin

    const bool EnableIconDropFolder = false;
    static HookProc? _proc;
    static IntPtr _hook;
    static string _root = "";
    static Func<RectInt32?> _openBounds = () => null;
    static Action _dismissOutside = () => { };
    static Action<ShowRequest> _show = _ => { };

    static IntPtr _downList;
    static POINT _down;
    static long _downTick, _lastFireTick;

    // masaüstü ikon sürükleme
    sealed record DeskItem(string Name, RectInt32 Rect);
    static bool _dragging;
    static volatile List<DeskItem>? _items;
    static volatile string? _srcName;

    public static void Install(string root, Func<RectInt32?> openBounds, Action dismissOutside, Action<ShowRequest> show)
    {
        _root = root; _openBounds = openBounds; _dismissOutside = dismissOutside; _show = show;
        _proc = Proc;
        _hook = SetWindowsHookExW(WH_MOUSE_LL, _proc, GetModuleHandleW(null), 0);
    }

    static IntPtr Proc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0)
        {
            try
            {
                var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                if (ms.extra != Marker && OnMouse((int)w, ms.pt)) return (IntPtr)1; // bırakmayı yut
            }
            catch { }
        }
        return CallNextHookEx(_hook, code, w, l);
    }

    /// <summary>true = olayı yut (Explorer görmesin).</summary>
    static bool OnMouse(int msg, POINT pt)
    {
        if (msg is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            if (_openBounds() is RectInt32 b &&
                (pt.X < b.X || pt.Y < b.Y || pt.X >= b.X + b.Width || pt.Y >= b.Y + b.Height))
                _dismissOutside();
        }

        if (msg == WM_LBUTTONDOWN)
        {
            IntPtr h = WindowFromPoint(pt);
            bool mods = Pressed(0x10) || Pressed(0x11) || Pressed(0x12);
            _downList = !mods && IsDesktopList(h) ? h : IntPtr.Zero;
            _down = pt;
            _downTick = Environment.TickCount64;
            _dragging = false; _items = null; _srcName = null;
        }
        else if (msg == WM_MOUSEMOVE && _downList != IntPtr.Zero && !_dragging && Pressed(0x01))
        {
            if (Math.Abs(pt.X - _down.X) > GetSystemMetrics(68) || Math.Abs(pt.Y - _down.Y) > GetSystemMetrics(69))
            {
                // Explorer ikon sürüklemesi başladı: ikonların yerlerini ve sürükleneni arka planda oku
                _dragging = true;
                IntPtr list = _downList; POINT d = _down;
                _ = Task.Run(() =>
                {
                    try
                    {
                        var items = AllItems(list);
                        _srcName = items.FirstOrDefault(i => Inside(i.Rect, d))?.Name;
                        _items = items;
                    }
                    catch { }
                });
            }
        }
        else if (msg == WM_LBUTTONUP && _downList != IntPtr.Zero)
        {
            IntPtr list = _downList;
            _downList = IntPtr.Zero;
            if (_dragging)
            {
                _dragging = false;
                return TryMakeFolder(list, pt);
            }
            long now = Environment.TickCount64;
            bool still = Math.Abs(pt.X - _down.X) <= GetSystemMetrics(68) && Math.Abs(pt.Y - _down.Y) <= GetSystemMetrics(69);
            if (!still || now - _downTick > 1000) return false;
            if (now - _lastFireTick < GetDoubleClickTime()) return false;
            return false;   // kapalı: eski Windows-ikonu yöntemi; masaüstünde grup adlı gerçek klasöre tık popup açmasın
            POINT up = pt;
            _ = Task.Run(() =>
            {
                (string Name, RectInt32 Rect)? hit = HitTest(list, up);
                if (hit is not { } x) return;
                string name = x.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? x.Name[..^4] : x.Name;
                if (name.Length == 0 || name.StartsWith('_')) return;
                string folder = Path.Combine(_root, name);
                if (!Directory.Exists(folder)) return;
                _lastFireTick = Environment.TickCount64;
                _show(new ShowRequest(folder, x.Rect, new PointInt32(up.X, up.Y)));
            });
        }
        return false;
    }

    /// <summary>Bir uygulama ikonu başka bir uygulama ikonunun üstüne bırakıldıysa yeni klasör aç. true = bırakma yutuldu.</summary>
    static bool TryMakeFolder(IntPtr list, POINT up)
    {
        // Kapalı: Windows'ta ikonu ikonun üstüne bırakmak "bu uygulamayla aç" demek; denemede yakalama her zaman yetişmedi,
        // iki Not Defteri açıldı ("iyi olmaz", 2026-11-09). Yeni klasör başka yoldan açılıyor.
        if (!EnableIconDropFolder) return false;
        var items = _items; string? src = _srcName;
        if (items == null || src == null) return false;
        if (WindowFromPoint(up) != list) return false;              // masaüstü değil (ör. bir klasör kutusu): Explorer bıraksın
        DeskItem? tgt = items.FirstOrDefault(i => Inside(i.Rect, up) && i.Name != src);
        if (tgt == null) return false;
        string? srcPath = AppPath(src), tgtPath = AppPath(tgt.Name);
        if (srcPath == null || tgtPath == null) return false;       // klasör, Geri Dönüşüm Kutusu vb.: normal davranış

        RectInt32 r = tgt.Rect;
        _ = Task.Run(() =>
        {
            CancelExplorerDrag();
            Tiles.NewGroupFromApps(new[] { tgtPath, srcPath }, new PointInt32(r.X + r.Width / 2, r.Y + r.Height / 2));
        });
        return true;
    }

    /// <summary>Explorer'ın sürükle-bırakını Esc ile iptal et, sonra yuttuğumuz fare bırakmasını işaretli gönder.</summary>
    static void CancelExplorerDrag()
    {
        int sz = Marshal.SizeOf<INPUT>();
        var esc = new[]
        {
            new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x1B, extra = Marker } },
            new INPUT { type = 1, ki = new KEYBDINPUT { vk = 0x1B, flags = 2, extra = Marker } },
        };
        SendInput(2, esc, sz);
        Thread.Sleep(40);
        var up = new[] { new INPUT { type = 0, mi = new MOUSEINPUT { flags = 0x0004 /* LEFTUP */, extra = Marker } } };
        SendInput(1, up, sz);
    }

    /// <summary>Masaüstü öğesinin adından dosya yolu; yalnız uygulama sayılanlar (.lnk/.url/.exe).</summary>
    static string? AppPath(string name)
    {
        string[] dirs =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        foreach (string d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            foreach (string f in Directory.GetFiles(d))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".exe")) continue;
                if (Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        return null;
    }

    // ---------- masaüstü sağ tık → New → App folder ----------
    // Explorer sağ tıklanan yerde boş "New App folder.grupfolder" açar (menu-kur.ps1 kaydı). Onu yakala, sil ve
    // aynı yerde yeni klasör kutusunu aç.

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowW(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    static readonly List<FileSystemWatcher> _watchers = new();

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

    /// <summary>
    /// Masaüstüne taşınan kısayolun ikonunu bırakılan noktaya koy (Explorer öğeyi ekleyince LVM_SETITEMPOSITION).
    /// "Simgeleri ızgaraya hizala" açıksa Explorer en yakın hücreye oturtur.
    /// </summary>
    public static void PlaceOnDesktop(string path, PointInt32 at)
    {
        string a = Path.GetFileName(path), b = Path.GetFileNameWithoutExtension(path);
        IntPtr list = DesktopList();
        if (list == IntPtr.Zero) return;
        for (int i = 0; i < 30; i++)
        {
            Thread.Sleep(50);
            var items = AllItems(list);
            int idx = items.FindIndex(x => x.Name.Equals(a, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(b, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) continue;
            long sp = (long)SendMessageW(list, 0x1033 /* LVM_GETITEMSPACING */, IntPtr.Zero, IntPtr.Zero);
            int cx = (int)(sp & 0xFFFF), cy = (int)((sp >> 16) & 0xFFFF);
            var p = new POINT { X = at.X, Y = at.Y };
            ScreenToClient(list, ref p);
            int x = Math.Max(0, p.X - cx / 2), y = Math.Max(0, p.Y - cy / 3);
            SendMessageW(list, 0x100F /* LVM_SETITEMPOSITION */, (IntPtr)idx, (IntPtr)((y << 16) | (x & 0xFFFF)));
            return;
        }
    }

    public static bool IsDesktopWindow(IntPtr h)
    {
        if (IsDesktopList(h)) return true;
        string c = Cls(h);
        return c is "Progman" or "WorkerW" or "SHELLDLL_DefView";
    }

    public static void WatchNewFolderRequests()
    {
        foreach (var sf in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.CommonDesktopDirectory })
        {
            string dir = Environment.GetFolderPath(sf);
            if (!Directory.Exists(dir)) continue;
            foreach (string old in Directory.GetFiles(dir, "*.grupfolder")) { try { File.Delete(old); } catch { } }  // artık
            try
            {
                var w = new FileSystemWatcher(dir, "*.grupfolder") { NotifyFilter = NotifyFilters.FileName };
                w.Created += (_, e) => _ = Task.Run(() => OnNewFolderFile(e.FullPath));
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch { }
        }
    }

    static void OnNewFolderFile(string path)
    {
        try
        {
            GetCursorPos(out POINT cur);
            var at = new PointInt32(cur.X, cur.Y);
            // Explorer dosyayı sağ tıklanan yere koyar; ikonunun yerini bul (adı uzantılı ya da uzantısız görünür)
            string a = Path.GetFileName(path), b = Path.GetFileNameWithoutExtension(path);
            IntPtr list = DesktopList();
            for (int i = 0; i < 10 && list != IntPtr.Zero; i++)
            {
                Thread.Sleep(60);
                var it = AllItems(list).FirstOrDefault(x => x.Name.Equals(a, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(b, StringComparison.OrdinalIgnoreCase));
                if (it != null) { at = new PointInt32(it.Rect.X + it.Rect.Width / 2, it.Rect.Y + it.Rect.Height / 2); break; }
            }
            for (int i = 0; i < 20; i++) { try { File.Delete(path); break; } catch { Thread.Sleep(50); } }
            Tiles.NewGroupFromApps(Array.Empty<string>(), at);
        }
        catch { }
    }

    static bool Inside(RectInt32 r, POINT p) => p.X >= r.X && p.Y >= r.Y && p.X < r.X + r.Width && p.Y < r.Y + r.Height;
    static bool Pressed(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    static string Cls(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static bool IsDesktopList(IntPtr h)
    {
        if (h == IntPtr.Zero || Cls(h) != "SysListView32") return false;
        IntPtr dv = GetParent(h);
        if (dv == IntPtr.Zero || Cls(dv) != "SHELLDLL_DefView") return false;
        string top = Cls(GetParent(dv));
        return top is "Progman" or "WorkerW";
    }

    // ---------- Explorer masaüstü listesinden okuma (başka sürecin belleği) ----------

    static T WithRemote<T>(IntPtr list, Func<IntPtr, IntPtr, T> body, T fail)
    {
        GetWindowThreadProcessId(list, out uint pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid); // VM_OPERATION | VM_READ | VM_WRITE
        if (proc == IntPtr.Zero) return fail;
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)1024, 0x3000, 0x04);
        if (remote == IntPtr.Zero) { CloseHandle(proc); return fail; }
        try { return body(proc, remote); }
        finally { VirtualFreeEx(proc, remote, UIntPtr.Zero, 0x8000); CloseHandle(proc); }
    }

    static string ItemText(IntPtr list, IntPtr proc, IntPtr remote, int i)
    {
        IntPtr textPtr = remote + 256;
        byte[] lv = new byte[64];                                         // LVITEMW (x64)
        BitConverter.GetBytes(1).CopyTo(lv, 0);                           // mask = LVIF_TEXT
        BitConverter.GetBytes(i).CopyTo(lv, 4);
        BitConverter.GetBytes(textPtr.ToInt64()).CopyTo(lv, 24);
        BitConverter.GetBytes(260).CopyTo(lv, 32);
        WriteProcessMemory(proc, remote, lv, lv.Length, out _);
        SendMessageW(list, 0x1073, (IntPtr)i, remote);                    // LVM_GETITEMTEXTW
        byte[] tb = new byte[520];
        ReadProcessMemory(proc, textPtr, tb, tb.Length, out _);
        string t = Encoding.Unicode.GetString(tb);
        int z = t.IndexOf('\0');
        return z >= 0 ? t[..z] : t;
    }

    static RectInt32 ItemRect(IntPtr list, IntPtr proc, IntPtr remote, int i)
    {
        IntPtr rectPtr = remote + 128;
        WriteProcessMemory(proc, rectPtr, new byte[16], 16, out _);       // left = LVIR_BOUNDS
        SendMessageW(list, 0x100E, (IntPtr)i, rectPtr);                   // LVM_GETITEMRECT
        byte[] rb = new byte[16];
        ReadProcessMemory(proc, rectPtr, rb, rb.Length, out _);
        var tl = new POINT { X = BitConverter.ToInt32(rb, 0), Y = BitConverter.ToInt32(rb, 4) };
        var br = new POINT { X = BitConverter.ToInt32(rb, 8), Y = BitConverter.ToInt32(rb, 12) };
        ClientToScreen(list, ref tl); ClientToScreen(list, ref br);
        return new RectInt32(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y);
    }

    static List<DeskItem> AllItems(IntPtr list) => WithRemote(list, (proc, remote) =>
    {
        var res = new List<DeskItem>();
        int n = (int)SendMessageW(list, 0x1004 /* LVM_GETITEMCOUNT */, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < n; i++) res.Add(new DeskItem(ItemText(list, proc, remote, i), ItemRect(list, proc, remote, i)));
        return res;
    }, new List<DeskItem>());

    /// <summary>Tıklanan masaüstü öğesinin adı ve ekran dikdörtgeni.</summary>
    static (string, RectInt32)? HitTest(IntPtr list, POINT screen) => WithRemote<(string, RectInt32)?>(list, (proc, remote) =>
    {
        POINT c = screen; ScreenToClient(list, ref c);
        byte[] hti = new byte[24];                                        // LVHITTESTINFO
        BitConverter.GetBytes(c.X).CopyTo(hti, 0);
        BitConverter.GetBytes(c.Y).CopyTo(hti, 4);
        WriteProcessMemory(proc, remote, hti, hti.Length, out _);
        int i = (int)SendMessageW(list, 0x1012, IntPtr.Zero, remote);     // LVM_HITTEST
        if (i < 0) return null;
        byte[] back = new byte[24];
        ReadProcessMemory(proc, remote, back, back.Length, out _);
        if ((BitConverter.ToUInt32(back, 8) & 0x6) == 0) return null;    // LVHT_ONITEMICON | LVHT_ONITEMLABEL
        return (ItemText(list, proc, remote, i), ItemRect(list, proc, remote, i));
    }, null);
}
