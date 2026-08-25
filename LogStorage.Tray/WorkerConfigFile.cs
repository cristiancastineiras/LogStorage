using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LogStorage.Tray;

/// <summary>
/// La configuración del worker tal y como la ve el panel: la sección <c>LogStorage</c> del
/// <c>appsettings.json</c>.
/// </summary>
public sealed class WorkerConfig
{
    /// <summary>Carpeta raíz del almacén.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>Cada cuánto corre un ciclo de mantenimiento.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Espera antes del primer ciclo.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Nivel de zstd al escribir los Parquet.</summary>
    public int CompressionLevel { get; set; } = 3;

    /// <summary>Días que un día cerrado sigue como segmento antes de pasar a Parquet.</summary>
    public int ArchiveAfterDays { get; set; } = 2;

    /// <summary>Días que se conservan los segmentos. 0 = para siempre.</summary>
    public int SegmentRetentionDays { get; set; }

    /// <summary>Días que se conservan los Parquet. 0 = para siempre.</summary>
    public int ArchiveRetentionDays { get; set; } = 365 * 5;

    /// <summary>Reconstruir el manifiesto al final de cada ciclo.</summary>
    public bool Reindex { get; set; } = true;

    /// <summary>Verificar la integridad en cada ciclo.</summary>
    public bool Verify { get; set; }

    /// <summary>
    /// Devuelve el primer problema de la configuración, o <c>null</c> si es válida.
    /// </summary>
    /// <remarks>
    /// Repite las reglas de <c>MaintenanceOptions.Validate</c> a propósito: más vale decirlo
    /// en un cuadro de diálogo que dejar que el servicio se niegue a arrancar y que el
    /// usuario tenga que ir a buscar el porqué al registro de eventos.
    /// </remarks>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath))
            return "La carpeta del almacén no puede estar vacía.";

        if (!Path.IsPathFullyQualified(RootPath))
            return "La carpeta del almacén debe ser una ruta absoluta: un servicio de Windows "
                 + "trabaja desde C:\\Windows\\System32 y una ruta relativa acabaría ahí.";

        if (Interval <= TimeSpan.Zero)
            return "El intervalo debe ser mayor que cero.";

        if (CompressionLevel is < 1 or > 22)
            return "El nivel de compresión va de 1 a 22.";

        if (SegmentRetentionDays > 0 && ArchiveAfterDays > 0 && SegmentRetentionDays <= ArchiveAfterDays)
            return "Conservar segmentos debe ser mayor que archivar después de, o borrarías "
                 + "segmentos que aún no se han archivado.";

        return null;
    }
}

/// <summary>Lee y escribe el <c>appsettings.json</c> del worker sin tocar el resto del fichero.</summary>
public static class WorkerConfigFile
{
    private const string Section = "LogStorage";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Carga la configuración. Si el fichero no existe devuelve valores por defecto con el
    /// almacén dentro de la carpeta de datos, que es lo que instala el instalador.
    /// </summary>
    public static WorkerConfig Load()
    {
        var config = new WorkerConfig { RootPath = Path.Combine(Paths.DataDirectory, "logs") };

        if (!File.Exists(Paths.ConfigFile)) return config;

        var section = ReadRoot()?[Section]?.AsObject();
        if (section is null) return config;

        config.RootPath = GetString(section, nameof(WorkerConfig.RootPath)) ?? config.RootPath;
        config.Interval = GetTimeSpan(section, nameof(WorkerConfig.Interval)) ?? config.Interval;
        config.InitialDelay = GetTimeSpan(section, nameof(WorkerConfig.InitialDelay)) ?? config.InitialDelay;
        config.CompressionLevel = GetInt(section, nameof(WorkerConfig.CompressionLevel)) ?? config.CompressionLevel;
        config.ArchiveAfterDays = GetInt(section, nameof(WorkerConfig.ArchiveAfterDays)) ?? config.ArchiveAfterDays;
        config.SegmentRetentionDays = GetInt(section, nameof(WorkerConfig.SegmentRetentionDays)) ?? config.SegmentRetentionDays;
        config.ArchiveRetentionDays = GetInt(section, nameof(WorkerConfig.ArchiveRetentionDays)) ?? config.ArchiveRetentionDays;
        config.Reindex = GetBool(section, nameof(WorkerConfig.Reindex)) ?? config.Reindex;
        config.Verify = GetBool(section, nameof(WorkerConfig.Verify)) ?? config.Verify;

        return config;
    }

    /// <summary>
    /// Guarda la sección <c>LogStorage</c> conservando todo lo demás que hubiera en el fichero
    /// (por ejemplo la sección <c>Logging</c>).
    /// </summary>
    public static void Save(WorkerConfig config)
    {
        Directory.CreateDirectory(Paths.DataDirectory);

        var root = ReadRoot() ?? new JsonObject();
        var section = root[Section]?.AsObject();
        if (section is null)
        {
            section = new JsonObject();
            root[Section] = section;
        }

        section[nameof(WorkerConfig.RootPath)] = config.RootPath;
        section[nameof(WorkerConfig.Interval)] = Format(config.Interval);
        section[nameof(WorkerConfig.InitialDelay)] = Format(config.InitialDelay);
        section[nameof(WorkerConfig.CompressionLevel)] = config.CompressionLevel;
        section[nameof(WorkerConfig.ArchiveAfterDays)] = config.ArchiveAfterDays;
        section[nameof(WorkerConfig.SegmentRetentionDays)] = config.SegmentRetentionDays;
        section[nameof(WorkerConfig.ArchiveRetentionDays)] = config.ArchiveRetentionDays;
        section[nameof(WorkerConfig.Reindex)] = config.Reindex;
        section[nameof(WorkerConfig.Verify)] = config.Verify;

        // Temporal + Move: si se corta la luz a mitad no dejamos un JSON roto que impida
        // arrancar el servicio.
        var temporary = Paths.ConfigFile + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(WriteOptions));
        File.Move(temporary, Paths.ConfigFile, overwrite: true);
    }

    private static JsonObject? ReadRoot()
    {
        try
        {
            if (!File.Exists(Paths.ConfigFile)) return null;
            return JsonNode.Parse(File.ReadAllText(Paths.ConfigFile), documentOptions: ReadOptions)?.AsObject();
        }
        catch (JsonException)
        {
            // Fichero corrupto o editado a mano con un error: partimos de cero en vez de reventar.
            return null;
        }
    }

    private static string? GetString(JsonObject section, string name)
        => section[name] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text
            : null;

    private static int? GetInt(JsonObject section, string name)
        => section[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static bool? GetBool(JsonObject section, string name)
        => section[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static TimeSpan? GetTimeSpan(JsonObject section, string name)
        => GetString(section, name) is { } text && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span)
            ? span
            : null;

    private static string Format(TimeSpan value)
        => value.ToString(value.Days > 0 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss", CultureInfo.InvariantCulture);
}
