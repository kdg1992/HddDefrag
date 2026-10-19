using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HddDefrag.Core;

namespace HddDefrag.UI;

/// <summary>Zeile der Laufwerksliste.</summary>
internal sealed class DriveItem
{
    public DriveItem(DriveDescriptor drive)
    {
        Descriptor = drive;
        Letter = drive.Letter + ":";
        Label = drive.VolumeLabel;
        MediaText = drive.MediaText;
        Model = drive.DiskModel;
        Bus = drive.BusType;
        FileSystem = drive.FileSystem;
        SizeText = drive.TotalBytes > 0 ? UiFormat.Bytes(drive.TotalBytes) : string.Empty;
        FreeText = drive.TotalBytes > 0 ? UiFormat.Bytes(drive.FreeBytes) : string.Empty;
        IsAllowed = drive.IsDefragAllowed;
        Status = IsAllowed ? "Bereit" : "🔒 " + drive.BlockReason;
    }

    public DriveDescriptor Descriptor { get; }
    public string Letter { get; }
    public string Label { get; }
    public string MediaText { get; }
    public string Model { get; }
    public string Bus { get; }
    public string FileSystem { get; }
    public string SizeText { get; }
    public string FreeText { get; }
    public bool IsAllowed { get; }
    public string Status { get; }
}

public partial class MainWindow : Window
{
    private const int MaxLogLines = 5000;
    private const int LogTrimSlack = 250;

    private readonly IDriveEnumerator _enumerator;
    private readonly Func<DriveDescriptor, IDefragEngine> _engineFactory;

    private IDefragEngine? _engine;
    private Action? _detachEngine;
    private CancellationTokenSource? _cts;
    private PauseController? _pause;
    private bool _running;
    private bool _paused;
    private bool _closeWhenDone;
    private bool _suppressSelection;
    private FullscreenMapWindow? _fullscreen;

    // Log: Engine-Threads reihen nur ein, ein einzelner Dispatcher-Aufruf leert die Warteschlange.
    private readonly ConcurrentQueue<string> _logQueue = new();
    private int _logFlushScheduled;
    private int _logLineCount;

    // Fortschritt: nur der jeweils neueste Wert wird angezeigt.
    private EngineProgress? _pendingProgress;
    private int _progressScheduled;

    public MainWindow(IDriveEnumerator enumerator, Func<DriveDescriptor, IDefragEngine> engineFactory)
    {
        _enumerator = enumerator;
        _engineFactory = engineFactory;
        InitializeComponent();
        UpdateUi();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    // ------------------------------------------------------------------ Start

    private async Task OnLoadedAsync()
    {
        await ReloadDrivesAsync();

        // Nur zum Testen der Demo: Analyse automatisch auf dem ersten erlaubten Laufwerk starten.
        bool autorun = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--demo-autorun", StringComparison.OrdinalIgnoreCase));
        if (autorun)
        {
            var first = _items.FirstOrDefault(i => i.IsAllowed);
            if (first != null)
            {
                DriveList.SelectedItem = first;
                await AnalyzeAsync();
            }
        }
    }

    private readonly List<DriveItem> _items = new();

    private async Task ReloadDrivesAsync()
    {
        if (_running) return;
        DisposeEngine();
        try
        {
            IReadOnlyList<DriveDescriptor> drives = await Task.Run(() => _enumerator.Enumerate());
            _items.Clear();
            _items.AddRange(drives.OrderBy(d => d.Letter).Select(d => new DriveItem(d)));

            _suppressSelection = true;
            DriveList.ItemsSource = null;
            DriveList.ItemsSource = _items;
            _suppressSelection = false;

            int allowed = _items.Count(i => i.IsAllowed);
            EnqueueLog($"{_items.Count} Laufwerke gefunden, {allowed} davon dürfen defragmentiert werden.");
            foreach (var item in _items.Where(i => !i.IsAllowed))
                EnqueueLog($"{item.Letter} gesperrt: {item.Descriptor.BlockReason}");
        }
        catch (Exception ex)
        {
            _suppressSelection = false;
            ReportError("Die Laufwerksliste konnte nicht geladen werden", ex);
        }
        ResetResult();
        SetStatus("Bereit.", string.Empty, 0);
        UpdateUi();
    }

    // -------------------------------------------------------------- Laufwerk

    private void DriveList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection || _running) return;

