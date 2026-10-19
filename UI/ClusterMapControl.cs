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
/// Zeichnet die Clusterkarte als Raster kleiner Blöcke (7 px Block + 1 px Raster) in eine <see cref="WriteableBitmap"/>.
/// Die Aktualisierung ist gedrosselt: <see cref="IDefragEngine.MapChanged"/> markiert die Karte nur als veraltet,
/// ein Timer rendert höchstens alle ~150 ms neu.
/// </summary>
public sealed class ClusterMapControl : FrameworkElement
{
    private const int BlockSize = 7;
    private const int CellSize = BlockSize + 1; // Block + 1 px Rasterlinie

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
    private double _dpiScale = 1.0;
    private volatile bool _dirty;
    private bool _subscribed;
    private int _lastTipIndex = -1;

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
            Subscribe();
            _timer.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            Unsubscribe();
        };
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
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int pxW = (int)Math.Floor(sizeInfo.NewSize.Width * _dpiScale);
        int pxH = (int)Math.Floor(sizeInfo.NewSize.Height * _dpiScale);
        int cols = Math.Max(0, (pxW - 1) / CellSize);
        int rows = Math.Max(0, (pxH - 1) / CellSize);
        if (cols == _cols && rows == _rows && _bitmap != null) return;

        _cols = cols;
        _rows = rows;
        _lastTipIndex = -1;
        if (cols == 0 || rows == 0)
        {
            _bitmap = null;
            return;
        }

        _pixelWidth = cols * CellSize + 1;
        _pixelHeight = rows * CellSize + 1;
        _bitmap = new WriteableBitmap(_pixelWidth, _pixelHeight, 96 * _dpiScale, 96 * _dpiScale, PixelFormats.Bgra32, null);
        _pixels = new uint[_pixelWidth * _pixelHeight];
        _gridTemplate = new uint[_pixelWidth * _pixelHeight];
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

                int x0 = col * CellSize + 1;
                int y0 = row * CellSize + 1;
                for (int y = 0; y < BlockSize; y++)
                {
                    int offset = (y0 + y) * _pixelWidth + x0;
                    for (int x = 0; x < BlockSize; x++) _pixels[offset + x] = color;
                }
            }
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _pixels, _pixelWidth * 4, 0);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(_gridBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_bitmap != null)
            dc.DrawImage(_bitmap, new Rect(0, 0, _pixelWidth / _dpiScale, _pixelHeight / _dpiScale));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_cols == 0 || _rows == 0)
        {
            ToolTip = null;
            return;
        }

        Point p = e.GetPosition(this);
        int col = (int)(p.X * _dpiScale) / CellSize;
        int row = (int)(p.Y * _dpiScale) / CellSize;
        if (col < 0 || row < 0 || col >= _cols || row >= _rows)
        {
            ToolTip = null;
            _lastTipIndex = -1;
            return;
        }

        int index = row * _cols + col;
        ClusterState state = index < _states.Length ? _states[index] : ClusterState.Free;
        _tip.Content = $"Block {UiFormat.Number(index + 1)} von {UiFormat.Number(_cols * _rows)}: {StateText(state)}";
        _tip.HorizontalOffset = p.X + 14;
        _tip.VerticalOffset = p.Y + 18;
        if (!ReferenceEquals(ToolTip, _tip)) ToolTip = _tip;
        _lastTipIndex = index;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        ToolTip = null;
        _lastTipIndex = -1;
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
