using ZstdSharp;

namespace LogStorage.Compression;

/// <summary>
/// Zstandard sobre <c>ZstdSharp</c> (port gestionado: sin dependencias nativas ni ficheros extra que desplegar).
/// Reutiliza los contextos de zstd entre bloques, que es de donde sale la mayor parte del rendimiento.
/// </summary>
internal sealed class ZstdBlockCodec : IBlockCodec
{
    private readonly Compressor _compressor;
    private readonly Decompressor _decompressor;

    public ZstdBlockCodec(int level)
    {
        _compressor = new Compressor(level);
        _decompressor = new Decompressor();
    }

    public CompressionCodec Id => CompressionCodec.Zstd;

    public int MaxCompressedLength(int sourceLength) => Compressor.GetCompressBound(sourceLength);

    public int Compress(ReadOnlySpan<byte> source, Span<byte> destination) => _compressor.Wrap(source, destination);

    public int Decompress(ReadOnlySpan<byte> source, Span<byte> destination) => _decompressor.Unwrap(source, destination);

    public void Dispose()
    {
        _compressor.Dispose();
        _decompressor.Dispose();
    }
}
