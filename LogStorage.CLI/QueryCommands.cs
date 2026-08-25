using System.Globalization;
using System.Text;
using System.Text.Json;
using Cysharp.Text;

namespace LogStorage.Cli;

/// <summary>Comandos de consulta: buscar, seguir en vivo, exportar y estadísticas.</summary>
public sealed class QueryCommands
{
    /// <summary>Busca registros en el almacén y los pinta por pantalla.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="from">-f, Desde: fecha ISO, 'today', 'yesterday' o relativo como '-2h'.</param>
    /// <param name="to">-t, Hasta (exclusivo), mismo formato que --from.</param>
    /// <param name="contains">-c, Texto que debe aparecer en el mensaje.</param>
    /// <param name="level">-l, Nivel mínimo: trace, debug, info, warn, error, fatal.</param>
    /// <param name="exactLevel">Nivel exacto en vez de mínimo.</param>
    /// <param name="service">-s, Servicio exacto.</param>
    /// <param name="host">Máquina exacta.</param>
    /// <param name="trace">Identificador de traza exacto.</param>
    /// <param name="limit">-n, Máximo de resultados.</param>
    /// <param name="desc">Del más reciente al más antiguo.</param>
    /// <param name="caseSensitive">Distinguir mayúsculas en --contains.</param>
    /// <param name="showHost">Mostrar la máquina en cada línea.</param>
    /// <param name="json">Sacar los resultados como NDJSON en vez de texto coloreado.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Search(
        string root = "logs",
        string? from = null,
        string? to = null,
        string? contains = null,
        string? level = null,
        string? exactLevel = null,
        string? service = null,
        string? host = null,
        string? trace = null,
        int limit = 100,
        bool desc = false,
        bool caseSensitive = false,
        bool showHost = false,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(from, to, contains, level, exactLevel, service, host, trace, limit, desc, caseSensitive);

        await using var store = LogStore.OpenReadOnly(root);

        var started = TimeProvider.System.GetTimestamp();
        var count = 0;

        await foreach (var record in store.QueryAsync(query, cancellationToken))
        {
            Console.WriteLine(json ? ToJson(record) : Output.Record(record, showHost));
            count++;
        }

