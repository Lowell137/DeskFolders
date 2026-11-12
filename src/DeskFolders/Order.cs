using System.Text.Json;

namespace GrupPopup;

/// <summary>
/// Klasör içi uygulama sırası (popup'ta sürükleyerek değişir). %LOCALAPPDATA%\GrupPopup\order.json:
/// { "Grup adı": ["dosya1.lnk", ...], "Grup adı\Alt klasör": [...] }. Anahtar = kök klasöre göre yol; üst düzey
/// gruplarda yalnız ad (eski kayıtlarla aynı). Listede olmayanlar sona, ada göre.
/// </summary>
static class Order
{
    static readonly object _lock = new();
    static Dictionary<string, List<string>>? _map;
    static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrupPopup", "order.json");

    /// <summary>Grupların kök klasörü; alt klasör anahtarları buna göre hesaplanır.</summary>
    public static string? Root { get; set; }

    /// <summary>Klasörün anahtarı: kök'e göre yol (kök bilinmiyorsa yalnız ad).</summary>
    public static string Key(string folder)
    {
        string f = folder.TrimEnd('\\', '/');
        if (Root != null)
        {
            string r = Root.TrimEnd('\\', '/') + "\\";
            if (f.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return f[r.Length..].Replace('/', '\\');
        }
        return Path.GetFileName(f);
    }

    static Dictionary<string, List<string>> Map()
    {
        if (_map != null) return _map;
        try
        {
            if (File.Exists(File_))
                _map = new(JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(File_)) ?? new(), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return _map ??= new(StringComparer.OrdinalIgnoreCase);
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Girişleri kayıtlı sıraya diz (yoksa ada göre).</summary>
    public static IEnumerable<string> Sort(string folder, IEnumerable<string> entries)
    {
        List<string>? order;
        lock (_lock) { Map().TryGetValue(Key(folder), out order); order = order?.ToList(); }
        var byName = entries.OrderBy(x => Path.GetFileName(x), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (order == null || order.Count == 0) return byName;
        int Rank(string f)
        {
            int i = order.FindIndex(o => o.Equals(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
        return byName.OrderBy(Rank); // OrderBy kararlı: kayıtsızlar ada göre kalır
    }

    public static void Set(string folder, IEnumerable<string> fileNames)
    {
        lock (_lock) { Map()[Key(folder)] = fileNames.ToList(); Save(); }
    }

    /// <summary>Anahtarı ve altındaki alt klasörlerin kayıtlarını sil.</summary>
    public static void Forget(string key)
    {
        lock (_lock)
        {
            var m = Map();
            bool ch = m.Remove(key);
            foreach (string k in m.Keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList()) { m.Remove(k); ch = true; }
            if (ch) Save();
        }
    }

    /// <summary>Klasör adı değişti: kendi ve alt klasörlerinin kayıtları yeni anahtara taşınır.</summary>
    public static void Rename(string oldKey, string newKey)
    {
        lock (_lock)
        {
            var m = Map();
            bool ch = false;
            if (m.Remove(oldKey, out var l)) { m[newKey] = l; ch = true; }
            foreach (string k in m.Keys.Where(k => k.StartsWith(oldKey + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                var v = m[k]; m.Remove(k); m[newKey + k[oldKey.Length..]] = v; ch = true;
            }
            if (ch) Save();
        }
    }
}
