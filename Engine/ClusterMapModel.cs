using HddDefrag.Core;

namespace HddDefrag.Engine;

/// <summary>Ein Overlay-Bereich [Start, Start+Count) mit einem Zustand, der über die Bitmap-Grundfarbe gelegt wird.</summary>
public readonly record struct OverlayRange(long Start, long Count, ClusterState State)
{
    public long EndExclusive => Start + Count;
}

/// <summary>
/// Datenmodell der Clusterkarte: die Belegungs-Bitmap plus sortierte Overlay-Bereiche (System, Unmovable, Fragmented, Moving).
/// Es wird nie ein Eintrag pro Cluster angelegt; <see cref="Render"/> rechnet direkt auf die gewünschte Blockanzahl herunter.
/// Alle Methoden sind threadsicher. Schreibzugriffe auf die Bitmap sollen nur über dieses Modell erfolgen;
/// lesende Zugriffe des (einzigen) schreibenden Threads dürfen direkt über <see cref="Bitmap"/> erfolgen.
/// </summary>
public sealed class ClusterMapModel
{
    private readonly object _lock = new();

    // Eine sortierte Liste pro Overlay-Zustand, Index = (int)ClusterState. Free/Used haben keine Overlays.
    private readonly List<OverlayRange>[] _layers;
    private VolumeBitmap? _bitmap;

    public ClusterMapModel()
    {
        int layerCount = Enum.GetValues<ClusterState>().Max(s => (int)s) + 1;
        _layers = new List<OverlayRange>[layerCount];
        for (int i = 0; i < layerCount; i++) _layers[i] = new List<OverlayRange>();
    }

    /// <summary>Aktuelle Bitmap oder null, solange keine gelesen wurde.</summary>
    public VolumeBitmap? Bitmap
    {
        get { lock (_lock) return _bitmap; }
    }

    public bool HasBitmap => Bitmap is not null;

    /// <summary>Ersetzt die Bitmap (z. B. nach einem frischen Lesen). Overlays bleiben erhalten.</summary>
    public void SetBitmap(VolumeBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        lock (_lock) _bitmap = bitmap;
    }

    // ------------------------------------------------------------------ Overlays

    /// <summary>Ersetzt alle Bereiche eines Zustands durch die übergebenen (werden sortiert).</summary>
    public void ReplaceOverlay(ClusterState state, IEnumerable<OverlayRange> ranges)
    {
        var list = new List<OverlayRange>();
        foreach (var r in ranges)
            if (r.Count > 0 && r.Start >= 0) list.Add(r with { State = state });
        list.Sort(CompareByStart);
        int idx = LayerIndex(state);
        lock (_lock) _layers[idx] = list;
    }

    /// <summary>Ersetzt alle Bereiche eines Zustands durch Extents (virtuelle Extents werden ignoriert).</summary>
    public void ReplaceOverlay(ClusterState state, IEnumerable<Extent> extents) =>
        ReplaceOverlay(state, ToRanges(extents, state));

    /// <summary>Fügt Bereiche zu einem Zustand hinzu und hält die Liste sortiert.</summary>
    public void AddOverlay(ClusterState state, IEnumerable<OverlayRange> ranges)
    {
        var add = new List<OverlayRange>();
        foreach (var r in ranges)
            if (r.Count > 0 && r.Start >= 0) add.Add(r with { State = state });
        if (add.Count == 0) return;
        int layerIdx = LayerIndex(state);
        lock (_lock)
        {
            var layer = _layers[layerIdx];
            if (add.Count <= 32)
            {
                foreach (var r in add)
                {
                    int idx = layer.BinarySearch(r, StartComparer.Instance);
                    if (idx < 0) idx = ~idx;
                    layer.Insert(idx, r);
                }
            }
            else
            {
                layer.AddRange(add);
                layer.Sort(CompareByStart);
            }
        }
    }

    public void AddOverlay(ClusterState state, IEnumerable<Extent> extents) =>
        AddOverlay(state, ToRanges(extents, state));

    /// <summary>
    /// Entfernt Bereiche eines Zustands. Entfernt wird jeweils der Eintrag mit exakt gleichem Start und gleicher Länge;
    /// nicht vorhandene Bereiche werden ignoriert.
    /// </summary>
    public void RemoveOverlay(ClusterState state, IEnumerable<OverlayRange> ranges)
    {
        var copy = ranges.ToList();
        int layerIdx = LayerIndex(state);
        lock (_lock) RemoveLocked(layerIdx, state, copy);
    }

    public void RemoveOverlay(ClusterState state, IEnumerable<Extent> extents) =>
        RemoveOverlay(state, ToRanges(extents, state));

    /// <summary>Entfernt alle Bereiche eines Zustands.</summary>
    public void ClearOverlay(ClusterState state)
    {
        int idx = LayerIndex(state);
        lock (_lock) _layers[idx].Clear();
    }

    /// <summary>Anzahl der Overlay-Bereiche eines Zustands (für Tests und Diagnose).</summary>
    public int OverlayCount(ClusterState state)
    {
        int idx = LayerIndex(state);
        lock (_lock) return _layers[idx].Count;
    }

    /// <summary>Momentaufnahme der Bereiche eines Zustands (sortiert).</summary>
    public IReadOnlyList<OverlayRange> GetOverlay(ClusterState state)
    {
        int idx = LayerIndex(state);
        lock (_lock) return _layers[idx].ToArray();
    }

    /// <summary>Setzt die aktuell bewegten Bereiche (Quelle und Ziel) und ersetzt dabei den vorherigen Moving-Zustand.</summary>
    public void SetMoving(IEnumerable<(long Start, long Count)> ranges)
    {
        ReplaceOverlay(ClusterState.Moving, ranges.Select(r => new OverlayRange(r.Start, r.Count, ClusterState.Moving)));
    }

