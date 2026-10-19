using System.Numerics;

namespace HddDefrag.Core;

/// <summary>Ein Extent (zusammenhängender Lauf) einer Datei auf dem Datenträger.</summary>
/// <param name="Vcn">Virtuelle Clusternummer innerhalb der Datei (Start).</param>
/// <param name="Lcn">Logische Clusternummer auf dem Volume; -1 bei virtuellen Extents (sparse/komprimiert, kein Platz belegt).</param>
/// <param name="ClusterCount">Anzahl Cluster dieses Extents.</param>
public readonly record struct Extent(long Vcn, long Lcn, long ClusterCount)
{
    public bool IsVirtual => Lcn < 0;
    public long EndLcnExclusive => Lcn + ClusterCount;
}

/// <summary>Belegungs-Bitmap des Volumes: 1 Bit pro Cluster, Bit gesetzt = belegt.</summary>
public sealed class VolumeBitmap
{
    private readonly ulong[] _words;

    public VolumeBitmap(long totalClusters, ulong[] words)
    {
        if (words.LongLength * 64 < totalClusters)
            throw new ArgumentException("Bitmap ist kleiner als die Clusteranzahl.", nameof(words));
        TotalClusters = totalClusters;
        _words = words;
    }

    public long TotalClusters { get; }

    /// <summary>Rohe Wörter; Bit i von Wort w entspricht Cluster w*64+i (LSB zuerst).</summary>
    public ulong[] Words => _words;

    public bool IsUsed(long lcn)
    {
        if ((ulong)lcn >= (ulong)TotalClusters) return true; // außerhalb gilt als belegt
        return (_words[lcn >> 6] & (1UL << (int)(lcn & 63))) != 0;
    }

    public void SetUsed(long lcn, bool used)
    {
        if ((ulong)lcn >= (ulong)TotalClusters) return;
        if (used) _words[lcn >> 6] |= 1UL << (int)(lcn & 63);
        else _words[lcn >> 6] &= ~(1UL << (int)(lcn & 63));
    }

    /// <summary>Setzt einen Bereich [lcn, lcn+count) auf belegt/frei.</summary>
    public void SetRange(long lcn, long count, bool used)
    {
        for (long i = lcn; i < lcn + count; i++) SetUsed(i, used);
    }

    /// <summary>Anzahl belegter Cluster im Bereich [start, endExclusive).</summary>
    public long CountUsed(long start, long endExclusive)
    {
        if (start < 0) start = 0;
        if (endExclusive > TotalClusters) endExclusive = TotalClusters;
        if (start >= endExclusive) return 0;
        long count = 0;
        long i = start;
        while (i < endExclusive && (i & 63) != 0) { if (IsUsed(i)) count++; i++; }
        while (i + 64 <= endExclusive)
        {
            count += BitOperations.PopCount(_words[i >> 6]);
            i += 64;
        }
        while (i < endExclusive) { if (IsUsed(i)) count++; i++; }
        return count;
    }

    /// <summary>Prüft, ob der ganze Bereich [lcn, lcn+count) frei ist.</summary>
    public bool IsRangeFree(long lcn, long count)
    {
        if (lcn < 0 || count <= 0 || lcn + count > TotalClusters) return false;
        return CountUsed(lcn, lcn + count) == 0;
    }

