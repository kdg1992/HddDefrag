using System.Text;
using HddDefrag.Core;
using Microsoft.Win32.SafeHandles;
using static HddDefrag.Native.NativeMethods;

namespace HddDefrag.Native;

/// <summary>
/// Listet alle Laufwerke A–Z auf und bestimmt pro Laufwerk Dateisystem, Größe, physischen Datenträger,
/// Modell, Bus-Typ und Medientyp (über die Seek-Penalty-Abfrage). Alle Handles werden mit Zugriff 0
/// (nur Metadaten) geöffnet, damit die Abfrage auch ohne Administratorrechte funktioniert.
/// </summary>
public sealed unsafe class DriveEnumerator : IDriveEnumerator
{
    public IReadOnlyList<DriveDescriptor> Enumerate()
    {
        var result = new List<DriveDescriptor>();

        // Verhindert Systemdialoge wie "Kein Datenträger im Laufwerk" bei leeren CD-/Kartenlaufwerken.
        bool modeSet = false;
        uint oldMode = 0;
        try
        {
            modeSet = SetThreadErrorMode(SEM_FAILCRITICALERRORS | SEM_NOOPENFILEERRORBOX, out oldMode);
        }
        catch
        {
            // Ohne geänderten Fehlermodus weitermachen.
        }

        try
        {
            for (char letter = 'A'; letter <= 'Z'; letter++)
            {
                try
                {
                    DriveDescriptor? drive = QueryDrive(letter);
                    if (drive is not null) result.Add(drive);
                }
                catch
                {
                    // Enumerate() darf nie werfen – ein fehlerhaftes Laufwerk wird übersprungen
                    // bzw. (falls schon bekannt) mit Minimalangaben aufgenommen.
                    try
                    {
                        uint type = GetDriveType($"{letter}:\\");
                        if (type != DRIVE_NO_ROOT_DIR) result.Add(CreateFallback(letter, type == DRIVE_FIXED));
                    }
                    catch
                    {
                        // Laufwerk endgültig ignorieren.
                    }
                }
            }
        }
        finally
        {
            if (modeSet)
            {
                try { SetThreadErrorMode(oldMode, out _); } catch { /* ignorieren */ }
            }
        }

        return result;
    }

    /// <summary>Ermittelt alle Angaben zu einem Laufwerksbuchstaben; null, wenn der Buchstabe nicht vergeben ist.</summary>
    private static DriveDescriptor? QueryDrive(char letter)
    {
        string root = $"{letter}:\\";
        uint driveType = GetDriveType(root);
        if (driveType == DRIVE_NO_ROOT_DIR) return null; // Buchstabe nicht vorhanden

        bool isFixed = driveType == DRIVE_FIXED;

        // Dateisystem und Bezeichnung (schlägt z. B. bei leerem CD-Laufwerk fehl → leere Werte).
        string label = string.Empty;
        string fileSystem = string.Empty;
        const int bufLen = 261; // MAX_PATH + 1
        char* labelBuf = stackalloc char[bufLen];
        char* fsBuf = stackalloc char[bufLen];
        if (GetVolumeInformation(root, labelBuf, bufLen, out _, out _, out _, fsBuf, bufLen))
        {
            label = new string(labelBuf);
            fileSystem = new string(fsBuf);
        }

        // Größe und freier Platz.
        long totalBytes = 0, freeBytes = 0;
        if (GetDiskFreeSpaceEx(root, out _, out ulong total, out ulong free))
        {
            totalBytes = (long)Math.Min(total, long.MaxValue);
            freeBytes = (long)Math.Min(free, long.MaxValue);
        }

        int diskNumber = -1;
        string model = string.Empty;
        string busType = string.Empty;
        bool? seekPenalty = null;

        // Netzlaufwerke haben kein lokales Volume-Gerät – Hardwareabfragen nur für lokale Laufwerke.
        if (driveType != DRIVE_REMOTE)
        {
            using SafeFileHandle volume = OpenMetadataHandle($@"\\.\{letter}:");
            if (!volume.IsInvalid)
            {
                diskNumber = QueryDiskNumber(volume);

                SafeFileHandle? physical = null;
                try
                {
                    if (diskNumber >= 0)
                    {
                        physical = OpenMetadataHandle($@"\\.\PhysicalDrive{diskNumber}");
                        if (physical.IsInvalid)
                        {
                            physical.Dispose();
                            physical = null;
                        }
                    }

                    // Seek-Penalty: zuerst auf dem physischen Datenträger, Fallback auf dem Volume-Handle.
                    if (physical is not null) seekPenalty = QuerySeekPenalty(physical);
                    seekPenalty ??= QuerySeekPenalty(volume);

                    // Modell und Bus-Typ: ebenfalls zuerst physisch, dann Volume.
                    (string Model, string Bus)? device = null;
                    if (physical is not null) device = QueryDeviceDescriptor(physical);
                    device ??= QueryDeviceDescriptor(volume);
                    if (device is { } d)
                    {
                        model = d.Model;
                        busType = d.Bus;
                    }
                }
                finally
                {
                    physical?.Dispose();
                }
            }
        }

        MediaKind media = seekPenalty switch
        {
            true => MediaKind.Hdd,
            false => MediaKind.Ssd,
            null => MediaKind.Unknown,
        };

        return new DriveDescriptor
        {
            Letter = letter,
            VolumeLabel = label,
            FileSystem = fileSystem,
            IsFixed = isFixed,
            DiskNumber = diskNumber,
            DiskModel = model,
            BusType = busType,
            Media = media,
            HasSeekPenalty = seekPenalty,
            TotalBytes = totalBytes,
            FreeBytes = freeBytes,
        };
    }

