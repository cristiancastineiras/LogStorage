using System.Runtime.CompilerServices;
using LogStorage.Querying;
using LogStorage.Storage;

namespace LogStorage;

/// <summary>
/// Punto de entrada de la librería: abre una carpeta y ya puedes escribir y buscar.
/// </summary>
/// <remarks>
/// <para>Es seguro usar la misma instancia desde muchos hilos.</para>
/// <para>
/// Un proceso debe abrir el almacén en escritura una sola vez. Pueden abrirlo en modo
/// lectura tantos procesos como quieras a la vez, incluso mientras otro escribe.
/// </para>
/// </remarks>
public sealed class LogStore : ILogStore
{
    private readonly SegmentWriter? _writer;
    private readonly QueryEngine _engine;
    private readonly LogStoreMaintenance _maintenance;
    private int _disposed;

    private LogStore(LogStoreOptions options)
    {
        Options = options;
        _engine = new QueryEngine(options);
        _maintenance = new LogStoreMaintenance(options);

        if (!options.ReadOnly)
        {
            Directory.CreateDirectory(options.RootPath);
            _writer = new SegmentWriter(options);
        }
    }

    /// <summary>Abre (o crea) un almacén en la carpeta indicada, con la configuración por defecto.</summary>
    public static LogStore Open(string rootPath) => Open(new LogStoreOptions { RootPath = rootPath });

    /// <summary>Abre (o crea) un almacén con configuración a medida.</summary>
    public static LogStore Open(LogStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new LogStore(options.Clone().Validate());
    }

    /// <summary>Abre un almacén solo para consultar. No crea carpetas ni bloquea nada.</summary>
    public static LogStore OpenReadOnly(string rootPath)
        => Open(new LogStoreOptions { RootPath = rootPath, ReadOnly = true });

    /// <summary>Abre (o crea) un almacén configurándolo con una lambda.</summary>
    public static LogStore Open(string rootPath, Action<LogStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var options = new LogStoreOptions { RootPath = rootPath };
        configure(options);
        return Open(options);
    }

    /// <inheritdoc />
    public LogStoreOptions Options { get; }

    /// <inheritdoc />
    public LogStoreStats Stats => _writer is null
        ? default
        : new LogStoreStats(_writer.RecordsWritten, _writer.BlocksWritten, _writer.BytesWritten);

    /// <summary>Tareas de mantenimiento: archivar a Parquet, aplicar retención, verificar y reindexar.</summary>
    public LogStoreMaintenance Maintenance => _maintenance;

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Write(in LogEntry entry) => Writer.Append(entry);

    /// <inheritdoc />
    public void Write(LogLevel level, string message, string? service = null, string? host = null, string? traceId = null, ReadOnlySpan<byte> payload = default)
    {
        var entry = new LogEntry(
            DateTime.UtcNow.Ticks,
            level,
            message,
            service ?? Options.DefaultService,
            host ?? Options.DefaultHost,
            traceId);

        if (payload.IsEmpty)
        {
            Writer.Append(entry);
            return;
        }

        Writer.Append(new LogEntry(entry.TimestampTicks, level, entry.Message, entry.Service, entry.Host, traceId, payload.ToArray()));
    }

    /// <summary>Escribe con una marca de tiempo explícita (útil al importar de otro sistema).</summary>
    public void WriteAt(DateTimeOffset timestamp, LogLevel level, string message, string? service = null, string? host = null, string? traceId = null)
        => Writer.Append(new LogEntry(timestamp.UtcTicks, level, message, service ?? Options.DefaultService, host ?? Options.DefaultHost, traceId));

    /// <summary>Atajo para nivel Trace.</summary>
    public void Trace(string message, string? service = null, string? traceId = null) => Write(LogLevel.Trace, message, service, traceId: traceId);

    /// <summary>Atajo para nivel Debug.</summary>
    public void Debug(string message, string? service = null, string? traceId = null) => Write(LogLevel.Debug, message, service, traceId: traceId);

    /// <summary>Atajo para nivel Information.</summary>
    public void Information(string message, string? service = null, string? traceId = null) => Write(LogLevel.Information, message, service, traceId: traceId);

    /// <summary>Atajo para nivel Warning.</summary>
    public void Warning(string message, string? service = null, string? traceId = null) => Write(LogLevel.Warning, message, service, traceId: traceId);

    /// <summary>Atajo para nivel Error.</summary>
    public void Error(string message, string? service = null, string? traceId = null) => Write(LogLevel.Error, message, service, traceId: traceId);

    /// <summary>Atajo para nivel Critical.</summary>
    public void Critical(string message, string? service = null, string? traceId = null) => Write(LogLevel.Critical, message, service, traceId: traceId);

    /// <inheritdoc />
    public IAsyncEnumerable<LogRecord> QueryAsync(LogQuery query, CancellationToken cancellationToken = default)
        => _engine.QueryAsync(query, cancellationToken);

    /// <summary>Cuenta los registros que cumplen la consulta sin materializarlos.</summary>
    public async ValueTask<long> CountAsync(LogQuery query, CancellationToken cancellationToken = default)
    {
        long count = 0;
        await foreach (var _ in _engine.QueryAsync(query, cancellationToken).ConfigureAwait(false)) count++;
        return count;
    }

    /// <inheritdoc />
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => _writer is null ? Task.CompletedTask : _writer.FlushAsync(toDisk: true, cancellationToken);

    /// <summary>Cierra el segmento en curso. El siguiente registro empezará uno nuevo.</summary>
    public Task SealAsync(CancellationToken cancellationToken = default)
        => _writer is null ? Task.CompletedTask : _writer.SealAsync(cancellationToken);

    private SegmentWriter Writer => _writer
        ?? throw new InvalidOperationException("El almacén está abierto en modo solo lectura.");

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
