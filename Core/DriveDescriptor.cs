namespace HddDefrag.Core;

/// <summary>Art des Datenträgers, ermittelt über die Seek-Penalty-Abfrage.</summary>
public enum MediaKind
{
    /// <summary>Konnte nicht ermittelt werden; wird aus Sicherheitsgründen wie SSD behandelt (gesperrt).</summary>
    Unknown,
    /// <summary>Rotierende Festplatte (Seek-Penalty vorhanden).</summary>
    Hdd,
    /// <summary>SSD oder NVMe (keine Seek-Penalty).</summary>
    Ssd,
}

/// <summary>Beschreibt ein Laufwerk mit Buchstaben, inklusive Sicherheitsbewertung.</summary>
public sealed record DriveDescriptor
{
    public required char Letter { get; init; }
    public required string VolumeLabel { get; init; }
    public required string FileSystem { get; init; }

    /// <summary>true bei DRIVE_FIXED (keine Wechselmedien, Netzlaufwerke, CD-ROM).</summary>
    public required bool IsFixed { get; init; }

    /// <summary>Nummer des physischen Datenträgers (-1 wenn unbekannt oder über mehrere Datenträger gespannt).</summary>
    public required int DiskNumber { get; init; }

    /// <summary>Modellbezeichnung des physischen Datenträgers, z. B. "ST8000DM004".</summary>
    public required string DiskModel { get; init; }

    /// <summary>Bus-Typ als Text, z. B. "SATA", "NVMe", "USB".</summary>
    public required string BusType { get; init; }

    public required MediaKind Media { get; init; }

    /// <summary>Rohwert der Abfrage StorageDeviceSeekPenaltyProperty; null, wenn die Abfrage fehlschlug.</summary>
    public required bool? HasSeekPenalty { get; init; }

    public required long TotalBytes { get; init; }
    public required long FreeBytes { get; init; }

    /// <summary>Nur feste NTFS-Laufwerke auf rotierenden Festplatten dürfen defragmentiert werden.</summary>
    public bool IsDefragAllowed => IsFixed && IsNtfs && Media == MediaKind.Hdd;

    public bool IsNtfs => string.Equals(FileSystem, "NTFS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Deutscher Grund, warum das Laufwerk gesperrt ist, oder leer wenn erlaubt.</summary>
    public string BlockReason
    {
        get
        {
            if (!IsFixed) return "Kein festes Laufwerk";
            if (!IsNtfs) return $"Dateisystem {FileSystem} wird nicht unterstützt (nur NTFS)";
            return Media switch
            {
                MediaKind.Ssd => "SSD/NVMe – wird nie defragmentiert",
                MediaKind.Unknown => "Medientyp unbekannt – aus Sicherheitsgründen gesperrt",
                _ => string.Empty,
            };
        }
    }

    public string MediaText => Media switch
    {
        MediaKind.Hdd => "Festplatte (HDD)",
        MediaKind.Ssd => "SSD/NVMe",
        _ => "Unbekannt",
    };

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{Letter}:"
        : $"{Letter}: ({VolumeLabel})";
}

/// <summary>Listet alle Laufwerke mit Buchstaben auf und bestimmt den Medientyp.</summary>
public interface IDriveEnumerator
{
    /// <summary>Liefert alle Laufwerke; darf nie werfen. Einzelne Fehler werden im Descriptor als Unknown abgebildet.</summary>
    IReadOnlyList<DriveDescriptor> Enumerate();
}