    private static DriveDescriptor CreateFallback(char letter, bool isFixed) => new()
    {
        Letter = letter,
        VolumeLabel = string.Empty,
        FileSystem = string.Empty,
        IsFixed = isFixed,
        DiskNumber = -1,
        DiskModel = string.Empty,
        BusType = string.Empty,
        Media = MediaKind.Unknown,
        HasSeekPenalty = null,
        TotalBytes = 0,
        FreeBytes = 0,
    };

    /// <summary>Öffnet ein Gerät nur für Metadatenabfragen (Zugriff 0, keine Adminrechte nötig).</summary>
    private static SafeFileHandle OpenMetadataHandle(string devicePath) =>
        CreateFile(devicePath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>
    /// Liefert die Nummer des physischen Datenträgers über IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS.
    /// -1, wenn die Abfrage fehlschlägt oder das Volume über mehrere Datenträger gespannt ist.
    /// </summary>
    private static int QueryDiskNumber(SafeFileHandle volume)
    {
        // Platz für mehrere Extents, damit gespannte Volumes sauber erkannt werden.
        const int bufferSize = 8 + 24 * 16;
        byte* buffer = stackalloc byte[bufferSize];
        bool ok = DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, null, 0,
            buffer, bufferSize, out uint returned, IntPtr.Zero);
        if (!ok)
        {
            // ERROR_MORE_DATA bedeutet: noch mehr Extents als Platz → in jedem Fall mehrere Datenträger.
            return -1;
        }
        if (returned < (uint)sizeof(VOLUME_DISK_EXTENTS)) return -1;

        var extents = (VOLUME_DISK_EXTENTS*)buffer;
        if (extents->NumberOfDiskExtents != 1) return -1;
        uint disk = extents->FirstExtent.DiskNumber;
        return disk <= int.MaxValue ? (int)disk : -1;
    }

