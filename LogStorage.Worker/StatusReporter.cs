using System.Text.Json;
using System.Text.Json.Serialization;

namespace LogStorage.Worker;

/// <summary>
/// Deja en disco el resultado del último ciclo de mantenimiento, para que el panel de
/// control pueda enseñarlo sin hablar con el servicio.
/// </summary>
/// <remarks>
/// <para>
/// Es la vía más simple que funciona: un fichero JSON diminuto junto al
/// <c>appsettings.json</c>. Sin tuberías con nombre, sin puertos, sin permisos que negociar.
/// El panel lo lee cuando quiere y si no existe, simplemente no muestra nada.
/// </para>
/// <para>
/// La escritura es atómica (fichero temporal + <see cref="File.Move(string, string, bool)"/>),
/// así que el panel nunca lee un JSON a medias. Un fallo escribiendo el estado jamás
/// tumba el ciclo de mantenimiento: es información, no funcionalidad.
/// </para>
/// </remarks>
public sealed class StatusReporter(string directory)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _path = System.IO.Path.Combine(directory, WorkerConstants.StatusFileName);
    private readonly Lock _gate = new();

    /// <summary>Ruta del fichero de estado.</summary>
    public string FilePath => _path;

    /// <summary>Escribe el estado. Nunca lanza.</summary>
    public void Write(WorkerStatus status)
    {
        try
        {
            lock (_gate)
            {
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(status, SerializerOptions));
                File.Move(temporary, _path, overwrite: true);
            }
        }
        catch (Exception)
        {
            // El estado es cosmético: si la carpeta es de solo lectura, seguimos trabajando.
        }
    }
}

/// <summary>
/// Retrato del último ciclo de mantenimiento.
/// </summary>
/// <remarks>
/// Contrato compartido con el panel de control: <c>LogStorage.Tray/WorkerStatusFile.cs</c>
/// tiene una copia de esta forma. Si añades un campo, añádelo también allí.
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

    /// <summary>Días archivados a Parquet en el último ciclo.</summary>
    public int DaysArchived { get; init; }

    /// <summary>Ficheros borrados por retención en el último ciclo.</summary>
    public int FilesPruned { get; init; }

    /// <summary>Días con datos en el almacén.</summary>
    public int TotalDays { get; init; }

    /// <summary>Registros en el almacén.</summary>
    public long TotalRecords { get; init; }

    /// <summary>Bytes en disco del almacén.</summary>
    public long TotalBytes { get; init; }

    /// <summary>Día más antiguo con datos, en <c>yyyy-MM-dd</c>.</summary>
    public string? OldestDay { get; init; }

    /// <summary>Día más reciente con datos, en <c>yyyy-MM-dd</c>.</summary>
    public string? NewestDay { get; init; }

    /// <summary>Carpeta del almacén que estaba manteniendo.</summary>
    public string RootPath { get; init; } = string.Empty;
}
