namespace LogStorage.Compression;

/// <summary>
/// Compresor de bloque. <b>No es thread-safe</b> a propósito: cada escritor y cada lector
/// crea el suyo y así se evitan locks y estados compartidos en el camino caliente.
/// </summary>
internal interface IBlockCodec : IDisposable
{
    CompressionCodec Id { get; }

    /// <summary>Cota superior del tamaño comprimido para un origen de <paramref name="sourceLength"/> bytes.</summary>
    int MaxCompressedLength(int sourceLength);

    /// <summary>Comprime y devuelve los bytes escritos en <paramref name="destination"/>.</summary>
    int Compress(ReadOnlySpan<byte> source, Span<byte> destination);

    /// <summary>Descomprime y devuelve los bytes escritos en <paramref name="destination"/>.</summary>
    int Decompress(ReadOnlySpan<byte> source, Span<byte> destination);
}
