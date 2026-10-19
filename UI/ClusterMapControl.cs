using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HddDefrag.Core;

namespace HddDefrag.UI;

/// <summary>
/// Zeichnet die Clusterkarte als Raster quadratischer Blöcke mit 1 px breiten Rasterlinien in eine <see cref="WriteableBitmap"/>
/// (Standard: Rastermaß 8 = 7 px Block + 1 px Linie, siehe <see cref="BlockPitch"/>).
/// <para>
/// Die Bitmap wird in echten Gerätepixeln angelegt und 1:1 ohne Skalierung gezeichnet, damit die Blöcke auch bei
/// Windows-Skalierung (125 %, 150 % …) scharf und gleich groß bleiben. Das Raster wird in der Fläche zentriert,
/// der Rest wird mit der Rasterfarbe gefüllt.
/// </para>
/// Die Aktualisierung ist gedrosselt: <see cref="IDefragEngine.MapChanged"/> markiert die Karte nur als veraltet,
/// ein Timer rendert höchstens alle ~150 ms neu.
/// </summary>
public sealed class ClusterMapControl : FrameworkElement
{
    /// <summary>
    /// Rastermaß in Gerätepixeln: Blockkante plus 1 px Rasterlinie (3 bis 65). Hauptfenster 8, Vollbild 7.
    /// </summary>
    public static readonly DependencyProperty BlockPitchProperty = DependencyProperty.Register(
        nameof(BlockPitch), typeof(int), typeof(ClusterMapControl),
        new FrameworkPropertyMetadata(8, OnBlockPitchChanged),
        value => (int)value >= 3 && (int)value <= 65);

    private int _pitch = 8;
    private int BlockEdge => _pitch - 1; // Block ohne Rasterlinie

    private readonly DispatcherTimer _timer;
    private readonly ToolTip _tip = new();
    private readonly uint[] _palette = new uint[6];
    private uint _gridColor;
    private readonly Brush _gridBrush;

    private IDefragEngine? _engine;
    private WriteableBitmap? _bitmap;
    private uint[] _pixels = Array.Empty<uint>();
    private uint[] _gridTemplate = Array.Empty<uint>();
    private ClusterState[] _states = Array.Empty<ClusterState>();
    private int _cols, _rows, _pixelWidth, _pixelHeight;
    private double _dpiX = 1.0, _dpiY = 1.0;
    private int _offsetX, _offsetY; // Zentrierung des Rasters in Gerätepixeln
    private volatile bool _dirty;
    private bool _subscribed;

