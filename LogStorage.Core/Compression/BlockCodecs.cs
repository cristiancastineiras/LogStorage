namespace LogStorage.Compression;

/// <summary>Fábrica de códecs de bloque.</summary>
internal static class BlockCodecs
{
    public static IBlockCodec Create(CompressionCodec codec, int level) => codec switch
    {
        CompressionCodec.None => new PassthroughCodec(),
        CompressionCodec.Zstd => new ZstdBlockCodec(level),
        _ => throw new NotSupportedException($"Códec de compresión desconocido: {(byte)codec}. ¿Fichero de una versión más nueva?"),
    };
}

/// <summary>Copia tal cual. Sirve para medir el coste real de la compresión y para tests deterministas.</summary>
internal sealed class PassthroughCodec : IBlockCodec
{
    public CompressionCodec Id => CompressionCodec.None;

    public int MaxCompressedLength(int sourceLength) => sourceLength;

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        source.CopyTo(destination);
        return source.Length;
    }

    public void Dispose() { }
}
