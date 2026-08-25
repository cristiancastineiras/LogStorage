using System.Buffers;
using System.Runtime.CompilerServices;
using LogStorage.Archiving;
using LogStorage.Compression;
using LogStorage.Format;
using LogStorage.Storage;

namespace LogStorage.Querying;

/// <summary>
/// Resuelve consultas descartando trabajo en cuatro niveles, del más barato al más caro.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><b>Carpeta</b>: el árbol <c>yyyy/MM/dd</c> ya descarta los días fuera de rango.</item>
/// <item><b>Bloque</b>: cada bloque lleva su min/max de tiempo en la cabecera; los que no
/// tocan el rango no se descomprimen.</item>
/// <item><b>Diccionario</b>: se descomprime el bloque y se mira su diccionario de cadenas.
/// Si el servicio buscado no está, o ninguna cadena contiene el texto, el bloque se abandona
/// sin decodificar una sola columna.</item>
/// <item><b>Registro</b>: solo entonces se comparan campos, y con enteros (ids del
/// diccionario) en lugar de cadenas.</item>
/// </list>
/// </remarks>
internal sealed class QueryEngine
{
    private readonly LogStoreOptions _options;

    public QueryEngine(LogStoreOptions options) => _options = options;

    public async IAsyncEnumerable<LogRecord> QueryAsync(LogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit <= 0) yield break;

        var days = ResolveDays(query);
        if (days.Count == 0) yield break;

        var remaining = query.Limit;
        var buffer = new List<LogRecord>(1024);

        using var decoder = new BlockDecoder();
        using var codecs = new CodecCache();

