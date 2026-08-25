using LogStorage.Compression;

namespace LogStorage;

/// <summary>Configuración de un <see cref="LogStore"/>. Los valores por defecto son razonables para producción.</summary>
public sealed class LogStoreOptions
{
    /// <summary>Carpeta raíz del almacén. Dentro se crean <c>yyyy/MM/dd</c> y <c>archive</c>.</summary>
    public string RootPath { get; set; } = "logs";

    /// <summary>
    /// Tamaño objetivo de un bloque <b>sin comprimir</b>. Es la unidad de compresión y de
    /// lectura aleatoria: más grande comprime mejor, más pequeño hace las búsquedas más finas.
    /// </summary>
    public int BlockSizeBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>Tamaño máximo de un segmento <b>en disco</b> (ya comprimido) antes de rotar.</summary>
    /// <remarks>
    /// Tope de 1 GiB: los segmentos se leen mapeados en memoria y un mapeo se recorre como un
    /// único span, que no puede pasar de 2 GiB. Con 1 GiB queda margen de sobra aunque el
    /// último bloque se pase de la raya.
    /// </remarks>
    public long MaxSegmentBytes { get; set; } = 128L * 1024 * 1024;

    /// <summary>Tope duro de <see cref="MaxSegmentBytes"/>.</summary>
    public const long MaxSegmentBytesLimit = 1024L * 1024 * 1024;

    /// <summary>Registros máximos por segmento (0 = sin límite).</summary>
    public long MaxSegmentRecords { get; set; }

    /// <summary>Algoritmo de compresión de bloque.</summary>
    public CompressionCodec Codec { get; set; } = CompressionCodec.Zstd;

    /// <summary>Nivel de zstd (1 rápido … 19 máximo). 3 es el punto dulce.</summary>
    public int CompressionLevel { get; set; } = 3;

    /// <summary>Bloques que pueden estar pendientes de escribir antes de frenar a quien escribe.</summary>
    public int MaxPendingBlocks { get; set; } = 4;

    /// <summary>Cada cuánto se vuelca a disco el bloque en curso aunque no esté lleno. <see cref="TimeSpan.Zero"/> lo desactiva.</summary>
    public TimeSpan AutoFlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Servicio por defecto para las sobrecargas que no lo indican.</summary>
    public string DefaultService { get; set; } = "app";

    /// <summary>Host por defecto para las sobrecargas que no lo indican.</summary>
    public string DefaultHost { get; set; } = Environment.MachineName;

    /// <summary>Abre el almacén sin capacidad de escritura (no crea carpetas ni segmentos).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Subcarpeta (relativa a <see cref="RootPath"/>) donde viven los Parquet archivados.</summary>
    public string ArchiveFolderName { get; set; } = "archive";

    /// <summary>Ruta absoluta de la carpeta de archivo.</summary>
    public string ArchivePath => Path.Combine(RootPath, ArchiveFolderName);

    /// <summary>Valida y normaliza. Lanza si algo no tiene sentido.</summary>
    public LogStoreOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(RootPath)) throw new ArgumentException("RootPath no puede estar vacío.", nameof(RootPath));
        if (BlockSizeBytes < 4096) throw new ArgumentOutOfRangeException(nameof(BlockSizeBytes), "El bloque mínimo es de 4 KiB.");
        if (BlockSizeBytes > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(BlockSizeBytes), "El bloque máximo es de 512 MiB.");
        if (MaxSegmentBytes < BlockSizeBytes) throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes), "Un segmento debe caber al menos un bloque.");
        if (MaxSegmentBytes > MaxSegmentBytesLimit) throw new ArgumentOutOfRangeException(nameof(MaxSegmentBytes), "Un segmento no puede pasar de 1 GiB: se lee mapeado en memoria como un único span.");
        if (MaxPendingBlocks < 1) throw new ArgumentOutOfRangeException(nameof(MaxPendingBlocks));
        if (CompressionLevel is < 1 or > 22) throw new ArgumentOutOfRangeException(nameof(CompressionLevel), "zstd admite niveles de 1 a 22.");
        if (MaxSegmentRecords < 0) throw new ArgumentOutOfRangeException(nameof(MaxSegmentRecords));

        RootPath = Path.GetFullPath(RootPath);
        return this;
    }

    internal LogStoreOptions Clone() => (LogStoreOptions)MemberwiseClone();
}
