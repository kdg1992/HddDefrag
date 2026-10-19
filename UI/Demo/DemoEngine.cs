using HddDefrag.Core;

namespace HddDefrag.UI.Demo;

/// <summary>
/// Simulierte Engine zum Ausprobieren der Oberfläche ohne Adminrechte. Verhält sich wie die echte Engine:
/// Events kommen von Hintergrund-Threads, <see cref="RenderMap"/> ist threadsicher.
/// </summary>
public sealed class DemoEngine : IDefragEngine
{
    private const int ClusterCount = 20_000;
    private const int BytesPerCluster = 4096;

    private readonly object _lock = new();
    private readonly ClusterState[] _map = new ClusterState[ClusterCount];
    private bool _mapVisible;
    private int _revealLimit; // Fragmentierte Cluster unterhalb dieses Index sind bereits "entdeckt"
    private volatile EnginePhase _phase = EnginePhase.Idle;
    private AnalysisResult? _lastAnalysis;
    private NtfsVolumeInfo? _volumeInfo;

    public DemoEngine(DriveDescriptor drive)
    {
        Drive = drive;
        BuildMap(new Random(1998));
    }

    public DriveDescriptor Drive { get; }
    public NtfsVolumeInfo? VolumeInfo => _volumeInfo;
    public EnginePhase Phase => _phase;
    public AnalysisResult? LastAnalysis => _lastAnalysis;

    public event Action<string>? Log;
    public event Action<EngineProgress>? Progress;
    public event Action? MapChanged;

    // ---- Simulierte Karte ----

    private void BuildMap(Random rnd)
    {
        int pos = 0;
        void Fill(int count, ClusterState state)
        {
            for (int i = 0; i < count && pos < ClusterCount; i++) _map[pos++] = state;
        }

        Fill(700, ClusterState.System); // $MFT / MFT-Zone
        Fill(60, ClusterState.Unmovable);
        while (pos < ClusterCount)
        {
            int roll = rnd.Next(100);
            if (roll < 45) Fill(rnd.Next(150, 1100), ClusterState.Used);
            else if (roll < 70) Fill(rnd.Next(80, 700), ClusterState.Free);
            else if (roll < 92) Fill(rnd.Next(20, 160), ClusterState.Fragmented);
            else if (roll < 95) Fill(rnd.Next(40, 120), ClusterState.Unmovable);
            else Fill(rnd.Next(30, 90), ClusterState.System);
        }
        // Der hintere Teil der Platte ist überwiegend frei
        for (int i = (int)(ClusterCount * 0.88); i < ClusterCount; i++)
            if (_map[i] == ClusterState.Fragmented || _map[i] == ClusterState.Used)
                _map[i] = rnd.Next(100) < 70 ? ClusterState.Free : _map[i];
    }

    public ClusterState[] RenderMap(int blockCount)
    {
        if (blockCount <= 0) return Array.Empty<ClusterState>();
        lock (_lock)
        {
            if (!_mapVisible) return Array.Empty<ClusterState>();
            var result = new ClusterState[blockCount];
            for (int i = 0; i < blockCount; i++)
            {
                int start = (int)((long)i * ClusterCount / blockCount);
                int end = (int)((long)(i + 1) * ClusterCount / blockCount);
                if (end <= start) end = start + 1;
                if (end > ClusterCount) end = ClusterCount;
                if (start >= ClusterCount) start = ClusterCount - 1;

                ClusterState best = ClusterState.Free;
                for (int c = start; c < end; c++)
                {
                    ClusterState s = _map[c];
                    if (s == ClusterState.Fragmented && c >= _revealLimit) s = ClusterState.Used; // noch nicht analysiert
                    if (s > best) best = s;
                }
                result[i] = best;
            }
            return result;
        }
    }

    // ---- Analyse ----

