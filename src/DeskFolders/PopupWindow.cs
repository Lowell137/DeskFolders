using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace GrupPopup;

/// <summary>Tetikleyiciden gelen istek: hangi grup, kutunun ekran dikdörtgeni (varsa), imleç konumu.</summary>
sealed record ShowRequest(string Folder, RectInt32? Icon, PointInt32 Cursor, bool Rename = false);

/// <summary>
/// Açık klasör, Fluent / Windows 11 :
/// - Pencere = panel; arka planı Windows'un gerçek acrylic'i (DesktopAcrylicBackdrop), DWM yuvarlak köşe.
/// - Renkler, yazı tipi (Segoe UI Variable), yazı boyutları ve köşeler tema kaynaklarından; açık/koyu tema izlenir.
/// - Açılış/kapanış: pencere son boyutunda açılır, yalnız içerik canlanır. Windows 11 açılır panel hareketi:
///   açılış 250 ms yavaşlayan (cubic-bezier 0,0,0,1) kayma + solma, kapanış 150 ms hızlanan (1,0,1,1).
/// - Odak gidince kapanır; ad düzenlenebilir; sürükleyerek sıralama; dışarı bırak = klasörden çıkar.
/// </summary>
sealed class PopupWindow : Window
{
    // --- Windows 11 Başlat klasörü ölçüleri (DIP); yükseklik içerik satırına göre ---
    // sayfa = 4x4, Windows 11 Başlat gibi (dolunca sağda yeni sayfa)
    // sayfa = 4 sütun x 3 satır (Başlat klasörü kaydı); dolunca AŞAĞIYA yeni sayfa, sağda dikey noktalar
    const double PW = 432, Pad = 24, HeaderH = 84, CellH = 90, ItemW = 92, ItemH = 86, Icon = 36;
    const int Cols = 4, MaxRows = 3;
    int PerPage => Cols * _rowsWin;
    double PageH => _rowsWin * CellH;
    const double CellW = (PW - 2 * Pad) / Cols;
    const double AnchorDX = 54, AnchorDY = -17;   // panelin sol üstü, kutunun ortasına göre
    const double Slide = 24;                      // açılır panel kayma mesafesi (DIP)

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtrW(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    sealed class Slot { public required Item It; public required Button Btn; }

    readonly Grid _root = new();          // pencerenin tamamı = panel (arka plan acrylic)
    readonly Grid _content = new();       // canlanan içerik
    readonly TextBox _title = new();
    readonly Button _back = new();        // alt klasördeyken sol üstte ←
    readonly Border _outTop = new(), _outLeft = new();   // alt klasörden sürüklerken "bir üst klasöre bırak" bölgesi
    readonly TextBlock _outText = new();
    readonly Border _outFillT = new(), _outFillL = new();
    readonly Image _ghostImg = new();     // klasör geçişinde giden içeriğin görüntüsü
    readonly List<string> _nav = new();   // üst klasörler (son = bir üst); boş = en üst klasördeyiz
    string _top = "";                     // panelin açıldığı grup klasörü
    bool _navBusy;
    int _rowsWin = 1, _gap = -1, _gapCand = -1;
    long _gapSince;
    bool _backHot;
    const double Shift = PW * 0.4;        // klasör geçişi kayma mesafesi (DIP)
    const long MergeMs = 450;             // bir uygulamanın üstünde bu kadar durunca klasör oluşur / içine girer
    Slot? _mergeCand, _mergeTarget;
    long _mergeSince;
    readonly ScrollViewer _scroll = new();
    readonly PipsPager _pips = new();
    int _page, _pages = 1;
    long _edgeSince, _wheelAt, _pressTick;
    int _edgeDir;
    const double EdgeW = 44;        // sayfa çeviren kenar bölgesi (DIP)
    const long EdgeMs = 380;        // kenarda bu kadar tutunca sayfa kayar
    readonly Border _edgeL = new(), _edgeR = new();

    /// <summary>Kenar göstergesi: üst/alt yarı saydam şerit + ok; EdgeMs boyunca soldan sağa dolar (Fluent vurgu rengi).</summary>
    void BuildEdges()
    {
        foreach (var (b, up) in new[] { (_edgeL, true), (_edgeR, false) })   // _edgeL = üst, _edgeR = alt
        {
            b.Height = 26; b.Margin = new Thickness(Pad, up ? HeaderH + 2 : 0, Pad, up ? 0 : 14);
            b.HorizontalAlignment = HorizontalAlignment.Stretch;
            b.VerticalAlignment = up ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            b.CornerRadius = Res("ControlCornerRadius", new CornerRadius(4));
            b.Background = Res<Brush>("SubtleFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)));
            b.IsHitTestVisible = false;
            b.Opacity = 0;
            var g = new Grid();
            var fill = new Border { CornerRadius = b.CornerRadius, Background = Res<Brush>("AccentFillColorDefaultBrush", new SolidColorBrush(Colors.DodgerBlue)), Opacity = 0.35 };
            g.Children.Add(fill);
            g.Children.Add(new FontIcon { Glyph = up ? "\uE70E" : "\uE70D", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            b.Child = g;
            _content.Children.Add(b);
        }
    }

    void ShowEdge(int dir)
    {
        Border b = dir < 0 ? _edgeL : _edgeR, other = dir < 0 ? _edgeR : _edgeL;
        Fade(other, 0, 100);
        Fade(b, 1, 120);
        // dolum: yatay ölçek 0 → 1, EdgeMs içinde
        var fill = (Border)((Grid)b.Child).Children[0];
        Visual v = ElementCompositionPreview.GetElementVisual(fill);
        v.CenterPoint = new Vector3(0, 0, 0);
        var a = v.Compositor.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0f, new Vector3(0, 1, 1));
        a.InsertKeyFrame(1f, new Vector3(1, 1, 1), v.Compositor.CreateLinearEasingFunction());
        a.Duration = TimeSpan.FromMilliseconds(EdgeMs);
        v.StartAnimation("Scale", a);
    }

    void HideEdge() { Fade(_edgeL, 0, 120); Fade(_edgeR, 0, 120); }

    static void Fade(UIElement e, float to, int ms)
    {
        Visual v = ElementCompositionPreview.GetElementVisual(e);
        var a = v.Compositor.CreateScalarKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, v.Compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(ms);
        v.StartAnimation("Opacity", a);
        if (e is FrameworkElement fe) fe.Opacity = 1;   // XAML opaklığı 1 kalır, görünürlüğü görsel animasyon belirler
    }

    /// <summary>Tutulan uygulama hafifçe büyür (kalkmış gibi), bırakınca yerine iner.</summary>
    static void Lift(UIElement e, bool up)
    {
        Visual v = ElementCompositionPreview.GetElementVisual(e);
        v.CenterPoint = new Vector3((float)ItemW / 2, (float)ItemH / 2, 0);
        var a = v.Compositor.CreateVector3KeyFrameAnimation();
        float k = up ? 1.12f : 1f;
        a.InsertKeyFrame(1f, new Vector3(k, k, 1), v.Compositor.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(up ? 167 : 250);
        v.StartAnimation("Scale", a);
    }
    bool _swipe;
    Slot? _swipeBy;                 // öğenin üstünden başlayan kaydırma
    double _swipeY0, _swipeOff0;
    const long HoldMs = 300;        // bu kadar basılı tutulursa sürükleme = sıralama; daha hızlıysa sayfa kaydırma

    void BeginSwipe(PointerRoutedEventArgs e, Slot? by = null)
    {
        _swipe = true; _swipeBy = by;
        _swipeY0 = e.GetCurrentPoint(_root).Position.Y;
        _swipeOff0 = _page * PageH;
    }

    /// <summary>Parmağı izle: sayfa imleçle birlikte kayar, uçlarda hafif direnç.</summary>
    void SwipeMove(PointerRoutedEventArgs e)
    {
        double dy = e.GetCurrentPoint(_root).Position.Y - _swipeY0;
        double max = (_pages - 1) * PageH, off = _swipeOff0 - dy;
        if (off < 0) off /= 3; else if (off > max) off = max + (off - max) / 3;
        _scroll.ChangeView(null, Math.Clamp(off, 0, max), null, true);
    }

    /// <summary>Bırakınca: yeterince çekildiyse sonraki/önceki sayfa, değilse geri otur.</summary>
    void EndSwipe(PointerRoutedEventArgs e)
    {
        double dy = e.GetCurrentPoint(_root).Position.Y - _swipeY0;
        _swipe = false; _swipeBy = null;
        int target = dy < -PageH / 6 ? _page + 1 : dy > PageH / 6 ? _page - 1 : _page;
        GoPage(target);
    }

    /// <summary>Sayfaya kayarak geç (Fluent: ScrollViewer'ın kendi yumuşak geçişi).</summary>
    void GoPage(int pg, bool instant = false)
    {
        pg = Math.Clamp(pg, 0, Math.Max(0, _pages - 1));
        _page = pg;
        if (_pips.SelectedPageIndex != pg) _pips.SelectedPageIndex = pg;
        _scroll.ChangeView(null, pg * PageH, null, instant);
    }
    readonly Canvas _canvas = new();
    readonly List<Slot> _slots = new();
    readonly bool _keep = Environment.GetCommandLineArgs().Contains("--keep"); // test: otomatik kapanma yok
    readonly IntPtr _hwnd;

    string _folder = "", _popFolder = "", _popSig = "", _dismissFolder = "";
    bool _visible, _closing, _anim, _outsideDismiss, _closeAfterOpen, _gotFg;
    long _openTick, _dismissTick;
    RectInt32 _win;
    double _scale = 1, _ph = 300;

    // sürükleme
    Slot? _press;
    Windows.Foundation.Point _pressPt, _grab;
    bool _drag, _suppressClick;

    public RectInt32? OpenBounds => _visible && !_closing && !_anim && !_oleDrag ? _win : null;
    public IntPtr VisibleHwnd => _visible ? _hwnd : IntPtr.Zero;
    bool _oleDrag;
    public void DismissOutside() { if (!_keep) Dismiss(true); }

    /// <summary>Tema kaynağı (Fluent hazır değeri); yoksa yedek.</summary>
    static T Res<T>(string key, T fallback)
    {
        try { if (Application.Current.Resources.TryGetValue(key, out object v) && v is T t) return t; } catch { }
        return fallback;
    }

    public PopupWindow()
    {
        SystemBackdrop = new DesktopAcrylicBackdrop();   // Fluent'in kendi acrylic'i
        Title = "GrupPopup";
        BuildUi();
        Content = _root;

        _hwnd = WindowNative.GetWindowHandle(this);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
        }
        AppWindow.IsShownInSwitchers = false;
        // başlıksız açılır pencere (çerçevesiz WinUI'de kalan ince kenar stilini temizle)
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        long st = GetWindowLongPtrW(_hwnd, GWL_STYLE).ToInt64();
        st &= ~(0x00C00000L | 0x00040000L | 0x00800000L | 0x00400000L | 0x00020000L | 0x00010000L | 0x00080000L);
        st |= unchecked((long)0x80000000L);
        SetWindowLongPtrW(_hwnd, GWL_STYLE, new IntPtr(st));
        long ex = GetWindowLongPtrW(_hwnd, GWL_EXSTYLE).ToInt64();
        ex &= ~(0x00000001L | 0x00000200L | 0x00000100L | 0x00020000L);
        SetWindowLongPtrW(_hwnd, GWL_EXSTYLE, new IntPtr(ex));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x1 | 0x2 | 0x4 | 0x10 | 0x20);
        Native.Round(_hwnd);                               // Windows 11 yuvarlak köşe (DWM, 8 px)
        Activated += OnActivated;
        WatchForeground();
    }

    void BuildUi()
    {
        // tema: sistemi izle (RequestedTheme verilmez); yazı tipi tema varsayılanı Segoe UI Variable
        var pad = Res("FlyoutContentPadding", new Thickness(12));
        _root.Children.Add(_content);
        _ghostImg.Stretch = Stretch.Fill; _ghostImg.IsHitTestVisible = false;
        _ghostImg.HorizontalAlignment = HorizontalAlignment.Left; _ghostImg.VerticalAlignment = VerticalAlignment.Top;
        _ghostImg.Visibility = Visibility.Collapsed;
        ElementCompositionPreview.SetIsTranslationEnabled(_ghostImg, true);
        _root.Children.Add(_ghostImg);

        // ad: Fluent TextBox; boşta zeminsiz, üzerine gelince/odakta temanın kendi görünümü
        _title.Width = PW - 2 * 76;                          // ← düğmesinin altına uzanmasın
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Top;
        _title.Margin = new Thickness(0, 26, 0, 0);
        _title.TextAlignment = TextAlignment.Center;
        _title.FontSize = Res("SubtitleTextBlockFontSize", 20.0);
        _title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        _title.TextWrapping = TextWrapping.Wrap;           // temizle (X) düğmesi çıkmasın
        _title.IsSpellCheckEnabled = false;
        _title.CornerRadius = Res("ControlCornerRadius", new CornerRadius(4));
        var clear = new SolidColorBrush(Colors.Transparent);
        _title.Resources["TextControlBackground"] = clear;
        _title.Resources["TextControlBorderBrush"] = clear;
        _title.Resources["TextControlBackgroundPointerOver"] = Res<Brush>("SubtleFillColorSecondaryBrush", clear);
        _title.Resources["TextControlBorderBrushPointerOver"] = clear;
        _title.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; CommitRename(); FocusItems(); }
        };
        _title.LostFocus += (_, _) => CommitRename();
        _content.Children.Add(_title);

        // ← : bir üst klasöre dön (Windows 11 Başlat klasörü gibi)
        _back.Content = new FontIcon { Glyph = "", FontSize = 14 };
        _back.Width = 36; _back.Height = 36; _back.Padding = new Thickness(0);
        _back.HorizontalAlignment = HorizontalAlignment.Left;
        _back.VerticalAlignment = VerticalAlignment.Top;
        _back.Margin = new Thickness(16, 28, 0, 0);
        _back.CornerRadius = Res("ControlCornerRadius", new CornerRadius(4));
        _back.Visibility = Visibility.Collapsed;
        PlateOff(_back, false);
        _back.Click += (_, _) => GoBack();
        _content.Children.Add(_back);

        // sürüklerken görünen bırakma bölgesi: üst başlık şeridi + sol kenar şeridi
        // Sayfa kenar göstergesiyle (BuildEdges) aynı stil: tema fırçalı şerit + vurgu dolgusu + ok ikonu, sabit renk yok.
        _outTop.Margin = new Thickness(Pad, 16, Pad, 0); _outTop.Height = HeaderH - 40;
        _outTop.VerticalAlignment = VerticalAlignment.Top; _outTop.HorizontalAlignment = HorizontalAlignment.Stretch;
        _outLeft.Margin = new Thickness(4, HeaderH - 8, 0, 14); _outLeft.Width = 20;
        _outLeft.VerticalAlignment = VerticalAlignment.Stretch; _outLeft.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var (z, fill) in new[] { (_outTop, _outFillT), (_outLeft, _outFillL) })
        {
            z.CornerRadius = Res("ControlCornerRadius", new CornerRadius(4));
            z.Background = Res<Brush>("SubtleFillColorSecondaryBrush", new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)));
            z.IsHitTestVisible = false; z.Opacity = 0;
            fill.CornerRadius = z.CornerRadius;
            fill.Background = Res<Brush>("AccentFillColorDefaultBrush", new SolidColorBrush(Colors.DodgerBlue));
            fill.Opacity = 0.35;                     // kenar göstergesiyle aynı: dolgu ölçekle dolar (SetOutHot)
            var g = new Grid();
            g.Children.Add(fill);
            if (z == _outTop)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                row.Children.Add(new FontIcon { Glyph = "", FontSize = 14 });   // kenar göstergesindeki yukarı ok
                row.Children.Add(_outText);
                g.Children.Add(row);
            }
            else g.Children.Add(new FontIcon { Glyph = "", FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            z.Child = g;
            if (z == _outLeft) _content.Children.Add(z);      // yalnız sol şerit görünür; üst başlık bölgesi yok
        }
        _outText.VerticalAlignment = VerticalAlignment.Center;
        if (Res<Style?>("BodyStrongTextBlockStyle", null) is Style ocs) _outText.Style = ocs;
        SetOutHot(false);

        _scroll.Margin = new Thickness(0, HeaderH, 0, 0);
        _scroll.VerticalAlignment = VerticalAlignment.Top;
        _scroll.VerticalScrollMode = ScrollMode.Disabled;     // kullanıcı kaydırması yok; ChangeView ile biz süreriz
        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollMode = ScrollMode.Disabled;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.ZoomMode = ZoomMode.Disabled;
        _canvas.Width = PW;
        _canvas.VerticalAlignment = VerticalAlignment.Top;
        _canvas.HorizontalAlignment = HorizontalAlignment.Left;
        _scroll.Content = _canvas;
        _content.Children.Add(_scroll);
        // dokunmatik yüzey/kaydırma bitince en yakın sayfaya otur
        // boş yere basıp sağa/sola çekmek de sayfa çevirir
        _content.PointerPressed += (_, e) =>
        {
            if (_pages <= 1 || !e.GetCurrentPoint(_content).Properties.IsLeftButtonPressed) return;
            if (e.OriginalSource is not (Grid or Canvas or ScrollViewer)) return;
            BeginSwipe(e);
            _content.CapturePointer(e.Pointer);
        };
        _content.PointerMoved += (_, e) => { if (_swipe && _swipeBy == null) SwipeMove(e); };
        _content.PointerReleased += (_, e) => { if (_swipe && _swipeBy == null) { EndSwipe(e); _content.ReleasePointerCaptures(); } };
        _content.PointerCaptureLost += (_, e) => { if (_swipe && _swipeBy == null) EndSwipe(e); };
        // fare tekerleği: sonraki / önceki sayfa
        // (ScrollViewer tekerleği kendisi "işledi" sayıyor; işlenmiş olayları da dinle)
        _content.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            if (_pages <= 1) return;
            int d = e.GetCurrentPoint(_content).Properties.MouseWheelDelta;
            if (d == 0) return;
            long now = Environment.TickCount64;
            if (now - _wheelAt < 250) { e.Handled = true; return; }   // bir tekerlek hareketi = bir sayfa
            _wheelAt = now;
            GoPage(_page + (d < 0 ? 1 : -1));
            e.Handled = true;
        }), true);
        // sayfa noktaları (Fluent PipsPager)
        _pips.Orientation = Orientation.Vertical;            // sağ kenarda dikey noktalar (Başlat klasörü)
        _pips.HorizontalAlignment = HorizontalAlignment.Right;
        _pips.VerticalAlignment = VerticalAlignment.Center;
        _pips.Margin = new Thickness(0, HeaderH, 4, 12);
        _pips.PreviousButtonVisibility = PipsPagerButtonVisibility.VisibleOnPointerOver;   // noktaların üstünde ▲ altında ▼ (Başlat klasörü)
        _pips.NextButtonVisibility = PipsPagerButtonVisibility.VisibleOnPointerOver;
        _pips.SelectedIndexChanged += (_, _) => { if (_pips.SelectedPageIndex != _page) GoPage(_pips.SelectedPageIndex); };
        _content.Children.Add(_pips);
        BuildEdges();

        // boş yere sağ tık: yeniden adlandır / sil
        var pm = new MenuFlyout();
        var ren = new MenuFlyoutItem { Text = "Rename folder", Icon = new SymbolIcon(Symbol.Rename) };
        ren.Click += (_, _) => { _title.Focus(FocusState.Programmatic); _title.SelectAll(); };
        var del = new MenuFlyoutItem { Text = "Delete folder", Icon = new SymbolIcon(Symbol.Delete) };
        del.Click += (_, _) => DeleteFolder();
        var sort = new MenuFlyoutItem { Text = "Sort by name", Icon = new SymbolIcon(Symbol.Sort) };
        sort.Click += (_, _) =>
        {
            Order.Forget(Order.Key(_folder));
            Icons.Invalidate(_folder);
            _popSig = "";
            Populate(_folder);
            GoPage(0);
            Tiles.Refresh();
        };
        var newSub = new MenuFlyoutItem { Text = "New folder", Icon = new SymbolIcon(Symbol.NewFolder) };
        newSub.Click += (_, _) =>
        {
            try { Groups.New(_folder); } catch (Exception e) { Log(e); return; }
            Icons.Invalidate(_folder);
            _popSig = "";
            Populate(_folder);
            EnsureSize();
            Tiles.Refresh();
        };
        pm.Items.Add(newSub);
        pm.Items.Add(ren);
        pm.Items.Add(sort);
        pm.Items.Add(new MenuFlyoutSeparator());
        pm.Items.Add(del);
        _root.ContextFlyout = pm;
        _root.Background = clear;                          // tıklanabilir, acrylic görünür

        // uygulamayı panele sürükle-bırak → gruba eklenir
        _root.AllowDrop = true;
        _root.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Link;
                e.DragUIOverride.Caption = "Add to " + Path.GetFileName(_folder);
            }
        };
        _root.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
            if (_oleDrag) return;   // kendi öğesi geri bırakıldı
            var items = await e.DataView.GetStorageItemsAsync();
            // bir alt klasör hücresinin üstüne bırakıldıysa onun içine, yoksa açık klasöre
            var pos = e.GetPosition(_canvas);
            Slot? hit = _slots.FirstOrDefault(sl => Directory.Exists(sl.It.Path)
                && pos.X >= Canvas.GetLeft(sl.Btn) && pos.X < Canvas.GetLeft(sl.Btn) + ItemW
                && pos.Y >= Canvas.GetTop(sl.Btn) && pos.Y < Canvas.GetTop(sl.Btn) + ItemH);
            if (Groups.Add(hit?.It.Path ?? _folder, items.Select(i => i.Path)) > 0)
            {
                Icons.Invalidate(_folder);
                _popSig = "";
                Populate(_folder);
                EnsureSize();
                Tiles.Refresh();
            }
        };

        var esc = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        esc.Invoked += (_, a) =>
        {
            a.Handled = true;
            if (_title.FocusState != FocusState.Unfocused) { _title.Text = Path.GetFileName(_folder); FocusItems(); }
            else if (_nav.Count > 0) GoBack();      // alt klasördeyken Esc bir üst klasöre döner
            else Dismiss(false);
        };
        _root.KeyboardAccelerators.Add(esc);
        // WinUI kısayolun tuşunu ("Esc") fare durunca ipucu olarak gösteriyordu : gizle
        _root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    /// <summary>Pencereyi ekran dışında bir kez oluşturur: ilk açılış ucuz olsun.</summary>
    public void WarmUp()
    {
        AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 448, 300));
        Native.Cloak(_hwnd, true);
        Activate();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { AppWindow.Hide(); Native.Cloak(_hwnd, false); });
    }

    // ---------- açma / kapama ----------

    public async void Present(ShowRequest r)
    {
        if (_anim) return;
        long now = Environment.TickCount64;
        bool same = r.Folder.Equals(_top, StringComparison.OrdinalIgnoreCase);
        if (_visible && !_closing && same)
        {
            if (now - _openTick < 700) return;
            Dismiss(false);
            return;
        }
        if (_outsideDismiss && r.Folder.Equals(_dismissFolder, StringComparison.OrdinalIgnoreCase) && now - _dismissTick < 600)
        {
            _outsideDismiss = false;
            return;
        }
        if (_visible) return;

        PointInt32 c = r.Icon is RectInt32 ic ? new PointInt32(ic.X + ic.Width / 2, ic.Y + (int)(ic.Height * 0.36)) : r.Cursor;
        _scale = 1.0;
        if (GetDpiForMonitor(MonitorFromPoint(new POINT { X = c.X, Y = c.Y }, 2), 0, out uint dx, out _) == 0 && dx > 0) _scale = dx / 96.0;

        _anim = true;
        try
        {
            _folder = r.Folder; _top = r.Folder; _nav.Clear(); _navBusy = false;
            _back.Visibility = Visibility.Collapsed; _ghostImg.Visibility = Visibility.Collapsed;
            Populate(r.Folder);
            FocusItems();

            // panel boyu içerik satırına göre (en çok 4 satır; alt klasörde daha fazlası gerekirse büyür, küçülmez)
            int rows = Math.Clamp((_slots.Count + Cols - 1) / Cols, 1, MaxRows);
            _rowsWin = rows;
            _ph = HeaderH + rows * CellH + 12;
            _scroll.Height = rows * CellH;
            Layout(null, -1, snap: true);                // sayfa boyu (satır sayısı) belli oldu
            GoPage(0, instant: true);
            int pw = (int)Math.Round(PW * _scale), ph = (int)Math.Round(_ph * _scale), m = (int)(8 * _scale);
            RectInt32 wa = DisplayArea.GetFromPoint(c, DisplayAreaFallback.Nearest).WorkArea;
            int left = Math.Clamp(c.X + (int)(AnchorDX * _scale), wa.X + m, Math.Max(wa.X + m, wa.X + wa.Width - pw - m));
            int top = Math.Clamp(c.Y + (int)(AnchorDY * _scale), wa.Y + m, Math.Max(wa.Y + m, wa.Y + wa.Height - ph - m));
            _win = new RectInt32(left, top, pw, ph);

            Visual cv = ElementCompositionPreview.GetElementVisual(_content);
            ElementCompositionPreview.SetIsTranslationEnabled(_content, true);
            cv.Opacity = 0;
            cv.Properties.InsertVector3("Translation", new Vector3(0, (float)-Slide, 0));

            AppWindow.MoveAndResize(_win);
            Native.Cloak(_hwnd, true);
            AppWindow.Show(false);
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);
            Native.NoModEffect(_hwnd);
            _root.UpdateLayout();
            await Frames(3);                          // boyut değişince yüzey hazır olsun (gri kare görünmesin)

            _visible = true; _closing = false; _outsideDismiss = false; _closeAfterOpen = false; _gotFg = false;
            _openTick = now;
            Native.Cloak(_hwnd, false);
            Activate();
            ForceForeground();
            if (GetForegroundWindow() == _hwnd) _gotFg = true;
            SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10);   // kutuların üstünde kal
            GuardModEffect(700);

            // Windows 11 açılır panel: 250 ms, yavaşlayan; içerik yukarıdan kayıp gelir ve belirir
            Animate(cv, open: true);
            await Task.Delay(250);
            if (r.Rename) { _title.Focus(FocusState.Programmatic); _title.SelectAll(); }
            if (_closeAfterOpen) { _closeAfterOpen = false; _anim = false; Dismiss(true); }
        }
        catch (Exception e)
        {
            Log(e);
            Native.Cloak(_hwnd, false);
        }
        finally { _anim = false; }
    }

    /// <summary>Fluent hareket: açılış 250 ms decelerate (0,0,0,1), kapanış 150 ms accelerate (1,0,1,1).</summary>
    static void Animate(Visual v, bool open)
    {
        Compositor c = v.Compositor;
        var ease = open
            ? c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f))
            : c.CreateCubicBezierEasingFunction(new Vector2(1f, 0f), new Vector2(1f, 1f));
        var dur = TimeSpan.FromMilliseconds(open ? 250 : 150);

        var tr = c.CreateVector3KeyFrameAnimation();
        tr.Duration = dur;
        tr.InsertKeyFrame(1f, open ? Vector3.Zero : new Vector3(0, (float)-Slide / 2, 0), ease);
        var op = c.CreateScalarKeyFrameAnimation();
        op.Duration = TimeSpan.FromMilliseconds(open ? 167 : 150);
        op.InsertKeyFrame(1f, open ? 1f : 0f, ease);
        v.StartAnimation("Translation", tr);
        v.StartAnimation("Opacity", op);
    }

    /// <summary>Windhawk translucent-windows kendi efektini sonradan yeniden uyguluyor; ilk karelerde kapat.</summary>
    void GuardModEffect(int ms)
    {
        var sw = Stopwatch.StartNew();
        EventHandler<object>? h = null;
        h = (_, _) =>
        {
            Native.NoModEffect(_hwnd);
            if (sw.ElapsedMilliseconds > ms || !_visible) CompositionTarget.Rendering -= h;
        };
        CompositionTarget.Rendering += h;
    }

    async void Dismiss(bool outside)
    {
        if (!_visible || _closing || _anim) return;
        _closing = true; _anim = true;
        if (outside) { _outsideDismiss = true; _dismissFolder = _folder; _dismissTick = Environment.TickCount64; }
        try
        {
            if (_title.FocusState != FocusState.Unfocused) { CommitRename(); FocusItems(); }
            Animate(ElementCompositionPreview.GetElementVisual(_content), open: false);
            await Task.Delay(150);
        }
        catch (Exception e) { Log(e); }
        finally
        {
            AppWindow.Hide();
            _visible = false; _closing = false; _anim = false;
        }
    }

    // ---------- odak ----------

    void LostFocus()
    {
        if (!_visible || _closing || _keep || _oleDrag) return;
        if (_anim) { _closeAfterOpen = true; return; }
        Dismiss(true);
    }

    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc fn, uint pid, uint tid, uint flags);
    static WinEventProc? _fgProc;

    /// <summary>WinUI "Activated" pencere zaten etkinse gelmiyor; ön plan değişimine bak.</summary>
    void WatchForeground()
    {
        _fgProc = (_, _, h, _, _, _, _) =>
        {
            if (h == IntPtr.Zero || !_visible) return;
            if (h == _hwnd) { _gotFg = true; return; }
            if (_gotFg) LostFocus();
        };
        SetWinEventHook(3, 3, IntPtr.Zero, _fgProc, 0, 0, 0);
    }

    void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (e.WindowActivationState == WindowActivationState.Deactivated) { if (_gotFg) LostFocus(); }
        else { _gotFg = true; Native.NoModEffect(_hwnd); }
    }

    Task Frames(int n)
    {
        var tcs = new TaskCompletionSource();
        int c = 0;
        EventHandler<object>? h = null;
        h = (_, _) => { if (++c >= n) { CompositionTarget.Rendering -= h; tcs.TrySetResult(); } };
        CompositionTarget.Rendering += h;
        return Task.WhenAny(tcs.Task, Task.Delay(100)).ContinueWith(_ => { CompositionTarget.Rendering -= h; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void ForceForeground()
    {
        IntPtr fg = GetForegroundWindow();
        uint ft = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        bool attached = ft != 0 && ft != me && AttachThreadInput(me, ft, true);
        BringWindowToTop(_hwnd);
        SetForegroundWindow(_hwnd);
        if (attached) AttachThreadInput(me, ft, false);
    }

    void FocusItems()
    {
        if (_slots.Count > 0) _slots[0].Btn.Focus(FocusState.Pointer);
        else _root.Focus(FocusState.Programmatic);
    }

    static void Log(Exception e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrupPopup");
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"{DateTime.Now:s} [popup] {e}\n\n");
        }
        catch { }
    }

    // ---------- içerik ----------

    void Populate(string folder)
    {
        string sig = Icons.Signature(folder);
        _title.Text = Path.GetFileName(folder.TrimEnd('\\', '/'));
        _mergeCand = _mergeTarget = null;
        if (folder.Equals(_popFolder, StringComparison.OrdinalIgnoreCase) && sig == _popSig) return;
        _popFolder = folder; _popSig = sig;

        _canvas.Children.Clear();
        _slots.Clear();
        foreach (ItemData d in Icons.GetData(folder))
        {
            var it = new Item(d.Name, d.Path, Icons.ToSource(d.Pix));
            var s = new Slot { It = it, Btn = MakeButton(it) };
            _slots.Add(s);
            _canvas.Children.Add(s.Btn);
        }
        if (_slots.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "Drag apps here", Width = PW, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24, 0, 0),
                Foreground = Res<Brush>("TextFillColorSecondaryBrush", new SolidColorBrush(Colors.Gray)),
            };
            if (Res<Style?>("BodyTextBlockStyle", null) is Style bs) hint.Style = bs;
            _canvas.Children.Add(hint);
        }
        Layout(null, -1, snap: true);
    }

    (double, double) SlotPos(int i)
    {
        int page = i / PerPage, j = i % PerPage;
        return (Pad + (j % Cols) * CellW + (CellW - ItemW) / 2, page * PageH + (j / Cols) * CellH);
    }

    void Layout(Slot? skip, int gap, bool snap = false)
    {
        int idx = 0;
        foreach (Slot s in _slots)
        {
            if (s == skip) continue;
            if (idx == gap) idx++;
            var (x, y) = SlotPos(idx++);
            if (snap) ElementCompositionPreview.GetElementVisual(s.Btn).ImplicitAnimations = null;
            Canvas.SetLeft(s.Btn, x);
            Canvas.SetTop(s.Btn, y);
        }
        _pages = Math.Max(1, (_slots.Count + PerPage - 1) / PerPage);
        _canvas.Height = _pages * PageH;
        _canvas.Width = PW;
        _pips.NumberOfPages = _pages;
        _pips.Visibility = _pages > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_page > _pages - 1) GoPage(_pages - 1);
        if (snap)
        {
            _canvas.UpdateLayout();
            foreach (Slot s in _slots) EnableSlide(s.Btn);
        }
    }

    /// <summary>Yer değişince öğe kayarak gider (Fluent: 250 ms decelerate).</summary>
    static void EnableSlide(UIElement e)
    {
        Visual v = ElementCompositionPreview.GetElementVisual(e);
        Compositor c = v.Compositor;
        var a = c.CreateVector3KeyFrameAnimation();
        a.Target = "Offset";
        a.InsertExpressionKeyFrame(1f, "this.FinalValue", c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(250);
        var col = c.CreateImplicitAnimationCollection();
        col["Offset"] = a;
        v.ImplicitAnimations = col;
    }

    Button MakeButton(Item it)
    {
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(Directory.Exists(it.Path) ? FolderGlyph(it)
            : new Image { Width = Icon, Height = Icon, Source = it.Icon, Stretch = Stretch.Uniform });
        var label = new TextBlock
        {
            Text = it.Name, TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, MaxWidth = ItemW - 8,
        };
        if (Res<Style?>("CaptionTextBlockStyle", null) is Style cs) label.Style = cs;
        label.TextTrimming = TextTrimming.CharacterEllipsis; label.MaxLines = 1; label.TextAlignment = TextAlignment.Center;
        stack.Children.Add(label);
        var b = new Button
        {
            Content = stack, Width = ItemW, Height = ItemH,
            Padding = new Thickness(4, 12, 4, 4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
            CornerRadius = Res("ControlCornerRadius", new CornerRadius(4)),
        };
        PlateOff(b, false);

        b.Click += (_, _) =>
        {
            if (_suppressClick) { _suppressClick = false; return; }
            if (Environment.TickCount64 - _openTick < 350) return;
            Activate(it);
        };
        var menu = new MenuFlyout();
        var open = new MenuFlyoutItem { Text = "Open", Icon = new SymbolIcon(Symbol.OpenFile) };
        open.Click += (_, _) => Activate(it);
        var remove = new MenuFlyoutItem { Text = "Remove from folder", Icon = new SymbolIcon(Symbol.Remove) };
        remove.Click += (_, _) => { var s = _slots.FirstOrDefault(x => x.It == it); if (s != null) RemoveItem(s); };
        menu.Items.Add(open);
        menu.Items.Add(remove);
        b.ContextFlyout = menu;
        b.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnItemPressed), true);
        b.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnItemMoved), true);
        b.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnItemReleased), true);
        b.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnItemReleased), true);
        return b;
    }

    /// <summary>Uygulama: çalıştırıp paneli kapat. Alt klasör: panelin içinde aç (kayarak).</summary>
    void Activate(Item it)
    {
        if (Directory.Exists(it.Path)) { Navigate(it.Path, +1); return; }
        try { Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); } catch { }
        Dismiss(false);
    }

    /// <summary>Alt klasörün hücre ikonu: içindeki ilk 4 uygulamanın mini önizlemesi (boşsa Windows klasör ikonu).</summary>
    UIElement FolderGlyph(Item it)
    {
        List<ItemData> prev;
        try { prev = Icons.GetData(it.Path).Take(4).ToList(); } catch { prev = new(); }
        if (prev.Count == 0) return new Image { Width = Icon, Height = Icon, Source = it.Icon, Stretch = Stretch.Uniform };
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < prev.Count; i++)
        {
            var im = new Image { Source = Icons.ToSource(prev[i].Pix), Stretch = Stretch.Uniform, Margin = new Thickness(1) };
            Grid.SetRow(im, i / 2); Grid.SetColumn(im, i % 2);
            g.Children.Add(im);
        }
        Brush fill = Res<Brush>("CardBackgroundFillColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)));
        Brush line = Res<Brush>("CardStrokeColorDefaultBrush", new SolidColorBrush(Color.FromArgb(0x20, 255, 255, 255)));
        return new Border
        {
            Width = Icon, Height = Icon, Padding = new Thickness(3), CornerRadius = new CornerRadius(8),
            Background = fill, BorderBrush = line, BorderThickness = new Thickness(1), Child = g,
        };
    }

    /// <summary>Düğme zeminleri tema fırçalarından (Subtle fill); sürüklerken plakasız.</summary>
    static void PlateOff(Button b, bool off)
    {
        try
        {
            Brush clear = new SolidColorBrush(Colors.Transparent);
            b.Resources["ButtonBackground"] = clear;
            b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Colors.Transparent);
            b.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Colors.Transparent);
            b.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Colors.Transparent);
            b.Resources["ButtonBackgroundPointerOver"] = off ? new SolidColorBrush(Colors.Transparent) : Res<Brush>("SubtleFillColorSecondaryBrush", clear);
            b.Resources["ButtonBackgroundPressed"] = off ? new SolidColorBrush(Colors.Transparent) : Res<Brush>("SubtleFillColorTertiaryBrush", clear);
            VisualStateManager.GoToState(b, "Normal", false);
        }
        catch (Exception e) { Log(e); }
    }

    // ---------- sürükleyerek sıralama ----------

    Slot? SlotOf(object sender) => _slots.FirstOrDefault(s => s.Btn == sender);

    void OnItemPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed) return;
        _press = SlotOf(sender);
        if (_press == null) return;
        _pressPt = e.GetCurrentPoint(_canvas).Position;
        _grab = new Windows.Foundation.Point(_pressPt.X - Canvas.GetLeft(_press.Btn), _pressPt.Y - Canvas.GetTop(_press.Btn));
        _drag = false;
        _pressTick = Environment.TickCount64;
    }

    void OnItemMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_press == null || _press.Btn != sender || _drag) return;
        var pt = e.GetCurrentPoint(_canvas);
        if (!pt.Properties.IsLeftButtonPressed) return;
        var p = pt.Position;
        if (Math.Abs(p.X - _pressPt.X) < 6 && Math.Abs(p.Y - _pressPt.Y) < 6) return;
        StartReorder(_press);
    }

    DispatcherQueueTimer? _dragTimer;
    Slot? _dragSlot;

    /// <summary>
    /// Sürükleyerek sıralama. WinUI fare yakalamasını yer yer koparıyordu, bu "bırakıldı" sanılıp uygulama yarı yolda
    /// bırakılıyor, dizilme bozuluyordu. Artık sürükleme boyunca imleç ve sol tuş doğrudan okunur (~8 ms); yalnız
    /// tuş gerçekten bırakılınca yerine konur.
    /// </summary>
    void StartReorder(Slot s)
    {
        _drag = true; _suppressClick = true; _dragSlot = s;
        _gap = Math.Max(0, _slots.IndexOf(s)); _gapCand = -1; _mergeCand = _mergeTarget = null;
        if (_nav.Count > 0) ShowOut(true);
        Lift(s.Btn, true);
        ElementCompositionPreview.GetElementVisual(s.Btn).ImplicitAnimations = null;
        PlateOff(s.Btn, true);
        Canvas.SetZIndex(s.Btn, 10);
        _dragTimer ??= DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(8);
        _dragTimer.Tick -= DragTick;
        _dragTimer.Tick += DragTick;
        _dragTimer.Start();
    }

    void DragTick(DispatcherQueueTimer t, object o)
    {
        Slot? s = _dragSlot;
        if (s == null || !_visible) { ShowOut(false); t.Stop(); _drag = false; return; }
        GetCursorPos(out POINT c);
        double rx = (c.X - _win.X) / _scale, ry = (c.Y - _win.Y) / _scale;      // pencere içi (DIP)
        var co = _canvas.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point(0, 0));
        var p = new Windows.Foundation.Point(rx - co.X, ry - co.Y);              // tuval içi (sayfa kaydırması dahil)
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        bool overBack = _nav.Count > 0 && rx >= 0 && rx <= 30 && ry >= HeaderH - 8 && ry <= _ph;      // yalnız sol kenar şeridi = bir üst klasör
        if (overBack != _backHot) { _backHot = overBack; SetOutHot(overBack); }
        if (!down) { ShowOut(false); t.Stop(); if (overBack) DropToParent(s); else DropReorder(s, p); return; }
        if (rx < 0 || ry < 0 || rx > PW || ry > _ph)                           // panelden çıktı: dışarı sürükleme
        {
            ClearMerge(); ShowOut(false);
            t.Stop(); _drag = false; _dragSlot = null; _press = null;
            StartOleDrag(s, null);
            return;
        }
        // kenarda tut: ok göstergesi dolar, dolunca sayfa kayar; tutmaya devam = art arda
        double top = HeaderH, bot = HeaderH + _scroll.Height;
        int dir = overBack ? 0 : ry < top + EdgeW ? -1 : ry > bot - EdgeW ? 1 : 0;
        if (dir != 0 && _page + dir >= 0 && _page + dir < _pages)
        {
            long now = Environment.TickCount64;
            if (_edgeSince == 0 || _edgeDir != dir) { _edgeSince = now; _edgeDir = dir; ShowEdge(dir); }
            else if (now - _edgeSince > EdgeMs) { GoPage(_page + dir); _edgeSince = now + 250; ShowEdge(dir); }
        }
        else if (_edgeSince != 0) { _edgeSince = 0; _edgeDir = 0; HideEdge(); }
        Canvas.SetLeft(s.Btn, p.X - _grab.X);
        Canvas.SetTop(s.Btn, p.Y - _grab.Y);
        if (overBack) ClearMerge(); else UpdateGapOrMerge(s, p);
    }

    /// <summary>
    /// Sürüklenen öğe bir hücrenin ortasında duruyorsa: MergeMs sonra o uygulamayla klasör olur (üstü klasörse içine girer);
    /// hücrenin kenarındaysa normal sıralama (boşluk imleci izler). Hücre ortasında boşluk oynamaz, altındaki öğe yerinde kalır.
    /// </summary>
    void UpdateGapOrMerge(Slot s, Windows.Foundation.Point p)
    {
        int page = Math.Clamp((int)Math.Floor(p.Y / PageH), 0, _pages - 1);
        int col = Math.Clamp((int)Math.Floor((p.X - Pad) / CellW), 0, Cols - 1);
        int row = Math.Clamp((int)Math.Floor((p.Y - page * PageH) / CellH), 0, _rowsWin - 1);
        int ci = page * PerPage + row * Cols + col;
        var (cx, cy) = SlotPos(ci);
        bool center = Math.Abs(p.X - (cx + ItemW / 2)) < 22 && Math.Abs(p.Y - (cy + 30)) < 22;   // yalnız ikonun üstü

        Slot? vis = null;
        if (ci != _gap)
        {
            var others = _slots.Where(x => x != s).ToList();
            int pos = ci < _gap ? ci : ci - 1;
            if (pos >= 0 && pos < others.Count) vis = others[pos];
        }

        long now = Environment.TickCount64;
        if (ci == _gap) { ClearMerge(); _gapCand = -1; return; }
        if (center && vis != null)
        {
            _gapCand = -1;
            long need = Directory.Exists(vis.It.Path) ? 120 : MergeMs;      // klasörün içine hemen, uygulamayla klasör için bekle
            if (_mergeCand != vis) { ClearMerge(); _mergeCand = vis; _mergeSince = now; }
            else if (_mergeTarget == null && now - _mergeSince > need)
            {
                _mergeTarget = vis;
                Lift(vis.Btn, true);
                PlateMark(vis.Btn, true);
            }
            return;
        }
        ClearMerge();
        int g = TargetIndex(p);
        if (g == _gap) { _gapCand = -1; return; }
        if (_gapCand != g) { _gapCand = g; _gapSince = now; return; }       // boşluk, hücrede biraz durunca kayar (geçerken oynamaz)
        if (now - _gapSince > 120) { _gap = g; _gapCand = -1; Layout(s, _gap); }
    }

    void ShowOut(bool on)
    {
        if (on) _outText.Text = "Move to " + Path.GetFileName(_nav[^1].TrimEnd('\\', '/'));
        Fade(_outLeft, on ? 1 : 0, on ? 150 : 120);
        if (!on) SetOutHot(false);
    }

    void SetOutHot(bool hot)
    {
        // kenar göstergesiyle aynı animasyon: vurgu dolgusu EdgeMs boyunca soldan sağa (sol şeritte yukarıdan aşağı) dolar
        foreach (var (fill, vertical) in new[] { (_outFillT, false), (_outFillL, true) })
        {
            Visual v = ElementCompositionPreview.GetElementVisual(fill);
            v.CenterPoint = Vector3.Zero;
            Vector3 empty = vertical ? new Vector3(1, 0, 1) : new Vector3(0, 1, 1);
            if (hot)
            {
                var a = v.Compositor.CreateVector3KeyFrameAnimation();
                a.InsertKeyFrame(0f, empty);
                a.InsertKeyFrame(1f, Vector3.One, v.Compositor.CreateLinearEasingFunction());
                a.Duration = TimeSpan.FromMilliseconds(EdgeMs);
                v.StartAnimation("Scale", a);
            }
            else { v.StopAnimation("Scale"); v.Scale = empty; }
        }
    }

    void ClearMerge()
    {
        if (_mergeTarget != null) { Lift(_mergeTarget.Btn, false); PlateMark(_mergeTarget.Btn, false); }
        _mergeCand = _mergeTarget = null;
    }

    /// <summary>Klasör oluşturma/girme işareti: hücre vurgu renginde hafif dolar.</summary>
    static void PlateMark(Button b, bool on)
    {
        try
        {
            Brush clear = new SolidColorBrush(Colors.Transparent);
            Brush acc = Res<Brush>("AccentFillColorDefaultBrush", new SolidColorBrush(Colors.DodgerBlue));
            if (on)
            {
                b.Resources["ButtonBackground"] = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0xA0, 0xFF));
                b.Resources["ButtonBackgroundPointerOver"] = b.Resources["ButtonBackground"];
                b.Resources["ButtonBackgroundPressed"] = b.Resources["ButtonBackground"];
                b.Resources["ButtonBorderBrush"] = acc;
            }
            else
            {
                PlateOff(b, false);
                b.Resources["ButtonBackground"] = clear;
                b.Resources["ButtonBorderBrush"] = new SolidColorBrush(Colors.Transparent);
            }
            VisualStateManager.GoToState(b, "Normal", false);
        }
        catch { }
    }

    void DropReorder(Slot s, Windows.Foundation.Point p)
    {
        _drag = false; _dragSlot = null; _press = null;
        Lift(s.Btn, false);
        if (_edgeSince != 0) { _edgeSince = 0; _edgeDir = 0; HideEdge(); }
        if (_mergeTarget != null)                                   // üstünde durulan uygulamayla klasör / klasörün içine
        {
            Slot t = _mergeTarget; _mergeTarget = _mergeCand = null;
            try { s.Btn.ReleasePointerCaptures(); } catch { }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _suppressClick = false);
            MergeInto(s, t);
            return;
        }
        ClearMerge();
        int target = TargetIndex(p);                                  // bırakılan hücreye yerleş
        _slots.Remove(s);
        _slots.Insert(Math.Clamp(target, 0, _slots.Count), s);
        EnableSlide(s.Btn);
        Canvas.SetZIndex(s.Btn, 0);
        PlateOff(s.Btn, false);
        Layout(null, -1);
        try { s.Btn.ReleasePointerCaptures(); } catch { }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _suppressClick = false);
        Order.Set(_folder, _slots.Select(x => Path.GetFileName(x.It.Path)));
        Icons.Invalidate(_folder);
        _popSig = Icons.Signature(_folder);
        Tiles.Refresh();
    }

    /// <summary>Öğe ← üstüne bırakıldı: bir üst klasöre taşınır.</summary>
    void DropToParent(Slot s)
    {
        _drag = false; _dragSlot = null; _press = null;
        _backHot = false; PlateMark(_back, false);
        Lift(s.Btn, false);
        if (_edgeSince != 0) { _edgeSince = 0; _edgeDir = 0; HideEdge(); }
        ClearMerge();
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _suppressClick = false);
        try { s.Btn.ReleasePointerCaptures(); } catch { }
        string parent = _nav[^1];
        if (Groups.MoveTo(_folder, parent, s.It.Path)) { Icons.Invalidate(parent); ItemLeft(s); }
        else { Canvas.SetZIndex(s.Btn, 0); PlateOff(s.Btn, false); Layout(null, -1); }
    }

    /// <summary>
    /// s, t'nin üstüne bırakıldı. t klasörse s onun içine taşınır; değilse yeni bir alt klasör ("New folder") açılır,
    /// ikisi de içine girer ve klasör t'nin yerinde durur.
    /// </summary>
    void MergeInto(Slot s, Slot t)
    {
        string from = _folder, sp = s.It.Path, tp = t.It.Path;
        var names = _slots.Where(x => x != s).Select(x => Path.GetFileName(x.It.Path)).ToList();
        string dest = tp;
        try
        {
            if (!Directory.Exists(tp))
            {
                dest = Groups.New(from);
                if (!Groups.MoveTo(from, dest, tp)) { Directory.Delete(dest); throw new IOException("move failed"); }
                int i = names.FindIndex(n => n.Equals(Path.GetFileName(tp), StringComparison.OrdinalIgnoreCase));
                if (i >= 0) names[i] = Path.GetFileName(dest);
            }
            if (!sp.Equals(dest, StringComparison.OrdinalIgnoreCase)) Groups.MoveTo(from, dest, sp);
            Order.Set(from, names);
        }
        catch (Exception e) { Log(e); }
        Icons.Invalidate(from); Icons.Invalidate(dest);
        _popSig = "";
        Populate(from);
        EnsureSize();
        Tiles.Refresh();
    }

    /// <summary>
    /// Öğe panelden çıkınca: Explorer'ın anladığı dosya sürüklemesi (kısayol dosyası, taşı). Masaüstüne bırakılırsa
    /// Explorer kısayolu bırakılan yere taşır; başka kutuya bırakılırsa o klasöre eklenir. Sonra paneli güncelle.
    /// </summary>
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    DragGhost? _ghost;

    /// <summary>
    /// Öğe panelden çıktı: imleci izleyen ikonla elle sürükle (Windows OLE sürüklemesi WinUI'de fare olaylarını
    /// görmüyor). Bırakınca: masaüstü → kısayol masaüstünde o noktaya; başka klasör kutusu → o klasöre; başka yer → iptal.
    /// </summary>
    void StartOleDrag(Slot s, PointerRoutedEventArgs? e)
    {
        if (_oleDrag) return;
        _oleDrag = true;
        ClearMerge();
        _press = null; _drag = false;
        Lift(s.Btn, false);
        if (_edgeSince != 0) { _edgeSince = 0; _edgeDir = 0; HideEdge(); }
        try { s.Btn.ReleasePointerCaptures(); } catch { }
        ReleaseCapture();
        Canvas.SetZIndex(s.Btn, 0);
        PlateOff(s.Btn, false);
        EnableSlide(s.Btn);
        Layout(null, -1);
        s.Btn.Opacity = 0.35;                                  // yerinde soluk kalır, bırakma başarılıysa kalkar
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => _suppressClick = false);

        _ghost ??= new DragGhost();
        Pix? pix = Icons.GetData(_folder).FirstOrDefault(x => x.Path.Equals(s.It.Path, StringComparison.OrdinalIgnoreCase))?.Pix;
        _ghost.SetImage(pix, (int)Math.Round(48 * _scale));
        GetCursorPos(out POINT c0);
        _ghost.MoveTo(c0.X, c0.Y);

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(8);
        timer.Tick += (_, _) =>
        {
            GetCursorPos(out POINT c);
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0) { _ghost.MoveTo(c.X, c.Y); return; }
            timer.Stop();
            _ghost.Hide();
            s.Btn.Opacity = 1;
            try { DropOutside(s, c); } catch (Exception ex) { Log(ex); }
            _oleDrag = false;
        };
        timer.Start();
    }

    void DropOutside(Slot s, POINT c)
    {
        // panelin içine geri bırakıldı: hiçbir şey yapma
        if (c.X >= _win.X && c.Y >= _win.Y && c.X < _win.X + _win.Width && c.Y < _win.Y + _win.Height) return;
        IntPtr h = WindowFromPoint(c);
        string path = s.It.Path;
        if (Tiles.FolderAt(h) is string other)
        {
            if (other.Equals(_folder, StringComparison.OrdinalIgnoreCase)) return;
            if (Groups.MoveTo(_folder, other, path)) ItemLeft(s);
            return;
        }
        if (DesktopClick.IsDesktopWindow(h))
        {
            string? dst = Groups.RemoveTo(_folder, path);
            if (dst == null) return;
            ItemLeft(s);
            var at = new PointInt32(c.X, c.Y);
            _ = Task.Run(() => DesktopClick.PlaceOnDesktop(dst, at));
        }
        // başka bir pencere: iptal, öğe klasörde kalır
    }

    /// <summary>Öğe dosyası klasörden çıktı (sürükle-bırakla): paneli ve kutuyu güncelle, boşaldıysa klasörü kaldır.</summary>
    void ItemLeft(Slot s)
    {
        _slots.Remove(s);
        _canvas.Children.Remove(s.Btn);
        Icons.Invalidate(_folder);
        if (_slots.Count == 0) { Emptied(); return; }
        Layout(null, -1);
        Order.Set(_folder, _slots.Select(x => Path.GetFileName(x.It.Path)));
        _popSig = Icons.Signature(_folder);
        Tiles.Refresh();
    }

    /// <summary>
    /// Son uygulama çıktı. En üst klasör: panel kapanır, klasör kalkar. Alt klasör: bir üst klasöre kayarak dönülür,
    /// boşalan alt klasör silinir.
    /// </summary>
    void Emptied()
    {
        string f = _folder;
        if (_nav.Count > 0)
        {
            GoBack();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () =>
            {
                await Task.Delay(450);
                try { Directory.Delete(f); Order.Forget(Order.Key(f)); } catch { }
                Icons.Invalidate(f); Icons.Invalidate(_folder);
                if (_visible) { _popSig = ""; Populate(_folder); }
                Tiles.Refresh();
            });
            return;
        }
        Dismiss(false);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, async () => { await Task.Delay(400); Groups.Dissolve(f); Tiles.Refresh(); });
    }

    int TargetIndex(Windows.Foundation.Point p)
    {
        int page = Math.Clamp((int)Math.Floor(p.Y / PageH), 0, _pages - 1);
        int col = Math.Clamp((int)Math.Floor((p.X - Pad) / CellW), 0, Cols - 1);
        int row = Math.Clamp((int)Math.Floor((p.Y - page * PageH) / CellH), 0, _rowsWin - 1);
        return Math.Clamp(page * PerPage + row * Cols + col, 0, Math.Max(0, _slots.Count - 1));
    }

    void OnItemReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_drag) return;                     // sürüklemeyi yoklama bitirir (yakalama kopması bırakma sayılmaz)
        if (_press != null && _press.Btn == sender) _press = null;
    }

    /// <summary>Delete folder: popup kapanır, onaydan sonra uygulamalar masaüstüne döner ve klasör kalkar.</summary>
    void DeleteFolder()
    {
        string f = _folder, name = Path.GetFileName(f.TrimEnd('\\', '/'));
        Dismiss(false);
        _ = Task.Run(() =>
        {
            Thread.Sleep(250);
            int r = MessageBoxW(IntPtr.Zero, $"Delete the folder \"{name}\"?\nIts apps will be kept in a regular folder on the desktop.", "Delete folder",
                0x4 | 0x30 | 0x40000 | 0x10000);
            if (r != 6) return;
            if (!Groups.Dissolve(f))
                MessageBoxW(IntPtr.Zero, "The folder could not be moved to the desktop, so it was kept.", "Delete folder", 0x10 | 0x40000);
            Tiles.Refresh();
        });
    }

    /// <summary>Uygulamayı klasörden çıkar (masaüstüne), kalanlar kayarak yer kapatır; son uygulamayla klasör kalkar.</summary>
    void RemoveItem(Slot s)
    {
        if (!Groups.Remove(_folder, s.It.Path)) { Layout(null, -1); return; }
        _slots.Remove(s);
        _canvas.Children.Remove(s.Btn);
        if (_slots.Count == 0) { Emptied(); return; }
        Layout(null, -1);
        Order.Set(_folder, _slots.Select(x => Path.GetFileName(x.It.Path)));
        Icons.Invalidate(_folder);
        _popSig = Icons.Signature(_folder);
        Tiles.Refresh();
    }

    // ---------- alt klasörler: panelin içinde açılır, kayarak ----------

    void GoBack()
    {
        if (_nav.Count == 0 || _navBusy) return;
        Navigate(_nav[^1], -1);
    }

    /// <summary>
    /// Windows 11 Başlat klasörü geçişi: giden içerik sola kayıp solar, gelen sağdan kayıp belirir (geri dönüşte ters yön).
    /// Giden içeriğin görüntüsü (RenderTargetBitmap) alınıp ayrı bir katmanda kaydırılır; pencere boyutu animasyonda
    /// değişmez, yalnız alt klasör daha çok satır istiyorsa bir kez büyür.
    /// </summary>
    async void Navigate(string target, int dir)
    {
        if (_navBusy || _anim || _closing || !_visible) return;
        _navBusy = true;
        try
        {
            if (_title.FocusState != FocusState.Unfocused)
            {
                string before = _folder;
                CommitRename();
                if (dir > 0 && !before.Equals(_folder, StringComparison.OrdinalIgnoreCase)) target = Path.Combine(_folder, Path.GetFileName(target));
            }
            if (!Directory.Exists(target)) { _popSig = ""; Populate(_folder); return; }

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_content, (int)Math.Round(PW * _scale), (int)Math.Round(_ph * _scale));
            _ghostImg.Source = rtb; _ghostImg.Width = PW; _ghostImg.Height = _ph;
            Visual gv = ElementCompositionPreview.GetElementVisual(_ghostImg);
            gv.Opacity = 1; gv.Properties.InsertVector3("Translation", Vector3.Zero);
            _ghostImg.Visibility = Visibility.Visible;

            if (dir > 0) _nav.Add(_folder); else _nav.RemoveAt(_nav.Count - 1);
            _folder = target; _popSig = "";
            Populate(target);
            _page = 0; GoPage(0, instant: true);
            _back.Visibility = _nav.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            bool grown = EnsureSize();

            Visual cv = ElementCompositionPreview.GetElementVisual(_content);
            cv.Opacity = 0;
            if (grown) await Frames(2);
            FocusItems();

            Compositor c = cv.Compositor;
            var ease = c.CreateCubicBezierEasingFunction(new Vector2(0f, 0f), new Vector2(0f, 1f));
            var dur = TimeSpan.FromMilliseconds(300);
            Vector3KeyFrameAnimation SlideAnim(float from, float to)
            {
                var a = c.CreateVector3KeyFrameAnimation();
                a.Duration = dur;
                a.InsertKeyFrame(0f, new Vector3(from, 0, 0));
                a.InsertKeyFrame(1f, new Vector3(to, 0, 0), ease);
                return a;
            }
            ScalarKeyFrameAnimation FadeAnim(float from, float to, int ms)
            {
                var a = c.CreateScalarKeyFrameAnimation();
                a.Duration = TimeSpan.FromMilliseconds(ms);
                a.InsertKeyFrame(0f, from);
                a.InsertKeyFrame(1f, to, ease);
                return a;
            }
            cv.StartAnimation("Translation", SlideAnim((float)(dir * Shift), 0));
            cv.StartAnimation("Opacity", FadeAnim(0, 1, 250));
            gv.StartAnimation("Translation", SlideAnim(0, (float)(-dir * Shift)));
            gv.StartAnimation("Opacity", FadeAnim(1, 0, 200));
            await Task.Delay(320);
        }
        catch (Exception e) { Log(e); }
        finally
        {
            _ghostImg.Visibility = Visibility.Collapsed;
            _ghostImg.Source = null;
            _navBusy = false;
        }
    }

    /// <summary>Alt klasör daha çok satır / sayfa noktası istiyorsa pencere bir kez büyür (küçülmez). Büyüdüyse true.</summary>
    bool EnsureSize()
    {
        int rows = Math.Clamp((_slots.Count + Cols - 1) / Cols, 1, MaxRows);
        if (rows > _rowsWin) { _rowsWin = rows; _scroll.Height = rows * CellH; Layout(null, -1, snap: true); GoPage(_page, instant: true); }
        double ph = HeaderH + _rowsWin * CellH + 12;
        if (ph <= _ph + 0.5) return false;
        _ph = ph;
        int h = (int)Math.Round(_ph * _scale), m = (int)(8 * _scale);
        RectInt32 wa = DisplayArea.GetFromPoint(new PointInt32(_win.X, _win.Y), DisplayAreaFallback.Nearest).WorkArea;
        int top = _win.Y;
        if (top + h > wa.Y + wa.Height - m) top = Math.Max(wa.Y + m, wa.Y + wa.Height - h - m);
        _win = new RectInt32(_win.X, top, _win.Width, h);
        AppWindow.MoveAndResize(_win);
        return true;
    }

    // ---------- yeniden adlandırma ----------

    void CommitRename()
    {
        if (string.IsNullOrEmpty(_folder)) return;
        string old = Path.GetFileName(_folder.TrimEnd('\\', '/'));
        string want = new string(_title.Text.Trim().Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray()).Trim().TrimEnd('.');
        if (want.Length == 0 || want.StartsWith('_') || want == old) { _title.Text = old; return; }
        string root = Path.GetDirectoryName(_folder.TrimEnd('\\', '/'))!;
        string target = Path.Combine(root, want);
        try
        {
            if (Directory.Exists(target) && !want.Equals(old, StringComparison.OrdinalIgnoreCase)) { _title.Text = old; return; }
            string oldKey = Order.Key(_folder);
            Directory.Move(_folder, target);
            Order.Rename(oldKey, Order.Key(target));
            if (_nav.Count == 0)                 // en üst klasör: masaüstü kutusu da yeni adı alır
            {
                Tiles.Rename(old, want);
                if (_dismissFolder.Equals(_folder, StringComparison.OrdinalIgnoreCase)) _dismissFolder = target;
                _top = target;
            }
            Icons.Invalidate(_folder);
            _folder = target; _popFolder = target; _popSig = "";
            Populate(_folder);                   // öğelerin yolları yeni klasör adına göre yenilensin
            _title.Text = want;
        }
        catch { _title.Text = old; }
    }
}
