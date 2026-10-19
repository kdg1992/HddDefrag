using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using HddDefrag.Core;
using Microsoft.Win32.SafeHandles;
using static HddDefrag.Native.NativeMethods;

namespace HddDefrag.Native;

/// <summary>
/// Low-Level-Zugriff auf ein NTFS-Volume über <c>\\.\X:</c>. Benötigt Administratorrechte.
/// Nicht threadsicher; alle Aufrufe sind synchron und blockierend.
/// </summary>
public sealed unsafe class VolumeAccess : IVolumeAccess
{
    /// <summary>Größe des Ausgabepuffers für FSCTL_GET_VOLUME_BITMAP (64 MB ≈ 537 Mio. Cluster pro Aufruf).</summary>
    private const int BitmapChunkBytes = 64 * 1024 * 1024;

    /// <summary>Größe des Ausgabepuffers für FSCTL_GET_RETRIEVAL_POINTERS (64 KB ≈ 4096 Extents pro Aufruf).</summary>
    private const int RetrievalBufferBytes = 64 * 1024;

    /// <summary>Ab dieser Pfadlänge wird das Präfix <c>\\?\</c> vorangestellt.</summary>
    private const int LongPathThreshold = 240;

    private const uint ShareAll = FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE;

    private readonly SafeFileHandle _volume;
    private bool _disposed;

    private VolumeAccess(char letter, SafeFileHandle volume, NtfsVolumeInfo info)
    {
        Letter = letter;
        _volume = volume;
        Info = info;
    }

    public char Letter { get; }

    public NtfsVolumeInfo Info { get; }

