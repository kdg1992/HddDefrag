using System.Windows;
using System.Windows.Input;
using HddDefrag.Core;

namespace HddDefrag.UI;

/// <summary>
/// Vollbildansicht der Clusterkarte (wie die „Details“-Ansicht der alten Windows-98-Defragmentierung).
/// Das Fenster teilt sich die Engine mit dem Hauptfenster; die Karte rendert abhängig von der Fenstergröße
/// mit deutlich mehr Blöcken. Der Fortschritt wird hier unabhängig vom Hauptfenster aus den
/// <see cref="IDefragEngine.Progress"/>-Ereignissen (Hintergrund-Threads) über den Dispatcher angezeigt.
/// </summary>
public partial class FullscreenMapWindow : Window
{
    private IDefragEngine? _engine;
    private Action<EngineProgress>? _onProgress;

    private EngineProgress? _pendingProgress;
    private int _progressScheduled;
    private bool _closed;

    public FullscreenMapWindow(IDefragEngine? engine, string initialStatus)
    {
        InitializeComponent();
        StatusLine.Text = initialStatus;
        Engine = engine;
        Closed += (_, _) =>
        {
            _closed = true;
            Engine = null;
        };
    }

    /// <summary>
    /// Die angezeigte Engine. Beim Wechsel (anderes Laufwerk) oder Entsorgen im Hauptfenster wird sie hier nachgezogen.
    /// </summary>
    public IDefragEngine? Engine
    {
        get => _engine;
        set
        {
            if (ReferenceEquals(_engine, value)) return;

            if (_engine != null && _onProgress != null) _engine.Progress -= _onProgress;
            _onProgress = null;
            _engine = value;
            Map.Engine = value;

            if (value != null)
            {
                _onProgress = OnProgress;
                value.Progress += _onProgress;
                // Die Statuszeile des alten Laufwerks ist nicht mehr gültig.
                if (IsLoaded) StatusLine.Text = "Bereit.";
            }
        }
    }

    /// <summary>Öffnet das Fenster maximiert auf dem Bildschirm, auf dem sich <paramref name="owner"/> befindet.</summary>
    public void ShowOn(Window owner)
    {
        Owner = owner;
        Rect b = owner.WindowState == WindowState.Normal
            ? new Rect(owner.Left, owner.Top, owner.Width, owner.Height)
            : owner.RestoreBounds;
        // Ein kleines Startrechteck innerhalb des Zielbildschirms genügt; Maximized wählt den Monitor danach.
        Left = b.Left + 20;
        Top = b.Top + 20;
        Width = 400;
        Height = 300;
        WindowState = WindowState.Maximized;
        Show();
        Activate();
    }

    // Hintergrund-Thread: nur den neuesten Wert merken und einen einzigen Dispatcher-Aufruf einreihen.
    private void OnProgress(EngineProgress p)
    {
        Volatile.Write(ref _pendingProgress, p);
        if (Interlocked.Exchange(ref _progressScheduled, 1) == 0)
        {
            try { Dispatcher.BeginInvoke(ApplyPendingProgress); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref _progressScheduled, 0); }
        }
    }

    private void ApplyPendingProgress()
    {
        Interlocked.Exchange(ref _progressScheduled, 0);
        var p = Interlocked.Exchange(ref _pendingProgress, null);
        if (p == null || _closed) return;

        double percent = Math.Clamp(double.IsNaN(p.Percent) ? 0 : p.Percent, 0, 100);
        string files = p.FilesTotal > 0 ? $" – Dateien {UiFormat.Number(p.FilesProcessed)} / {UiFormat.Number(p.FilesTotal)}" : string.Empty;
        string message = string.IsNullOrWhiteSpace(p.Message) ? string.Empty : " – " + p.Message;
        StatusLine.Text = $"Phase: {MainWindow.PhaseText(p.Phase)} – {percent.ToString("0", UiFormat.De)} %{files}{message}";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        var owner = Owner;
        base.OnClosed(e);
        // Das Hauptfenster wieder nach vorn holen (es blieb die ganze Zeit dahinter bestehen).
        if (owner is { IsVisible: true }) owner.Activate();
    }
}
