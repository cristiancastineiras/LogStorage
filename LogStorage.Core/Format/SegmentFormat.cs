using System.Buffers.Binary;
using LogStorage.Compression;

namespace LogStorage.Format;

/// <summary>
/// Constantes y disposición en disco del formato de segmento (<c>.lsz</c>).
/// </summary>
/// <remarks>
/// <para>Un segmento es: <c>[cabecera 64B][bloque][bloque]…</c> y nada más. No hay footer.</para>
/// <para>
/// Cada bloque es autónomo (lleva su propio diccionario de cadenas), así que:
/// se puede leer cualquier bloque sin tocar los demás, se pueden leer en paralelo y,
/// si el proceso muere a mitad, lo escrito hasta el último bloque sigue siendo válido.
/// </para>
/// <para>
/// La cabecera se reescribe (64 bytes) después de cada bloque, de modo que el fichero
/// siempre está consultable aunque nunca se llegue a cerrar limpiamente.
/// </para>
/// </remarks>
internal static class SegmentFormat
{
    /// <summary>Firma del fichero, 8 bytes.</summary>
    public static ReadOnlySpan<byte> Magic => "LSSEGv1\0"u8;

    public const ushort Version = 1;

    /// <summary>Tamaño fijo de la cabecera del segmento.</summary>
    public const int HeaderSize = 64;

    /// <summary>Firma de bloque: 'S','B','L','K'.</summary>
    public const uint BlockMagic = 0x4B4C4253;

    /// <summary>Tamaño fijo de la cabecera de bloque.</summary>
    public const int BlockHeaderSize = 40;

    /// <summary>Extensión de los ficheros de segmento.</summary>
    public const string SegmentExtension = ".lsz";

    /// <summary>Extensión de los ficheros de índice de día.</summary>
    public const string IndexFileName = "day.idx";

    // ---- flags de cabecera de segmento ----
    public const byte FlagSealed = 0x01;

    // ---- flags de payload de bloque ----
    public const byte BlockFlagHasTrace = 0x01;
    public const byte BlockFlagHasPayload = 0x02;
}

/// <summary>Cabecera de un segmento. Se serializa en <see cref="SegmentFormat.HeaderSize"/> bytes.</summary>
internal struct SegmentHeader
{
    public ushort Version;
    public CompressionCodec Codec;
    public byte Flags;
    public int BlockCount;
    public long MinTicks;
    public long MaxTicks;
    public long RecordCount;
    public long CreatedTicks;

    public readonly bool IsSealed => (Flags & SegmentFormat.FlagSealed) != 0;

    public static SegmentHeader CreateNew(CompressionCodec codec) => new()
    {
        Version = SegmentFormat.Version,
        Codec = codec,
        Flags = 0,
        BlockCount = 0,
        MinTicks = long.MaxValue,
        MaxTicks = long.MinValue,
        RecordCount = 0,
        CreatedTicks = DateTime.UtcNow.Ticks,
    };

    public readonly void WriteTo(Span<byte> destination)
    {
        if (destination.Length < SegmentFormat.HeaderSize) throw new ArgumentException("Búfer de cabecera insuficiente.", nameof(destination));
        destination[..SegmentFormat.HeaderSize].Clear();
        SegmentFormat.Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], Version);
        destination[10] = (byte)Codec;
        destination[11] = Flags;
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], BlockCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], MinTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], MaxTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[32..], RecordCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[40..], CreatedTicks);
        // 48..63 reservado para futuras versiones (queda a cero).
    }

    public static SegmentHeader ReadFrom(ReadOnlySpan<byte> source)
    {
        if (source.Length < SegmentFormat.HeaderSize) throw new InvalidDataException("Segmento truncado: no cabe ni la cabecera.");
        if (!source[..8].SequenceEqual(SegmentFormat.Magic)) throw new InvalidDataException("Firma de segmento no reconocida.");

        var header = new SegmentHeader
        {
            Version = BinaryPrimitives.ReadUInt16LittleEndian(source[8..]),
            Codec = (CompressionCodec)source[10],
            Flags = source[11],
            BlockCount = BinaryPrimitives.ReadInt32LittleEndian(source[12..]),
            MinTicks = BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            MaxTicks = BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            RecordCount = BinaryPrimitives.ReadInt64LittleEndian(source[32..]),
            CreatedTicks = BinaryPrimitives.ReadInt64LittleEndian(source[40..]),
        };

        if (header.Version > SegmentFormat.Version)
            throw new InvalidDataException($"El segmento usa la versión de formato {header.Version} y esta build entiende hasta la {SegmentFormat.Version}.");

        return header;
    }
}

/// <summary>Descriptor de un bloque dentro de un segmento. Se obtiene leyendo su cabecera.</summary>
internal readonly struct BlockDescriptor
{
    /// <summary>Posición de la cabecera del bloque dentro del fichero.</summary>
    public readonly long HeaderOffset;

    public readonly int CompressedLength;
    public readonly int RawLength;
    public readonly int RecordCount;
    public readonly long MinTicks;
    public readonly long MaxTicks;
    public readonly ulong Hash;

    public BlockDescriptor(long headerOffset, int compressedLength, int rawLength, int recordCount, long minTicks, long maxTicks, ulong hash)
    {
        HeaderOffset = headerOffset;
        CompressedLength = compressedLength;
        RawLength = rawLength;
        RecordCount = recordCount;
        MinTicks = minTicks;
        MaxTicks = maxTicks;
        Hash = hash;
    }

    /// <summary>Posición de los bytes comprimidos.</summary>
    public long PayloadOffset => HeaderOffset + SegmentFormat.BlockHeaderSize;

    /// <summary>Bytes totales que ocupa el bloque (cabecera + datos).</summary>
    public long TotalLength => SegmentFormat.BlockHeaderSize + (long)CompressedLength;

    public void WriteHeader(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, SegmentFormat.BlockMagic);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], CompressedLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], RawLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], RecordCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[16..], MinTicks);
        BinaryPrimitives.WriteInt64LittleEndian(destination[24..], MaxTicks);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[32..], Hash);
    }

    /// <summary>Intenta leer una cabecera de bloque. Devuelve <c>false</c> si está incompleta o corrupta (final del fichero tras un corte).</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, long headerOffset, out BlockDescriptor descriptor)
    {
        descriptor = default;
        if (source.Length < SegmentFormat.BlockHeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != SegmentFormat.BlockMagic) return false;

        var compressed = BinaryPrimitives.ReadInt32LittleEndian(source[4..]);
        var raw = BinaryPrimitives.ReadInt32LittleEndian(source[8..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(source[12..]);
        if (compressed < 0 || raw < 0 || count < 0) return false;

        descriptor = new BlockDescriptor(
            headerOffset,
            compressed,
            raw,
            count,
            BinaryPrimitives.ReadInt64LittleEndian(source[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[24..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[32..]));
        return true;
    }
}
