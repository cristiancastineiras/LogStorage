using LogStorage.Compression;
using LogStorage.Storage;

namespace LogStorage;

/// <summary>Lee la estructura de un fichero de segmento sin decodificar los registros.</summary>
/// <remarks>
/// Todo sale de las cabeceras, así que inspeccionar un segmento de 128 MB cuesta lo mismo
/// que inspeccionar uno de 1 MB: unos cientos de saltos de puntero sobre el fichero mapeado.
/// </remarks>
public static class SegmentInspector
{
    /// <summary>Analiza un fichero <c>.lsz</c>. Devuelve null si no existe o es demasiado corto.</summary>
    public static SegmentInfo? Inspect(string path)
    {
        using var reader = SegmentFileReader.TryOpen(path);
        if (reader is null) return null;

        var blocks = new List<BlockInfo>(reader.Blocks.Count);
        long rawBytes = 0;
        long compressedBytes = 0;

        for (var i = 0; i < reader.Blocks.Count; i++)
        {
            var block = reader.Blocks[i];
            rawBytes += block.RawLength;
            compressedBytes += block.CompressedLength;

            blocks.Add(new BlockInfo(
                i,
                block.HeaderOffset,
                block.RecordCount,
                block.RawLength,
                block.CompressedLength,
                new DateTime(block.MinTicks, DateTimeKind.Utc),
                new DateTime(block.MaxTicks, DateTimeKind.Utc)));
        }

        return new SegmentInfo(
            Path.GetFullPath(path),
            reader.Header.Version,
            reader.Header.Codec,
            reader.Header.IsSealed,
            new DateTime(reader.Header.CreatedTicks, DateTimeKind.Utc),
            reader.Header.BlockCount,
            reader.Length,
            reader.RecordCount,
            rawBytes,
            compressedBytes,
            blocks.Count == 0 ? null : new DateTime(reader.MinTicks, DateTimeKind.Utc),
            blocks.Count == 0 ? null : new DateTime(reader.MaxTicks, DateTimeKind.Utc),
            blocks);
    }
}

/// <summary>Radiografía de un fichero de segmento.</summary>
/// <param name="Path">Ruta completa.</param>
/// <param name="FormatVersion">Versión del formato con la que se escribió.</param>
/// <param name="Codec">Algoritmo de compresión de sus bloques.</param>
/// <param name="Sealed">Indica si el segmento está cerrado (ya no se le añadirán bloques).</param>
/// <param name="CreatedUtc">Momento de creación.</param>
/// <param name="DeclaredBlockCount">Bloques que declara la cabecera.</param>
/// <param name="FileBytes">Tamaño del fichero.</param>
/// <param name="RecordCount">Registros realmente legibles.</param>
/// <param name="RawBytes">Bytes que ocuparían los bloques sin comprimir.</param>
/// <param name="CompressedBytes">Bytes que ocupan los bloques comprimidos.</param>
/// <param name="MinUtc">Registro más antiguo, o null si no hay bloques.</param>
/// <param name="MaxUtc">Registro más reciente, o null si no hay bloques.</param>
/// <param name="Blocks">Detalle de cada bloque.</param>
public sealed record SegmentInfo(
    string Path,
    int FormatVersion,
    CompressionCodec Codec,
    bool Sealed,
    DateTime CreatedUtc,
    int DeclaredBlockCount,
    long FileBytes,
    long RecordCount,
    long RawBytes,
    long CompressedBytes,
    DateTime? MinUtc,
    DateTime? MaxUtc,
    IReadOnlyList<BlockInfo> Blocks)
{
    /// <summary>Cuánto encoge el contenido al comprimirse.</summary>
    public double CompressionRatio => CompressedBytes == 0 ? 0 : (double)RawBytes / CompressedBytes;

    /// <summary>Bytes de disco por registro almacenado.</summary>
    public double BytesPerRecord => RecordCount == 0 ? 0 : (double)FileBytes / RecordCount;

    /// <summary>
    /// Indica si el fichero se cortó a mitad (por ejemplo, por un apagón): la cabecera
    /// declara más bloques de los que se pueden leer enteros.
    /// </summary>
    public bool IsTruncated => DeclaredBlockCount != Blocks.Count;
}

/// <summary>Detalle de un bloque dentro de un segmento.</summary>
/// <param name="Index">Posición del bloque en el fichero.</param>
/// <param name="Offset">Desplazamiento en bytes de su cabecera.</param>
/// <param name="RecordCount">Registros que contiene.</param>
/// <param name="RawBytes">Tamaño sin comprimir.</param>
/// <param name="CompressedBytes">Tamaño comprimido.</param>
/// <param name="MinUtc">Registro más antiguo del bloque.</param>
/// <param name="MaxUtc">Registro más reciente del bloque.</param>
public readonly record struct BlockInfo(
    int Index,
    long Offset,
    int RecordCount,
    int RawBytes,
    int CompressedBytes,
    DateTime MinUtc,
    DateTime MaxUtc)
{
    /// <summary>Cuánto encoge este bloque al comprimirse.</summary>
    public double CompressionRatio => CompressedBytes == 0 ? 0 : (double)RawBytes / CompressedBytes;
}
