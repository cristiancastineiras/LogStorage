namespace LogStorage.Worker;

/// <summary>Configuración del worker de mantenimiento (sección <c>LogStorage</c> de appsettings).</summary>
public sealed class MaintenanceOptions
{
    /// <summary>Carpeta raíz del almacén.</summary>
    public string RootPath { get; set; } = "logs";

    /// <summary>Cada cuánto se ejecuta el ciclo de mantenimiento.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Espera inicial antes del primer ciclo, para no competir con el arranque de la aplicación.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Nivel de compresión de zstd usado al escribir los Parquet.</summary>
    public int CompressionLevel { get; set; } = 3;

    /// <summary>
    /// Días que un día completo sigue en formato segmento antes de pasar a Parquet.
    /// Con 2, hoy y ayer se quedan como segmentos y todo lo anterior se archiva.
    /// Un 0 o menos desactiva el archivado.
    /// </summary>
    public int ArchiveAfterDays { get; set; } = 2;

    /// <summary>
    /// Días que se conservan los segmentos. Red de seguridad para días que no se pudieron
    /// archivar. Debe ser mayor que <see cref="ArchiveAfterDays"/>. Un 0 o menos lo desactiva.
    /// </summary>
    public int SegmentRetentionDays { get; set; }

    /// <summary>Días que se conservan los Parquet archivados. Un 0 o menos conserva para siempre.</summary>
    public int ArchiveRetentionDays { get; set; } = 365 * 5;

    /// <summary>Reconstruye <c>manifest.idx</c> al final de cada ciclo.</summary>
    public bool Reindex { get; set; } = true;

    /// <summary>Comprueba la integridad de todos los segmentos en cada ciclo. Cuesta E/S: solo si te hace falta.</summary>
    public bool Verify { get; set; }

    /// <summary>Valida la configuración y lanza si hay algo incoherente.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("LogStorage:RootPath no puede estar vacío.");
        if (Interval <= TimeSpan.Zero) throw new ArgumentException("LogStorage:Interval debe ser mayor que cero.");
        if (SegmentRetentionDays > 0 && ArchiveAfterDays > 0 && SegmentRetentionDays <= ArchiveAfterDays)
        {
            throw new ArgumentException("LogStorage:SegmentRetentionDays debe ser mayor que ArchiveAfterDays, o borrarías segmentos sin archivar.");
        }
    }
}
