using System.IO;
using HddDefrag.Core;

namespace HddDefrag.Engine.Testing;

/// <summary>Eine Datei des simulierten Volumes: Pfad und Extents.</summary>
public sealed record FakeFile(string Path, IReadOnlyList<Extent> Extents);

/// <summary>Protokolleintrag eines <see cref="IVolumeAccess.MoveFile"/>-Aufrufs.</summary>
public sealed record FakeMoveCall(string Path, long StartVcn, long TargetLcn, long ClusterCount);

/// <summary>
/// In-Memory-Implementierung von <see cref="IVolumeAccess"/> für Tests. Simuliert ein kleines Volume mit Bitmap und Dateien;
/// <see cref="MoveFile"/> prüft, dass das Ziel frei ist, und schreibt Bitmap und Extents um.
/// </summary>
public sealed class FakeVolumeAccess : IVolumeAccess
{
    private readonly object _lock = new();
    private readonly VolumeBitmap _bitmap;
    private readonly Dictionary<string, List<Extent>> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FakeMoveCall> _moves = new();
    private readonly Dictionary<string, int> _failingPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <param name="totalClusters">Gesamtzahl der Cluster (Standard 100.000).</param>
    /// <param name="files">Dateien mit ihren Extents; deren Cluster werden in der Bitmap als belegt markiert.</param>
    /// <param name="mftStartLcn">Start der $MFT; wird mit <see cref="NtfsVolumeInfo.MftClusters"/> Clustern als belegt markiert.</param>
    /// <param name="mftZoneStart">Beginn der MFT-Zone (einschließlich).</param>
    /// <param name="mftZoneEnd">Ende der MFT-Zone (ausschließlich).</param>
    public FakeVolumeAccess(IEnumerable<FakeFile> files,
        long totalClusters = 100_000,
        int bytesPerCluster = 4096,
        char letter = 'Z',
        long mftStartLcn = 1_000,
        long mftClusters = 100,
        long mftZoneStart = 1_000,
        long mftZoneEnd = 12_000)
    {
        Letter = letter;
        _bitmap = new VolumeBitmap(totalClusters, new ulong[(totalClusters + 63) / 64]);
        Info = new NtfsVolumeInfo(
            totalClusters, bytesPerCluster, 512,
            mftStartLcn, totalClusters / 2,
            MftValidDataLengthBytes: mftClusters * bytesPerCluster,
            mftZoneStart, mftZoneEnd);

        _bitmap.SetRange(mftStartLcn, mftClusters, true);
        _bitmap.SetRange(Info.Mft2StartLcn, 1, true);

        foreach (var file in files)
        {
            var extents = file.Extents.ToList();
            foreach (var e in extents)
            {
                if (e.IsVirtual) continue;
                if (!_bitmap.IsRangeFree(e.Lcn, e.ClusterCount))
                    throw new ArgumentException($"Extent von '{file.Path}' überschneidet bereits belegte Cluster: LCN {e.Lcn}, {e.ClusterCount} Cluster.");
                _bitmap.SetRange(e.Lcn, e.ClusterCount, true);
            }
            _files[file.Path] = extents;
        }
    }

    public char Letter { get; }
    public NtfsVolumeInfo Info { get; }

    /// <summary>Zusätzliche Wartezeit pro <see cref="MoveFile"/>-Aufruf (zum Testen von Abbruch und Pause).</summary>
    public TimeSpan MoveDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Wird nach jedem erfolgreichen <see cref="MoveFile"/> aufgerufen (z. B. um in Tests abzubrechen).</summary>
    public Action<FakeMoveCall>? OnMoved { get; set; }

    /// <summary>Anzahl der <see cref="ReadBitmap"/>-Aufrufe.</summary>
    public int ReadBitmapCalls { get; private set; }

    public IReadOnlyList<FakeMoveCall> Moves
    {
        get { lock (_lock) return _moves.ToArray(); }
    }

    public bool IsDisposed => _disposed;

    /// <summary>Lässt <see cref="MoveFile"/> für diese Datei mit einer <see cref="VolumeAccessException"/> (Win32-Code) scheitern.</summary>
    public void FailMovesFor(string path, int win32Error)
    {
        lock (_lock) _failingPaths[path] = win32Error;
    }

    /// <summary>Kopie der aktuellen Bitmap (wie beim echten Lesen) – gleichzeitig der Referenzzustand für Prüfungen.</summary>
    public VolumeBitmap ReadBitmap()
    {
        lock (_lock)
        {
            ReadBitmapCalls++;
            return new VolumeBitmap(_bitmap.TotalClusters, (ulong[])_bitmap.Words.Clone());
        }
    }

