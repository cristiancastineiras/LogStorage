using Parquet;

namespace LogStorage.Archiving;

/// <summary>
/// Escribe registros a un fichero Parquet acumulándolos por grupos de filas.
/// </summary>
/// <remarks>
/// Parquet es columnar, así que hay que darle columnas enteras. Se van llenando arrays de
/// tamaño fijo (<c>rowGroupSize</c>) y cada vez que se completan se sueltan como un row group.
/// Con eso la memoria se mantiene constante aunque el día tenga mil millones de líneas.
/// </remarks>
internal sealed class ParquetArchiveWriter : IAsyncDisposable
{
    private readonly ParquetWriter _writer;
    private readonly int _rowGroupSize;

    private readonly DateTime[] _timestamps;
    private readonly string[] _levels;
    private readonly string[] _services;
    private readonly string[] _hosts;
    private readonly string[] _messages;
    private readonly string[] _traceIds;
    private readonly byte[][] _payloads;

    private int _count;
    private long _totalRows;
    private bool _completed;

    private ParquetArchiveWriter(ParquetWriter writer, int rowGroupSize)
    {
        _writer = writer;
        _rowGroupSize = rowGroupSize;

        _timestamps = new DateTime[rowGroupSize];
        _levels = new string[rowGroupSize];
        _services = new string[rowGroupSize];
        _hosts = new string[rowGroupSize];
        _messages = new string[rowGroupSize];
        _traceIds = new string[rowGroupSize];
        _payloads = new byte[rowGroupSize][];
    }

    /// <summary>Filas escritas hasta el momento.</summary>
    public long RowCount => _totalRows;

    public static async Task<ParquetArchiveWriter> CreateAsync(
        Stream stream,
        int rowGroupSize = 128 * 1024,
        int compressionLevel = 3,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowGroupSize);

        var options = new ParquetOptions
        {
            CompressionMethod = CompressionMethod.Zstd,
            CompressionLevel = ToCompressionLevel(compressionLevel),
        };

        var writer = await ParquetWriter.CreateAsync(ParquetArchive.Schema, stream, options, append: false, cancellationToken).ConfigureAwait(false);
        return new ParquetArchiveWriter(writer, rowGroupSize);
    }

    /// <summary>Añade un registro. Suelta un row group automáticamente cuando se llena el búfer.</summary>
    public ValueTask AddAsync(in LogRecord record, CancellationToken cancellationToken = default)
    {
        var i = _count;
        _timestamps[i] = record.TimestampUtc;
        _levels[i] = record.Level.ToShortName();
        _services[i] = record.Service;
        _hosts[i] = record.Host;
        _messages[i] = record.Message;
        _traceIds[i] = record.TraceId!;
        _payloads[i] = record.Payload.IsEmpty ? null! : record.Payload.ToArray();
        _count = i + 1;

        return _count >= _rowGroupSize ? new ValueTask(FlushRowGroupAsync(cancellationToken)) : ValueTask.CompletedTask;
    }

    /// <summary>Cierra el último row group. Hay que llamarlo antes de deshacerse del escritor.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        _completed = true;
        if (_count > 0) await FlushRowGroupAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushRowGroupAsync(CancellationToken cancellationToken)
    {
        var count = _count;
        if (count == 0) return;
        _count = 0;
        _totalRows += count;

        using var group = _writer.CreateRowGroup();
        await group.WriteAsync<DateTime>(ParquetArchive.Timestamp, _timestamps.AsMemory(0, count), null, null, cancellationToken).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.Level, new ArraySegment<string>(_levels, 0, count)).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.Service, new ArraySegment<string>(_services, 0, count)).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.Host, new ArraySegment<string>(_hosts, 0, count)).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.Message, new ArraySegment<string>(_messages, 0, count)).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.TraceId, new ArraySegment<string>(_traceIds, 0, count)).ConfigureAwait(false);
        await group.WriteAsync(ParquetArchive.Payload, new ArraySegment<byte[]>(_payloads, 0, count)).ConfigureAwait(false);
    }

    /// <summary>Traduce el nivel de zstd de la librería al enum de compresión que espera Parquet.Net.</summary>
    private static System.IO.Compression.CompressionLevel ToCompressionLevel(int level) => level switch
    {
        <= 1 => System.IO.Compression.CompressionLevel.Fastest,
        <= 9 => System.IO.Compression.CompressionLevel.Optimal,
        _ => System.IO.Compression.CompressionLevel.SmallestSize,
    };

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