    public void ClearMoving() => ClearOverlay(ClusterState.Moving);

    // ------------------------------------------------------------------ Bitmap-Änderungen

    /// <summary>Markiert [lcn, lcn+count) in der Bitmap als belegt oder frei.</summary>
    public void MarkRange(long lcn, long count, bool used)
    {
        lock (_lock) _bitmap?.SetRange(lcn, count, used);
    }

    /// <summary>
    /// Wendet eine Verschiebung atomar an: Zielbereich belegt, alle Quellbereiche frei; der Moving-Zustand wird dabei zurückgesetzt.
    /// </summary>
    public void ApplyMove(IEnumerable<(long Start, long Count)> freedSources, long targetLcn, long targetCount)
    {
        var sources = freedSources.ToList();
        lock (_lock)
        {
            if (_bitmap is not null)
            {
                _bitmap.SetRange(targetLcn, targetCount, true);
                foreach (var (start, count) in sources) _bitmap.SetRange(start, count, false);
            }
            _layers[(int)ClusterState.Moving].Clear();
        }
    }

    // ------------------------------------------------------------------ Rendern

    /// <summary>
    /// Rechnet die Karte auf <paramref name="blockCount"/> Blöcke herunter. Pro Block gilt: Used, wenn mindestens ein Cluster belegt ist, sonst Free;
    /// danach werden die Overlays darübergelegt (höchste Priorität gewinnt). Ein Overlay wirkt nur auf Blöcke, die nicht komplett frei sind –
    /// außer Moving, das immer sichtbar ist. Gibt ein leeres Array zurück, solange keine Bitmap vorliegt.
    /// </summary>
    public ClusterState[] Render(int blockCount)
    {
        if (blockCount <= 0) return Array.Empty<ClusterState>();
        lock (_lock)
        {
            if (_bitmap is null || _bitmap.TotalClusters <= 0) return Array.Empty<ClusterState>();
            long total = _bitmap.TotalClusters;
            var result = new ClusterState[blockCount];

            long blockStart = 0;
            for (int i = 0; i < blockCount; i++)
            {
                long blockEnd = BlockStart(i + 1, blockCount, total);
                result[i] = _bitmap.CountUsed(blockStart, blockEnd) > 0 ? ClusterState.Used : ClusterState.Free;
                blockStart = blockEnd;
            }

            // Pro Zustand sind die Bereiche nach Start sortiert; jeder Bereich überstreicht einen zusammenhängenden
            // Blockbereich, die Blockzeiger laufen dadurch pro Liste im Wesentlichen nur vorwärts (linearer Aufwand).
            for (int s = (int)ClusterState.System; s < _layers.Length; s++)
            {
                var state = (ClusterState)s;
                foreach (var range in _layers[s])
                {
                    long start = Math.Max(0, range.Start);
                    long end = Math.Min(total, range.Start + range.Count);
                    if (start >= end) continue;
                    int first = BlockOf(start, blockCount, total);
                    int last = BlockOf(end - 1, blockCount, total);
                    for (int b = first; b <= last; b++)
                    {
                        if (result[b] >= state) continue;
                        if (state == ClusterState.Moving || result[b] != ClusterState.Free) result[b] = state;
                    }
                }
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ Hilfsmethoden

    /// <summary>Erster Cluster des Blocks <paramref name="block"/> (gleichmäßige Aufteilung).</summary>
    internal static long BlockStart(int block, int blockCount, long total) => block * total / blockCount;

    /// <summary>Index des Blocks, der den Cluster <paramref name="cluster"/> enthält.</summary>
    internal static int BlockOf(long cluster, int blockCount, long total)
    {
        long b = ((cluster + 1) * blockCount + total - 1) / total - 1;
        if (b < 0) b = 0;
        if (b >= blockCount) b = blockCount - 1;
        return (int)b;
    }

    private static int LayerIndex(ClusterState state)
    {
        if (state < ClusterState.System)
            throw new ArgumentException("Für Free und Used gibt es keine Overlays.", nameof(state));
        return (int)state;
    }

    private static IEnumerable<OverlayRange> ToRanges(IEnumerable<Extent> extents, ClusterState state)
    {
        foreach (var e in extents)
            if (!e.IsVirtual && e.ClusterCount > 0) yield return new OverlayRange(e.Lcn, e.ClusterCount, state);
    }

    private void RemoveLocked(int layerIdx, ClusterState state, List<OverlayRange> ranges)
    {
        var layer = _layers[layerIdx];
        foreach (var r in ranges)
        {
            if (r.Count <= 0) continue;
            int idx = layer.BinarySearch(new OverlayRange(r.Start, 0, state), StartComparer.Instance);
            if (idx < 0) continue;
            // Bei mehreren Einträgen mit gleichem Start (z. B. Hardlinks) den mit passender Länge suchen.
            int i = idx;
            while (i > 0 && layer[i - 1].Start == r.Start) i--;
            for (; i < layer.Count && layer[i].Start == r.Start; i++)
            {
                if (layer[i].Count == r.Count)
                {
                    layer.RemoveAt(i);
                    break;
                }
            }
        }
    }

    private static int CompareByStart(OverlayRange a, OverlayRange b) => a.Start.CompareTo(b.Start);

    private sealed class StartComparer : IComparer<OverlayRange>
    {
        public static readonly StartComparer Instance = new();
        public int Compare(OverlayRange x, OverlayRange y) => x.Start.CompareTo(y.Start);
    }
}