    /// <summary>Fragt StorageDeviceSeekPenaltyProperty ab; null bei Fehlschlag.</summary>
    private static bool? QuerySeekPenalty(SafeFileHandle device)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceSeekPenaltyProperty,
            QueryType = PropertyStandardQuery,
        };
        DEVICE_SEEK_PENALTY_DESCRIPTOR desc = default;
        bool ok = DeviceIoControl(device, IOCTL_STORAGE_QUERY_PROPERTY,
            &query, (uint)sizeof(STORAGE_PROPERTY_QUERY),
            &desc, (uint)sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR),
            out uint returned, IntPtr.Zero);
        if (!ok || returned < (uint)sizeof(DEVICE_SEEK_PENALTY_DESCRIPTOR)) return null;
        return desc.IncursSeekPenalty != 0;
    }

    /// <summary>Fragt StorageDeviceProperty ab und liefert Modell (VendorId + ProductId) und Bus-Typ als Text.</summary>
    private static (string Model, string Bus)? QueryDeviceDescriptor(SafeFileHandle device)
    {
        var query = new STORAGE_PROPERTY_QUERY
        {
            PropertyId = StorageDeviceProperty,
            QueryType = PropertyStandardQuery,
        };
        const int bufferSize = 4096;
        byte* buffer = stackalloc byte[bufferSize];
        new Span<byte>(buffer, bufferSize).Clear();

        bool ok = DeviceIoControl(device, IOCTL_STORAGE_QUERY_PROPERTY,
            &query, (uint)sizeof(STORAGE_PROPERTY_QUERY),
            buffer, bufferSize, out uint returned, IntPtr.Zero);
        // Mindestens bis einschließlich BusType (Offset 28, 4 Byte) muss die Struktur gefüllt sein.
        const uint minimumBytes = 32;
        if (!ok || returned < minimumBytes) return null;

        var desc = (STORAGE_DEVICE_DESCRIPTOR*)buffer;
        int limit = (int)Math.Min(returned, (uint)bufferSize);

        string vendor = ReadAnsiString(buffer, limit, desc->VendorIdOffset);
        string product = ReadAnsiString(buffer, limit, desc->ProductIdOffset);

        string model;
        if (vendor.Length == 0)
            model = product;
        else if (product.Length == 0)
            model = vendor;
        else if (product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase))
            model = product; // Hersteller steht bereits in der Produktbezeichnung
        else
            model = $"{vendor} {product}";

        return (model, BusTypeToText(desc->BusType));
    }

    /// <summary>Liest eine nullterminierte ASCII-Zeichenkette ab Offset; leer bei Offset 0 oder außerhalb des Puffers.</summary>
    private static string ReadAnsiString(byte* buffer, int length, uint offset)
    {
        if (offset == 0 || offset >= (uint)length) return string.Empty;
        var span = new ReadOnlySpan<byte>(buffer + offset, length - (int)offset);
        int end = span.IndexOf((byte)0);
        if (end >= 0) span = span[..end];
        return Encoding.ASCII.GetString(span).Trim();
    }

    private static string BusTypeToText(STORAGE_BUS_TYPE bus) => bus switch
    {
        STORAGE_BUS_TYPE.BusTypeScsi => "SCSI",
        STORAGE_BUS_TYPE.BusTypeAtapi => "ATAPI",
        STORAGE_BUS_TYPE.BusTypeAta => "ATA",
        STORAGE_BUS_TYPE.BusType1394 => "1394",
        STORAGE_BUS_TYPE.BusTypeSsa => "SSA",
        STORAGE_BUS_TYPE.BusTypeFibre => "Fibre Channel",
        STORAGE_BUS_TYPE.BusTypeUsb => "USB",
        STORAGE_BUS_TYPE.BusTypeRAID => "RAID",
        STORAGE_BUS_TYPE.BusTypeiScsi => "iSCSI",
        STORAGE_BUS_TYPE.BusTypeSas => "SAS",
        STORAGE_BUS_TYPE.BusTypeSata => "SATA",
        STORAGE_BUS_TYPE.BusTypeSd => "SD",
        STORAGE_BUS_TYPE.BusTypeMmc => "MMC",
        STORAGE_BUS_TYPE.BusTypeVirtual => "Virtuell",
        STORAGE_BUS_TYPE.BusTypeFileBackedVirtual => "Virtuell (Datei)",
        STORAGE_BUS_TYPE.BusTypeSpaces => "Speicherplätze",
        STORAGE_BUS_TYPE.BusTypeNvme => "NVMe",
        STORAGE_BUS_TYPE.BusTypeSCM => "SCM",
        STORAGE_BUS_TYPE.BusTypeUfs => "UFS",
        STORAGE_BUS_TYPE.BusTypeNvmeof => "NVMe-oF",
        STORAGE_BUS_TYPE.BusTypeUnknown => "Unbekannt",
        _ => $"Unbekannt ({(int)bus})",
    };
}
