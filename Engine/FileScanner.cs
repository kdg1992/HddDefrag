using System.IO;
using System.IO.Enumeration;
using HddDefrag.Core;

namespace HddDefrag.Engine;

/// <summary>Fortschritt des Verzeichnislaufs.</summary>
public sealed record ScanProgress(long FilesProcessed, string CurrentDirectory);

/// <summary>Ergebnis des Verzeichnislaufs. Es werden nur fragmentierte Dateien mit ihren Extents im Speicher gehalten.</summary>
public sealed record ScanResult(
    long FilesScanned,
    long FilesFragmented,
    long FragmentsTotal,
    long ClustersInFragmentedFiles,
    long ErrorCount,
    long DirectoriesSkipped,
    IReadOnlyList<FileFragmentInfo> FragmentedFiles,
    IReadOnlyList<Extent> UnmovableExtents);

/// <summary>
/// Läuft rekursiv durch ein Laufwerk und ermittelt für jede Datei die Extents.
/// Die Dateiaufzählung ist über eine Funktion austauschbar, damit der Scanner ohne echtes Dateisystem getestet werden kann.
/// </summary>
public sealed class FileScanner
{
    /// <summary>Nach so vielen verarbeiteten Dateien wird der Fortschritt gemeldet.</summary>
    public const int ProgressInterval = 200;

    /// <summary>Höchstzahl der einzeln protokollierten Dateifehler (danach nur noch gezählt).</summary>
    private const int MaxLoggedErrors = 20;

    private readonly IVolumeAccess _volume;
    private readonly Func<string, IEnumerable<string>> _enumerateEntries;
    private readonly Func<string, long> _getFileSize;

    /// <param name="volume">Zugriff auf das Volume (für <see cref="IVolumeAccess.GetExtents"/>).</param>
    /// <param name="enumerateEntries">
    /// Liefert zu einem Verzeichnispfad die direkten Einträge als volle Pfade; Unterverzeichnisse enden mit einem Verzeichnistrennzeichen.
    /// Standard: echtes Dateisystem ohne Reparse-Points und ohne Unzugängliches.
    /// </param>
    /// <param name="getFileSize">Liefert die Dateigröße in Bytes (wird nur für fragmentierte Dateien benötigt). Standard: <see cref="FileInfo.Length"/>.</param>
    public FileScanner(IVolumeAccess volume,
        Func<string, IEnumerable<string>>? enumerateEntries = null,
        Func<string, long>? getFileSize = null)
    {
        _volume = volume ?? throw new ArgumentNullException(nameof(volume));
        _enumerateEntries = enumerateEntries ?? EnumerateFileSystem;
        _getFileSize = getFileSize ?? (path => new FileInfo(path).Length);
    }

