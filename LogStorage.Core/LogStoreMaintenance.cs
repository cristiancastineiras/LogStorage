using System.IO.Hashing;
using LogStorage.Archiving;
using LogStorage.Compression;
using LogStorage.Format;
using LogStorage.Querying;
using LogStorage.Storage;

namespace LogStorage;

/// <summary>
/// Tareas de mantenimiento del almacén: archivar a Parquet, aplicar retención,
/// verificar integridad y reconstruir el manifiesto.
/// </summary>
/// <remarks>
/// Se puede usar suelta, sin abrir un <see cref="LogStore"/> de escritura:
/// <c>new LogStoreMaintenance(new LogStoreOptions { RootPath = "./logs" })</c>.
/// </remarks>
public sealed class LogStoreMaintenance
{
    private readonly LogStoreOptions _options;

    public LogStoreMaintenance(LogStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Clone().Validate();
    }

    /// <summary>
    /// Convierte todos los segmentos de un día a un único fichero Parquet.
    /// </summary>
    /// <param name="day">Día a archivar (UTC).</param>
    /// <param name="deleteSegments">Si es cierto, borra los segmentos una vez escrito el Parquet.</param>
    /// <param name="cancellationToken">Token de cancelación.</param>
    /// <remarks>
    /// <para>
    /// El Parquet se escribe primero en un fichero temporal y solo al final se renombra, así que
    /// un corte a mitad no deja un archivo a medias ni borra segmentos que aún no estaban salvados.
    /// </para>
    /// <para>
    /// Si el día que se archiva es el que se está escribiendo ahora mismo, su segmento activo
    /// está abierto y no se puede borrar: el Parquet se crea igual y el segmento se queda,
    /// lo que se refleja en <see cref="ArchiveResult.SegmentsKept"/>. Archiva días ya cerrados
    /// (o llama antes a <c>SealAsync</c>) para que el borrado sea limpio.
    /// </para>
    /// </remarks>
    public async Task<ArchiveResult> ArchiveDayAsync(DateOnly day, bool deleteSegments = true, CancellationToken cancellationToken = default)
    {
        var directory = SegmentPaths.DayDirectory(_options.RootPath, day);
        var segments = SegmentPaths.SegmentsIn(directory);
        if (segments.Count == 0) return new ArchiveResult(day, 0, 0, 0, null, 0, 0);

        long segmentBytes = 0;
        foreach (var segment in segments) segmentBytes += new FileInfo(segment).Length;

        var target = ParquetArchive.FileFor(_options.ArchivePath, day);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = target + ".tmp";

        var query = new LogQuery
        {
            From = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            To = new DateTimeOffset(day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)),
            IncludeArchive = false,
        };

        var engine = new QueryEngine(_options);
        long rows = 0;

        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            await using (var writer = await ParquetArchiveWriter.CreateAsync(stream, compressionLevel: _options.CompressionLevel, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                await foreach (var record in engine.QueryAsync(query, cancellationToken).ConfigureAwait(false))
                {
                    await writer.AddAsync(record, cancellationToken).ConfigureAwait(false);
                    rows++;
                }

                await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        var archiveBytes = new FileInfo(target).Length;

        var deleted = 0;
        if (deleteSegments)
        {
            foreach (var segment in segments)
            {
                if (TryDelete(segment)) deleted++;
            }
            RemoveEmptyDirectories(directory);
        }

        return new ArchiveResult(day, rows, segmentBytes, archiveBytes, target, deleted, segments.Count - deleted);
    }

    /// <summary>Archiva todos los días con segmentos anteriores a <paramref name="firstDayToKeep"/>.</summary>
    public async Task<ArchiveSummary> ArchiveOlderThanAsync(
        DateOnly firstDayToKeep,
        bool deleteSegments = true,
        IProgress<ArchiveResult>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var days = SegmentPaths.EnumerateDays(_options.RootPath, to: firstDayToKeep.AddDays(-1));

        var count = 0;
        long records = 0;
        long segmentBytes = 0;
        long archiveBytes = 0;

        foreach (var (day, _) in days)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = await ArchiveDayAsync(day, deleteSegments, cancellationToken).ConfigureAwait(false);
            if (result.ArchiveFile is null) continue;

            count++;
            records += result.RecordCount;
            segmentBytes += result.SegmentBytes;
            archiveBytes += result.ArchiveBytes;
            progress?.Report(result);
        }

        return new ArchiveSummary(count, records, segmentBytes, archiveBytes);
    }

