using System.Globalization;
using Parquet.Schema;

namespace LogStorage.Archiving;

/// <summary>
/// Esquema y convenciones de la capa de archivo histórico en Parquet.
/// </summary>
/// <remarks>
/// Los segmentos <c>.lsz</c> son el formato caliente (escritura rapidísima, búsqueda por
/// rango). Cuando un día ya no cambia se convierte a un único Parquet: ocupa parecido,
/// pero lo abre cualquier herramienta del mundo (DuckDB, pandas, Spark, Power BI) sin
/// necesitar esta librería.
/// </remarks>
internal static class ParquetArchive
{
    public const string Extension = ".parquet";

    public static readonly DateTimeDataField Timestamp =
        new("timestamp", DateTimeFormat.DateAndTime, isAdjustedToUTC: true, unit: DateTimeTimeUnit.Micros, isNullable: false);

    public static readonly DataField<string> Level = new("level");
    public static readonly DataField<string> Service = new("service");
    public static readonly DataField<string> Host = new("host");
    public static readonly DataField<string> Message = new("message");
    public static readonly DataField<string> TraceId = new("trace_id");
    public static readonly DataField<byte[]> Payload = new("payload");

    public static readonly ParquetSchema Schema = new(Timestamp, Level, Service, Host, Message, TraceId, Payload);

    /// <summary>Ruta del Parquet de un día: <c>archive/yyyy/MM/logs-yyyy-MM-dd.parquet</c>.</summary>
    public static string FileFor(string archiveRoot, DateOnly day) => Path.Combine(
        archiveRoot,
        day.Year.ToString("D4", CultureInfo.InvariantCulture),
        day.Month.ToString("D2", CultureInfo.InvariantCulture),
        FileName(day));

    public static string FileName(DateOnly day)
        => $"logs-{day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}{Extension}";

    public static bool TryParseDay(ReadOnlySpan<char> fileName, out DateOnly day)
    {
        day = default;
        if (!fileName.StartsWith("logs-", StringComparison.Ordinal)) return false;
        if (!fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;

        var text = fileName[5..^Extension.Length];
        return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    /// <summary>Días archivados dentro del rango, ordenados de forma ascendente.</summary>
    public static List<(DateOnly Day, string File)> EnumerateDays(string archiveRoot, DateOnly? from = null, DateOnly? to = null)
    {
        var result = new List<(DateOnly, string)>();
        if (!Directory.Exists(archiveRoot)) return result;

        foreach (var file in Directory.EnumerateFiles(archiveRoot, "logs-*" + Extension, SearchOption.AllDirectories))
        {
            if (!TryParseDay(Path.GetFileName(file.AsSpan()), out var day)) continue;
            if (from is { } f && day < f) continue;
            if (to is { } t && day > t) continue;
            result.Add((day, file));
        }

        result.Sort(static (a, b) => a.Item1.CompareTo(b.Item1));
        return result;
    }
}