    /// <summary>Standard-Aufzählung über das echte Dateisystem (Verzeichnisse enden mit Trennzeichen).</summary>
    public static IEnumerable<string> EnumerateFileSystem(string directory)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };
        return new FileSystemEnumerable<string>(directory, static (ref FileSystemEntry entry) =>
            entry.IsDirectory
                ? entry.ToFullPath() + Path.DirectorySeparatorChar
                : entry.ToFullPath(), options);
    }

    /// <summary>
    /// Anzahl der Fragmente: Extents, die direkt aneinander anschließen, zählen als eines; virtuelle Extents (sparse/komprimiert) werden ignoriert.
    /// </summary>
    public static int CountFragments(IReadOnlyList<Extent> extents)
    {
        int fragments = 0;
        long expectedNextLcn = -1;
        foreach (var e in extents.Where(x => !x.IsVirtual && x.ClusterCount > 0).OrderBy(x => x.Vcn))
        {
            if (fragments == 0 || e.Lcn != expectedNextLcn) fragments++;
            expectedNextLcn = e.Lcn + e.ClusterCount;
        }
        return fragments;
    }

    /// <summary>Summe der Cluster aller nicht-virtuellen Extents.</summary>
    public static long CountClusters(IReadOnlyList<Extent> extents)
    {
        long sum = 0;
        foreach (var e in extents)
            if (!e.IsVirtual) sum += e.ClusterCount;
        return sum;
    }

    /// <summary>
    /// Durchläuft <paramref name="rootPath"/> (z. B. "G:\") vollständig. Geschützte Verzeichnisse laut <see cref="SafetyRules"/> werden übersprungen,
    /// Fehler einzelner Dateien werden gezählt und führen nicht zum Abbruch.
    /// </summary>
    /// <exception cref="OperationCanceledException">Bei Abbruch über <paramref name="ct"/>.</exception>
    public ScanResult Scan(string rootPath, CancellationToken ct, PauseController? pause = null,
        Action<ScanProgress>? progress = null, Action<string>? log = null)
    {
        long filesScanned = 0, filesFragmented = 0, fragmentsTotal = 0, clustersInFragmented = 0;
        long errors = 0, dirsSkipped = 0;
        var fragmented = new List<FileFragmentInfo>();
        var unmovable = new List<Extent>();

        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            pause?.WaitIfPaused(ct);
            string directory = stack.Pop();

            var entries = new List<string>();
            try
            {
                foreach (var entry in _enumerateEntries(directory))
                    entries.Add(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors++;
                if (errors <= MaxLoggedErrors) log?.Invoke($"Verzeichnis nicht lesbar: {directory} ({ex.Message})");
            }

            foreach (var entry in entries)
            {
                if (IsDirectoryEntry(entry))
                {
                    string dirPath = entry.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (dirPath.Length <= (Path.GetPathRoot(dirPath)?.Length ?? 0)) continue; // Wurzel nicht erneut betreten
                    if (SafetyRules.IsProtectedDirectory(dirPath))
                    {
                        dirsSkipped++;
                        continue;
                    }
                    stack.Push(dirPath);
                    continue;
                }

                ct.ThrowIfCancellationRequested();
                if ((filesScanned & 63) == 0) pause?.WaitIfPaused(ct);

                filesScanned++;
                IReadOnlyList<Extent> extents;
                try
                {
                    extents = _volume.GetExtents(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors++;
                    if (errors <= MaxLoggedErrors) log?.Invoke($"Extents nicht lesbar: {entry} ({ex.Message})");
                    ReportProgress(filesScanned, directory, progress);
                    continue;
                }

                int fragmentCount = CountFragments(extents);
                if (fragmentCount > 0)
                {
                    long clusters = CountClusters(extents);
                    bool isProtected = SafetyRules.IsProtected(entry);
                    if (isProtected)
                        foreach (var e in extents)
                            if (!e.IsVirtual) unmovable.Add(e);

                    if (fragmentCount > 1)
                    {
                        filesFragmented++;
                        fragmentsTotal += fragmentCount;
                        clustersInFragmented += clusters;
                        fragmented.Add(new FileFragmentInfo(entry, SafeFileSize(entry, clusters), clusters, fragmentCount, extents));
                    }
                }

                ReportProgress(filesScanned, directory, progress);
            }
        }

        if (errors > MaxLoggedErrors)
            log?.Invoke($"… und {errors - MaxLoggedErrors:N0} weitere Lesefehler (nicht einzeln protokolliert).");

        return new ScanResult(filesScanned, filesFragmented, fragmentsTotal, clustersInFragmented, errors, dirsSkipped, fragmented, unmovable);
    }

    private static void ReportProgress(long filesScanned, string directory, Action<ScanProgress>? progress)
    {
        if (progress is not null && filesScanned % ProgressInterval == 0)
            progress(new ScanProgress(filesScanned, directory));
    }

    private static bool IsDirectoryEntry(string entry) =>
        entry.Length > 0 && (entry[^1] == Path.DirectorySeparatorChar || entry[^1] == Path.AltDirectorySeparatorChar);

    private long SafeFileSize(string path, long clusters)
    {
        try
        {
            return _getFileSize(path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Größe nicht lesbar: grob aus der Clusteranzahl abschätzen
            return clusters * Math.Max(1, _volume.Info.BytesPerCluster);
        }
    }
}
