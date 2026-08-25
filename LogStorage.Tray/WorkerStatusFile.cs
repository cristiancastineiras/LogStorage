using System.Text.Json;

namespace LogStorage.Tray;

/// <summary>
/// Copia del contrato que el worker escribe en <c>status.json</c>.
/// </summary>
/// <remarks>
/// La fuente de la verdad es <c>LogStorage.Worker/StatusReporter.cs</c>. Está duplicado a
/// propósito: meterlo en <c>LogStorage.Core</c> obligaría a que el paquete de la librería
/// cargara con un detalle de operación que no le incumbe, y hacer que el panel referencie
/// al worker le arrastraría todo el hosting.
/// </remarks>
public sealed record WorkerStatus
{
    /// <summary>Momento en el que se escribió este estado.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Momento en el que terminó el último ciclo.</summary>
    public DateTimeOffset? LastCycleUtc { get; init; }

    /// <summary>Momento aproximado del siguiente ciclo.</summary>
    public DateTimeOffset? NextCycleUtc { get; init; }

    /// <summary>El último ciclo terminó sin errores.</summary>
    public bool Ok { get; init; } = true;

    /// <summary>Resumen legible del último ciclo, o el error si falló.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Duración del último ciclo en milisegundos.</summary>
    public double DurationMs { get; init; }

    /// <summary>Días archivados en el último ciclo.</summary>
    public int DaysArchived { get; init; }

    /// <summary>Ficheros borrados por retención en el último ciclo.</summary>
    public int FilesPruned { get; init; }

    /// <summary>Días con datos en el almacén.</summary>
    public int TotalDays { get; init; }

    /// <summary>Registros en el almacén.</summary>
    public long TotalRecords { get; init; }

    /// <summary>Bytes en disco del almacén.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Día más antiguo con datos.</summary>
    public string? OldestDay { get; init; }

    /// <summary>Día más reciente con datos.</summary>
    public string? NewestDay { get; init; }

    /// <summary>Carpeta del almacén que estaba manteniendo.</summary>
    public string RootPath { get; init; } = string.Empty;
}

/// <summary>Lee el <c>status.json</c> que deja el worker.</summary>
public static class WorkerStatusFile
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Devuelve el último estado, o <c>null</c> si no hay ninguno legible.</summary>
    public static WorkerStatus? TryRead()
    {
        try
        {
            if (!File.Exists(Paths.StatusFile)) return null;
            return JsonSerializer.Deserialize<WorkerStatus>(File.ReadAllText(Paths.StatusFile), Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // El worker puede estar reemplazándolo justo ahora. En el siguiente refresco estará.
            return null;
        }
    }
}