        DisposeEngine();
        ResetResult();
        SetStatus("Bereit.", string.Empty, 0);

        if (DriveList.SelectedItem is DriveItem { IsAllowed: true } item)
        {
            try
            {
                var engine = _engineFactory(item.Descriptor);
                AttachEngine(engine);
                EnqueueLog($"Laufwerk {item.Descriptor.DisplayName} ausgewählt ({item.Descriptor.DiskModel}).");
            }
            catch (Exception ex)
            {
                _suppressSelection = true;
                DriveList.SelectedItem = null;
                _suppressSelection = false;
                ReportError($"Das Laufwerk {item.Letter} konnte nicht geöffnet werden", ex);
            }
        }
        UpdateUi();
    }

    private void AttachEngine(IDefragEngine engine)
    {
        _engine = engine;

        Action<string> onLog = EnqueueLog;
        Action<EngineProgress> onProgress = p =>
        {
            Volatile.Write(ref _pendingProgress, p);
            if (Interlocked.Exchange(ref _progressScheduled, 1) == 0)
                Dispatcher.BeginInvoke(ApplyPendingProgress);
        };
        engine.Log += onLog;
        engine.Progress += onProgress;
        _detachEngine = () =>
        {
            engine.Log -= onLog;
            engine.Progress -= onProgress;
        };
        Map.Engine = engine;
        if (_fullscreen != null) _fullscreen.Engine = engine;
    }

    private void DisposeEngine()
    {
        Map.Engine = null;
        if (_fullscreen != null) _fullscreen.Engine = null;
        _detachEngine?.Invoke();
        _detachEngine = null;
        var engine = _engine;
        _engine = null;
        if (engine != null)
        {
            try { engine.Dispose(); }
            catch (Exception ex) { EnqueueLog("Fehler beim Freigeben der Engine: " + ex.Message); }
        }
    }

    // ----------------------------------------------------------------- Läufe

    private async void AnalyzeButton_Click(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private async void DefragButton_Click(object sender, RoutedEventArgs e) => await DefragAsync();

    private async Task AnalyzeAsync()
    {
        var engine = _engine;
        if (engine == null || _running) return;

        AnalysisResult? result = null;
        bool ok = await RunAsync("Analyse", async ct => { result = await engine.AnalyzeAsync(ct); });
        if (!ok || result == null) return;

        ShowResult(engine, result);
        var dialog = new AnalysisDoneDialog(engine.Drive, result) { Owner = DialogOwner };
        if (dialog.ShowDialog() == true) await DefragAsync();
    }

    private async Task DefragAsync()
    {
        var engine = _engine;
        if (engine == null || _running) return;

        // Sicherheitsabfrage: IMMER vor dem Verschieben von Dateien.
        var confirm = new ConfirmDefragDialog(engine.Drive) { Owner = DialogOwner };
        if (confirm.ShowDialog() != true)
        {
            EnqueueLog("Defragmentierung nicht bestätigt – es wurde nichts verändert.");
            return;
        }

        var pause = new PauseController();
        _pause = pause;
        bool ok = await RunAsync("Defragmentierung", ct => engine.DefragmentAsync(new DefragOptions(), pause, ct));
        if (ok && engine.LastAnalysis is { } analysis && ReferenceEquals(engine, _engine))
            ShowResult(engine, analysis);
    }

    /// <summary>Führt einen Engine-Lauf aus, sperrt dabei die Oberfläche und fängt alle Fehler ab.</summary>
    private async Task<bool> RunAsync(string name, Func<CancellationToken, Task> work)
    {
        var cts = new CancellationTokenSource();
        _cts = cts;
        _running = true;
        _paused = false;
        UpdateUi();
        EnqueueLog($"{name} gestartet.");

        bool success = false;
        try
        {
            await work(cts.Token);
            success = true;
        }
        catch (OperationCanceledException)
        {
            EnqueueLog($"{name} abgebrochen.");
            SetStatus("Abgebrochen.", string.Empty, null);
        }
        catch (Exception ex)
        {
            EnqueueLog($"Fehler bei {name}: {ex.Message}");
            SetStatus("Fehler.", ex.Message, null);
            if (!_closeWhenDone) ReportError($"Fehler bei {name}", ex, alsoLog: false);
        }
        finally
        {
            _running = false;
            _paused = false;
            _pause = null;
            _cts = null;
            cts.Dispose();
            UpdateUi();
        }

        if (_closeWhenDone) Close();
        return success;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running) return;
        EnqueueLog("Abbruch angefordert …");
        try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        _pause?.Resume(); // eine pausierte Engine muss aufwachen, um den Abbruch zu bemerken
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var pause = _pause;
        if (!_running || pause == null) return;
        if (_paused)
        {
            pause.Resume();
            _paused = false;
            EnqueueLog("Fortgesetzt.");
        }
        else
        {
            pause.Pause();
            _paused = true;
            EnqueueLog("Pausiert.");
        }
        UpdateUi();
    }

    private async void ReloadButton_Click(object sender, RoutedEventArgs e) => await ReloadDrivesAsync();

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            if (_closeWhenDone)
            {
                e.Cancel = true; // Abbruch läuft bereits
                return;
            }

            var answer = MessageBox.Show(DialogOwner,
                "Es läuft gerade ein Vorgang. Soll er abgebrochen und das Programm beendet werden?",
                "Defragmentierung", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            e.Cancel = true;
            if (answer == MessageBoxResult.Yes)
            {
                // Erst abbrechen und auf das Ende des Tasks warten; RunAsync schließt danach das Fenster.
                _closeWhenDone = true;
                EnqueueLog("Programmende angefordert – laufender Vorgang wird abgebrochen …");
                try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
                _pause?.Resume();
                UpdateUi();
            }
            return;
        }

        _fullscreen?.Close();
        DisposeEngine();
        base.OnClosing(e);
    }

    // ------------------------------------------------------------ Anzeige

    private void UpdateUi()
    {
        bool hasEngine = _engine != null;
        DriveList.IsEnabled = !_running;
        ReloadButton.IsEnabled = !_running;
        AnalyzeButton.IsEnabled = !_running && hasEngine;
        DefragButton.IsEnabled = !_running && hasEngine;
        PauseButton.IsEnabled = _running && _pause != null && !_closeWhenDone;
        PauseButton.Content = _paused ? "Fortsetzen" : "Pause";
        CancelButton.IsEnabled = _running && !_closeWhenDone;
        ExitButton.IsEnabled = !_running;
        FullscreenButton.IsEnabled = hasEngine;
        if (!hasEngine) _fullscreen?.Close(); // ohne Engine gibt es nichts mehr anzuzeigen
    }

    // -------------------------------------------------------------- Vollbild

    /// <summary>Besitzer für Dialoge: das Vollbildfenster, solange es offen ist (sonst läge der Dialog dahinter).</summary>
    private Window DialogOwner => _fullscreen is { IsVisible: true } fs ? fs : this;

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.F11 && !e.Handled)
        {
            e.Handled = true;
            ToggleFullscreen();
        }
    }

    private void ToggleFullscreen()
    {
        if (_fullscreen != null)
        {
            _fullscreen.Close();
            return;
        }
        if (_engine == null) return;

        // Das Hauptfenster bleibt dahinter bestehen, damit Log und Lauf unverändert weiterarbeiten.
        string status = MessageLine.Text.Length > 0 ? $"{StatusLine.Text} – {MessageLine.Text}" : StatusLine.Text;
        var window = new FullscreenMapWindow(_engine, status);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_fullscreen, window)) _fullscreen = null;
        };
        _fullscreen = window;
        window.ShowOn(this);
    }

    internal static string PhaseText(EnginePhase phase) => phase switch
    {
        EnginePhase.Idle => "Bereit",
        EnginePhase.ReadingBitmap => "Belegungsbitmap lesen",
        EnginePhase.Scanning => "Dateien lesen",
        EnginePhase.Analyzing => "Analyse",
        EnginePhase.Defragmenting => "Defragmentierung",
        EnginePhase.Done => "Fertig",
        EnginePhase.Cancelled => "Abgebrochen",
        EnginePhase.Failed => "Fehlgeschlagen",
        _ => phase.ToString(),
    };

    private void ApplyPendingProgress()
    {
        Interlocked.Exchange(ref _progressScheduled, 0);
        var p = Interlocked.Exchange(ref _pendingProgress, null);
        if (p == null) return;

        double percent = Math.Clamp(double.IsNaN(p.Percent) ? 0 : p.Percent, 0, 100);
        Progress.Value = percent;
        string files = p.FilesTotal > 0 ? $" – Dateien {UiFormat.Number(p.FilesProcessed)} / {UiFormat.Number(p.FilesTotal)}" : string.Empty;
        StatusLine.Text = $"Phase: {PhaseText(p.Phase)} – {percent.ToString("0", UiFormat.De)} %{files}";
        MessageLine.Text = p.Message;
    }

    private void SetStatus(string status, string message, double? percent)
    {
        StatusLine.Text = status;
        MessageLine.Text = message;
        if (percent.HasValue) Progress.Value = percent.Value;
    }

    private void ResetResult()
    {
        foreach (var t in new[] { ResFiles, ResFragFiles, ResFragments, ResPercent, ResUsed, ResFree, ResLargest })
            t.Text = "–";
    }

    private void ShowResult(IDefragEngine engine, AnalysisResult r)
    {
        long bytesPerCluster = engine.VolumeInfo?.BytesPerCluster
            ?? (r.TotalClusters > 0 && engine.Drive.TotalBytes > 0 ? engine.Drive.TotalBytes / r.TotalClusters : 4096);
        if (bytesPerCluster <= 0) bytesPerCluster = 4096;

        ResFiles.Text = UiFormat.Number(r.FilesScanned);
        ResFragFiles.Text = UiFormat.Number(r.FilesFragmented);
        ResFragments.Text = UiFormat.Number(r.FragmentsTotal);
        ResPercent.Text = UiFormat.Percent(r.FragmentationPercent);
        ResUsed.Text = UiFormat.Bytes(r.UsedClusters * bytesPerCluster);
        ResFree.Text = UiFormat.Bytes(r.FreeClusters * bytesPerCluster);
        ResLargest.Text = UiFormat.Bytes(r.LargestFreeRunClusters * bytesPerCluster);
    }

    // ------------------------------------------------------------------ Log

    /// <summary>Thread-sicher: stellt eine Zeile mit Zeitstempel in die Warteschlange.</summary>
    private void EnqueueLog(string message)
    {
        string stamp = DateTime.Now.ToString("HH:mm:ss");
        foreach (var line in message.Replace("\r", string.Empty).Split('\n'))
            _logQueue.Enqueue($"{stamp} {line}");

        if (Interlocked.Exchange(ref _logFlushScheduled, 1) == 0)
            Dispatcher.BeginInvoke(FlushLog);
    }

    private void FlushLog()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);
        var sb = new StringBuilder();
        int added = 0;
        while (_logQueue.TryDequeue(out var line))
        {
            sb.Append(line).Append('\n');
            added++;
        }
        if (added == 0) return;

        LogBox.AppendText(sb.ToString());
        _logLineCount += added;

        if (_logLineCount > MaxLogLines + LogTrimSlack)
        {
            // Älteste Zeilen in einem Schwung entfernen, damit nicht bei jeder Zeile der ganze Text kopiert wird.
            int remove = _logLineCount - MaxLogLines;
            string text = LogBox.Text;
            int index = 0;
            for (int i = 0; i < remove && index >= 0; i++)
            {
                index = text.IndexOf('\n', index);
                if (index >= 0) index++;
            }
            if (index > 0)
            {
                LogBox.Text = text[index..];
                _logLineCount -= remove;
            }
        }
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
    }

    private void ReportError(string headline, Exception ex, bool alsoLog = true)
    {
        if (alsoLog) EnqueueLog($"{headline}: {ex.Message}");
        MessageBox.Show(DialogOwner, $"{headline}.\n\n{ex.Message}", "Defragmentierung", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