        foreach (var day in days)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (day.Segments is { Count: > 0 } segments)
            {
                foreach (var path in segments)
                {
                    var reader = TryOpenSegment(path);
                    if (reader is null) continue;

                    using (reader)
                    {
                        var codec = codecs.Get(reader.Header.Codec);
                        var blocks = reader.Blocks;

                        for (var i = 0; i < blocks.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var block = blocks[query.Descending ? blocks.Count - 1 - i : i];
                            if (!query.Overlaps(block.MinTicks, block.MaxTicks)) continue;

                            buffer.Clear();
                            DecodeBlock(reader, block, codec, decoder, query, buffer, query.Descending ? int.MaxValue : remaining);
                            if (buffer.Count == 0) continue;
                            if (query.Descending) buffer.Reverse();

                            foreach (var record in buffer)
                            {
                                yield return record;
                                if (--remaining == 0) yield break;
                            }
                        }
                    }
                }
            }
            else if (day.ArchiveFile is { } archiveFile)
            {
                await foreach (var record in ParquetArchiveReader.ReadAsync(archiveFile, query, cancellationToken).ConfigureAwait(false))
                {
                    yield return record;
                    if (--remaining == 0) yield break;
                }
            }
        }
    }

    /// <summary>
    /// Días a recorrer y de dónde sale cada uno. Si un día tiene segmentos se leen los
    /// segmentos y se ignora su Parquet: así nunca salen registros duplicados durante la
    /// ventana en la que un día ya está archivado pero todavía no se han borrado sus segmentos.
    /// </summary>
    private List<DaySource> ResolveDays(LogQuery query)
    {
        var fromDay = query.From is { } from ? DateOnly.FromDateTime(from.UtcDateTime) : (DateOnly?)null;
        var toDay = query.To is { } to ? DateOnly.FromDateTime(to.UtcDateTime) : (DateOnly?)null;

        var map = new SortedDictionary<DateOnly, DaySource>();

        if (query.IncludeSegments)
        {
            foreach (var (day, directory) in SegmentPaths.EnumerateDays(_options.RootPath, fromDay, toDay))
            {
                map[day] = new DaySource { Day = day, Segments = SegmentPaths.SegmentsIn(directory) };
            }
        }

        if (query.IncludeArchive)
        {
            foreach (var (day, file) in ParquetArchive.EnumerateDays(_options.ArchivePath, fromDay, toDay))
            {
                if (map.TryGetValue(day, out var existing)) existing.ArchiveFile = file;
                else map[day] = new DaySource { Day = day, ArchiveFile = file };
            }
        }

        var result = new List<DaySource>(map.Values);
        if (query.Descending) result.Reverse();
        return result;
    }

    private static SegmentFileReader? TryOpenSegment(string path)
    {
        try
        {
            return SegmentFileReader.TryOpen(path);
        }
        catch (IOException)
        {
            return null; // el fichero acaba de rotar o de borrarse: no es un error de consulta
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void DecodeBlock(
        SegmentFileReader reader,
        in BlockDescriptor block,
        IBlockCodec codec,
        BlockDecoder decoder,
        LogQuery query,
        List<LogRecord> output,
        int limit)
    {
        var raw = ArrayPool<byte>.Shared.Rent(block.RawLength);
        try
        {
            var length = codec.Decompress(reader.BlockPayload(block), raw);
            var span = raw.AsSpan(0, length);

            decoder.ParseHeader(span);

            // Poda por diccionario: si el valor exacto no está en este bloque, no hay nada que hacer.
            var serviceId = -1;
            var hostId = -1;
            var traceId = -1;
            if (query.Service is { } service && (serviceId = decoder.FindExact(service)) < 0) return;
            if (query.Host is { } host && (hostId = decoder.FindExact(host)) < 0) return;
            if (query.TraceId is { } trace && (traceId = decoder.FindExact(trace)) < 0) return;

            bool[]? containsMask = null;
            if (query.Contains is { } needle)
            {
                containsMask = decoder.RentContainsMask(needle, query.Comparison, out var anyMatch);
                if (!anyMatch)
                {
                    BlockDecoder.ReturnMask(containsMask);
                    return;
                }
            }

            try
            {
                decoder.ParseColumns(span);
                Collect(decoder, query, output, limit, serviceId, hostId, traceId, containsMask);
            }
            finally
            {
                if (containsMask is not null) BlockDecoder.ReturnMask(containsMask);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(raw);
        }
    }

    private static void Collect(
        BlockDecoder decoder,
        LogQuery query,
        List<LogRecord> output,
        int limit,
        int serviceId,
        int hostId,
        int traceId,
        bool[]? containsMask)
    {
        var count = decoder.RecordCount;
        var timestamps = decoder.Timestamps;
        var levels = decoder.Levels;
        var services = decoder.Services;
        var hosts = decoder.Hosts;
        var messages = decoder.Messages;
        var traces = decoder.Traces;
        var strings = decoder.Strings;

        var fromTicks = query.FromTicks;
        var toTicks = query.ToTicks;
        var minLevel = query.MinLevel is { } min ? (byte)min : byte.MinValue;
        var exactLevel = query.Level is { } exact ? (int)exact : -1;
        var hasPayload = decoder.HasPayload;

        for (var i = 0; i < count; i++)
        {
            var ticks = timestamps[i];
            if (ticks < fromTicks || ticks >= toTicks) continue;

            var level = levels[i];
            if (level < minLevel) continue;
            if (exactLevel >= 0 && level != exactLevel) continue;

            if (serviceId >= 0 && services[i] != serviceId) continue;
            if (hostId >= 0 && hosts[i] != hostId) continue;
            if (traceId >= 0 && traces[i] != traceId) continue;
            if (containsMask is not null && !containsMask[messages[i]]) continue;

            output.Add(new LogRecord(
                ticks,
                (LogLevel)level,
                strings[services[i]] ?? string.Empty,
                strings[hosts[i]] ?? string.Empty,
                strings[messages[i]] ?? string.Empty,
                strings[traces[i]],
                hasPayload ? decoder.GetPayload(i) : default));

            if (output.Count >= limit) return;
        }
    }

    private sealed class DaySource
    {
        public DateOnly Day { get; init; }

        public List<string>? Segments { get; init; }

        public string? ArchiveFile { get; set; }
    }

    /// <summary>Reutiliza un descompresor por códec durante toda la consulta en vez de crear uno por bloque.</summary>
    private sealed class CodecCache : IDisposable
    {
        private readonly Dictionary<CompressionCodec, IBlockCodec> _codecs = [];

        public IBlockCodec Get(CompressionCodec id)
        {
            if (_codecs.TryGetValue(id, out var codec)) return codec;
            codec = BlockCodecs.Create(id, 1);
            _codecs[id] = codec;
            return codec;
        }

        public void Dispose()
        {
            foreach (var codec in _codecs.Values) codec.Dispose();
            _codecs.Clear();
        }
    }
}
