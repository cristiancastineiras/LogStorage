using System.Runtime.CompilerServices;
using Parquet;

namespace LogStorage.Archiving;

/// <summary>Lee registros de un Parquet archivado aplicando los filtros de una consulta.</summary>
internal static class ParquetArchiveReader
{
    /// <summary>
    /// Recorre el fichero grupo de filas a grupo de filas. Antes de leer ninguna columna
    /// consulta las estadísticas del grupo: si su rango de tiempos no toca el de la consulta,
    /// el grupo entero se descarta sin descomprimir nada.
    /// </summary>
    public static async IAsyncEnumerable<LogRecord> ReadAsync(
        string path,
        LogQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(path, null, cancellationToken).ConfigureAwait(false);

        var groups = Enumerable.Range(0, reader.RowGroupCount).ToArray();
        if (query.Descending) Array.Reverse(groups);

        var buffer = new List<LogRecord>(1024);

        foreach (var index in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var group = reader.OpenRowGroupReader(index);
            var count = (int)group.RowCount;
            if (count == 0) continue;
            if (!GroupOverlaps(group, query)) continue;

            var timestamps = new DateTime[count];
            var levels = new string?[count];
            var services = new string?[count];
            var hosts = new string?[count];
            var messages = new string?[count];
            var traceIds = new string?[count];
            var payloads = new byte[]?[count];

            await group.ReadAsync(ParquetArchive.Timestamp, timestamps.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.Level, levels.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.Service, services.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.Host, hosts.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.Message, messages.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.TraceId, traceIds.AsMemory(), null, cancellationToken).ConfigureAwait(false);
            await group.ReadAsync(ParquetArchive.Payload, payloads.AsMemory(), null, cancellationToken).ConfigureAwait(false);

            buffer.Clear();
            for (var i = 0; i < count; i++)
            {
                if (!LogLevels.TryParse(levels[i], out var level)) level = LogLevel.Information;

                var record = new LogRecord(
                    DateTime.SpecifyKind(timestamps[i], DateTimeKind.Utc).Ticks,
                    level,
                    services[i] ?? string.Empty,
                    hosts[i] ?? string.Empty,
                    messages[i] ?? string.Empty,
                    traceIds[i],
                    payloads[i] ?? []);

                if (query.Matches(record)) buffer.Add(record);
            }

            if (query.Descending) buffer.Reverse();
            foreach (var record in buffer) yield return record;
        }
    }

    /// <summary>Cuenta las filas de un Parquet sin leer sus columnas.</summary>
    public static async Task<long> CountRowsAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var reader = await ParquetReader.CreateAsync(path, null, cancellationToken).ConfigureAwait(false);

        long total = 0;
        for (var i = 0; i < reader.RowGroupCount; i++)
        {
            using var group = reader.OpenRowGroupReader(i);
            total += group.RowCount;
        }
        return total;
    }

    private static bool GroupOverlaps(ParquetRowGroupReader group, LogQuery query)
    {
        if (query.From is null && query.To is null) return true;

        try
        {
            var statistics = group.GetStatistics(ParquetArchive.Timestamp);
            if (statistics?.MinValue is not DateTime min || statistics.MaxValue is not DateTime max) return true;

            var minTicks = DateTime.SpecifyKind(min, DateTimeKind.Utc).Ticks;
            var maxTicks = DateTime.SpecifyKind(max, DateTimeKind.Utc).Ticks;
            return query.Overlaps(minTicks, maxTicks);
        }
        catch (Exception)
        {
            // Sin estadísticas fiables leemos el grupo: mejor lento que incompleto.
            return true;
        }
    }
}
