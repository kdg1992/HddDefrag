using System.Diagnostics;
using System.Globalization;
using System.IO;
using HddDefrag.Core;

namespace HddDefrag.Engine;

/// <summary>
/// Analyse- und Defragmentierungs-Engine für genau ein NTFS-Volume. Arbeitet ausschließlich gegen <see cref="IVolumeAccess"/>.
/// Alle Events werden auf Hintergrund-Threads ausgelöst. Es kann immer nur eine Operation gleichzeitig laufen.
/// </summary>
public sealed class DefragEngine : IDefragEngine
{
    /// <summary>Größe eines Verschiebe-Stücks in Clustern (4096 × 4 KiB = 16 MiB).</summary>
    public const long ChunkClusters = 4096;

    /// <summary>Höchstzahl der Dateien in <see cref="AnalysisResult.FragmentedFiles"/>.</summary>
    public const int MaxReportedFragmentedFiles = 5000;

    private const int MapChangedIntervalMs = 100;
    private const int MaxLoggedSkips = 100;
    private const int ErrorStreakWarnThreshold = 3;

    private static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    private readonly IVolumeAccess _volume;
    private readonly ClusterMapModel _model = new();
    private readonly Func<string, IEnumerable<string>>? _enumerateEntries;
    private readonly Func<string, long>? _getFileSize;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _stateLock = new();

    private int _busy;
    private bool _disposed;
    private Task? _currentTask;
    private volatile EnginePhase _phase = EnginePhase.Idle;
    private volatile AnalysisResult? _lastAnalysis;
    private List<FileFragmentInfo> _fragmentedAll = new();

    private long _lastMapRaiseMs = -MapChangedIntervalMs;
    private long _lastProgressMs = -MapChangedIntervalMs;

    public DefragEngine(DriveDescriptor drive, IVolumeAccess volume)
        : this(drive, volume, null, null)
    {
    }

    /// <summary>
    /// Wie der Standardkonstruktor, mit austauschbarer Dateiaufzählung und Dateigröße (für Tests ohne echtes Dateisystem).
    /// </summary>
    public DefragEngine(DriveDescriptor drive, IVolumeAccess volume,
        Func<string, IEnumerable<string>>? enumerateEntries, Func<string, long>? getFileSize)
    {
        Drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _enumerateEntries = enumerateEntries;
        _getFileSize = getFileSize;
    }

    public DriveDescriptor Drive { get; }
    public NtfsVolumeInfo? VolumeInfo => _volume.Info;
    public EnginePhase Phase => _phase;
    public AnalysisResult? LastAnalysis => _lastAnalysis;

    /// <summary>Das Kartenmodell (für Tests und Diagnose).</summary>
    public ClusterMapModel Model => _model;

    public event Action<string>? Log;
    public event Action<EngineProgress>? Progress;
    public event Action? MapChanged;

    // ------------------------------------------------------------------ Öffentliche API

    public Task<AnalysisResult> AnalyzeAsync(CancellationToken ct)
    {
        BeginOperation();
        return StartOperation(linked => AnalyzeCore(linked, announceDone: true), ct);
    }