    /// <summary>
    /// Sucht ab <paramref name="searchStart"/> den ersten freien zusammenhängenden Bereich mit mindestens
    /// <paramref name="count"/> Clustern. Gibt -1 zurück, wenn keiner gefunden wurde.
    /// </summary>
    public long FindFreeRun(long searchStart, long count, long searchEndExclusive = -1)
    {
        if (searchEndExclusive < 0 || searchEndExclusive > TotalClusters) searchEndExclusive = TotalClusters;
        if (count <= 0) return -1;
        long runStart = -1;
        long lcn = Math.Max(0, searchStart);
        while (lcn < searchEndExclusive)
        {
            // Schnell über komplett belegte Wörter springen
            if ((lcn & 63) == 0 && lcn + 64 <= searchEndExclusive && _words[lcn >> 6] == ulong.MaxValue)
            {
                runStart = -1;
                lcn += 64;
                continue;
            }
            // Schnell über komplett freie Wörter springen
            if ((lcn & 63) == 0 && lcn + 64 <= searchEndExclusive && _words[lcn >> 6] == 0UL)
            {
                if (runStart < 0) runStart = lcn;
                lcn += 64;
                if (lcn - runStart >= count) return runStart;
                continue;
            }
            if (IsUsed(lcn))
            {
                runStart = -1;
            }
            else
            {
                if (runStart < 0) runStart = lcn;
                if (lcn - runStart + 1 >= count) return runStart;
            }
            lcn++;
        }
        return -1;
    }

    /// <summary>Länge des längsten freien zusammenhängenden Bereichs.</summary>
    public long LargestFreeRun()
    {
        long best = 0, run = 0;
        long total = TotalClusters;
        for (long w = 0; w < _words.LongLength; w++)
        {
            ulong word = _words[w];
            long baseLcn = w << 6;
            if (baseLcn >= total) break;
            if (word == 0UL && baseLcn + 64 <= total) { run += 64; if (run > best) best = run; continue; }
            if (word == ulong.MaxValue) { run = 0; continue; }
            for (int b = 0; b < 64 && baseLcn + b < total; b++)
            {
                if ((word & (1UL << b)) == 0) { run++; if (run > best) best = run; }
                else run = 0;
            }
        }
        return best;
    }
}

/// <summary>NTFS-Kenndaten des Volumes (aus NTFS_VOLUME_DATA_BUFFER).</summary>
public sealed record NtfsVolumeInfo(
    long TotalClusters,
    int BytesPerCluster,
    int BytesPerSector,
    long MftStartLcn,
    long Mft2StartLcn,
    long MftValidDataLengthBytes,
    long MftZoneStart,
    long MftZoneEnd)
{
    /// <summary>Anzahl Cluster, die die $MFT aktuell belegt.</summary>
    public long MftClusters => BytesPerCluster <= 0 ? 0 : (MftValidDataLengthBytes + BytesPerCluster - 1) / BytesPerCluster;
}

/// <summary>
/// Low-Level-Zugriff auf ein NTFS-Volume. Alle Methoden sind synchron und blockierend.
/// Fehler der Win32-API werden als <see cref="VolumeAccessException"/> geworfen.
/// </summary>
public interface IVolumeAccess : IDisposable
{
    char Letter { get; }
    NtfsVolumeInfo Info { get; }

    /// <summary>Liest die komplette Belegungs-Bitmap (FSCTL_GET_VOLUME_BITMAP). Kann bei großen Volumes einige Sekunden dauern.</summary>
    VolumeBitmap ReadBitmap();

    /// <summary>Liest die Extents einer Datei oder eines Verzeichnisses (FSCTL_GET_RETRIEVAL_POINTERS). Leere Liste bei residenten Dateien.</summary>
    IReadOnlyList<Extent> GetExtents(string fullPath);

    /// <summary>
    /// Verschiebt <paramref name="clusterCount"/> Cluster der Datei ab VCN <paramref name="startVcn"/> nach LCN <paramref name="targetLcn"/>
    /// (FSCTL_MOVE_FILE). Der Zielbereich muss frei sein; der Aufrufer ist für die Prüfung zuständig.
    /// </summary>
    void MoveFile(string fullPath, long startVcn, long targetLcn, long clusterCount);
}

public sealed class VolumeAccessException : Exception
{
    public int Win32Error { get; }

    public VolumeAccessException(string message, int win32Error = 0, Exception? inner = null) : base(message, inner)
    {
        Win32Error = win32Error;
    }
}
