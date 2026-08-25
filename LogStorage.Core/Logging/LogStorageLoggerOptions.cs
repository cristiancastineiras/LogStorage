namespace LogStorage.Logging;

/// <summary>Ajustes del proveedor de <c>ILogger</c> que escribe en un <see cref="ILogStore"/>.</summary>
public sealed class LogStorageLoggerOptions
{
    /// <summary>Nivel mínimo que llega al almacén. Por debajo ni se formatea el mensaje.</summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Guarda la <b>plantilla</b> del mensaje en vez del texto ya formateado.
    /// </summary>
    /// <remarks>
    /// Con esto activado, <c>logger.LogInformation("Pedido {Id} creado", 42)</c> guarda
    /// <c>"Pedido {Id} creado"</c>, que se repite millones de veces y por tanto ocupa una sola
    /// entrada del diccionario del bloque. Los valores van aparte, en el payload.
    /// Desactívalo si prefieres leer el texto tal cual sin herramientas.
    /// </remarks>
    public bool UseMessageTemplate { get; set; } = true;

    /// <summary>Guarda los parámetros con nombre (y la excepción) como JSON UTF-8 en el payload.</summary>
    public bool IncludeParameters { get; set; } = true;

    /// <summary>Rellena <c>TraceId</c> con el identificador de la <c>Activity</c> actual, si la hay.</summary>
    public bool CaptureActivity { get; set; } = true;

    /// <summary>Servicio con el que se etiquetan los registros. Si es nulo se usa el del almacén.</summary>
    public string? Service { get; set; }

    /// <summary>Guarda la categoría del logger (normalmente el nombre del tipo) dentro del payload.</summary>
    public bool IncludeCategory { get; set; } = true;
}
