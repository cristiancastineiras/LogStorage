using System.Globalization;

namespace LogStorage.Cli;

/// <summary>Comandos de administración del almacén: listar, inspeccionar, verificar, archivar y limpiar.</summary>
public sealed class StoreCommands
{
    /// <summary>Lista los días del almacén con sus registros y su tamaño.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="refresh">Recalcular el manifiesto antes de listar.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Ls(string root = "logs", bool refresh = false, CancellationToken cancellationToken = default)
    {
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });
        var manifest = refresh
            ? await maintenance.ReindexAsync(cancellationToken)
            : await maintenance.GetManifestAsync(cancellationToken: cancellationToken);

        if (manifest.Days.Count == 0)
        {
            Console.WriteLine(Output.Warn($"No hay datos en '{Path.GetFullPath(root)}'."));
            return;
        }

        var rows = new List<string[]>(manifest.Days.Count);
        foreach (var day in manifest.Days)
        {
            rows.Add([
                day.Date,
                day.Archived && day.SegmentCount == 0 ? "parquet" : "segmentos",
                Output.Number(day.RecordCount),
                day.SegmentCount.ToString(CultureInfo.InvariantCulture),
                day.BlockCount.ToString(CultureInfo.InvariantCulture),
                Output.Bytes(day.SegmentBytes + day.ArchiveBytes),
                day.RecordCount == 0 ? "-" : ((double)(day.SegmentBytes + day.ArchiveBytes) / day.RecordCount).ToString("F2", CultureInfo.InvariantCulture),
            ]);
        }

        Output.Table(["DÍA", "DÓNDE", "REGISTROS", "SEG", "BLOQ", "TAMAÑO", "B/REG"], rows);
        Console.WriteLine();
        Console.WriteLine(Output.Dim($"{manifest.Days.Count} días · {Output.Number(manifest.TotalRecords)} registros · {Output.Bytes(manifest.TotalBytes)}"));
    }

    /// <summary>Muestra el detalle de un fichero de segmento: cabecera y bloques.</summary>
    /// <param name="file">-f, Ruta del fichero .lsz.</param>
    /// <param name="blocks">Mostrar la tabla de bloques.</param>
    public void Info(string file, bool blocks = false)
    {
        var info = SegmentInspector.Inspect(file);
        if (info is null)
        {
            Console.Error.WriteLine(Output.Bad($"'{file}' no existe o es demasiado corto para ser un segmento."));
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine(Output.Title(info.Path));
        Console.WriteLine($"  Formato       v{info.FormatVersion}, {info.Codec}");
        Console.WriteLine($"  Estado        {(info.Sealed ? Output.Ok("cerrado") : Output.Warn("abierto"))}");
        Console.WriteLine($"  Creado        {info.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        Console.WriteLine($"  Bloques       {info.Blocks.Count}" + (info.IsTruncated ? Output.Warn($"  (la cabecera declara {info.DeclaredBlockCount}: fichero cortado)") : string.Empty));
        Console.WriteLine($"  Registros     {Output.Number(info.RecordCount)}");
        if (info.MinUtc is { } min && info.MaxUtc is { } max)
        {
            Console.WriteLine($"  Rango         {min:yyyy-MM-dd HH:mm:ss.fff} .. {max:HH:mm:ss.fff} UTC");
        }
        Console.WriteLine($"  Tamaño        {Output.Bytes(info.FileBytes)} en disco, {Output.Bytes(info.RawBytes)} sin comprimir");
        if (info.CompressedBytes > 0) Console.WriteLine($"  Compresión    {Output.Accent($"x{info.CompressionRatio:F1}")}");
        if (info.RecordCount > 0) Console.WriteLine($"  Por registro  {info.BytesPerRecord:F2} bytes");

        if (!blocks || info.Blocks.Count == 0) return;

        Console.WriteLine();
        var rows = new List<string[]>(info.Blocks.Count);
        foreach (var block in info.Blocks)
        {
            rows.Add([
                block.Index.ToString(CultureInfo.InvariantCulture),
                block.Offset.ToString(CultureInfo.InvariantCulture),
                Output.Number(block.RecordCount),
                Output.Bytes(block.RawBytes),
                Output.Bytes(block.CompressedBytes),
                $"x{block.CompressionRatio:F1}",
                block.MinUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                block.MaxUtc.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            ]);
        }
        Output.Table(["#", "OFFSET", "REGISTROS", "CRUDO", "COMPRIMIDO", "RATIO", "DESDE", "HASTA"], rows);
    }

    /// <summary>Comprueba la integridad de todos los segmentos (huella y decodificación de cada bloque).</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="from">-f, Comprobar solo desde este día.</param>
    /// <param name="to">-t, Comprobar solo hasta este día.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public void Verify(string root = "logs", string? from = null, string? to = null, CancellationToken cancellationToken = default)
    {
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });
        var report = maintenance.Verify(TimeArg.ParseDay(from, "from"), TimeArg.ParseDay(to, "to"), cancellationToken);

        Console.WriteLine($"Revisados {report.Segments} segmentos, {report.Blocks} bloques, {Output.Number(report.Records)} registros.");

        if (report.Ok)
        {
            Console.WriteLine(Output.Ok("Todo correcto."));
            return;
        }

        foreach (var problem in report.Problems) Console.Error.WriteLine(Output.Bad("  " + problem));
        Console.Error.WriteLine(Output.Bad($"{report.Problems.Count} problemas encontrados."));
        Environment.ExitCode = 1;
    }

    /// <summary>Reconstruye el manifiesto manifest.idx recorriendo el almacén.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Reindex(string root = "logs", CancellationToken cancellationToken = default)
    {
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });

        var started = TimeProvider.System.GetTimestamp();
        var manifest = await maintenance.ReindexAsync(cancellationToken);
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        Console.WriteLine(Output.Ok(
            $"Manifiesto reconstruido: {manifest.Days.Count} días, {Output.Number(manifest.TotalRecords)} registros, {Output.Bytes(manifest.TotalBytes)} en {elapsed.TotalMilliseconds:N0} ms."));
    }

    /// <summary>Convierte a Parquet los días anteriores a una fecha y borra sus segmentos.</summary>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="before">-b, Archivar los días anteriores a esta fecha (por defecto, hoy).</param>
    /// <param name="keepSegments">No borrar los segmentos después de archivar.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Archive(string root = "logs", string? before = null, bool keepSegments = false, CancellationToken cancellationToken = default)
    {
        var cutoff = TimeArg.ParseDay(before, "before") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });

        var progress = new SyncProgress<ArchiveResult>(result =>
        {
            var change = result.GrewOnDisk
                ? Output.Warn($"ocupa x{1 / result.SizeRatio:F1} más")
                : Output.Accent($"ocupa x{result.SizeRatio:F1} menos");

            Console.WriteLine($"  {result.Day:yyyy-MM-dd}  {Output.Number(result.RecordCount),12} registros  {Output.Bytes(result.SegmentBytes)} -> {Output.Bytes(result.ArchiveBytes)}  {change}");
            if (result.SegmentsKept > 0) Console.WriteLine(Output.Warn($"    {result.SegmentsKept} segmentos no se pudieron borrar (¿abiertos por otro proceso?)"));
        });

        Console.WriteLine(Output.Title($"Archivando días anteriores a {cutoff:yyyy-MM-dd}"));
        var summary = await maintenance.ArchiveOlderThanAsync(cutoff, !keepSegments, progress, cancellationToken);

        if (summary.Days == 0)
        {
            Console.WriteLine(Output.Dim("No había nada que archivar."));
            return;
        }

        Console.WriteLine(Output.Ok(
            $"{summary.Days} días archivados, {Output.Number(summary.RecordCount)} registros, {Output.Bytes(summary.SegmentBytes)} -> {Output.Bytes(summary.ArchiveBytes)}."));

        if (summary.ArchiveBytes > summary.SegmentBytes)
        {
            Console.WriteLine(Output.Dim(
                "  Nota: el Parquet ocupa más que los segmentos. Es lo normal: se archiva para que"));
            Console.WriteLine(Output.Dim(
                "  cualquier herramienta (DuckDB, pandas, Spark) pueda leerlo, no para ahorrar disco."));
        }
    }

    /// <summary>Borra datos antiguos. Es irreversible.</summary>
    /// <param name="before">-b, Borrar lo anterior a esta fecha.</param>
    /// <param name="root">-r, Carpeta raíz del almacén.</param>
    /// <param name="segments">Borrar segmentos.</param>
    /// <param name="archive">Borrar también los Parquet archivados.</param>
    /// <param name="yes">-y, No preguntar.</param>
    public void Prune(string before, string root = "logs", bool segments = true, bool archive = false, bool yes = false)
    {
        var cutoff = TimeArg.ParseDay(before, "before") ?? throw new ArgumentException("--before es obligatorio.");
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = root, ReadOnly = true });

        if (!yes)
        {
            var what = archive ? "segmentos y Parquet archivados" : "segmentos";
            Console.Write(Output.Warn($"Se borrarán los {what} anteriores a {cutoff:yyyy-MM-dd}. Esto no se puede deshacer. ¿Seguir? [s/N] "));
            var answer = Console.ReadLine();
            if (answer is not ("s" or "S" or "si" or "sí" or "y" or "Y"))
            {
                Console.WriteLine(Output.Dim("Cancelado."));
                return;
            }
        }

        long freed = 0;
        var files = 0;

        if (segments)
        {
            var result = maintenance.PruneSegmentsOlderThan(cutoff);
            files += result.Files;
            freed += result.BytesFreed;
        }

        if (archive)
        {
            var result = maintenance.PruneArchiveOlderThan(cutoff);
            files += result.Files;
            freed += result.BytesFreed;
        }

        Console.WriteLine(Output.Ok($"Borrados {files} ficheros, {Output.Bytes(freed)} liberados."));
    }
}
