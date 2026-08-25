using BenchmarkDotNet.Attributes;
using LogStorage.Compression;

namespace LogStorage.Benchmarks;

/// <summary>Ingesta de extremo a extremo: del <c>Write</c> hasta el fichero en disco.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class IngestBenchmarks
{
    private const int Records = 200_000;

    private static readonly string[] Templates =
    [
        "Usuario creado", "Pedido creado", "Pedido enviado", "Pago aceptado", "Pago rechazado",
        "Sesión iniciada", "Sesión cerrada", "Consulta terminada", "Petición completada", "Reintento programado",
    ];

    private LogEntry[] _entries = [];
    private string _root = string.Empty;

    /// <summary>Códec de compresión de bloque.</summary>
    [Params(CompressionCodec.Zstd, CompressionCodec.None)]
    public CompressionCodec Codec { get; set; }

    /// <summary>Tamaño de bloque sin comprimir, en MiB.</summary>
    [Params(1, 4)]
    public int BlockMiB { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(4321);
        var baseTicks = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc).Ticks;

        _entries = new LogEntry[Records];
        for (var i = 0; i < Records; i++)
        {
            _entries[i] = new LogEntry(
                baseTicks + (long)i * TimeSpan.TicksPerMillisecond,
                (LogLevel)random.Next(0, 6),
                Templates[random.Next(Templates.Length)],
                "api",
                "host-01");
        }

        _root = Path.Combine(Path.GetTempPath(), "logstorage-bench", Guid.NewGuid().ToString("N"));
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        var parent = Path.GetDirectoryName(_root);
        if (parent is not null && Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
    }

    /// <summary>Escribe 200.000 registros desde un hilo y espera a tenerlos en disco.</summary>
    [Benchmark(OperationsPerInvoke = Records)]
    public async Task<long> WriteAndFlush()
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));

        await using var store = LogStore.Open(new LogStoreOptions
        {
            RootPath = directory,
            Codec = Codec,
            BlockSizeBytes = BlockMiB * 1024 * 1024,
            AutoFlushInterval = TimeSpan.Zero,
        });

        var entries = _entries;
        for (var i = 0; i < entries.Length; i++) store.Write(entries[i]);

        await store.FlushAsync();
        return store.Stats.BytesWritten;
    }
}

/// <summary>Consultas sobre un almacén ya escrito, con y sin poda por diccionario.</summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class QueryBenchmarks
{
    private const int Records = 500_000;

    private static readonly string[] Templates =
    [
        "Usuario creado", "Pedido creado", "Pedido enviado", "Pago aceptado", "Pago rechazado",
        "Sesión iniciada", "Sesión cerrada", "Consulta terminada", "Petición completada", "Reintento programado",
    ];

    private static readonly string[] Services = ["api", "ventas", "pagos", "auth"];

    private string _root = string.Empty;
    private LogStore _store = null!;
    private DateTimeOffset _start;

    [GlobalSetup]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), "logstorage-bench-query", Guid.NewGuid().ToString("N"));
        _start = new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

        var random = new Random(99);
        await using (var writer = LogStore.Open(new LogStoreOptions { RootPath = _root, AutoFlushInterval = TimeSpan.Zero }))
        {
            for (var i = 0; i < Records; i++)
            {
                writer.Write(new LogEntry(
                    _start.UtcTicks + (long)i * TimeSpan.TicksPerMillisecond,
                    (LogLevel)random.Next(0, 6),
                    Templates[random.Next(Templates.Length)],
                    Services[random.Next(Services.Length)],
                    "host-01"));
            }
            await writer.FlushAsync();
        }

        _store = LogStore.OpenReadOnly(_root);
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        await _store.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Recorrer los 500.000 registros sin filtrar.</summary>
    [Benchmark(Baseline = true)]
    public Task<long> FullScan() => Count(new LogQuery());

    /// <summary>Filtrar por nivel: se decodifican todos los bloques pero se descartan registros.</summary>
    [Benchmark]
    public Task<long> ByLevel() => Count(new LogQuery { MinLevel = LogLevel.Error });

    /// <summary>Filtrar por servicio: se resuelve con un entero contra el diccionario del bloque.</summary>
    [Benchmark]
    public Task<long> ByService() => Count(new LogQuery { Service = "pagos" });

    /// <summary>Buscar texto: la máscara se calcula una vez por bloque, no por registro.</summary>
    [Benchmark]
    public Task<long> ByContains() => Count(new LogQuery { Contains = "Pago" });

    /// <summary>Buscar texto que no existe: los bloques se descartan sin decodificar columnas.</summary>
    [Benchmark]
    public Task<long> ByContainsMiss() => Count(new LogQuery { Contains = "esto-no-existe-en-ningun-sitio" });

    /// <summary>Ventana de un minuto: la mayoría de bloques ni se descomprimen.</summary>
    [Benchmark]
    public Task<long> ByTimeWindow() => Count(LogQuery.Between(_start.AddMinutes(30), _start.AddMinutes(31)));

    /// <summary>Los 20 últimos: se lee el bloque final y se para.</summary>
    [Benchmark]
    public Task<long> LastTwenty() => Count(new LogQuery { Descending = true, Limit = 20 });

    private async Task<long> Count(LogQuery query)
    {
        long total = 0;
        await foreach (var _ in _store.QueryAsync(query)) total++;
        return total;
    }
}
