namespace LogStorage;

/// <summary>
/// Almacén de logs. Es la única interfaz que necesitas para escribir y para buscar.
/// </summary>
/// <example>
/// <code>
/// await using var store = LogStore.Open("./logs");
///
/// store.Information("Pedido creado", service: "ventas", traceId: activity.Id);
///
/// await foreach (var record in store.QueryAsync(new LogQuery
/// {
///     From = DateTimeOffset.UtcNow.AddDays(-1),
///     MinLevel = LogLevel.Warning,
///     Contains = "pedido",
/// }))
/// {
///     Console.WriteLine(record);
/// }
/// </code>
/// </example>
public interface ILogStore : IAsyncDisposable
{
    /// <summary>Configuración efectiva del almacén.</summary>
    LogStoreOptions Options { get; }

    /// <summary>Contadores acumulados desde que se abrió el almacén.</summary>
    LogStoreStats Stats { get; }

    /// <summary>Escribe un registro ya construido. Es el camino más rápido: no asigna nada.</summary>
    void Write(in LogEntry entry);

    /// <summary>Escribe un registro con la marca de tiempo actual.</summary>
    void Write(LogLevel level, string message, string? service = null, string? host = null, string? traceId = null, ReadOnlySpan<byte> payload = default);

    /// <summary>Busca registros. Los resultados llegan en streaming, sin cargarlo todo en memoria.</summary>
    IAsyncEnumerable<LogRecord> QueryAsync(LogQuery query, CancellationToken cancellationToken = default);

    /// <summary>Deja en disco todo lo escrito hasta ahora.</summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>Contadores de un <see cref="ILogStore"/>.</summary>
/// <param name="RecordsWritten">Registros ya volcados a disco.</param>
/// <param name="BlocksWritten">Bloques comprimidos y escritos.</param>
/// <param name="BytesWritten">Bytes escritos, ya comprimidos.</param>
public readonly record struct LogStoreStats(long RecordsWritten, long BlocksWritten, long BytesWritten)
{
    /// <summary>Bytes medios por registro en disco.</summary>
    public double BytesPerRecord => RecordsWritten == 0 ? 0 : (double)BytesWritten / RecordsWritten;
}