    public IReadOnlyList<Extent> GetExtents(string fullPath)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(fullPath, out var extents))
                throw new VolumeAccessException($"Datei nicht gefunden: {fullPath}", 2);
            return extents.ToArray();
        }
    }

    public void MoveFile(string fullPath, long startVcn, long targetLcn, long clusterCount)
    {
        if (MoveDelay > TimeSpan.Zero) Thread.Sleep(MoveDelay);

        FakeMoveCall call;
        lock (_lock)
        {
            if (_failingPaths.TryGetValue(fullPath, out int code))
                throw new VolumeAccessException($"Verschieben fehlgeschlagen (simuliert): {fullPath}", code);
            if (!_files.TryGetValue(fullPath, out var extents))
                throw new VolumeAccessException($"Datei nicht gefunden: {fullPath}", 2);
            if (clusterCount <= 0 || startVcn < 0)
                throw new VolumeAccessException("Ungültige Parameter für MoveFile.", 87);
            if (!_bitmap.IsRangeFree(targetLcn, clusterCount))
                throw new VolumeAccessException($"Zielbereich nicht frei: LCN {targetLcn}, {clusterCount} Cluster.", 87);

            long endVcn = startVcn + clusterCount;
            var result = new List<Extent>();
            var freed = new List<(long Lcn, long Count)>();
            long covered = 0;

            foreach (var e in extents)
            {
                long eEnd = e.Vcn + e.ClusterCount;
                if (eEnd <= startVcn || e.Vcn >= endVcn)
                {
                    result.Add(e);
                    continue;
                }

                // Überlappung: Anfang und Ende des Extents bleiben, der Mittelteil wird verschoben
                long ovStart = Math.Max(e.Vcn, startVcn);
                long ovEnd = Math.Min(eEnd, endVcn);
                if (e.IsVirtual)
                    throw new VolumeAccessException("Virtuelle Extents können nicht verschoben werden.", 87);
                if (ovStart > e.Vcn) result.Add(new Extent(e.Vcn, e.Lcn, ovStart - e.Vcn));
                freed.Add((e.Lcn + (ovStart - e.Vcn), ovEnd - ovStart));
                covered += ovEnd - ovStart;
                if (ovEnd < eEnd) result.Add(new Extent(ovEnd, e.Lcn + (ovEnd - e.Vcn), eEnd - ovEnd));
            }

            if (covered != clusterCount)
                throw new VolumeAccessException("Der angegebene VCN-Bereich ist nicht vollständig belegt.", 87);

            result.Add(new Extent(startVcn, targetLcn, clusterCount));
            result.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));

            // Direkt aneinander anschließende Extents wie im echten NTFS zu einem zusammenfassen
            var merged = new List<Extent>();
            foreach (var e in result)
            {
                if (merged.Count > 0)
                {
                    var last = merged[^1];
                    if (!last.IsVirtual && !e.IsVirtual && last.Vcn + last.ClusterCount == e.Vcn && last.Lcn + last.ClusterCount == e.Lcn)
                    {
                        merged[^1] = last with { ClusterCount = last.ClusterCount + e.ClusterCount };
                        continue;
                    }
                }
                merged.Add(e);
            }

            _bitmap.SetRange(targetLcn, clusterCount, true);
            foreach (var (lcn, count) in freed) _bitmap.SetRange(lcn, count, false);
            _files[fullPath] = merged;

            call = new FakeMoveCall(fullPath, startVcn, targetLcn, clusterCount);
            _moves.Add(call);
        }

        OnMoved?.Invoke(call);
    }

    /// <summary>Dateigröße in Bytes (Cluster × Clustergröße) für die Scanner-Abstraktion.</summary>
    public long GetFileSize(string fullPath)
    {
        lock (_lock)
        {
            if (!_files.TryGetValue(fullPath, out var extents))
                throw new FileNotFoundException(fullPath);
            return extents.Where(e => !e.IsVirtual).Sum(e => e.ClusterCount) * Info.BytesPerCluster;
        }
    }

    /// <summary>
    /// Dateiaufzählung für <see cref="FileScanner"/>: liefert die direkten Einträge eines Verzeichnisses aus den Pfaden der Fake-Dateien
    /// (Unterverzeichnisse enden mit Verzeichnistrennzeichen).
    /// </summary>
    public Func<string, IEnumerable<string>> CreateEnumerator()
    {
        return directory =>
        {
            string dir = directory.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string[] paths;
            lock (_lock) paths = _files.Keys.ToArray();

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) continue;
                string rest = path[dir.Length..];
                int sep = rest.IndexOfAny(new[] { '\\', '/' });
                result.Add(sep < 0 ? path : dir + rest[..sep] + Path.DirectorySeparatorChar);
            }
            return result.ToList();
        };
    }

    public void Dispose() => _disposed = true;
}
