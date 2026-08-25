using System.Globalization;
using LogStorage.Archiving;
using MemoryPack;

namespace LogStorage.Storage;

/// <summary>Construye, guarda y carga el <see cref="StoreManifest"/>.</summary>
internal static class IndexManager
{
    public const string FileName = "manifest.idx";

    public static string PathFor(LogStoreOptions options) => Path.Combine(options.RootPath, FileName);

    /// <summary>
    /// Recorre el almacén y recalcula el manifiesto desde cero.
    /// Los números salen de recorrer las cabeceras de bloque, no de la cabecera del segmento,
    /// así que un fichero truncado por un apagón se contabiliza por lo que de verdad se puede leer.
    /// </summary>
    public static async Task<StoreManifest> RebuildAsync(LogStoreOptions options, CancellationToken cancellationToken = default)
    {
        var manifest = new StoreManifest { UpdatedTicks = DateTime.UtcNow.Ticks };
        var byDay = new SortedDictionary<DateOnly, DaySummary>();

        foreach (var (day, directory) in SegmentPaths.EnumerateDays(options.RootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = GetOrCreate(byDay, day);
            foreach (var path in SegmentPaths.SegmentsIn(directory))
            {
                using var reader = SegmentFileReader.TryOpen(path);
                if (reader is null) continue;

                summary.SegmentCount++;
                summary.BlockCount += reader.Blocks.Count;
                summary.RecordCount += reader.RecordCount;
                summary.SegmentBytes += reader.Length;

                if (reader.Blocks.Count == 0) continue;
                var min = reader.MinTicks;
                var max = reader.MaxTicks;
                if (summary.MinTicks == 0 || min < summary.MinTicks) summary.MinTicks = min;
                if (max > summary.MaxTicks) summary.MaxTicks = max;
            }
        }

        foreach (var (day, file) in ParquetArchive.EnumerateDays(options.ArchivePath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var summary = GetOrCreate(byDay, day);
            summary.Archived = true;
            summary.ArchiveBytes = new FileInfo(file).Length;

            // Si el día ya no tiene segmentos, el recuento bueno es el del Parquet.
            if (summary.SegmentCount == 0)
            {
                summary.RecordCount = await ParquetArchiveReader.CountRowsAsync(file, cancellationToken).ConfigureAwait(false);
                summary.MinTicks = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks;
                summary.MaxTicks = day.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).Ticks - 1;
            }
        }

        manifest.Days.AddRange(byDay.Values);
        return manifest;
    }

    public static void Save(LogStoreOptions options, StoreManifest manifest)
    {
        Directory.CreateDirectory(options.RootPath);
        var path = PathFor(options);
        var temp = path + ".tmp";

        File.WriteAllBytes(temp, MemoryPackSerializer.Serialize(manifest));
        File.Move(temp, path, overwrite: true);
    }

    public static StoreManifest? TryLoad(LogStoreOptions options)
    {
        var path = PathFor(options);
        if (!File.Exists(path)) return null;

        try
        {
            return MemoryPackSerializer.Deserialize<StoreManifest>(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is MemoryPackSerializationException or IOException)
        {
            return null; // manifiesto corrupto o de otra versión: se reconstruye y punto
        }
    }

    private static DaySummary GetOrCreate(SortedDictionary<DateOnly, DaySummary> map, DateOnly day)
    {
        if (map.TryGetValue(day, out var summary)) return summary;

        summary = new DaySummary { Date = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        map[day] = summary;
        return summary;
    }
}
