using System.Reflection;

namespace GrupPopup;

/// <summary>Grup klasörü işlemleri: uygulama ekleme (sürükle-bırak) ve yeni grup.</summary>
static class Groups
{
    /// <summary>
    /// Bırakılan şeyi gruba ekler. Masaüstündeki kısayol klasöre taşınır (telefonda klasöre atmak gibi, masaüstünden
    /// kalkar); başka yerdeki kısayol kopyalanır; exe/dosya/klasör için kısayol oluşturulur. Eklenen sayısını döndürür.
    /// </summary>
    public static int Add(string folder, IEnumerable<string> paths)
    {
        int n = 0;
        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (string p in paths)
        {
            try
            {
                if (!File.Exists(p) && !Directory.Exists(p)) continue;
                string full = Path.GetFullPath(p);
                if (Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(folder), StringComparison.OrdinalIgnoreCase)) continue;
                string ext = Path.GetExtension(full).ToLowerInvariant();
                if (File.Exists(full) && (ext == ".lnk" || ext == ".url"))
                {
                    string dst = Unique(Path.Combine(folder, Path.GetFileName(full)));
                    bool onDesktop = Path.GetDirectoryName(full)!.Equals(desk, StringComparison.OrdinalIgnoreCase);
                    if (onDesktop) File.Move(full, dst); else File.Copy(full, dst);
                }
                else
                {
                    string name = File.Exists(full) ? Path.GetFileNameWithoutExtension(full) : Path.GetFileName(full.TrimEnd('\\'));
                    MakeShortcut(Unique(Path.Combine(folder, name + ".lnk")), full);
                }
                n++;
            }
            catch { }
        }
        if (n > 0) Icons.Invalidate(folder);
        return n;
    }

    /// <summary>Uygulamayı klasörden çıkarır: kısayol masaüstüne taşınır (telefonda klasörden dışarı sürüklemek gibi).</summary>
    public static bool Remove(string folder, string path) => RemoveTo(folder, path) != null;

    /// <summary>Masaüstüne taşır, masaüstündeki yeni yolu döndürür (başarısızsa null).</summary>
    public static string? RemoveTo(string folder, string path)
    {
        try
        {
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string dst = Unique(Path.Combine(desk, Path.GetFileName(path)));
            if (Directory.Exists(path)) Directory.Move(path, dst); else File.Move(path, dst);
            Icons.Invalidate(folder);
            return dst;
        }
        catch { return null; }
    }

    /// <summary>Uygulamayı başka bir gruba taşır.</summary>
    public static bool MoveTo(string fromFolder, string toFolder, string path)
    {
        try
        {
            string dst = Unique(Path.Combine(toFolder, Path.GetFileName(path)));
            if (Directory.Exists(path)) Directory.Move(path, dst); else File.Move(path, dst);
            Icons.Invalidate(fromFolder); Icons.Invalidate(toFolder);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Klasörü sil: içindekiler masaüstüne tek tek dağılmaz (çok uygulamada masaüstü doluyordu); grubun klasörü
    /// bütünüyle masaüstüne normal bir Windows klasörü olarak taşınır (aynı ad, doluysa "Ad (2)"). Boşsa yalnız silinir.
    /// </summary>
    public static bool Dissolve(string folder)
    {
        try
        {
            string name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            var entries = Directory.GetFileSystemEntries(folder)
                .Where(f => !Path.GetFileName(f).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)).ToList();
            if (entries.Count == 0) Directory.Delete(folder, true);
            else
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                Directory.Move(folder, Unique(Path.Combine(desk, name)));
            }
            Order.Forget(Order.Key(folder));
            Icons.Invalidate(folder);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Yeni boş grup: "Yeni klasör", "Yeni klasör 2", ...</summary>
    public static string New(string root)
    {
        string path = Path.Combine(root, "New folder");
        for (int i = 2; Directory.Exists(path); i++) path = Path.Combine(root, "New folder " + i);
        Directory.CreateDirectory(path);
        return path;
    }

    static string Unique(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        bool isDir = !Path.HasExtension(path) || Directory.Exists(path);
        string dir = Path.GetDirectoryName(path)!;
        string name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path), ext = isDir ? "" : Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p) && !Directory.Exists(p)) return p;
        }
    }

    static void MakeShortcut(string lnk, string target)
    {
        Type t = Type.GetTypeFromProgID("WScript.Shell")!;
        object sh = Activator.CreateInstance(t)!;
        object l = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk })!;
        Type lt = l.GetType();
        lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, l, new object[] { target });
        string? wd = File.Exists(target) ? Path.GetDirectoryName(target) : target;
        if (wd != null) lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, l, new object[] { wd });
        lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, l, null);
    }
}