    /// <summary>Borra los segmentos de los días anteriores a <paramref name="firstDayToKeep"/>. No toca el archivo Parquet.</summary>
    public PruneResult PruneSegmentsOlderThan(DateOnly firstDayToKeep)
    {
        var files = 0;
        long bytes = 0;

        foreach (var (_, directory) in SegmentPaths.EnumerateDays(_options.RootPath, to: firstDayToKeep.AddDays(-1)))
        {
            foreach (var segment in SegmentPaths.SegmentsIn(directory))
            {
                bytes += SafeLength(segment);
                if (TryDelete(segment)) files++;
            }
            RemoveEmptyDirectories(directory);
        }

        return new PruneResult(files, bytes);
    }

    /// <summary>Borra los Parquet de los días anteriores a <paramref name="firstDayToKeep"/>. Es el borrado definitivo.</summary>
    public PruneResult PruneArchiveOlderThan(DateOnly firstDayToKeep)
    {
        var files = 0;
        long bytes = 0;

        foreach (var (_, file) in ParquetArchive.EnumerateDays(_options.ArchivePath, to: firstDayToKeep.AddDays(-1)))
        {
            bytes += SafeLength(file);
            if (TryDelete(file)) files++;
            RemoveEmptyDirectories(Path.GetDirectoryName(file)!);
        }

        return new PruneResult(files, bytes);
    }

