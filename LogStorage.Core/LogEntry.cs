namespace LogStorage;

/// <summary>
/// Registro tal y como se entrega al almacén. Es un <c>struct</c> de solo lectura:
/// pasarlo por <c>in</c> no copia ni asigna.
/// </summary>
/// <remarks>
/// Las cadenas se guardan en el diccionario del bloque, así que repetirlas
/// (mismo servicio, misma plantilla de mensaje) no cuesta prácticamente nada.
/// </remarks>
public readonly struct LogEntry
{
    /// <summary>Instante del evento en ticks UTC.</summary>
    public readonly long TimestampTicks;

    /// <summary>Severidad.</summary>
    public readonly LogLevel Level;

    /// <summary>Servicio o aplicación que emite (se codifica con diccionario).</summary>
    public readonly string Service;

    /// <summary>Máquina que emite (se codifica con diccionario).</summary>
    public readonly string Host;

    /// <summary>Texto del evento (se codifica con diccionario).</summary>
    public readonly string Message;

    /// <summary>Identificador de traza distribuida, opcional.</summary>
    public readonly string? TraceId;

    /// <summary>Datos adicionales opacos (MemoryPack, JSON, lo que quieras). Opcional.</summary>
    public readonly ReadOnlyMemory<byte> Payload;

    public LogEntry(
        long timestampTicks,
        LogLevel level,
        string message,
        string? service = null,
        string? host = null,
        string? traceId = null,
        ReadOnlyMemory<byte> payload = default)
    {
        TimestampTicks = timestampTicks;
        Level = level;
        Message = message ?? string.Empty;
        Service = service ?? string.Empty;
        Host = host ?? string.Empty;
        TraceId = traceId;
        Payload = payload;
    }

    public LogEntry(
        DateTimeOffset timestamp,
        LogLevel level,
        string message,
        string? service = null,
        string? host = null,
        string? traceId = null,
        ReadOnlyMemory<byte> payload = default)
        : this(timestamp.UtcTicks, level, message, service, host, traceId, payload)
    {
    }

    /// <summary>Instante del evento en UTC.</summary>
    public DateTime TimestampUtc => new(TimestampTicks, DateTimeKind.Utc);
}

/// <summary>
/// Registro leído del almacén. Las cadenas se comparten con el diccionario del bloque,
/// así que devolver millones de registros no multiplica la memoria.
/// </summary>
public readonly struct LogRecord
{
    public readonly long TimestampTicks;
    public readonly LogLevel Level;
    public readonly string Service;
    public readonly string Host;
    public readonly string Message;
    public readonly string? TraceId;
    public readonly ReadOnlyMemory<byte> Payload;

    public LogRecord(
        long timestampTicks,
        LogLevel level,
        string service,
        string host,
        string message,
        string? traceId,
        ReadOnlyMemory<byte> payload)
    {
        TimestampTicks = timestampTicks;
        Level = level;
        Service = service;
        Host = host;
        Message = message;
        TraceId = traceId;
        Payload = payload;
    }

    /// <summary>Instante del evento en UTC.</summary>
    public DateTime TimestampUtc => new(TimestampTicks, DateTimeKind.Utc);

    public override string ToString()
        => $"{TimestampUtc:yyyy-MM-dd HH:mm:ss.fff} {Level.ToShortName(),-5} {Service} {Message}";
}
