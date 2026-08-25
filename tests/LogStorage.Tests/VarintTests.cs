using LogStorage.Buffers;
using Xunit;

namespace LogStorage.Tests;

public class VarintTests
{
    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(300UL)]
    [InlineData(16383UL)]
    [InlineData(16384UL)]
    [InlineData(uint.MaxValue)]
    [InlineData(long.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void IdaYVuelta_DevuelveElMismoValor(ulong value)
    {
        Span<byte> buffer = stackalloc byte[Varint.MaxSize];

        var written = Varint.Write(buffer, value);
        var read = Varint.Read(buffer, out var consumed);

        Assert.Equal(value, read);
        Assert.Equal(written, consumed);
        Assert.Equal(written, Varint.SizeOf(value));
    }

    [Theory]
    [InlineData(0UL, 1)]
    [InlineData(127UL, 1)]
    [InlineData(128UL, 2)]
    [InlineData(16383UL, 2)]
    [InlineData(16384UL, 3)]
    [InlineData(ulong.MaxValue, 10)]
    public void SizeOf_CoincideConLosBytesEscritos(ulong value, int expectedSize)
        => Assert.Equal(expectedSize, Varint.SizeOf(value));

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(-1L)]
    [InlineData(63L)]
    [InlineData(-64L)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void ZigZag_IdaYVuelta(long value)
        => Assert.Equal(value, Varint.UnZigZag(Varint.ZigZag(value)));

    [Fact]
    public void ZigZag_MantienePequenosLosValoresCercanosACero()
    {
        // Es justo lo que hace pequeños los deltas de timestamp.
        Assert.Equal(1, Varint.SizeOf(Varint.ZigZag(1)));
        Assert.Equal(1, Varint.SizeOf(Varint.ZigZag(-1)));
        Assert.Equal(1, Varint.SizeOf(Varint.ZigZag(63)));
    }

    [Fact]
    public void ReadAndAdvance_ConsumeLaSecuenciaEnOrden()
    {
        var buffer = new byte[64];
        var offset = 0;
        ulong[] values = [1, 300, 70000, 5];
        foreach (var value in values) offset += Varint.Write(buffer.AsSpan(offset), value);

        ReadOnlySpan<byte> cursor = buffer.AsSpan(0, offset);
        foreach (var expected in values) Assert.Equal(expected, Varint.ReadAndAdvance(ref cursor));

        Assert.True(cursor.IsEmpty);
    }

    [Fact]
    public void Read_LanzaSiElBufferEstaTruncado()
    {
        var buffer = new byte[] { 0x80 }; // dice "sigo" pero no hay más bytes
        Assert.Throws<InvalidDataException>(() => Varint.Read(buffer, out _));
    }
}
