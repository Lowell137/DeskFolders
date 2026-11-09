// Tetik.exe <grup klasörü>
// Masaüstü kısayolunun hedefi. Çok hafif (.NET Framework, Windows ile gelir): ikonun masaüstündeki yerini
// Explorer'dan okur, bekleyen GrupPopup sürecine boru üzerinden "şurada aç" der. Süreç yoksa başlatır.
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Tetik
{
    const string PipeName = "GrupPopup.Pipe";

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, UIntPtr size, uint type, uint prot);
    [DllImport("kernel32.dll")] static extern bool VirtualFreeEx(IntPtr h, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr written);
    [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, int size, out IntPtr read);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 1) return 1;
        string folder = args[0];

        if (args[0] == "--quit") { return TrySend("QUIT", 200) ? 0 : 1; }
        if (args[0] == "--new")   // masaüstü sağ tık → "New app folder": imlecin yanında yeni klasör
        {
            POINT p; GetCursorPos(out p);
            AllowSetForegroundWindow(-1);
            string m = "NEW\t" + p.X + "," + p.Y;
            if (TrySend(m, 30)) return 0;
            string d = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            string ex = Path.Combine(d, "GrupPopup.exe");
            if (!File.Exists(ex)) return 2;
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string root = Directory.Exists(Path.Combine(docs, "Masaustu-Gruplari")) ? Path.Combine(docs, "Masaustu-Gruplari") : Path.Combine(docs, "DeskFolders");
            Process.Start(new ProcessStartInfo(ex, "--serve \"" + root + "\"") { UseShellExecute = false, WorkingDirectory = d });
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < 20000) { if (TrySend(m, 250)) return 0; Thread.Sleep(40); }
            return 3;
        }
        if (args[0] == "--probe" && args.Length > 1)   // test: ikon konumunu dosyaya yaz
        {
            string res;
            try { res = FindIconRect(args[1]); } catch (Exception e) { res = "HATA " + e.Message; }
            IntPtr l = FindDesktopListView();
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "tetik-probe.txt"), "list=" + l + " rect=[" + res + "]");
            return 0;
        }
        if (!Directory.Exists(folder)) return 1;

        POINT cur; GetCursorPos(out cur);
        string rect = "";
        try { rect = FindIconRect(Path.GetFileName(folder.TrimEnd('\\', '/'))); } catch { }

        // Bekleyen sürecin öne geçebilmesi için ön plan hakkını devret (tıklamayı biz aldık).
        AllowSetForegroundWindow(-1);

        string msg = "SHOW\t" + folder + "\t" + rect + "\t" + cur.X + "," + cur.Y;
        if (TrySend(msg, 30)) return 0;

        // Bekleyen süreç yok: başlat, hazır olunca isteği ilet.
        string dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        string exe = Path.Combine(dir, "GrupPopup.exe");
        if (!File.Exists(exe)) return 2;
        Process.Start(new ProcessStartInfo(exe, "--serve \"" + Path.GetDirectoryName(folder.TrimEnd('\\', '/')) + "\"")
        { UseShellExecute = false, WorkingDirectory = dir });

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            if (TrySend(msg, 250)) return 0;
            Thread.Sleep(40);
        }
        return 3;
    }

    static bool TrySend(string msg, int timeoutMs)
    {
        try
        {
            using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
            {
                c.Connect(timeoutMs);
                byte[] b = new UTF8Encoding(false).GetBytes(msg);
                c.Write(b, 0, b.Length);
                c.Flush();
                return true;
            }
        }
        catch { return false; }
    }

    // ---- masaüstü ikonunun ekran konumu (Explorer'ın SysListView32'sinden) ----

    static IntPtr FindDesktopListView()
    {
        IntPtr dv = FindWindowEx(FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        if (dv == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero)
            {
                dv = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (dv != IntPtr.Zero) break;
            }
        }
        return dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>"sol,üst,sağ,alt" (ekran pikseli) ya da bulunamazsa boş metin.</summary>
    static string FindIconRect(string name)
    {
        IntPtr list = FindDesktopListView();
        if (list == IntPtr.Zero) return "";
        uint pid; GetWindowThreadProcessId(list, out pid);
        IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid); // VM_OPERATION | VM_READ | VM_WRITE
        if (proc == IntPtr.Zero) return "";
        IntPtr remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)1024, 0x3000, 0x04);
        if (remote == IntPtr.Zero) { CloseHandle(proc); return ""; }
        try
        {
            int count = (int)SendMessage(list, 0x1004, IntPtr.Zero, IntPtr.Zero); // LVM_GETITEMCOUNT
            IntPtr textPtr = new IntPtr(remote.ToInt64() + 256), rectPtr = new IntPtr(remote.ToInt64() + 128);
            IntPtr wr;
            for (int i = 0; i < count; i++)
            {
                byte[] lv = new byte[64]; // LVITEMW (x64)
                BitConverter.GetBytes(1).CopyTo(lv, 0);                          // mask = LVIF_TEXT
                BitConverter.GetBytes(i).CopyTo(lv, 4);                          // iItem
                BitConverter.GetBytes(textPtr.ToInt64()).CopyTo(lv, 24);         // pszText
                BitConverter.GetBytes(260).CopyTo(lv, 32);                       // cchTextMax
                WriteProcessMemory(proc, remote, lv, lv.Length, out wr);
                SendMessage(list, 0x1073, (IntPtr)i, remote);                    // LVM_GETITEMTEXTW

                byte[] tb = new byte[520]; IntPtr rd;
                ReadProcessMemory(proc, textPtr, tb, tb.Length, out rd);
                string t = Encoding.Unicode.GetString(tb);
                int z = t.IndexOf('\0'); if (z >= 0) t = t.Substring(0, z);
                if (!(t.Equals(name, StringComparison.OrdinalIgnoreCase) || t.Equals(name + ".lnk", StringComparison.OrdinalIgnoreCase))) continue;

                WriteProcessMemory(proc, rectPtr, new byte[16], 16, out wr);     // left = LVIR_BOUNDS (0)
                SendMessage(list, 0x100E, (IntPtr)i, rectPtr);                   // LVM_GETITEMRECT
                byte[] rb = new byte[16];
                ReadProcessMemory(proc, rectPtr, rb, rb.Length, out rd);
                POINT tl = new POINT { X = BitConverter.ToInt32(rb, 0), Y = BitConverter.ToInt32(rb, 4) };
                POINT br = new POINT { X = BitConverter.ToInt32(rb, 8), Y = BitConverter.ToInt32(rb, 12) };
                ClientToScreen(list, ref tl); ClientToScreen(list, ref br);
                return tl.X + "," + tl.Y + "," + br.X + "," + br.Y;
            }
            return "";
        }
        finally { VirtualFreeEx(proc, remote, UIntPtr.Zero, 0x8000); CloseHandle(proc); }
    }
}
