using System.Globalization;

namespace HddDefrag.UI;

/// <summary>Deutsche Zahlen- und Größenformatierung für die Oberfläche.</summary>
internal static class UiFormat
{
    public static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

    public static string Number(long value) => value.ToString("N0", De);

    public static string Percent(double value) => value.ToString("0.0", De) + " %";

    /// <summary>Größe in GB bzw. MB (1024er-Schritte), kleinere Werte in KB oder Byte.</summary>
    public static string Bytes(long bytes)
    {
        if (bytes < 0) bytes = 0;
        const double Kb = 1024.0, Mb = Kb * 1024, Gb = Mb * 1024;
        if (bytes >= Gb) return (bytes / Gb).ToString("N2", De) + " GB";
        if (bytes >= Mb) return (bytes / Mb).ToString("N1", De) + " MB";
        if (bytes >= Kb) return (bytes / Kb).ToString("N1", De) + " KB";
        return bytes.ToString("N0", De) + " Byte";
    }
}
