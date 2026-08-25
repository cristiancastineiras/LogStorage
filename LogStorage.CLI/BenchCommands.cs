using LogStorage.Compression;

namespace LogStorage.Cli;

/// <summary>Comando de banco de pruebas: mide ingesta, tamaño en disco y velocidad de búsqueda.</summary>
public sealed class BenchCommands
{
    private static readonly string[] Templates =
    [
        "Usuario creado",
        "Pedido creado",
        "Pedido enviado",
        "Pago aceptado",
        "Pago rechazado",
        "Sesión iniciada",
        "Sesión cerrada",
        "Consulta a base de datos terminada",
        "Petición HTTP completada",
        "Reintento programado",
    ];

    private static readonly string[] Services = ["api", "ventas", "pagos", "auth", "batch"];

    /// <summary>Genera carga sintética y mide escritura, tamaño y lectura.</summary>
    /// <param name="root">-r, Carpeta donde se escribe el banco de pruebas.</param>
    /// <param name="count">-n, Registros a escribir.</param>
    /// <param name="threads">-t, Hilos escribiendo a la vez.</param>
    /// <param name="blockSizeKb">Tamaño de bloque sin comprimir, en KB.</param>
    /// <param name="level">-l, Nivel de compresión de zstd (1-22).</param>
    /// <param name="codec">Códec: zstd o none.</param>
    /// <param name="keep">No borrar la carpeta al terminar.</param>
    /// <param name="cancellationToken">Cancelación.</param>
    public async Task Bench(
        string root = "bench-logs",
        int count = 1_000_000,
        int threads = 4,
        int blockSizeKb = 4096,
        int level = 3,
        string codec = "zstd",
        bool keep = false,
        CancellationToken cancellationToken = default)
    {
        var full = Path.GetFullPath(root);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);

        var options = new LogStoreOptions
        {
            RootPath = full,
            BlockSizeBytes = Math.Max(4096, blockSizeKb * 1024),
            CompressionLevel = level,
            Codec = codec.Equals("none", StringComparison.OrdinalIgnoreCase) ? CompressionCodec.None : CompressionCodec.Zstd,
            AutoFlushInterval = TimeSpan.Zero,
        };

        Console.WriteLine(Output.Title("Banco de pruebas de LogStorage"));
        Console.WriteLine(Output.Dim($"  {Output.Number(count)} registros · {threads} hilos · bloques de {blockSizeKb} KB · {options.Codec} nivel {level}"));
        Console.WriteLine();

        var perThread = count / threads;
        var total = perThread * threads;
        var baseTicks = new DateTimeOffset(DateTime.UtcNow.Date.AddHours(9)).UtcTicks;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var collectionsBefore = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        var started = TimeProvider.System.GetTimestamp();

        await using (var store = LogStore.Open(options))
        {
            await Task.WhenAll(Enumerable.Range(0, threads).Select(thread => Task.Run(() =>
            {
                var random = new Random(thread * 7919);
                var service = Services[thread % Services.Length];
                var host = $"host-{thread:D2}";

                for (var i = 0; i < perThread; i++)
                {
                    var entry = new LogEntry(
                        baseTicks + (long)i * TimeSpan.TicksPerMillisecond,
                        (LogLevel)(random.Next(100) switch { < 60 => 2, < 85 => 1, < 95 => 3, < 99 => 4, _ => 5 }),
                        Templates[random.Next(Templates.Length)],
                        service,
                        host,
                        i % 4 == 0 ? "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" : null);

                    store.Write(entry);
                }
            }, cancellationToken)));

            await store.FlushAsync(cancellationToken);

            var writeElapsed = TimeProvider.System.GetElapsedTime(started);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
            var collections = (GC.CollectionCount(0) - collectionsBefore.Item1, GC.CollectionCount(1) - collectionsBefore.Item2, GC.CollectionCount(2) - collectionsBefore.Item3);
            var stats = store.Stats;

            Console.WriteLine(Output.Title("Escritura"));
            Console.WriteLine($"  Tiempo            {writeElapsed.TotalMilliseconds:N0} ms");
            Console.WriteLine($"  Ritmo             {Output.Accent($"{total / writeElapsed.TotalSeconds:N0} registros/s")}");
            Console.WriteLine($"  En disco          {Output.Bytes(stats.BytesWritten)}  ({stats.BytesPerRecord:F2} bytes por registro)");
            Console.WriteLine($"  Bloques           {stats.BlocksWritten}");
            Console.WriteLine($"  Memoria asignada  {Output.Bytes(allocated)}  ({(double)allocated / total:F1} bytes por registro)");
            Console.WriteLine($"  Recolecciones GC  gen0={collections.Item1} gen1={collections.Item2} gen2={collections.Item3}");
            Console.WriteLine();
        }

        var onDisk = Directory.GetFiles(full, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

        await using (var reader = LogStore.OpenReadOnly(full))
        {
            Console.WriteLine(Output.Title("Lectura"));
            await Measure("recorrer todo", reader, new LogQuery(), cancellationToken);
            await Measure("solo errores", reader, new LogQuery { MinLevel = LogLevel.Error }, cancellationToken);
            await Measure("un servicio", reader, new LogQuery { Service = Services[0] }, cancellationToken);
            await Measure("contiene 'Pago'", reader, new LogQuery { Contains = "Pago" }, cancellationToken);
            await Measure("ventana de 1 min", reader, LogQuery.Between(new DateTimeOffset(baseTicks, TimeSpan.Zero), new DateTimeOffset(baseTicks, TimeSpan.Zero).AddMinutes(1)), cancellationToken);
            await Measure("últimos 20", reader, new LogQuery { Descending = true, Limit = 20 }, cancellationToken);
        }

        Console.WriteLine();
        Console.WriteLine(Output.Title("Resultado"));
        Console.WriteLine($"  Carpeta completa  {Output.Bytes(onDisk)} para {Output.Number(total)} registros");

        if (!keep)
        {
            Directory.Delete(full, recursive: true);
            Console.WriteLine(Output.Dim($"  Carpeta '{full}' borrada (usa --keep para conservarla)"));
        }
    }

    private static async Task Measure(string name, ILogStore store, LogQuery query, CancellationToken cancellationToken)
    {
        var started = TimeProvider.System.GetTimestamp();
        long matched = 0;
        await foreach (var _ in store.QueryAsync(query, cancellationToken)) matched++;
        var elapsed = TimeProvider.System.GetElapsedTime(started);

        var rate = elapsed.TotalSeconds > 0 ? $"{matched / elapsed.TotalSeconds:N0} reg/s" : "-";
        Console.WriteLine($"  {name,-20} {elapsed.TotalMilliseconds,8:N1} ms   {Output.Number(matched),12} resultados   {Output.Dim(rate)}");
    }
}