    public ClusterMapControl()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        LoadColors();
        _gridBrush = new SolidColorBrush(ToColor(_gridColor));
        _gridBrush.Freeze();

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) =>
        {
            if (_dirty) Refresh();
        };

        _tip.Placement = PlacementMode.Relative;
        _tip.PlacementTarget = this;
        ToolTipService.SetInitialShowDelay(this, 150);
        ToolTipService.SetShowDuration(this, 60000);

        Loaded += (_, _) =>
        {
            // Erst jetzt ist der Monitor (und damit die DPI) sicher bekannt.
            UpdateGrid(force: true);
            Subscribe();
            _timer.Start();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Unsubscribe();
        };
    }

    /// <summary>Rastermaß in Gerätepixeln (Blockkante + 1 px Rasterlinie), 3 bis 65.</summary>
    public int BlockPitch
    {
        get => (int)GetValue(BlockPitchProperty);
        set => SetValue(BlockPitchProperty, value);
    }

    private static void OnBlockPitchChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var map = (ClusterMapControl)d;
        map._pitch = (int)e.NewValue;
        map.UpdateGrid(force: true); // die Anzahl der Blöcke ändert sich
    }

    /// <summary>Die Engine, deren Karte angezeigt wird (darf null sein).</summary>
    public IDefragEngine? Engine
    {
        get => _engine;
        set
        {
            if (ReferenceEquals(_engine, value)) return;
            Unsubscribe();
            _engine = value;
            if (IsLoaded) Subscribe();
            _states = Array.Empty<ClusterState>();
            Refresh();
        }
    }

    /// <summary>Rendert die Karte sofort neu (einmal <see cref="IDefragEngine.RenderMap"/> pro Aufruf).</summary>
    public void Refresh()
    {
        _dirty = false;
        Render();
    }

    private void Subscribe()
    {
        if (_subscribed || _engine == null) return;
        _engine.MapChanged += OnMapChanged;
        _subscribed = true;
        _dirty = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed || _engine == null) return;
        _engine.MapChanged -= OnMapChanged;
        _subscribed = false;
    }

    // Wird auf einem Hintergrund-Thread aufgerufen: nur ein Flag setzen.
    private void OnMapChanged() => _dirty = true;

    private void LoadColors()
    {
        _palette[(int)ClusterState.Free] = Pack(Lookup("MapFreeColor", Colors.White));
        _palette[(int)ClusterState.Used] = Pack(Lookup("MapUsedColor", Color.FromRgb(0x00, 0x00, 0xFF)));
        _palette[(int)ClusterState.System] = Pack(Lookup("MapSystemColor", Color.FromRgb(0x00, 0xA0, 0x00)));
        _palette[(int)ClusterState.Unmovable] = Pack(Lookup("MapUnmovableColor", Color.FromRgb(0x60, 0x60, 0x60)));
        _palette[(int)ClusterState.Fragmented] = Pack(Lookup("MapFragmentedColor", Color.FromRgb(0xFF, 0x00, 0x00)));
        _palette[(int)ClusterState.Moving] = Pack(Lookup("MapMovingColor", Color.FromRgb(0xFF, 0xFF, 0x00)));
        _gridColor = Pack(Lookup("MapGridColor", Color.FromRgb(0x40, 0x40, 0x40)));
    }

    private Color Lookup(string key, Color fallback)
        => TryFindResource(key) is Color c ? c : (Application.Current?.TryFindResource(key) is Color c2 ? c2 : fallback);

    private static uint Pack(Color c) => 0xFF000000u | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static Color ToColor(uint packed)
        => Color.FromRgb((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed);

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateGrid(force: false);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateGrid(force: true); // anderer Monitor: Bitmap in der neuen Gerätepixelgröße aufbauen
    }

    /// <summary>
    /// Berechnet Spalten, Zeilen und Zentrierung aus der Fläche in Gerätepixeln und legt bei Bedarf die Bitmap neu an.
    /// </summary>
    private void UpdateGrid(bool force)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;

        // Gerätepixel der Fläche; Layoutrundung liefert ganze Pixel, das Epsilon fängt Rundungsreste ab.
        int pxW = (int)Math.Floor(ActualWidth * _dpiX + 0.01);
        int pxH = (int)Math.Floor(ActualHeight * _dpiY + 0.01);
        int cols = Math.Max(0, (pxW - 1) / _pitch);
        int rows = Math.Max(0, (pxH - 1) / _pitch);
        int bmpW = cols * _pitch + 1;
        int bmpH = rows * _pitch + 1;
        _offsetX = Math.Max(0, (pxW - bmpW) / 2);
        _offsetY = Math.Max(0, (pxH - bmpH) / 2);

        bool unchanged = cols == _cols && rows == _rows && _bitmap != null
            && _bitmap.PixelWidth == bmpW && _bitmap.PixelHeight == bmpH
            && Math.Abs(_bitmap.DpiX - dpi.PixelsPerInchX) < 0.01 && Math.Abs(_bitmap.DpiY - dpi.PixelsPerInchY) < 0.01;
        if (unchanged && !force)
        {
            InvalidateVisual(); // nur die Zentrierung kann sich geändert haben
            return;
        }

        _cols = cols;
        _rows = rows;
        if (cols == 0 || rows == 0)
        {
            _bitmap = null;
            _pixels = Array.Empty<uint>();
            _gridTemplate = Array.Empty<uint>();
            InvalidateVisual();
            return;
        }

        _pixelWidth = bmpW;
        _pixelHeight = bmpH;
        // Bitmap-DPI = Monitor-DPI: dann entspricht ein Bitmappixel genau einem Gerätepixel.
        _bitmap = new WriteableBitmap(bmpW, bmpH, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Bgr32, null);
        _pixels = new uint[bmpW * bmpH];
        _gridTemplate = new uint[bmpW * bmpH];
        Array.Fill(_gridTemplate, _gridColor);
        _states = Array.Empty<ClusterState>();
        Refresh();
    }

    private void Render()
    {
        if (_bitmap == null || _cols == 0 || _rows == 0) return;

        int blockCount = _cols * _rows;
        ClusterState[] states = Array.Empty<ClusterState>();
        if (_engine != null)
        {
            try
            {
                states = _engine.RenderMap(blockCount) ?? Array.Empty<ClusterState>();
            }
            catch
            {
                // Die Karte ist rein kosmetisch; ein Fehler der Engine darf die Oberfläche nicht stören.
                states = Array.Empty<ClusterState>();
            }
        }
        _states = states;

        Array.Copy(_gridTemplate, _pixels, _pixels.Length);
        uint free = _palette[(int)ClusterState.Free];
        int edge = BlockEdge;
        for (int row = 0; row < _rows; row++)
        {
            for (int col = 0; col < _cols; col++)
            {
                int index = row * _cols + col;
                uint color = free;
                if (index < states.Length)
                {
                    int s = (int)states[index];
                    if ((uint)s < (uint)_palette.Length) color = _palette[s];
                }

                int x0 = col * _pitch + 1;
                int y0 = row * _pitch + 1;
                for (int y = 0; y < edge; y++)
                {
                    int offset = (y0 + y) * _pixelWidth + x0;
                    for (int x = 0; x < edge; x++) _pixels[offset + x] = color;
                }
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels, _pixelWidth * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Randfläche in der Rasterfarbe; die Bitmap in ihrer natürlichen Größe (1 Bitmappixel = 1 Gerätepixel), zentriert, ohne Streckung.
        dc.DrawRectangle(_gridBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap == null) return;

        var rect = new Rect(_offsetX / _dpiX, _offsetY / _dpiY, _pixelWidth / _dpiX, _pixelHeight / _dpiY);
        // Führungslinien an allen vier Kanten: WPF rastet die Bitmap damit auf ganze Gerätepixel ein,
        // selbst wenn ein übergeordnetes Element auf einer Bruchteilposition liegt.
        var guidelines = new GuidelineSet(new[] { rect.Left, rect.Right }, new[] { rect.Top, rect.Bottom });
        guidelines.Freeze();
        dc.PushGuidelineSet(guidelines);
        dc.DrawImage(_bitmap, rect);
        dc.Pop();
    }

    /// <summary>Index des Blocks unter einem Punkt (in DIPs relativ zum Steuerelement) oder -1.</summary>
    private int BlockIndexAt(Point p)
    {
        if (_cols == 0 || _rows == 0) return -1;
        int px = (int)Math.Floor(p.X * _dpiX) - _offsetX - 1; // 1 px: obere/linke Rasterlinie
        int py = (int)Math.Floor(p.Y * _dpiY) - _offsetY - 1;
        if (px < 0 || py < 0) return -1;
        int col = px / _pitch;
        int row = py / _pitch;
        if (col >= _cols || row >= _rows) return -1;
        return row * _cols + col;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        int index = BlockIndexAt(p);
        if (index < 0)
        {
            ToolTip = null;
            return;
        }

        ClusterState state = index < _states.Length ? _states[index] : ClusterState.Free;
        _tip.Content = $"Block {UiFormat.Number(index + 1)} von {UiFormat.Number(_cols * _rows)}: {StateText(state)}";
        _tip.HorizontalOffset = p.X + 14;
        _tip.VerticalOffset = p.Y + 18;
        if (!ReferenceEquals(ToolTip, _tip)) ToolTip = _tip;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
    }

    /// <summary>Deutsche Bezeichnung eines Kartenzustands.</summary>
    public static string StateText(ClusterState state) => state switch
    {
        ClusterState.Free => "Frei",
        ClusterState.Used => "Belegt",
        ClusterState.System => "Systemdateien ($MFT)",
        ClusterState.Unmovable => "Nicht verschiebbar",
        ClusterState.Fragmented => "Fragmentiert",
        ClusterState.Moving => "Wird gerade verschoben",
        _ => "Unbekannt",
    };
}