    public Task<AnalysisResult> AnalyzeAsync(CancellationToken ct) => Task.Run(async () =>
    {
        _phase = EnginePhase.ReadingBitmap;
        RaiseLog($"Analyse von {Drive.DisplayName} gestartet (Demo-Modus, simulierte Daten).");
        try
        {
            lock (_lock) { _revealLimit = 0; }

            // 1) Belegungsbitmap lesen (~0,6 s)
            for (int i = 0; i <= 6; i++)
            {
                ct.ThrowIfCancellationRequested();
                RaiseProgress(EnginePhase.ReadingBitmap, i / 6.0 * 15, "Belegungsbitmap wird gelesen …", 0, 0);
                await Task.Delay(100, ct);
            }
            long totalClusters = Math.Max(1, Drive.TotalBytes / BytesPerCluster);
            _volumeInfo = new NtfsVolumeInfo(totalClusters, BytesPerCluster, 512, 786_432, totalClusters / 2, 220L * 1024 * 1024 * 1024 / 200, 786_432, 786_432 + 1_000_000);
            lock (_lock) { _mapVisible = true; }
            RaiseLog($"Belegungsbitmap gelesen: {UiFormat.Number(totalClusters)} Cluster à {BytesPerCluster} Byte.");
            MapChanged?.Invoke();

            // 2) Dateien durchsuchen (~2,4 s), fragmentierte Bereiche werden nach und nach sichtbar
            const long filesTotal = 187_452;
            const int steps = 24;
            for (int i = 1; i <= steps; i++)
            {
                ct.ThrowIfCancellationRequested();
                double f = i / (double)steps;
                lock (_lock) { _revealLimit = (int)(ClusterCount * f); }
                RaiseProgress(EnginePhase.Scanning, 15 + f * 80, $"Dateien werden gelesen: {(i * 100 / steps)} % der Dateitabelle", (long)(filesTotal * f), filesTotal);
                if (i % 6 == 0) RaiseLog($"{UiFormat.Number((long)(filesTotal * f))} Dateien untersucht …");
                MapChanged?.Invoke();
                await Task.Delay(100, ct);
            }

            // 3) Auswertung
            _phase = EnginePhase.Analyzing;
            RaiseProgress(EnginePhase.Analyzing, 97, "Ergebnis wird berechnet …", filesTotal, filesTotal);
            await Task.Delay(200, ct);

            var result = BuildResult(totalClusters, filesTotal);
            _lastAnalysis = result;
            _phase = EnginePhase.Done;
            RaiseProgress(EnginePhase.Done, 100, "Analyse abgeschlossen.", filesTotal, filesTotal);
            RaiseLog($"Analyse abgeschlossen: {UiFormat.Number(result.FilesFragmented)} von {UiFormat.Number(result.FilesScanned)} Dateien sind fragmentiert ({UiFormat.Percent(result.FragmentationPercent)}).");
            MapChanged?.Invoke();
            return result;
        }
        catch (OperationCanceledException)
        {
            _phase = EnginePhase.Cancelled;
            RaiseProgress(EnginePhase.Cancelled, 0, "Analyse abgebrochen.", 0, 0);
            RaiseLog("Analyse abgebrochen.");
            throw;
        }
    }, CancellationToken.None);

    private AnalysisResult BuildResult(long totalClusters, long filesTotal)
    {
        long used = 0, fragmented = 0, free = 0, run = 0, bestRun = 0;
        lock (_lock)
        {
            foreach (var s in _map)
            {
                if (s == ClusterState.Free)
                {
                    free++;
                    run++;
                    if (run > bestRun) bestRun = run;
                }
                else
                {
                    run = 0;
                    used++;
                    if (s == ClusterState.Fragmented) fragmented++;
                }
            }
        }

        // Auf die tatsächliche Größe des Demo-Laufwerks hochrechnen
        double scale = totalClusters / (double)ClusterCount;
        long usedScaled = (long)(used * scale), freeScaled = totalClusters - usedScaled;
        long fragClusters = (long)(fragmented * scale);
        long fragFiles = fragmented == 0 ? 0 : (long)(filesTotal * (fragmented / (double)used) * 0.6);
        long fragments = fragFiles == 0 ? 0 : (long)(fragFiles * 4.3);
        return new AnalysisResult(filesTotal, fragFiles, fragments, fragClusters, totalClusters, usedScaled, freeScaled,
            (long)(bestRun * scale), Array.Empty<FileFragmentInfo>(), TimeSpan.FromSeconds(3));
    }

    // ---- Defragmentierung ----