    public Task DefragmentAsync(DefragOptions options, PauseController pause, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pause);
        BeginOperation();
        // Abbruch und Fehler werden über Phase und Log gemeldet; die Task selbst endet dabei regulär.
        return StartOperation<object?>(linked =>
        {
            DefragmentCore(options, pause, linked);
            return null;
        }, ct, swallowFailures: true);
    }

    public ClusterState[] RenderMap(int blockCount) => _model.Render(blockCount);

    public void Dispose()
    {
        Task? running;
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
            running = _currentTask;
        }

        try { _disposeCts.Cancel(); } catch (ObjectDisposedException) { }

        // Das Volume erst freigeben, wenn eine laufende Operation beendet ist – ohne den aufrufenden Thread zu blockieren.
        if (running is { IsCompleted: false })
            running.ContinueWith(_ => _volume.Dispose(), TaskScheduler.Default);
        else
            _volume.Dispose();
    }

    // ------------------------------------------------------------------ Ablaufsteuerung

    private void BeginOperation()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DefragEngine));
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            throw new InvalidOperationException("Es läuft bereits eine Operation auf diesem Laufwerk.");
    }

    private Task<T> StartOperation<T>(Func<CancellationToken, T> work, CancellationToken ct, bool swallowFailures = false)
    {
        Task<T> task;
        try
        {
            task = Task.Run(() =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
                try
                {
                    return work(linked.Token);
                }
                catch (OperationCanceledException)
                {
                    SetPhase(EnginePhase.Cancelled, 0, "Abgebrochen.");
                    WriteLog("Vorgang abgebrochen.");
                    if (swallowFailures) return default!;
                    throw;
                }
                catch (Exception ex)
                {
                    WriteLog($"Fehler: {ex.Message}");
                    SetPhase(EnginePhase.Failed, 0, $"Fehler: {ex.Message}");
                    if (swallowFailures) return default!;
                    throw;
                }
                finally
                {
                    Volatile.Write(ref _busy, 0);
                }
            }, CancellationToken.None);
        }
        catch
        {
            Volatile.Write(ref _busy, 0);
            throw;
        }

        lock (_stateLock) _currentTask = task;
        return task;
    }

    // ------------------------------------------------------------------ Analyse

    private AnalysisResult AnalyzeCore(CancellationToken ct, bool announceDone)
    {
        var sw = Stopwatch.StartNew();
        var info = _volume.Info;
        WriteLog($"Analyse von Laufwerk {Drive.DisplayName} gestartet.");

        // Phase 1: Bitmap
        ct.ThrowIfCancellationRequested();
        SetPhase(EnginePhase.ReadingBitmap, 0, "Lese Belegungs-Bitmap …");
        ReadBitmapIntoModel(info);
        SetSystemOverlay(info);
        _model.ClearOverlay(ClusterState.Fragmented);
        _model.ClearOverlay(ClusterState.Unmovable);
        _model.ClearMoving();
        RaiseMapChanged(force: true);
        var bitmap = _model.Bitmap!;

        // Phase 2: Dateien durchlaufen
        ct.ThrowIfCancellationRequested();
        SetPhase(EnginePhase.Scanning, 5, "Durchsuche Dateien …");
        WriteLog("Durchsuche Dateien …");

        // Obergrenze der Dateianzahl: ein MFT-Eintrag ist 1 KiB groß. Dient nur zur Fortschrittsanzeige.
        long expectedEntries = Math.Max(1, info.MftValidDataLengthBytes / 1024);
        var scanner = new FileScanner(_volume, _enumerateEntries, _getFileSize);
        var scanWatch = Stopwatch.StartNew();
        var scan = scanner.Scan($"{_volume.Letter}:\\", ct, pause: null,
            progress: p =>
            {
                double percent = 5 + Math.Min(0.9, (double)p.FilesProcessed / expectedEntries) * 90;
                RaiseProgress(new EngineProgress(EnginePhase.Scanning, percent, $"Durchsuche: {p.CurrentDirectory}", p.FilesProcessed, expectedEntries), force: false);
            },
            log: WriteLog);
        WriteLog($"{N(scan.FilesScanned)} Dateien in {FormatDuration(scanWatch.Elapsed)} durchsucht, {N(scan.ErrorCount)} Lesefehler, {N(scan.DirectoriesSkipped)} geschützte Verzeichnisse übersprungen.");

        // Phase 3: Auswerten
        ct.ThrowIfCancellationRequested();
        SetPhase(EnginePhase.Analyzing, 95, "Werte Ergebnisse aus …");

        var movable = new List<FileFragmentInfo>();
        foreach (var f in scan.FragmentedFiles)
            if (!SafetyRules.IsProtected(f.FullPath)) movable.Add(f);

        _model.ReplaceOverlay(ClusterState.Unmovable, scan.UnmovableExtents);
        _model.ReplaceOverlay(ClusterState.Fragmented, movable.SelectMany(f => f.Extents));
        RaiseMapChanged(force: true);

        long total = bitmap.TotalClusters;
        long used = bitmap.CountUsed(0, total);
        long largestFree = bitmap.LargestFreeRun();
        var top = scan.FragmentedFiles
            .OrderByDescending(f => f.ClusterCount)
            .Take(MaxReportedFragmentedFiles)
            .ToList();

        lock (_stateLock) _fragmentedAll = scan.FragmentedFiles.ToList();

        sw.Stop();
        var result = new AnalysisResult(
            FilesScanned: scan.FilesScanned,
            FilesFragmented: scan.FilesFragmented,
            FragmentsTotal: scan.FragmentsTotal,
            ClustersInFragmentedFiles: scan.ClustersInFragmentedFiles,
            TotalClusters: total,
            UsedClusters: used,
            FreeClusters: total - used,
            LargestFreeRunClusters: largestFree,
            FragmentedFiles: top,
            Duration: sw.Elapsed);
        _lastAnalysis = result;

        WriteLog($"Analyse abgeschlossen: {N(result.FilesFragmented)} von {N(result.FilesScanned)} Dateien fragmentiert " +
                 $"({result.FragmentationPercent.ToString("F1", De)} % der belegten Cluster), größter freier Bereich {FormatClusters(largestFree, info)}, Dauer {FormatDuration(sw.Elapsed)}.");

        if (announceDone) SetPhase(EnginePhase.Done, 100, "Analyse abgeschlossen.");
        return result;
    }

    private void ReadBitmapIntoModel(NtfsVolumeInfo info)
    {
        var sw = Stopwatch.StartNew();
        var bitmap = _volume.ReadBitmap();
        sw.Stop();
        _model.SetBitmap(bitmap);
        WriteLog($"Bitmap gelesen: {N(bitmap.TotalClusters)} Cluster ({FormatSize(bitmap.TotalClusters * info.BytesPerCluster)}) in {FormatDuration(sw.Elapsed)}.");
    }

    private void SetSystemOverlay(NtfsVolumeInfo info)
    {
        var ranges = new List<OverlayRange>();
        if (info.MftZoneEnd > info.MftZoneStart && info.MftZoneStart >= 0)
            ranges.Add(new OverlayRange(info.MftZoneStart, info.MftZoneEnd - info.MftZoneStart, ClusterState.System));
        if (info.MftClusters > 0 && info.MftStartLcn >= 0)
            ranges.Add(new OverlayRange(info.MftStartLcn, info.MftClusters, ClusterState.System));
        if (info.Mft2StartLcn > 0 && info.BytesPerCluster > 0)
        {
            // Der $MFT-Spiegel enthält die ersten vier Datensätze (4 KiB).
            long mirrorClusters = Math.Max(1, (4096 + info.BytesPerCluster - 1) / info.BytesPerCluster);
            ranges.Add(new OverlayRange(info.Mft2StartLcn, mirrorClusters, ClusterState.System));
        }
        _model.ReplaceOverlay(ClusterState.System, ranges);
    }

    // ------------------------------------------------------------------ Defragmentierung

    private enum FileOutcome { Moved, Skipped, Error }

    private sealed class RunStats
    {
        public long Moved, Skipped, Errors, SkipLogged;
        public long ClustersDone, ClustersTotal, FilesTotal, FilesDone;
        public int Win32Streak, LastWin32 = int.MinValue;
        public long Cursor;                 // Nächste Suche beginnt hier (Next-Fit)
        // Ab dieser Größe war die Zielsuche erfolglos. Da kleinste Dateien zuerst kommen, wären alle späteren ebenfalls zu groß;
        // nach einem Fehlschlag wird keine Datei mehr verschoben, es wird also auch kein Platz frei.
        public long FailedSearchSize = long.MaxValue;
    }

    private void DefragmentCore(DefragOptions options, PauseController pause, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var info = _volume.Info;
        WriteLog($"Defragmentierung von Laufwerk {Drive.DisplayName} gestartet.");

        if (_lastAnalysis is null)
        {
            WriteLog("Keine Analyse vorhanden – führe zuerst eine Analyse durch.");
            AnalyzeCore(ct, announceDone: false);
        }
        else
        {
            ct.ThrowIfCancellationRequested();
            SetPhase(EnginePhase.ReadingBitmap, 0, "Lese Belegungs-Bitmap …");
            ReadBitmapIntoModel(info);
            _model.ClearMoving();
            RaiseMapChanged(force: true);
        }

        ct.ThrowIfCancellationRequested();
        List<FileFragmentInfo> candidates;
        lock (_stateLock) candidates = _fragmentedAll.OrderBy(f => f.ClusterCount).ThenBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();

        var stats = new RunStats { FilesTotal = candidates.Count, ClustersTotal = Math.Max(1, candidates.Sum(f => f.ClusterCount)) };
        SetPhase(EnginePhase.Defragmenting, 0, "Defragmentiere …");
        WriteLog($"{N(candidates.Count)} fragmentierte Dateien werden bearbeitet (kleinste zuerst).");

        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            pause.WaitIfPaused(ct);

            FileOutcome outcome;
            try
            {
                outcome = ProcessFile(file, options, pause, ct, stats);
            }
            catch (VolumeAccessException ex)
            {
                outcome = FileOutcome.Error;
                HandleVolumeError(file, ex, stats);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcome = FileOutcome.Error;
                stats.Errors++;
                stats.LastWin32 = int.MinValue;
                stats.Win32Streak = 0;
                WriteLog($"Fehler bei {file.FullPath}: {ex.Message}");
            }
            finally
            {
                _model.ClearMoving();
            }

            if (outcome == FileOutcome.Moved) stats.Moved++;
            else if (outcome == FileOutcome.Skipped) stats.Skipped++;

            stats.FilesDone++;
            stats.ClustersDone += file.ClusterCount;
            double percent = 100.0 * stats.ClustersDone / stats.ClustersTotal;
            RaiseProgress(new EngineProgress(EnginePhase.Defragmenting, Math.Min(100, percent), $"Defragmentiere: {file.FullPath}", stats.FilesDone, stats.FilesTotal), force: false);
            RaiseMapChanged(force: false);
        }

        sw.Stop();
        RaiseMapChanged(force: true);
        WriteLog($"Defragmentierung abgeschlossen: {N(stats.Moved)} Dateien verschoben, {N(stats.Skipped)} übersprungen, {N(stats.Errors)} Fehler, Dauer {FormatDuration(sw.Elapsed)}.");
        SetPhase(EnginePhase.Done, 100, "Defragmentierung abgeschlossen.");
    }

    private FileOutcome ProcessFile(FileFragmentInfo file, DefragOptions options, PauseController pause, CancellationToken ct, RunStats stats)
    {
        var info = _volume.Info;
        string path = file.FullPath;

        if (SafetyRules.IsProtected(path))
            return Skip(stats, $"Übersprungen (geschützt): {path}");
        if (options.MaxFileSizeBytes > 0 && file.SizeBytes > options.MaxFileSizeBytes)
            return Skip(stats, $"Übersprungen (größer als Limit, {FormatSize(file.SizeBytes)}): {path}");
        if (file.Extents.Any(e => e.IsVirtual))
            return Skip(stats, $"Übersprungen (sparse/komprimierte Bereiche): {path}");

        // Aktuelle Extents lesen: die Datei kann sich seit der Analyse geändert haben.
        var current = _volume.GetExtents(path);
        int fragmentsBefore = FileScanner.CountFragments(current);
        if (current.Any(e => e.IsVirtual))
        {
            _model.RemoveOverlay(ClusterState.Fragmented, file.Extents);
            return Skip(stats, $"Übersprungen (sparse/komprimierte Bereiche): {path}");
        }
        if (fragmentsBefore <= 1)
        {
            _model.RemoveOverlay(ClusterState.Fragmented, file.Extents);
            return Skip(stats, $"Übersprungen (inzwischen nicht mehr fragmentiert): {path}");
        }

        var sorted = current.Where(e => e.ClusterCount > 0).OrderBy(e => e.Vcn).ToList();
        long clusters = 0;
        foreach (var e in sorted)
        {
            if (e.Vcn != clusters)
                return Skip(stats, $"Übersprungen (Lücken im Extent-Verlauf): {path}");
            clusters += e.ClusterCount;
        }

        // Zielbereich suchen (MFT-Zone ausgespart)
        if (clusters >= stats.FailedSearchSize)
            return Skip(stats, $"Übersprungen (kein zusammenhängender freier Platz für {FormatClusters(clusters, info)}): {path}");

        var bitmap = _model.Bitmap!;
        long target = FindTarget(bitmap, info, clusters, stats.Cursor);
        if (target < 0)
        {
            stats.FailedSearchSize = clusters;
            return Skip(stats, $"Übersprungen (kein zusammenhängender freier Platz für {FormatClusters(clusters, info)}): {path}");
        }
        stats.FailedSearchSize = long.MaxValue;

        bool moveStarted = false;
        try
        {
            for (long vcn = 0; vcn < clusters; vcn += ChunkClusters)
            {
                pause.WaitIfPaused(ct);
                ct.ThrowIfCancellationRequested();

                long n = Math.Min(ChunkClusters, clusters - vcn);
                var sources = ChunkSources(sorted, vcn, n);

                _model.SetMoving(sources.Append((target + vcn, n)));
                RaiseMapChanged(force: false);

                moveStarted = true;
                _volume.MoveFile(path, vcn, target + vcn, n);
                _model.ApplyMove(sources, target + vcn, n);
                RaiseMapChanged(force: false);
            }
        }
        catch
        {
            // Teilweise verschobene Datei: Overlay anhand der tatsächlichen Extents wieder in Ordnung bringen.
            if (moveStarted) TryRefreshOverlay(file, path);
            throw;
        }

        stats.Cursor = target + clusters;
        stats.Win32Streak = 0;
        stats.LastWin32 = int.MinValue;

        int fragmentsAfter = 1;
        _model.RemoveOverlay(ClusterState.Fragmented, file.Extents);
        if (options.VerifyAfterMove)
        {
            var after = _volume.GetExtents(path);
            fragmentsAfter = FileScanner.CountFragments(after);
            if (fragmentsAfter > 1)
            {
                _model.AddOverlay(ClusterState.Fragmented, after);
                stats.Errors++;
                WriteLog($"Warnung: Verifikation fehlgeschlagen, Datei weiterhin fragmentiert ({fragmentsBefore} → {fragmentsAfter} Fragmente): {path}");
                return FileOutcome.Error;
            }
        }

        WriteLog($"Verschoben: {path} ({FormatSize(file.SizeBytes)}), Fragmente {fragmentsBefore} → {fragmentsAfter}.");
        return FileOutcome.Moved;
    }

    private void TryRefreshOverlay(FileFragmentInfo file, string path)
    {
        try
        {
            var now = _volume.GetExtents(path);
            _model.RemoveOverlay(ClusterState.Fragmented, file.Extents);
            if (FileScanner.CountFragments(now) > 1) _model.AddOverlay(ClusterState.Fragmented, now);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Overlay bleibt veraltet; die nächste Analyse stellt es wieder her.
        }
    }

    private FileOutcome Skip(RunStats stats, string message)
    {
        if (stats.SkipLogged < MaxLoggedSkips)
        {
            stats.SkipLogged++;
            WriteLog(message);
            if (stats.SkipLogged == MaxLoggedSkips)
                WriteLog("Weitere übersprungene Dateien werden nicht mehr einzeln protokolliert.");
        }
        return FileOutcome.Skipped;
    }

    private void HandleVolumeError(FileFragmentInfo file, VolumeAccessException ex, RunStats stats)
    {
        stats.Errors++;
        if (ex.Win32Error == stats.LastWin32) stats.Win32Streak++;
        else
        {
            stats.LastWin32 = ex.Win32Error;
            stats.Win32Streak = 1;
        }

        WriteLog($"Fehler bei {file.FullPath}: {ex.Message} (Win32-Fehler {ex.Win32Error})");
        if (stats.Win32Streak == ErrorStreakWarnThreshold)
            WriteLog($"Warnung: {ErrorStreakWarnThreshold} Fehler in Folge mit demselben Win32-Fehlercode {ex.Win32Error} – es wird mit der nächsten Datei fortgefahren.");
    }

    /// <summary>
    /// Sucht einen freien zusammenhängenden Bereich, der die MFT-Zone ausspart: zuerst vor der Zone, dann dahinter.
    /// Die Suche beginnt hinter der zuletzt belegten Zielposition (Next-Fit) und fällt danach auf den Anfang des Volumes zurück.
    /// </summary>
    private static long FindTarget(VolumeBitmap bitmap, NtfsVolumeInfo info, long clusters, long cursor)
    {
        long total = bitmap.TotalClusters;
        var segments = new List<(long Start, long End)>();
        if (info.MftZoneEnd > info.MftZoneStart && info.MftZoneStart >= 0)
        {
            segments.Add((0, Math.Min(info.MftZoneStart, total)));
            segments.Add((Math.Min(info.MftZoneEnd, total), total));
        }
        else
        {
            segments.Add((0, total));
        }

        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1 && cursor <= 0) break;
            foreach (var (segStart, segEnd) in segments)
            {
                long start = pass == 0 ? Math.Max(segStart, cursor) : segStart;
                if (segEnd - start < clusters) continue;
                long found = bitmap.FindFreeRun(start, clusters, segEnd);
                if (found >= 0) return found;
            }
        }
        return -1;
    }

    /// <summary>Alte Cluster-Bereiche der Datei, die zum VCN-Bereich [vcn, vcn+count) gehören.</summary>
    private static List<(long Start, long Count)> ChunkSources(List<Extent> sortedExtents, long vcn, long count)
    {
        var result = new List<(long, long)>();
        long end = vcn + count;
        foreach (var e in sortedExtents)
        {
            long ovStart = Math.Max(e.Vcn, vcn);
            long ovEnd = Math.Min(e.Vcn + e.ClusterCount, end);
            if (ovEnd > ovStart) result.Add((e.Lcn + (ovStart - e.Vcn), ovEnd - ovStart));
            if (e.Vcn >= end) break;
        }
        return result;
    }

    // ------------------------------------------------------------------ Events und Formatierung

    private void SetPhase(EnginePhase phase, double percent, string message)
    {
        _phase = phase;
        RaiseProgress(new EngineProgress(phase, percent, message, 0, 0), force: true);
    }

    private void RaiseProgress(EngineProgress progress, bool force)
    {
        if (!force)
        {
            long now = _clock.ElapsedMilliseconds;
            if (now - Interlocked.Read(ref _lastProgressMs) < MapChangedIntervalMs) return;
            Interlocked.Exchange(ref _lastProgressMs, now);
        }
        try { Progress?.Invoke(progress); }
        catch { /* Ein fehlerhafter Handler darf die Engine nicht beenden. */ }
    }

    private void RaiseMapChanged(bool force)
    {
        long now = _clock.ElapsedMilliseconds;
        if (!force && now - Interlocked.Read(ref _lastMapRaiseMs) < MapChangedIntervalMs) return;
        Interlocked.Exchange(ref _lastMapRaiseMs, now);
        try { MapChanged?.Invoke(); }
        catch { /* siehe oben */ }
    }

    private void WriteLog(string message)
    {
        try { Log?.Invoke(message); }
        catch { /* siehe oben */ }
    }

    private static string N(long value) => value.ToString("N0", De);

    private static string FormatSize(long bytes)
    {
        string[] units = { "Bytes", "KB", "MB", "GB", "TB" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} Bytes" : $"{v.ToString("F1", De)} {units[u]}";
    }

    private static string FormatClusters(long clusters, NtfsVolumeInfo info) =>
        $"{clusters.ToString("N0", De)} Cluster ({FormatSize(clusters * info.BytesPerCluster)})";

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{t.TotalSeconds.ToString("F1", De)} s" : $"{(int)t.TotalMinutes} min {t.Seconds} s";
}
