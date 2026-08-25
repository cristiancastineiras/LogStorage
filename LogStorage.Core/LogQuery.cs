namespace LogStorage;

/// <summary>
/// Filtro de búsqueda. Todos los campos son opcionales; los que rellenes se combinan con AND.
/// </summary>
/// <remarks>
/// El motor usa estos filtros para <b>descartar ficheros y bloques enteros</b> antes de
/// descomprimir nada: primero por carpeta de día, luego por rango de tiempo del segmento,
/// luego por rango del bloque y, por último, mirando el diccionario de cadenas del bloque.
/// </remarks>
public sealed record LogQuery
{
    /// <summary>Límite inferior inclusivo.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Límite superior exclusivo.</summary>
    public DateTimeOffset? To { get; init; }

    /// <summary>Severidad mínima (incluida).</summary>
    public LogLevel? MinLevel { get; init; }

    /// <summary>Severidad exacta.</summary>
    public LogLevel? Level { get; init; }

    /// <summary>Servicio exacto.</summary>
    public string? Service { get; init; }

    /// <summary>Host exacto.</summary>
    public string? Host { get; init; }

    /// <summary>Identificador de traza exacto.</summary>
    public string? TraceId { get; init; }

    /// <summary>Subcadena que debe aparecer en el mensaje.</summary>
    public string? Contains { get; init; }

    /// <summary>Si <c>false</c> (por defecto) <see cref="Contains"/> ignora mayúsculas.</summary>
    public bool CaseSensitive { get; init; }

    /// <summary>Número máximo de registros a devolver.</summary>
    public int Limit { get; init; } = int.MaxValue;

    /// <summary>Devuelve del más reciente al más antiguo.</summary>
    public bool Descending { get; init; }

    /// <summary>Incluye también los ficheros Parquet archivados.</summary>
    public bool IncludeArchive { get; init; } = true;

    /// <summary>Incluye los segmentos activos (los <c>.lsz</c>).</summary>
    public bool IncludeSegments { get; init; } = true;

    internal long FromTicks => From?.UtcTicks ?? long.MinValue;

    internal long ToTicks => To?.UtcTicks ?? long.MaxValue;

    /// <summary>¿Se solapa el rango [min,max] con el rango pedido?</summary>
    internal bool Overlaps(long minTicks, long maxTicks) => minTicks < ToTicks && maxTicks >= FromTicks;

    internal StringComparison Comparison => CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Comprueba un registro ya decodificado contra los filtros que no se pueden podar antes.</summary>
    internal bool Matches(in LogRecord record)
    {
        if (record.TimestampTicks < FromTicks || record.TimestampTicks >= ToTicks) return false;
        if (MinLevel is { } min && record.Level < min) return false;
        if (Level is { } exact && record.Level != exact) return false;
        if (Service is { } svc && !string.Equals(record.Service, svc, StringComparison.Ordinal)) return false;
        if (Host is { } host && !string.Equals(record.Host, host, StringComparison.Ordinal)) return false;
        if (TraceId is { } trace && !string.Equals(record.TraceId, trace, StringComparison.Ordinal)) return false;
        if (Contains is { } needle && record.Message.IndexOf(needle, Comparison) < 0) return false;
        return true;
    }

    /// <summary>Últimas <paramref name="hours"/> horas, del más reciente al más antiguo.</summary>
    public static LogQuery LastHours(double hours, int limit = int.MaxValue) => new()
    {
        From = DateTimeOffset.UtcNow.AddHours(-hours),
        Limit = limit,
        Descending = true,
    };

    /// <summary>Un día completo en UTC.</summary>
    public static LogQuery OnDay(DateOnly day) => new()
    {
        From = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        To = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
    };

    /// <summary>Rango explícito.</summary>
    public static LogQuery Between(DateTimeOffset from, DateTimeOffset to) => new() { From = from, To = to };
}
