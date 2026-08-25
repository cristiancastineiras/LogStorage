using System.Globalization;
using LogStorage.Format;

namespace LogStorage.Storage;

/// <summary>
/// Convenciones de rutas: <c>raiz/yyyy/MM/dd/segment-00001.lsz</c>.
/// La propia estructura de carpetas es el primer nivel del índice temporal:
/// para buscar en un rango de fechas basta con mirar las carpetas de ese rango.
/// </summary>
internal static class SegmentPaths
{
    private const string SegmentPrefix = "segment-";

    public static string DayDirectory(string root, DateOnly day)
        => Path.Combine(
            root,
            day.Year.ToString("D4", CultureInfo.InvariantCulture),
            day.Month.ToString("D2", CultureInfo.InvariantCulture),
            day.Day.ToString("D2", CultureInfo.InvariantCulture));

    public static string SegmentFile(string dayDirectory, int id)
        => Path.Combine(dayDirectory, $"{SegmentPrefix}{id.ToString("D5", CultureInfo.InvariantCulture)}{SegmentFormat.SegmentExtension}");

    public static DateOnly DayOf(long utcTicks) => DateOnly.FromDateTime(new DateTime(utcTicks, DateTimeKind.Utc));

    public static bool TryParseSegmentId(ReadOnlySpan<char> fileName, out int id)
    {
        id = 0;
        if (!fileName.StartsWith(SegmentPrefix, StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(SegmentFormat.SegmentExtension, StringComparison.OrdinalIgnoreCase)) return false;

        var digits = fileName[SegmentPrefix.Length..^SegmentFormat.SegmentExtension.Length];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    /// <summary>Siguiente id libre en una carpeta de día. Devuelve 1 si la carpeta está vacía.</summary>
    public static int NextSegmentId(string dayDirectory)
    {
        if (!Directory.Exists(dayDirectory)) return 1;

        var max = 0;
        foreach (var file in Directory.EnumerateFiles(dayDirectory, SegmentPrefix + "*" + SegmentFormat.SegmentExtension))
        {
            if (TryParseSegmentId(Path.GetFileName(file.AsSpan()), out var id) && id > max) max = id;
        }
        return max + 1;
    }

    /// <summary>Segmentos de una carpeta de día, ordenados por id.</summary>
    public static List<string> SegmentsIn(string dayDirectory)
    {
        var result = new List<string>();
        if (!Directory.Exists(dayDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(dayDirectory, SegmentPrefix + "*" + SegmentFormat.SegmentExtension))
        {
            result.Add(file);
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Días con datos dentro del rango, en orden ascendente. Recorre el árbol podando por
    /// nombre de carpeta, así que un rango de dos días no toca los otros cinco años de logs.
    /// </summary>
    public static List<(DateOnly Day, string Directory)> EnumerateDays(string root, DateOnly? from = null, DateOnly? to = null)
    {
        var days = new List<(DateOnly, string)>();
        if (!Directory.Exists(root)) return days;

        foreach (var yearPath in Directory.EnumerateDirectories(root))
        {
            if (!TryParseNumber(Path.GetFileName(yearPath.AsSpan()), 4, out var year)) continue;
            if (from is { } f1 && year < f1.Year) continue;
            if (to is { } t1 && year > t1.Year) continue;

            foreach (var monthPath in Directory.EnumerateDirectories(yearPath))
            {
                if (!TryParseNumber(Path.GetFileName(monthPath.AsSpan()), 2, out var month)) continue;

                foreach (var dayPath in Directory.EnumerateDirectories(monthPath))
                {
                    if (!TryParseNumber(Path.GetFileName(dayPath.AsSpan()), 2, out var dayNumber)) continue;
                    if (!IsValidDate(year, month, dayNumber)) continue;

                    var day = new DateOnly(year, month, dayNumber);
                    if (from is { } f2 && day < f2) continue;
                    if (to is { } t2 && day > t2) continue;

                    days.Add((day, dayPath));
                }
            }
        }

        days.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        return days;
    }

    private static bool IsValidDate(int year, int month, int day)
        => year is >= 1 and <= 9999
           && month is >= 1 and <= 12
           && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    private static bool TryParseNumber(ReadOnlySpan<char> text, int expectedLength, out int value)
    {
        value = 0;
        return text.Length == expectedLength && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
