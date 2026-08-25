using MemoryPack;

namespace LogStorage.Storage;

/// <summary>
/// Resumen persistido del almacén: qué días tienen datos, cuántos registros y cuánto ocupan.
/// </summary>
/// <remarks>
/// <para>
/// No hace falta para consultar (el árbol de carpetas y las cabeceras de segmento son la
/// verdad), pero permite responder al instante a "cuántos logs tengo", "de qué fechas" o
/// "qué toca archivar" sin recorrer cinco años de ficheros.
/// </para>
/// <para>Se serializa con MemoryPack: un fichero de unos pocos KB incluso con años de historia.</para>
/// </remarks>
[MemoryPackable]
public sealed partial class StoreManifest
{
    /// <summary>Versión del formato del manifiesto.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Momento en el que se generó, en ticks UTC.</summary>
    public long UpdatedTicks { get; set; }

    /// <summary>Un elemento por día con datos, ordenados de forma ascendente.</summary>
    public List<DaySummary> Days { get; set; } = [];

    /// <summary>Registros de todo el almacén.</summary>
    public long TotalRecords
    {
        get
        {
            long total = 0;
            foreach (var day in Days) total += day.RecordCount;
            return total;
        }
    }

    /// <summary>Bytes en disco de todo el almacén (segmentos + Parquet).</summary>
    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var day in Days) total += day.SegmentBytes + day.ArchiveBytes;
            return total;
        }
    }
}

/// <summary>Resumen de un día del almacén.</summary>
[MemoryPackable]
public sealed partial class DaySummary
{
    /// <summary>Día en formato <c>yyyy-MM-dd</c>.</summary>
    public string Date { get; set; } = string.Empty;

    /// <summary>Segmentos <c>.lsz</c> presentes.</summary>
    public int SegmentCount { get; set; }

    /// <summary>Bloques comprimidos entre todos los segmentos.</summary>
    public int BlockCount { get; set; }

    /// <summary>Registros del día.</summary>
    public long RecordCount { get; set; }

    /// <summary>Bytes que ocupan los segmentos.</summary>
    public long SegmentBytes { get; set; }

    /// <summary>Bytes que ocupa el Parquet archivado, si lo hay.</summary>
    public long ArchiveBytes { get; set; }

    /// <summary>Instante del registro más antiguo, en ticks UTC.</summary>
    public long MinTicks { get; set; }

    /// <summary>Instante del registro más reciente, en ticks UTC.</summary>
    public long MaxTicks { get; set; }

    /// <summary>Indica si el día ya está volcado a Parquet.</summary>
    public bool Archived { get; set; }

    /// <summary>Día como <see cref="DateOnly"/>.</summary>
    public DateOnly Day => DateOnly.TryParse(Date, out var day) ? day : default;
}
