using System.Text;
using LogStorage.Compression;
using Xunit;

namespace LogStorage.Tests;

public class CompressionTests
{
    [Theory]
    [InlineData(CompressionCodec.None)]
    [InlineData(CompressionCodec.Zstd)]
    public void IdaYVuelta_DevuelveLosMismosBytes(CompressionCodec id)
    {
        using var codec = BlockCodecs.Create(id, 3);
        var source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Usuario creado|", 5000)));

        var compressed = new byte[codec.MaxCompressedLength(source.Length)];
        var compressedLength = codec.Compress(source, compressed);

        var restored = new byte[source.Length];
        var restoredLength = codec.Decompress(compressed.AsSpan(0, compressedLength), restored);

        Assert.Equal(source.Length, restoredLength);
        Assert.Equal(source, restored);
    }

    [Fact]
    public void Zstd_ComprimeMuchoElTextoRepetido()
    {
        using var codec = BlockCodecs.Create(CompressionCodec.Zstd, 3);
        var source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Usuario creado|", 10_000)));

        var compressed = new byte[codec.MaxCompressedLength(source.Length)];
        var compressedLength = codec.Compress(source, compressed);

        Assert.True(compressedLength * 50 < source.Length, $"esperaba al menos 50x, salió {(double)source.Length / compressedLength:F1}x");
    }

    [Fact]
    public void Vacio_IdaYVuelta()
    {
        using var codec = BlockCodecs.Create(CompressionCodec.Zstd, 3);
        var compressed = new byte[codec.MaxCompressedLength(0)];

        var compressedLength = codec.Compress([], compressed);
        var restoredLength = codec.Decompress(compressed.AsSpan(0, compressedLength), []);

        Assert.Equal(0, restoredLength);
    }

    [Fact]
    public void CodecDesconocido_Lanza()
        => Assert.Throws<NotSupportedException>(() => BlockCodecs.Create((CompressionCodec)200, 3));

    [Fact]
    public void Passthrough_NoCambiaElTamano()
    {
        using var codec = BlockCodecs.Create(CompressionCodec.None, 3);
        Assert.Equal(1234, codec.MaxCompressedLength(1234));
    }
}
