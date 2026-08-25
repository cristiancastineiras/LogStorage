using BenchmarkDotNet.Attributes;
using LogStorage;
using LogStorage.Buffers;
using LogStorage.Compression;
using LogStorage.Format;

namespace LogStorage.Benchmarks;

/// <summary>
/// El camino caliente puro: codificar registros en columnas, serializar, comprimir y decodificar.
/// Aquí no hay disco ni hilos, solo CPU y memoria.
/// </summary>
[MemoryDiagnoser]
[HideColumns("Error", "StdDev", "Median", "RatioSD")]
public class BlockBenchmarks
{
    private static readonly string[] Templates =
    [
        "Usuario creado", "Pedido creado", "Pedido enviado", "Pago aceptado", "Pago rechazado",
        "Sesión iniciada", "Sesión cerrada", "Consulta terminada", "Petición completada", "Reintento programado",
    ];

    private LogEntry[] _entries = [];
    private BlockBuilder _builder = null!;
    private PooledBufferWriter _serialized = null!;
    private BlockDecoder _decoder = null!;
    private IBlockCodec _codec = null!;
    private byte[] _compressed = [];
    private int _compressedLength;
    private int _rawLength;

    /// <summary>Registros por bloque.</summary>
    [Params(100_000)]
    public int Records { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1234);
        var baseTicks = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc).Ticks;

        _entries = new LogEntry[Records];
        for (var i = 0; i < Records; i++)
        {
            _entries[i] = new LogEntry(
                baseTicks + (long)i * TimeSpan.TicksPerMillisecond,
                (LogLevel)random.Next(0, 6),
                Templates[random.Next(Templates.Length)],
                "api",
                "host-01",
                i % 4 == 0 ? "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01" : null);
        }

        _builder = new BlockBuilder(1 << 20);
        _serialized = new PooledBufferWriter(1 << 22);
        _decoder = new BlockDecoder();
        _codec = BlockCodecs.Create(CompressionCodec.Zstd, 3);

        // Estado de partida para los benchmarks de serializar/comprimir/decodificar.
        foreach (var entry in _entries) _builder.Append(entry);
        _builder.Serialize(_serialized);
        _rawLength = _serialized.WrittenCount;

        _compressed = new byte[_codec.MaxCompressedLength(_rawLength)];
        _compressedLength = _codec.Compress(_serialized.WrittenSpan, _compressed);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _builder.Dispose();
        _serialized.Dispose();
        _decoder.Dispose();
        _codec.Dispose();
    }

    /// <summary>Codificar los registros en columnas. Es lo que paga el hilo que loguea.</summary>
    [Benchmark(Baseline = true, OperationsPerInvoke = 100_000)]
    public int Append()
    {
        _builder.Reset();
        var entries = _entries;
        for (var i = 0; i < entries.Length; i++) _builder.Append(entries[i]);
        return _builder.RecordCount;
    }

    /// <summary>Volcar las columnas al búfer del bloque. Va en el hilo de fondo.</summary>
    [Benchmark(OperationsPerInvoke = 100_000)]
    public int Serialize()
    {
        _serialized.Reset();
        _builder.Serialize(_serialized);
        return _serialized.WrittenCount;
    }

    /// <summary>Comprimir el bloque con zstd. Va en el hilo de fondo.</summary>
    [Benchmark(OperationsPerInvoke = 100_000)]
    public int Compress() => _codec.Compress(_serialized.WrittenSpan.Length > 0 ? _serialized.WrittenSpan : default, _compressed);

    /// <summary>Descomprimir el bloque al consultarlo.</summary>
    [Benchmark(OperationsPerInvoke = 100_000)]
    public int Decompress()
    {
        var destination = new byte[_rawLength];
        return _codec.Decompress(_compressed.AsSpan(0, _compressedLength), destination);
    }

    /// <summary>Decodificar el diccionario y todas las columnas del bloque.</summary>
    [Benchmark(OperationsPerInvoke = 100_000)]
    public int Decode()
    {
        var raw = _serialized.WrittenSpan;
        _decoder.ParseHeader(raw);
        _decoder.ParseColumns(raw);
        return _decoder.RecordCount;
    }
}