        var elapsed = TimeProvider.System.GetElapsedTime(started);
        if (!json)
        {
            Console.Error.WriteLine(Output.Dim($"-- {Output.Number(count)} registros en {elapsed.TotalMilliseconds:N0} ms"));
            if (count >= limit) Console.Error.WriteLine(Output.Warn($"-- se ha alcanzado el límite de {limit}; sube --limit para ver más"));
        }
    }

    /// <summary>Muestra los últimos registros, del más reciente al más antiguo.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="lines">-n, Cuántos registros mostrar.</param>
    /// <param name="level">-l, Nivel mínimo.</param>
    /// <param name="service">-s, Servicio exacto.</param>
    /// <param name="contains">-c, Texto que debe aparecer en el mensaje.</param>
    /// <param name="follow">-F, Quedarse esperando registros nuevos.</param>
    /// <param name="intervalMs">Cada cuánto se comprueba si hay novedades, en milisegundos.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Tail(
        string root = "logs",
        int lines = 20,
        string? level = null,
        string? service = null,
        string? contains = null,
        bool follow = false,
        int intervalMs = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var store = LogStore.OpenReadOnly(root);

        var minLevel = ParseLevel(level, nameof(level));
        var baseQuery = new LogQuery
        {
            MinLevel = minLevel,
            Service = service,
            Contains = contains,
        };

        var initial = new List<LogRecord>(lines);
        await foreach (var record in store.QueryAsync(baseQuery with { Descending = true, Limit = lines }, cancellationToken))
        {
            initial.Add(record);
        }
        initial.Reverse();

        var lastTicks = long.MinValue;
        foreach (var record in initial)
        {
            Console.WriteLine(Output.Record(record, showHost: false));
            lastTicks = record.TimestampTicks;
        }

        if (!follow) return;

        if (lastTicks == long.MinValue) lastTicks = DateTimeOffset.UtcNow.UtcTicks;

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(Math.Max(100, intervalMs)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var query = baseQuery with { From = new DateTimeOffset(lastTicks + 1, TimeSpan.Zero) };
            await foreach (var record in store.QueryAsync(query, cancellationToken))
            {
                Console.WriteLine(Output.Record(record, showHost: false));
                lastTicks = Math.Max(lastTicks, record.TimestampTicks);
            }
        }
    }

    /// <summary>Exporta el resultado de una búsqueda a un fichero.</summary>
    /// <param name="output">-o, Fichero de salida.</param>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="format">Formato: ndjson, csv o json.</param>
    /// <param name="from">-f, Desde.</param>
    /// <param name="to">-t, Hasta (exclusivo).</param>
    /// <param name="contains">-c, Texto que debe aparecer en el mensaje.</param>
    /// <param name="level">-l, Nivel mínimo.</param>
    /// <param name="service">-s, Servicio exacto.</param>
    /// <param name="host">Máquina exacta.</param>
    /// <param name="trace">Identificador de traza exacto.</param>
    /// <param name="limit">-n, Máximo de registros a exportar.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Export(
        string output,
        string root = "logs",
        string format = "ndjson",
        string? from = null,
        string? to = null,
        string? contains = null,
        string? level = null,
        string? service = null,
        string? host = null,
        string? trace = null,
        int limit = int.MaxValue,
        CancellationToken cancellationToken = default)
    {
        var kind = format.ToLowerInvariant();
        if (kind is not ("ndjson" or "csv" or "json"))
        {
            throw new ArgumentException($"Formato '{format}' no soportado. Usa ndjson, csv o json.");
        }

        var query = BuildQuery(from, to, contains, level, null, service, host, trace, limit, false, false);

        await using var store = LogStore.OpenReadOnly(root);

        var directory = Path.GetDirectoryName(Path.GetFullPath(output));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var started = TimeProvider.System.GetTimestamp();
        long count = 0;

        await using (var writer = new StreamWriter(output, append: false, new UTF8Encoding(false), 1 << 20))
        {
            if (kind == "csv") await writer.WriteLineAsync("timestamp,level,service,host,trace_id,message");
            if (kind == "json") await writer.WriteAsync("[");

            await foreach (var record in store.QueryAsync(query, cancellationToken))
            {
                switch (kind)
                {
                    case "csv":
                        await writer.WriteLineAsync(ToCsv(record));
                        break;
                    case "json":
                        if (count > 0) await writer.WriteAsync(",");
                        await writer.WriteAsync('\n' + ToJson(record));
                        break;
                    default:
                        await writer.WriteLineAsync(ToJson(record));
                        break;
                }
                count++;
            }

            if (kind == "json") await writer.WriteAsync("\n]");
        }

        var elapsed = TimeProvider.System.GetElapsedTime(started);
        var size = new FileInfo(output).Length;
        Console.WriteLine(Output.Ok($"Exportados {Output.Number(count)} registros a {output} ({Output.Bytes(size)}) en {elapsed.TotalMilliseconds:N0} ms."));
    }

    /// <summary>Resume qué hay en el almacén: días, registros, tamaño y ratio de compresión.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="refresh">Recalcular el manifiesto antes de mostrarlo.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Stats(string root = "logs", bool refresh = false, CancellationToken cancellationToken = default)
    {
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });
        var manifest = refresh
            ? await maintenance.ReindexAsync(cancellationToken)
            : await maintenance.GetManifestAsync(cancellationToken: cancellationToken);

        if (manifest.Days.Count == 0)
        {
            Console.WriteLine(Output.Warn($"No hay datos en '{Path.GetFullPath(root)}'."));
            return;
        }

        var segmentDays = 0;
        var archivedDays = 0;
        long segmentBytes = 0;
        long archiveBytes = 0;
        foreach (var day in manifest.Days)
        {
            if (day.SegmentCount > 0) segmentDays++;
            if (day.Archived) archivedDays++;
            segmentBytes += day.SegmentBytes;
            archiveBytes += day.ArchiveBytes;
        }

        var first = manifest.Days[0];
        var last = manifest.Days[^1];

        Console.WriteLine(Output.Title($"Almacén {Path.GetFullPath(root)}"));
        Console.WriteLine($"  Rango           {first.Date} .. {last.Date}  ({manifest.Days.Count} días con datos)");
        Console.WriteLine($"  Registros       {Output.Number(manifest.TotalRecords)}");
        Console.WriteLine($"  En disco        {Output.Bytes(manifest.TotalBytes)}  (segmentos {Output.Bytes(segmentBytes)}, archivo {Output.Bytes(archiveBytes)})");
        if (manifest.TotalRecords > 0)
        {
            Console.WriteLine($"  Por registro    {(double)manifest.TotalBytes / manifest.TotalRecords:F2} bytes");
        }
        Console.WriteLine($"  Días calientes  {segmentDays} en segmentos, {archivedDays} archivados en Parquet");
        Console.WriteLine(Output.Dim($"  Manifiesto      {new DateTime(manifest.UpdatedTicks, DateTimeKind.Utc):yyyy-MM-dd HH:mm:ss} UTC"));
    }

    internal static LogQuery BuildQuery(
        string? from,
        string? to,
        string? contains,
        string? level,
        string? exactLevel,
        string? service,
        string? host,
        string? trace,
        int limit,
        bool descending,
        bool caseSensitive) => new()
        {
            From = TimeArg.Parse(from, "from"),
            To = TimeArg.Parse(to, "to"),
            Contains = contains,
            MinLevel = ParseLevel(level, "level"),
            Level = ParseLevel(exactLevel, "exact-level"),
            Service = service,
            Host = host,
            TraceId = trace,
            Limit = limit <= 0 ? int.MaxValue : limit,
            Descending = descending,
            CaseSensitive = caseSensitive,
        };

    internal static LogLevel? ParseLevel(string? text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (LogLevels.TryParse(text, out var level)) return level;

        throw new FormatException($"Nivel '{text}' no reconocido en --{parameterName}. Usa trace, debug, info, warn, error o fatal.");
    }

    private static string ToJson(in LogRecord record)
    {
        using var buffer = ZString.CreateUtf8StringBuilder();
        using var stream = new MemoryStream(512);
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true }))
        {
            json.WriteStartObject();
            json.WriteString("timestamp", record.TimestampUtc);
            json.WriteString("level", record.Level.ToShortName());
            json.WriteString("service", record.Service);
            json.WriteString("host", record.Host);
            if (record.TraceId is { } trace) json.WriteString("traceId", trace);
            json.WriteString("message", record.Message);
            if (!record.Payload.IsEmpty) json.WriteString("payload", Encoding.UTF8.GetString(record.Payload.Span));
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }

    private static string ToCsv(in LogRecord record)
    {
        using var builder = ZString.CreateStringBuilder();
        builder.Append(record.TimestampUtc.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(',');
        builder.Append(record.Level.ToShortName());
        builder.Append(',');
        builder.Append(CsvField(record.Service));
        builder.Append(',');
        builder.Append(CsvField(record.Host));
        builder.Append(',');
        builder.Append(CsvField(record.TraceId ?? string.Empty));
        builder.Append(',');
        builder.Append(CsvField(record.Message));
        return builder.ToString();
    }

    private static string CsvField(string value)
    {
        if (value.IndexOfAny([',', '"', '\n', '\r']) < 0) return value;
        return string.Concat("\"", value.Replace("\"", "\"\""), "\"");
    }
}
