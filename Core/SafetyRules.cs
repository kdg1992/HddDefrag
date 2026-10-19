using System.IO;

namespace HddDefrag.Core;

/// <summary>Zentrale Sicherheitsregeln: welche Dateien und Verzeichnisse nie angefasst werden dürfen.</summary>
public static class SafetyRules
{
    private static readonly HashSet<string> ForbiddenRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "bootmgr", "BOOTNXT",
    };

    private static readonly HashSet<string> ForbiddenRootDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information", "$RECYCLE.BIN", "Recovery", "Boot", "EFI", "Config.Msi",
    };

    /// <summary>true, wenn der voll qualifizierte Pfad (z. B. "G:\pagefile.sys") nie verschoben werden darf.</summary>
    public static bool IsProtected(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var relative = fullPath.Length > root.Length ? fullPath[root.Length..] : string.Empty;
        if (relative.Length == 0) return true;

        // NTFS-Metadateien liegen mit $-Präfix im Wurzelverzeichnis ($MFT, $LogFile, $Bitmap, ...)
        var firstSegment = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        if (firstSegment.StartsWith('$')) return true;
        if (ForbiddenRootDirectories.Contains(firstSegment)) return true;

        bool isRootLevel = !relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar);
        if (isRootLevel && ForbiddenRootFiles.Contains(relative)) return true;
        return false;
    }

    /// <summary>true, wenn das Verzeichnis beim Scan komplett übersprungen wird.</summary>
    public static bool IsProtectedDirectory(string fullPath) => IsProtected(fullPath);
}
