using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace GrupPopup;

public partial class App : Application
{
    static Mutex? _single;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrupPopup");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:s} {e.Exception}\n\n");
            }
            catch { }
            e.Handled = true; // bekleyen süreç tek bir hatada ölmesin
        };
    }

    // Bekleyen süreç: `GrupPopup.exe --serve [kök klasör]`. Tek örnek; pencere ilk istekle birlikte gelir.
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _single = new Mutex(true, "GrupPopup.Resident", out bool first);
        if (!first) { Environment.Exit(0); return; }

        string[] a = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(a, "--serve");
        string root = i >= 0 && i + 1 < a.Length && Directory.Exists(a[i + 1])
            ? a[i + 1]
            : Paths.DefaultRoot();

        PopupHost.Start(DispatcherQueue.GetForCurrentThread(), root);
        if (Directory.Exists(root)) _ = Task.Run(() => Icons.Prewarm(root));
    }
}