    /// <summary>Recalcula el manifiesto recorriendo el almacén y lo guarda en <c>manifest.idx</c>.</summary>
    public async Task<StoreManifest> ReindexAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await IndexManager.RebuildAsync(_options, cancellationToken).ConfigureAwait(false);
        IndexManager.Save(_options, manifest);
        return manifest;
    }

    /// <summary>Carga el manifiesto guardado, o lo reconstruye si no existe o está corrupto.</summary>
    public async Task<StoreManifest> GetManifestAsync(bool rebuildIfMissing = true, CancellationToken cancellationToken = default)
    {
        var manifest = IndexManager.TryLoad(_options);
        if (manifest is not null) return manifest;
        if (!rebuildIfMissing) return new StoreManifest { UpdatedTicks = DateTime.UtcNow.Ticks };
        return await ReindexAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Comprueba la integridad de los segmentos: firma de cada bloque, huella xxHash3 de los
    /// bytes comprimidos y que el bloque se pueda descomprimir y decodificar entero.
    /// </summary>
    public VerifyReport Verify(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        var segments = 0;
        var blocks = 0;
        long records = 0;

        using var decoder = new BlockDecoder();
        var codecs = new Dictionary<CompressionCodec, IBlockCodec>();

        try
        {
            foreach (var (_, directory) in SegmentPaths.EnumerateDays(_options.RootPath, from, to))
            {
                foreach (var path in SegmentPaths.SegmentsIn(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    segments++;

                    SegmentFileReader? reader;
                    try
                    {
                        reader = SegmentFileReader.TryOpen(path);
                    }
                    catch (Exception ex)
                    {
                        problems.Add($"{path}: no se puede abrir ({ex.Message})");
                        continue;
                    }

                    if (reader is null)
                    {
                        problems.Add($"{path}: fichero demasiado corto para ser un segmento");
                        continue;
                    }

                    using (reader)
                    {
                        if (!codecs.TryGetValue(reader.Header.Codec, out var codec))
                        {
                            codec = BlockCodecs.Create(reader.Header.Codec, 1);
                            codecs[reader.Header.Codec] = codec;
                        }

                        if (reader.Header.BlockCount != reader.Blocks.Count)
                        {
                            problems.Add($"{path}: la cabecera dice {reader.Header.BlockCount} bloques y solo hay {reader.Blocks.Count} íntegros");
                        }

                        foreach (var block in reader.Blocks)
                        {
                            blocks++;
                            records += block.RecordCount;
                            var problem = VerifyBlock(reader, block, codec, decoder);
                            if (problem is not null) problems.Add($"{path} @{block.HeaderOffset}: {problem}");
                        }
                    }
                }
            }
        }
        finally
        {
            foreach (var codec in codecs.Values) codec.Dispose();
        }

        return new VerifyReport(segments, blocks, records, problems);
    }

    private static string? VerifyBlock(SegmentFileReader reader, in BlockDescriptor block, IBlockCodec codec, BlockDecoder decoder)
    {
        var compressed = reader.BlockPayload(block);
        if (XxHash3.HashToUInt64(compressed) != block.Hash) return "la huella no coincide (bytes corruptos)";

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(block.RawLength);
        try
        {
            var length = codec.Decompress(compressed, buffer);
            if (length != block.RawLength) return $"al descomprimir salen {length} bytes y se esperaban {block.RawLength}";

            var span = buffer.AsSpan(0, length);
            decoder.ParseHeader(span);
            if (decoder.RecordCount != block.RecordCount) return $"el bloque declara {block.RecordCount} registros y contiene {decoder.RecordCount}";
            decoder.ParseColumns(span);
            return null;
        }
        catch (Exception ex)
        {
            return $"no se puede decodificar ({ex.Message})";
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Borra la carpeta y sus padres mientras vayan quedando vacíos.</summary>
    private static void RemoveEmptyDirectories(string directory)
    {
        var current = directory;
        for (var depth = 0; depth < 3; depth++)
        {
            try
            {
                if (!Directory.Exists(current)) return;
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                var parent = Path.GetDirectoryName(current);
                Directory.Delete(current);
                if (parent is null) return;
                current = parent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}

/// <summary>Resultado de archivar un día.</summary>
/// <param name="Day">Día archivado.</param>
/// <param name="RecordCount">Registros escritos en el Parquet.</param>
/// <param name="SegmentBytes">Bytes que ocupaban los segmentos.</param>
/// <param name="ArchiveBytes">Bytes que ocupa el Parquet.</param>
/// <param name="ArchiveFile">Ruta del Parquet, o null si el día no tenía segmentos.</param>
/// <param name="SegmentsDeleted">Segmentos borrados tras archivar.</param>
/// <param name="SegmentsKept">Segmentos que no se pudieron borrar (normalmente el que sigue abierto).</param>
public readonly record struct ArchiveResult(
    DateOnly Day,
    long RecordCount,
    long SegmentBytes,
    long ArchiveBytes,
    string? ArchiveFile,
    int SegmentsDeleted,
    int SegmentsKept)
{
    /// <summary>
    /// Cuánto cambia de tamaño el día al pasar a Parquet: mayor que 1 significa que ocupa
    /// menos, menor que 1 que ocupa más.
    /// </summary>
    /// <remarks>
    /// Es normal que salga por debajo de 1. El formato de segmento aplica diccionario de
    /// cadenas por bloque y deltas de timestamp, y ahí gana casi siempre. Se archiva a Parquet
    /// para que cualquier herramienta pueda leer los datos dentro de cinco años, no para ahorrar disco.
    /// </remarks>
    public double SizeRatio => ArchiveBytes == 0 ? 0 : (double)SegmentBytes / ArchiveBytes;

    /// <summary>Indica si el Parquet ha salido más grande que los segmentos originales.</summary>
    public bool GrewOnDisk => ArchiveBytes > SegmentBytes;
}

/// <summary>Resultado agregado de archivar varios días.</summary>
public readonly record struct ArchiveSummary(int Days, long RecordCount, long SegmentBytes, long ArchiveBytes);

/// <summary>Resultado de un borrado por retención.</summary>
public readonly record struct PruneResult(int Files, long BytesFreed);

/// <summary>Resultado de una verificación de integridad.</summary>
/// <param name="Segments">Segmentos revisados.</param>
/// <param name="Blocks">Bloques revisados.</param>
/// <param name="Records">Registros contabilizados.</param>
/// <param name="Problems">Problemas encontrados, vacío si todo está bien.</param>
public sealed record VerifyReport(int Segments, int Blocks, long Records, IReadOnlyList<string> Problems)
{
    /// <summary>Indica si no se encontró ningún problema.</summary>
    public bool Ok => Problems.Count == 0;
}
