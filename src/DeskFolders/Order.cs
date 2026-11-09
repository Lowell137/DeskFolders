using System.Text.Json;

namespace GrupPopup;

/// <summary>
/// Klasör içi uygulama sırası (popup'ta sürükleyerek değişir). %LOCALAPPDATA%\GrupPopup\order.json:
/// { "Grup adı": ["dosya1.lnk", "dosya2.lnk", ...] }. Listede olmayanlar sona, ada göre.
/// </summary>
static class Order
{
    static readonly object _lock = new();
    static Dictionary<string, List<string>>? _map;
    static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrupPopup", "order.json");

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
        lock (_lock) { Map().TryGetValue(Path.GetFileName(folder.TrimEnd('\\', '/')), out order); order = order?.ToList(); }
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
        lock (_lock) { Map()[Path.GetFileName(folder.TrimEnd('\\', '/'))] = fileNames.ToList(); Save(); }
    }

    public static void Forget(string name)
    {
        lock (_lock) { if (Map().Remove(name)) Save(); }
    }

    public static void Rename(string oldName, string newName)
    {
        lock (_lock)
        {
            var m = Map();
            if (m.Remove(oldName, out var l)) { m[newName] = l; Save(); }
        }
    }
}
