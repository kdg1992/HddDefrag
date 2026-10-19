namespace HddDefrag.Core;

/// <summary>
/// Zustand eines Kartenblocks (ein Block fasst viele Cluster zusammen).
/// Die numerische Reihenfolge ist die Priorität beim Zusammenfassen: der höchste Wert gewinnt.
/// </summary>
public enum ClusterState : byte
{
    /// <summary>Alle Cluster frei (Win98: weiß).</summary>
    Free = 0,
    /// <summary>Belegt, nicht fragmentiert (Win98: blau).</summary>
    Used = 1,
    /// <summary>Systembereich: $MFT, MFT-Zone, $MFT-Spiegel (Win98: grün).</summary>
    System = 2,
    /// <summary>Datei nicht verschiebbar (Auslagerungsdatei usw.) (dunkelgrau).</summary>
    Unmovable = 3,
    /// <summary>Gehört zu einer fragmentierten Datei (Win98: rot).</summary>
    Fragmented = 4,
    /// <summary>Wird gerade gelesen oder geschrieben (Win98: gelb).</summary>
    Moving = 5,
}

public enum EnginePhase
{
    Idle,
    ReadingBitmap,
    Scanning,
    Analyzing,
    Defragmenting,
    Done,
    Cancelled,
    Failed,
}

/// <summary>Fortschrittsmeldung der Engine; wird vom Hintergrund-Thread ausgelöst.</summary>
public sealed record EngineProgress(EnginePhase Phase, double Percent, string Message, long FilesProcessed, long FilesTotal);

/// <summary>Informationen zu einer Datei und ihren Extents.</summary>
public sealed record FileFragmentInfo(string FullPath, long SizeBytes, long ClusterCount, int FragmentCount, IReadOnlyList<Extent> Extents)
{
    public bool IsFragmented => FragmentCount > 1;
}

public sealed record AnalysisResult(
    long FilesScanned,
    long FilesFragmented,
    long FragmentsTotal,
    long ClustersInFragmentedFiles,
    long TotalClusters,
    long UsedClusters,
    long FreeClusters,
    long LargestFreeRunClusters,
    IReadOnlyList<FileFragmentInfo> FragmentedFiles,
    TimeSpan Duration)
{
    /// <summary>Anteil der belegten Cluster, die zu fragmentierten Dateien gehören (0..100).</summary>
    public double FragmentationPercent => UsedClusters == 0 ? 0 : 100.0 * ClustersInFragmentedFiles / UsedClusters;
}

public sealed record DefragOptions
{
    /// <summary>Dateien größer als dieser Wert werden übersprungen (0 = kein Limit).</summary>
    public long MaxFileSizeBytes { get; init; } = 0;

    /// <summary>Nach jeder Verschiebung die Extents erneut lesen und prüfen.</summary>
    public bool VerifyAfterMove { get; init; } = true;
}

/// <summary>Pause-Steuerung, die von der UI gesetzt und von der Engine abgefragt wird.</summary>
public sealed class PauseController
{
    private readonly ManualResetEventSlim _resume = new(true);

    public bool IsPaused => !_resume.IsSet;
    public void Pause() => _resume.Reset();
    public void Resume() => _resume.Set();

    /// <summary>Blockiert, solange pausiert ist; wirft OperationCanceledException bei Abbruch.</summary>
    public void WaitIfPaused(CancellationToken ct) => _resume.Wait(ct);
}

/// <summary>
/// Analyse- und Defragmentierungs-Engine für genau ein Volume.
/// Alle Events werden auf Hintergrund-Threads ausgelöst; die UI muss selbst auf den Dispatcher wechseln.
/// </summary>
public interface IDefragEngine : IDisposable
{
    DriveDescriptor Drive { get; }
    NtfsVolumeInfo? VolumeInfo { get; }
    EnginePhase Phase { get; }
    AnalysisResult? LastAnalysis { get; }

    /// <summary>Logzeile (deutsch, ohne Zeitstempel).</summary>
    event Action<string>? Log;

    event Action<EngineProgress>? Progress;

    /// <summary>Die Karte hat sich geändert; die UI sollte <see cref="RenderMap"/> neu aufrufen. Wird gedrosselt ausgelöst (höchstens ~10/s).</summary>
    event Action? MapChanged;

    /// <summary>Liest die Bitmap und analysiert alle Dateien. Setzt <see cref="LastAnalysis"/>.</summary>
    Task<AnalysisResult> AnalyzeAsync(CancellationToken ct);

    /// <summary>Defragmentiert anhand der letzten Analyse (führt vorher automatisch eine Analyse aus, wenn keine vorliegt).</summary>
    Task DefragmentAsync(DefragOptions options, PauseController pause, CancellationToken ct);

    /// <summary>
    /// Rechnet die Karte auf <paramref name="blockCount"/> Blöcke herunter. Jeder Block erhält den Zustand mit der höchsten Priorität
    /// (siehe <see cref="ClusterState"/>); ein Block ist nur Free, wenn alle seine Cluster frei sind. Threadsicher.
    /// Gibt ein leeres Array zurück, solange keine Bitmap gelesen wurde.
    /// </summary>
    ClusterState[] RenderMap(int blockCount);
}
