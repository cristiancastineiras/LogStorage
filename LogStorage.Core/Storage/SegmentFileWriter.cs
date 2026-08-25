using System.IO.Hashing;
using LogStorage.Compression;
using LogStorage.Format;

namespace LogStorage.Storage;

/// <summary>
/// Escribe un único fichero de segmento: cabecera, bloques y nada más.
/// </summary>
/// <remarks>
/// Tras cada bloque se reescribe la cabecera (64 bytes). Cuesta un microsegundo cada varios
/// megabytes y a cambio el fichero siempre está consultable, incluso si el proceso muere
/// de golpe: no hay footer que reconstruir ni ficheros a medio cerrar.
/// </remarks>
internal sealed class SegmentFileWriter : IDisposable
{
    private readonly FileStream _file;
    private readonly byte[] _headerBuffer = new byte[SegmentFormat.HeaderSize];
    private SegmentHeader _header;
    private long _length;

    public SegmentFileWriter(string path, CompressionCodec codec)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

        // BufferSize 0 = sin búfer intermedio: ya escribimos en bloques de megabytes.
        _file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 0,
            Options = FileOptions.None,
        });

        _header = SegmentHeader.CreateNew(codec);
        _length = SegmentFormat.HeaderSize;
        WriteHeader();
    }

    public string Path { get; }

    /// <summary>Bytes ocupados en disco.</summary>
    public long Length => _length;

    public SegmentHeader Header => _header;

    public int BlockCount => _header.BlockCount;

    public long RecordCount => _header.RecordCount;

    /// <summary>Añade un bloque ya comprimido y actualiza la cabecera.</summary>
    public BlockDescriptor WriteBlock(ReadOnlySpan<byte> compressed, int rawLength, int recordCount, long minTicks, long maxTicks)
    {
        var descriptor = new BlockDescriptor(
            _length,
            compressed.Length,
            rawLength,
            recordCount,
            minTicks,
            maxTicks,
            XxHash3.HashToUInt64(compressed));

        Span<byte> blockHeader = stackalloc byte[SegmentFormat.BlockHeaderSize];
        descriptor.WriteHeader(blockHeader);

        _file.Seek(_length, SeekOrigin.Begin);
        _file.Write(blockHeader);
        _file.Write(compressed);
        _length += SegmentFormat.BlockHeaderSize + compressed.Length;

        _header.BlockCount++;
        _header.RecordCount += recordCount;
        if (minTicks < _header.MinTicks) _header.MinTicks = minTicks;
        if (maxTicks > _header.MaxTicks) _header.MaxTicks = maxTicks;
        WriteHeader();

        return descriptor;
    }

    /// <summary>Marca el segmento como cerrado. A partir de aquí ya no se le añaden bloques.</summary>
    public void Seal()
    {
        if (_header.IsSealed) return;
        _header.Flags |= SegmentFormat.FlagSealed;
        WriteHeader();
        _file.Flush(flushToDisk: true);
    }

    /// <summary>Fuerza el volcado al sistema de ficheros (y opcionalmente al plato).</summary>
    public void Flush(bool toDisk = false) => _file.Flush(toDisk);

    private void WriteHeader()
    {
        _header.WriteTo(_headerBuffer);
        _file.Seek(0, SeekOrigin.Begin);
        _file.Write(_headerBuffer);
    }

    public void Dispose() => _file.Dispose();
}
