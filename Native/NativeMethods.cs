using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HddDefrag.Native;

/// <summary>
/// P/Invoke-Deklarationen, Strukturen und Konstanten für den Low-Level-Zugriff auf Volumes und Datenträger.
/// Alle Strukturen sind blittable und entsprechen dem Layout aus winioctl.h (x64 und x86).
/// </summary>
internal static unsafe partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    // ------------------------------------------------------------------
    // Zugriffsrechte, Freigabemodi, Erstellungsarten, Flags
    // ------------------------------------------------------------------

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_READ_ATTRIBUTES = 0x0080;

    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint FILE_SHARE_DELETE = 0x00000004;

    public const uint OPEN_EXISTING = 3;

    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    public const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;

    // ------------------------------------------------------------------
    // Laufwerkstypen (GetDriveTypeW)
    // ------------------------------------------------------------------

    public const uint DRIVE_UNKNOWN = 0;
    public const uint DRIVE_NO_ROOT_DIR = 1;
    public const uint DRIVE_REMOVABLE = 2;
    public const uint DRIVE_FIXED = 3;
    public const uint DRIVE_REMOTE = 4;
    public const uint DRIVE_CDROM = 5;
    public const uint DRIVE_RAMDISK = 6;

    // ------------------------------------------------------------------
    // Fehlermodus (verhindert "Kein Datenträger"-Dialoge bei leeren Laufwerken)
    // ------------------------------------------------------------------

    public const uint SEM_FAILCRITICALERRORS = 0x0001;
    public const uint SEM_NOOPENFILEERRORBOX = 0x8000;

    // ------------------------------------------------------------------
    // Win32-Fehlercodes
    // ------------------------------------------------------------------

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_HANDLE = 6;
    public const int ERROR_NOT_READY = 21;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_MORE_DATA = 234;
    public const int ERROR_INVALID_FUNCTION = 1;

    // ------------------------------------------------------------------
    // IOCTL- und FSCTL-Codes (CTL_CODE aus winioctl.h)
    // ------------------------------------------------------------------

    /// <summary>IOCTL_STORAGE_QUERY_PROPERTY = CTL_CODE(IOCTL_STORAGE_BASE, 0x500, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    /// <summary>IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = CTL_CODE(IOCTL_VOLUME_BASE, 0, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

    /// <summary>FSCTL_GET_NTFS_VOLUME_DATA = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 25, METHOD_BUFFERED, FILE_ANY_ACCESS).</summary>
    public const uint FSCTL_GET_NTFS_VOLUME_DATA = 0x00090064;

    /// <summary>FSCTL_GET_VOLUME_BITMAP = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 27, METHOD_NEITHER, FILE_ANY_ACCESS).</summary>
    public const uint FSCTL_GET_VOLUME_BITMAP = 0x0009006F;

    /// <summary>FSCTL_GET_RETRIEVAL_POINTERS = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 28, METHOD_NEITHER, FILE_ANY_ACCESS).</summary>
    public const uint FSCTL_GET_RETRIEVAL_POINTERS = 0x00090073;

    /// <summary>FSCTL_MOVE_FILE = CTL_CODE(FILE_DEVICE_FILE_SYSTEM, 29, METHOD_BUFFERED, FILE_SPECIAL_ACCESS).</summary>
    public const uint FSCTL_MOVE_FILE = 0x00090074;

    // ------------------------------------------------------------------
    // STORAGE_PROPERTY_ID / STORAGE_QUERY_TYPE
    // ------------------------------------------------------------------

    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceSeekPenaltyProperty = 7;
    public const int PropertyStandardQuery = 0;

    /// <summary>Kennzeichnung eines virtuellen (sparse/komprimierten) Extents in RETRIEVAL_POINTERS_BUFFER.</summary>
    public const long VirtualLcn = -1; // 0xFFFFFFFFFFFFFFFF

    // ------------------------------------------------------------------
    // Funktionen
    // ------------------------------------------------------------------

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        void* lpInBuffer,
        uint nInBufferSize,
        void* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport(Kernel32, EntryPoint = "GetDriveTypeW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetDriveType(string lpRootPathName);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumeInformationW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetVolumeInformation(
        string lpRootPathName,
        char* lpVolumeNameBuffer,
        uint nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        char* lpFileSystemNameBuffer,
        uint nFileSystemNameSize);

    [LibraryImport(Kernel32, EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetDiskFreeSpaceEx(
        string lpDirectoryName,
        out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes,
        out ulong lpTotalNumberOfFreeBytes);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadErrorMode(uint dwNewMode, out uint lpOldMode);

    // ------------------------------------------------------------------
    // Strukturen
    // ------------------------------------------------------------------

    /// <summary>STORAGE_PROPERTY_QUERY (12 Byte).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_PROPERTY_QUERY
    {
        public int PropertyId;
        public int QueryType;
        public byte AdditionalParameters; // BYTE AdditionalParameters[1]
    }

    /// <summary>DEVICE_SEEK_PENALTY_DESCRIPTOR (12 Byte).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DEVICE_SEEK_PENALTY_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte IncursSeekPenalty; // BOOLEAN
    }

    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR (Kopfteil). Hinter der Struktur folgen die Zeichenketten,
    /// auf die die *Offset-Felder relativ zum Strukturanfang zeigen (0 = nicht vorhanden).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STORAGE_DEVICE_DESCRIPTOR
    {
        public uint Version;
        public uint Size;
        public byte DeviceType;
        public byte DeviceTypeModifier;
        public byte RemovableMedia;   // BOOLEAN
        public byte CommandQueueing;  // BOOLEAN
        public uint VendorIdOffset;
        public uint ProductIdOffset;
        public uint ProductRevisionOffset;
        public uint SerialNumberOffset;
        public STORAGE_BUS_TYPE BusType;
        public uint RawPropertiesLength;
        public byte RawDeviceProperties; // BYTE RawDeviceProperties[1]
    }

    /// <summary>STORAGE_BUS_TYPE aus winioctl.h.</summary>
    public enum STORAGE_BUS_TYPE : int
    {
        BusTypeUnknown = 0,
        BusTypeScsi = 1,
        BusTypeAtapi = 2,
        BusTypeAta = 3,
        BusType1394 = 4,
        BusTypeSsa = 5,
        BusTypeFibre = 6,
        BusTypeUsb = 7,
        BusTypeRAID = 8,
        BusTypeiScsi = 9,
        BusTypeSas = 10,
        BusTypeSata = 11,
        BusTypeSd = 12,
        BusTypeMmc = 13,
        BusTypeVirtual = 14,
        BusTypeFileBackedVirtual = 15,
        BusTypeSpaces = 16,
        BusTypeNvme = 17,
        BusTypeSCM = 18,
        BusTypeUfs = 19,
        BusTypeNvmeof = 20,
    }

    /// <summary>DISK_EXTENT (24 Byte, 8-Byte-Ausrichtung).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISK_EXTENT
    {
        public uint DiskNumber;
        public long StartingOffset;
        public long ExtentLength;
    }

    /// <summary>VOLUME_DISK_EXTENTS-Kopf; die Extents beginnen bei Offset 8.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VOLUME_DISK_EXTENTS
    {
        public uint NumberOfDiskExtents;
        public DISK_EXTENT FirstExtent; // DISK_EXTENT Extents[ANYSIZE_ARRAY]
    }

    /// <summary>NTFS_VOLUME_DATA_BUFFER (96 Byte).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct NTFS_VOLUME_DATA_BUFFER
    {
        public long VolumeSerialNumber;
        public long NumberSectors;
        public long TotalClusters;
        public long FreeClusters;
        public long TotalReserved;
        public uint BytesPerSector;
        public uint BytesPerCluster;
        public uint BytesPerFileRecordSegment;
        public uint ClustersPerFileRecordSegment;
        public long MftValidDataLength;
        public long MftStartLcn;
        public long Mft2StartLcn;
        public long MftZoneStart;
        public long MftZoneEnd;
    }

    /// <summary>STARTING_LCN_INPUT_BUFFER.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STARTING_LCN_INPUT_BUFFER
    {
        public long StartingLcn;
    }

    /// <summary>VOLUME_BITMAP_BUFFER-Kopf (16 Byte); danach folgen die Bitmap-Bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct VOLUME_BITMAP_BUFFER
    {
        public long StartingLcn;
        public long BitmapSize; // Anzahl Cluster ab StartingLcn bis zum Volume-Ende
        // BYTE Buffer[ANYSIZE_ARRAY]
    }

    /// <summary>STARTING_VCN_INPUT_BUFFER.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct STARTING_VCN_INPUT_BUFFER
    {
        public long StartingVcn;
    }

    /// <summary>Ein Eintrag in RETRIEVAL_POINTERS_BUFFER.Extents (16 Byte).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RETRIEVAL_POINTER_EXTENT
    {
        public long NextVcn;
        public long Lcn;
    }

    /// <summary>RETRIEVAL_POINTERS_BUFFER-Kopf (16 Byte); die Extents beginnen bei Offset 16.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RETRIEVAL_POINTERS_BUFFER
    {
        public uint ExtentCount;
        public long StartingVcn;
        // RETRIEVAL_POINTER_EXTENT Extents[ANYSIZE_ARRAY]
    }

    /// <summary>MOVE_FILE_DATA (32 Byte auf x64 und x86 dank 8-Byte-Ausrichtung der LARGE_INTEGER-Felder).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MOVE_FILE_DATA
    {
        public IntPtr FileHandle;
        public long StartingVcn;
        public long StartingLcn;
        public uint ClusterCount;
    }
}
