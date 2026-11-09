namespace GrupPopup;

/// <summary>Grupların kök klasörü: eski kurulumda Documents\Masaustu-Gruplari, yenide Documents\DeskFolders.</summary>
static class Paths
{
    public static string DefaultRoot()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string legacy = Path.Combine(docs, "Masaustu-Gruplari");
        if (Directory.Exists(legacy)) return legacy;
        string root = Path.Combine(docs, "DeskFolders");
        Directory.CreateDirectory(root);
        return root;
    }
}
