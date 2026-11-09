using System.IO.Pipes;
using System.Text;
using Microsoft.UI.Dispatching;
using Windows.Graphics;

namespace GrupPopup;

/// <summary>
/// Arka planda bekleyen süreç: adlandırılmış borudan tetikleyicinin (Tetik.exe) isteklerini dinler,
/// tek PopupWindow'u gösterir. İstek biçimi (UTF-8): SHOW \t klasör \t l,t,r,b (boş olabilir) \t imleçX,imleçY
/// </summary>
static class PopupHost
{
    public const string PipeName = "GrupPopup.Pipe";

    static DispatcherQueue _dq = null!;
    static PopupWindow? _win;

    public static void Start(DispatcherQueue dq, string root)
    {
        _dq = dq;
        _win = new PopupWindow();
        _win.WarmUp();
        _ = Task.Run(PipeLoop);
        // Masaüstünde tek tıklama (Explorer çift tık ister). Kanca bu (UI) iş parçacığında kurulmalı.
        DesktopClick.Install(root,
            openBounds: () => _win?.OpenBounds,
            dismissOutside: () => _dq.TryEnqueue(DispatcherQueuePriority.High, () => _win?.DismissOutside()),
            show: Request);
        // Masaüstü widget'ları (Windows ikonu yerine kendi çizdiğimiz klasör kutuları), kendi iş parçacığında.
        Tiles.PopupHwnd = () => _win?.VisibleHwnd ?? IntPtr.Zero;
        Tiles.Start(root, Request);
        DesktopClick.WatchNewFolderRequests();   // masaüstü sağ tık → New → App folder
    }

    public static void Request(ShowRequest req)
    {
        // İkon verisini arka planda hazırla; pencere hazır olunca göster.
        _ = Task.Run(() => Icons.GetData(req.Folder)).ContinueWith(_ =>
            _dq.TryEnqueue(DispatcherQueuePriority.High, () => _win?.Present(req)));
        // Kardeş grupları da ısıt.
        string? root = Path.GetDirectoryName(req.Folder.TrimEnd('\\', '/'));
        if (root != null) _ = Task.Run(() => Icons.Prewarm(root));
    }

    static async Task PipeLoop()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.UTF8);
                string msg = await reader.ReadToEndAsync();
                Handle(msg);
            }
            catch { await Task.Delay(100); }
        }
    }

    static void Handle(string msg)
    {
        string[] p = msg.Split('\t');
        if (p.Length == 0) return;
        if (p[0] == "QUIT") { Environment.Exit(0); return; }
        if (p[0] == "NEW" && p.Length >= 2)   // masaüstü sağ tık → New app folder
        {
            int[] q = p[1].Split(',').Select(int.Parse).ToArray();
            Tiles.NewGroupFromApps(Array.Empty<string>(), new PointInt32(q[0], q[1]));
            return;
        }
        if (p[0] != "SHOW" || p.Length < 4 || !Directory.Exists(p[1])) return;

        RectInt32? icon = null;
        if (p[2].Length > 0)
        {
            int[] n = p[2].Split(',').Select(int.Parse).ToArray();
            icon = new RectInt32(n[0], n[1], n[2] - n[0], n[3] - n[1]);
        }
        int[] c = p[3].Split(',').Select(int.Parse).ToArray();
        Request(new ShowRequest(p[1], icon, new PointInt32(c[0], c[1])));
    }
}