    public Task DefragmentAsync(DefragOptions options, PauseController pause, CancellationToken ct) => Task.Run(async () =>
    {
        try
        {
            if (_lastAnalysis == null || !_mapVisible)
            {
                RaiseLog("Keine Analyse vorhanden – es wird zuerst analysiert.");
                await AnalyzeAsync(ct);
            }

            _phase = EnginePhase.Defragmenting;
            RaiseLog($"Defragmentierung von {Drive.DisplayName} gestartet (Demo-Modus).");

            const int steps = 100; // ~10 s
            int window = ClusterCount / steps;
            long fragFilesTotal = Math.Max(1, _lastAnalysis!.FilesFragmented);
            var original = new ClusterState[window];
            var movedIdx = new List<int>(window);

            for (int step = 0; step < steps; step++)
            {
                pause.WaitIfPaused(ct);
                ct.ThrowIfCancellationRequested();

                int start = step * window, end = Math.Min(ClusterCount, start + window);
                movedIdx.Clear();
                lock (_lock)
                {
                    for (int c = start; c < end; c++)
                    {
                        if (_map[c] == ClusterState.Used || _map[c] == ClusterState.Fragmented)
                        {
                            original[c - start] = _map[c];
                            _map[c] = ClusterState.Moving;
                            movedIdx.Add(c);
                        }
                    }
                }
                MapChanged?.Invoke();

                try
                {
                    await Task.Delay(100, ct); // „Verschieben“
                }
                finally
                {
                    // Auch beim Abbruch den Zustand wiederherstellen (rot wird nur bei Erfolg blau)
                    lock (_lock)
                    {
                        foreach (int c in movedIdx)
                        {
                            var o = original[c - start];
                            _map[c] = ct.IsCancellationRequested ? o : (o == ClusterState.Fragmented ? ClusterState.Used : o);
                        }
                    }
                    MapChanged?.Invoke();
                }

                double f = (step + 1) / (double)steps;
                long done = (long)(fragFilesTotal * f);
                RaiseProgress(EnginePhase.Defragmenting, f * 100,
                    $"Defragmentiere Cluster {UiFormat.Number(start)} bis {UiFormat.Number(end - 1)} …", done, fragFilesTotal);
                if ((step + 1) % 20 == 0)
                    RaiseLog($"{UiFormat.Number(done)} von {UiFormat.Number(fragFilesTotal)} fragmentierten Dateien zusammengeführt.");
            }

            lock (_lock)
            {
                for (int c = 0; c < ClusterCount; c++)
                    if (_map[c] == ClusterState.Fragmented) _map[c] = ClusterState.Used;
                _revealLimit = ClusterCount;
            }
            var old = _lastAnalysis;
            _lastAnalysis = old with
            {
                FilesFragmented = 0,
                FragmentsTotal = 0,
                ClustersInFragmentedFiles = 0,
                FragmentedFiles = Array.Empty<FileFragmentInfo>(),
            };
            _phase = EnginePhase.Done;
            RaiseProgress(EnginePhase.Done, 100, "Defragmentierung abgeschlossen.", fragFilesTotal, fragFilesTotal);
            RaiseLog("Defragmentierung abgeschlossen.");
            MapChanged?.Invoke();
        }
        catch (OperationCanceledException)
        {
            _phase = EnginePhase.Cancelled;
            RaiseProgress(EnginePhase.Cancelled, 0, "Defragmentierung abgebrochen.", 0, 0);
            RaiseLog("Defragmentierung abgebrochen.");
            MapChanged?.Invoke();
            throw;
        }
    }, CancellationToken.None);

    private void RaiseLog(string message) => Log?.Invoke(message);

    private void RaiseProgress(EnginePhase phase, double percent, string message, long processed, long total)
        => Progress?.Invoke(new EngineProgress(phase, percent, message, processed, total));

    public void Dispose()
    {
        // Nichts freizugeben.
    }
}

/// <summary>Liefert simulierte Laufwerke für den Demo-Modus.</summary>
public sealed class DemoDriveEnumerator : IDriveEnumerator
{
    public IReadOnlyList<DriveDescriptor> Enumerate() => new List<DriveDescriptor>
    {
        new()
        {
            Letter = 'C', VolumeLabel = "System", FileSystem = "NTFS", IsFixed = true, DiskNumber = 0,
            DiskModel = "Samsung SSD 980 PRO 1TB", BusType = "NVMe", Media = MediaKind.Ssd, HasSeekPenalty = false,
            TotalBytes = 999_130_927_104, FreeBytes = 412_345_678_848,
        },
        new()
        {
            Letter = 'E', VolumeLabel = "Archiv", FileSystem = "NTFS", IsFixed = true, DiskNumber = 3,
            DiskModel = "WDC WD40EFRX-68N32N0", BusType = "SATA", Media = MediaKind.Hdd, HasSeekPenalty = true,
            TotalBytes = 4_000_787_025_920, FreeBytes = 1_234_567_890_000,
        },
        new()
        {
            Letter = 'G', VolumeLabel = "Daten", FileSystem = "NTFS", IsFixed = true, DiskNumber = 1,
            DiskModel = "ST8000DM004-2CX188", BusType = "SATA", Media = MediaKind.Hdd, HasSeekPenalty = true,
            TotalBytes = 8_001_427_099_648, FreeBytes = 3_187_654_321_152,
        },
        new()
        {
            Letter = 'H', VolumeLabel = "Spiele", FileSystem = "NTFS", IsFixed = true, DiskNumber = 2,
            DiskModel = "Crucial MX500 2TB", BusType = "SATA", Media = MediaKind.Ssd, HasSeekPenalty = false,
            TotalBytes = 2_000_396_742_656, FreeBytes = 765_432_109_056,
        },
        new()
        {
            Letter = 'J', VolumeLabel = "Backup", FileSystem = "FAT32", IsFixed = true, DiskNumber = 4,
            DiskModel = "Hitachi HDT721010SLA360", BusType = "SATA", Media = MediaKind.Hdd, HasSeekPenalty = true,
            TotalBytes = 1_000_202_043_392, FreeBytes = 400_000_000_000,
        },
        new()
        {
            Letter = 'I', VolumeLabel = "", FileSystem = "", IsFixed = false, DiskNumber = -1,
            DiskModel = "", BusType = "", Media = MediaKind.Unknown, HasSeekPenalty = null,
            TotalBytes = 0, FreeBytes = 0,
        },
    };
}