    /// <summary>
    /// Öffnet das Volume <paramref name="letter"/> mit Lese- und Schreibzugriff und liest sofort die NTFS-Kenndaten.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Kein gültiger Laufwerksbuchstabe.</exception>
    /// <exception cref="VolumeAccessException">Volume konnte nicht geöffnet werden oder ist kein NTFS.</exception>
    public static VolumeAccess Open(char letter)
    {
        letter = char.ToUpperInvariant(letter);
        if (letter is < 'A' or > 'Z')
            throw new ArgumentOutOfRangeException(nameof(letter), letter, "Ungültiger Laufwerksbuchstabe.");

        SafeFileHandle handle = CreateFile($@"\\.\{letter}:", GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            handle.Dispose();
            string hint = code == ERROR_ACCESS_DENIED
                ? " Zugriff verweigert – das Programm muss mit Administratorrechten gestartet werden."
                : string.Empty;
            throw CreateError($"Volume {letter}: konnte nicht geöffnet werden.{hint}", code);
        }

        try
        {
            NTFS_VOLUME_DATA_BUFFER data = default;
            bool ok = DeviceIoControl(handle, FSCTL_GET_NTFS_VOLUME_DATA, null, 0,
                &data, (uint)sizeof(NTFS_VOLUME_DATA_BUFFER), out uint returned, IntPtr.Zero);
            if (!ok)
            {
                int code = Marshal.GetLastWin32Error();
                // ERROR_MORE_DATA tritt auf, wenn NTFS zusätzlich NTFS_EXTENDED_VOLUME_DATA liefern möchte –
                // der Basisteil ist dann trotzdem vollständig gefüllt.
                if (code != ERROR_MORE_DATA)
                {
                    string hint = code is ERROR_INVALID_FUNCTION or ERROR_INVALID_PARAMETER
                        ? " Das Volume ist vermutlich kein NTFS-Volume."
                        : string.Empty;
                    throw CreateError($"NTFS-Volumedaten von {letter}: konnten nicht gelesen werden.{hint}", code);
                }
            }
            else if (returned < (uint)sizeof(NTFS_VOLUME_DATA_BUFFER))
            {
                throw new VolumeAccessException(
                    $"NTFS-Volumedaten von {letter}: sind unvollständig ({returned} von {sizeof(NTFS_VOLUME_DATA_BUFFER)} Byte).");
            }

            if (data.TotalClusters <= 0 || data.BytesPerCluster == 0)
                throw new VolumeAccessException($"NTFS-Volumedaten von {letter}: sind ungültig (keine Cluster).");

            var info = new NtfsVolumeInfo(
                TotalClusters: data.TotalClusters,
                BytesPerCluster: checked((int)data.BytesPerCluster),
                BytesPerSector: checked((int)data.BytesPerSector),
                MftStartLcn: data.MftStartLcn,
                Mft2StartLcn: data.Mft2StartLcn,
                MftValidDataLengthBytes: data.MftValidDataLength,
                MftZoneStart: data.MftZoneStart,
                MftZoneEnd: data.MftZoneEnd);

            return new VolumeAccess(letter, handle, info);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public VolumeBitmap ReadBitmap()
    {
        ThrowIfDisposed();

        byte* buffer = (byte*)NativeMemory.Alloc(BitmapChunkBytes);
        try
        {
            ulong[]? words = null;
            long totalClusters = 0;
            long nextLcn = 0;
            int headerSize = sizeof(VOLUME_BITMAP_BUFFER);

            while (true)
            {
                var input = new STARTING_LCN_INPUT_BUFFER { StartingLcn = nextLcn };
                bool ok = DeviceIoControl(_volume, FSCTL_GET_VOLUME_BITMAP,
                    &input, (uint)sizeof(STARTING_LCN_INPUT_BUFFER),
                    buffer, BitmapChunkBytes, out uint returned, IntPtr.Zero);
                int code = ok ? ERROR_SUCCESS : Marshal.GetLastWin32Error();
                if (!ok && code != ERROR_MORE_DATA)
                    throw CreateError($"Belegungs-Bitmap von {Letter}: konnte ab Cluster {nextLcn} nicht gelesen werden.", code);
                if (returned < (uint)headerSize)
                    throw new VolumeAccessException($"Belegungs-Bitmap von {Letter}: Antwort zu kurz ({returned} Byte).");

                var header = (VOLUME_BITMAP_BUFFER*)buffer;
                long startLcn = header->StartingLcn;     // von NTFS ggf. auf ein Vielfaches von 8 abgerundet
                long remainingClusters = header->BitmapSize; // Cluster ab startLcn bis zum Volume-Ende

                if (startLcn < 0 || (startLcn & 7) != 0 || remainingClusters < 0)
                    throw new VolumeAccessException(
                        $"Belegungs-Bitmap von {Letter}: unerwartete Antwort (StartingLcn {startLcn}, BitmapSize {remainingClusters}).");

                if (words is null)
                {
                    // Erster Aufruf ab LCN 0: BitmapSize ist die Gesamtzahl der Cluster.
                    totalClusters = startLcn + remainingClusters;
                    long wordCount = (totalClusters + 63) / 64;
                    if (wordCount > Array.MaxLength)
                        throw new VolumeAccessException($"Volume {Letter}: ist zu groß für die Bitmap ({totalClusters} Cluster).");
                    words = new ulong[wordCount];
                }

                // Alle Größen als long rechnen – bei 8 TB / 4 KB sind es ~1,95 Mrd. Cluster bzw. ~244 MB Bitmap.
                long bytesInResponse = (long)returned - headerSize;
                long bytesUntilEnd = (remainingClusters + 7) / 8;
                long byteOffset = startLcn / 8;
                long capacityBytes = words.LongLength * sizeof(ulong);
                long bytesToCopy = Math.Min(bytesInResponse, bytesUntilEnd);
                bytesToCopy = Math.Min(bytesToCopy, Math.Max(0, capacityBytes - byteOffset));

                if (bytesToCopy > 0)
                {
                    // Byte k, Bit b = Cluster 8k+b; als Little-Endian-ulong ergibt das Bit i von Wort w = Cluster 64w+i.
                    fixed (ulong* target = words)
                    {
                        Buffer.MemoryCopy(buffer + headerSize, (byte*)target + byteOffset,
                            capacityBytes - byteOffset, bytesToCopy);
                    }
                }

                if (ok) break; // alles gelesen

                long next = startLcn + bytesToCopy * 8;
                if (next >= totalClusters) break;
                if (next <= nextLcn)
                    throw new VolumeAccessException($"Belegungs-Bitmap von {Letter}: kein Fortschritt beim Lesen ab Cluster {nextLcn}.");
                nextLcn = next;
            }

            // Bits hinter dem letzten Cluster als belegt markieren, damit sie nie als freier Platz gelten.
            if (words.Length > 0 && (totalClusters & 63) != 0)
                words[^1] |= ulong.MaxValue << (int)(totalClusters & 63);

            return new VolumeBitmap(totalClusters, words);
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<Extent> GetExtents(string fullPath)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(fullPath);

        string path = ToWin32Path(fullPath);
        using SafeFileHandle file = CreateFile(path, FILE_READ_ATTRIBUTES, ShareAll, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (file.IsInvalid)
            throw CreateError($"Datei \"{fullPath}\" konnte nicht geöffnet werden.", Marshal.GetLastWin32Error());

        return ReadRetrievalPointers(file, fullPath);
    }

    /// <summary>Liest alle Extents über FSCTL_GET_RETRIEVAL_POINTERS (auch ohne Adminrechte für eigene Dateien möglich).</summary>
    internal static List<Extent> ReadRetrievalPointers(SafeFileHandle file, string displayPath, int bufferBytes = RetrievalBufferBytes)
    {
        var extents = new List<Extent>();
        bufferBytes = Math.Max(bufferBytes, 64);
        byte* buffer = (byte*)NativeMemory.Alloc((nuint)bufferBytes);
        try
        {
            int headerSize = sizeof(RETRIEVAL_POINTERS_BUFFER);
            int entrySize = sizeof(RETRIEVAL_POINTER_EXTENT);
            long startVcn = 0;

            while (true)
            {
                var input = new STARTING_VCN_INPUT_BUFFER { StartingVcn = startVcn };
                bool ok = DeviceIoControl(file, FSCTL_GET_RETRIEVAL_POINTERS,
                    &input, (uint)sizeof(STARTING_VCN_INPUT_BUFFER),
                    buffer, (uint)bufferBytes, out uint returned, IntPtr.Zero);
                int code = ok ? ERROR_SUCCESS : Marshal.GetLastWin32Error();

                if (!ok)
                {
                    // ERROR_HANDLE_EOF: residente Datei (Daten in der MFT) bzw. keine weiteren Extents.
                    if (code == ERROR_HANDLE_EOF) break;
                    if (code != ERROR_MORE_DATA)
                        throw CreateError($"Extents von \"{displayPath}\" konnten nicht gelesen werden.", code);
                }

                if (returned < (uint)headerSize)
                {
                    if (ok) break;
                    throw new VolumeAccessException($"Extents von \"{displayPath}\": Antwort zu kurz ({returned} Byte).");
                }

                var header = (RETRIEVAL_POINTERS_BUFFER*)buffer;
                long available = ((long)returned - headerSize) / entrySize;
                long count = Math.Min(header->ExtentCount, available);
                var entries = (RETRIEVAL_POINTER_EXTENT*)(buffer + headerSize);

                long vcn = header->StartingVcn;
                for (long i = 0; i < count; i++)
                {
                    long nextVcn = entries[i].NextVcn;
                    long lcn = entries[i].Lcn;
                    long length = nextVcn - vcn;
                    if (length > 0)
                        extents.Add(new Extent(vcn, lcn < 0 ? -1 : lcn, length)); // 0xFFFFFFFFFFFFFFFF = virtuell/sparse
                    vcn = nextVcn;
                }

                if (ok) break;

                if (vcn <= startVcn)
                    throw new VolumeAccessException($"Extents von \"{displayPath}\": kein Fortschritt ab VCN {startVcn}.");
                startVcn = vcn;
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
        return extents;
    }

    /// <inheritdoc />
    public void MoveFile(string fullPath, long startVcn, long targetLcn, long clusterCount)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrEmpty(fullPath);
        ArgumentOutOfRangeException.ThrowIfNegative(startVcn);
        ArgumentOutOfRangeException.ThrowIfNegative(targetLcn);
        if (clusterCount <= 0 || clusterCount > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(clusterCount), clusterCount,
                "Die Clusteranzahl muss zwischen 1 und 4.294.967.295 liegen (MOVE_FILE_DATA.ClusterCount ist 32 Bit).");
        if (targetLcn + clusterCount > Info.TotalClusters)
            throw new ArgumentOutOfRangeException(nameof(targetLcn), targetLcn,
                $"Der Zielbereich liegt außerhalb des Volumes ({Info.TotalClusters} Cluster).");

        string path = ToWin32Path(fullPath);

        // Primär mit Lesezugriff öffnen (bei verschlüsselten Dateien verlangt FSCTL_MOVE_FILE FILE_READ_DATA).
        SafeFileHandle file = CreateFile(path, GENERIC_READ | FILE_READ_ATTRIBUTES, ShareAll, IntPtr.Zero,
            OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int code = Marshal.GetLastWin32Error();
            file.Dispose();
            // Fallback: Ein reines Attribut-Handle unterliegt keiner Freigabeprüfung und funktioniert daher
            // auch bei Dateien, die ein anderer Prozess ohne FILE_SHARE_READ geöffnet hält.
            if (code is ERROR_SHARING_VIOLATION or ERROR_ACCESS_DENIED)
            {
                file = CreateFile(path, FILE_READ_ATTRIBUTES, ShareAll, IntPtr.Zero,
                    OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
                if (file.IsInvalid)
                {
                    code = Marshal.GetLastWin32Error();
                    file.Dispose();
                    throw CreateError($"Datei \"{fullPath}\" konnte zum Verschieben nicht geöffnet werden.", code);
                }
            }
            else
            {
                throw CreateError($"Datei \"{fullPath}\" konnte zum Verschieben nicht geöffnet werden.", code);
            }
        }

        using (file)
        {
            bool addedRef = false;
            try
            {
                file.DangerousAddRef(ref addedRef);
                var data = new MOVE_FILE_DATA
                {
                    FileHandle = file.DangerousGetHandle(),
                    StartingVcn = startVcn,
                    StartingLcn = targetLcn,
                    ClusterCount = (uint)clusterCount,
                };
                bool ok = DeviceIoControl(_volume, FSCTL_MOVE_FILE,
                    &data, (uint)sizeof(MOVE_FILE_DATA), null, 0, out _, IntPtr.Zero);
                if (!ok)
                {
                    int code = Marshal.GetLastWin32Error();
                    throw CreateError(
                        $"Verschieben von {clusterCount} Cluster(n) der Datei \"{fullPath}\" (VCN {startVcn}) nach LCN {targetLcn} fehlgeschlagen.",
                        code);
                }
            }
            finally
            {
                if (addedRef) file.DangerousRelease();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _volume.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Stellt bei langen Pfaden <c>\\?\</c> bzw. <c>\\?\UNC\</c> voran.</summary>
    internal static string ToWin32Path(string fullPath)
    {
        if (fullPath.Length <= LongPathThreshold) return fullPath;
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal) || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal))
            return fullPath;

        // \\?\ schaltet die Pfadnormalisierung ab – daher vorher selbst normalisieren (/, .., doppelte \).
        string normalized = Path.GetFullPath(fullPath);
        if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + normalized[2..];
        return @"\\?\" + normalized;
    }

    /// <summary>Erzeugt eine <see cref="VolumeAccessException"/> mit deutschem Text und der Systemmeldung zum Win32-Code.</summary>
    internal static VolumeAccessException CreateError(string message, int win32Error)
    {
        var inner = new Win32Exception(win32Error);
        return new VolumeAccessException($"{message} (Win32-Fehler {win32Error}: {inner.Message})", win32Error, inner);
    }
}
