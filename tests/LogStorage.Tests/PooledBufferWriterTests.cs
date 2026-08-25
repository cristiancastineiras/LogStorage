using LogStorage.Buffers;
using Xunit;

namespace LogStorage.Tests;

public class PooledBufferWriterTests
{
    [Fact]
    public void Write_ConservaTodoLoEscritoAlCrecer()
    {
        using var writer = new PooledBufferWriter(16);
        var expected = new List<byte>();

        for (var i = 0; i < 5000; i++)
        {
            var value = (byte)(i % 251);
            writer.WriteByte(value);
            expected.Add(value);
        }

        Assert.Equal(expected.Count, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.SequenceEqual(expected.ToArray()));
    }

    [Fact]
    public void Write_DeSpanGrande_CreceDeGolpe()
    {
        using var writer = new PooledBufferWriter(16);
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);

        writer.Write(payload);

        Assert.Equal(payload.Length, writer.WrittenCount);
        Assert.True(writer.WrittenSpan.SequenceEqual(payload));
    }

    [Fact]
    public void Reset_VaciaPeroConservaLaCapacidad()
    {
        using var writer = new PooledBufferWriter(16);
        writer.Write(new byte[10_000]);
        var capacity = writer.Capacity;

        writer.Reset();

        Assert.Equal(0, writer.WrittenCount);
        Assert.Equal(capacity, writer.Capacity);
    }

    [Fact]
    public void GetSpan_MasAdvance_EquivaleAEscribirDirecto()
    {
        using var writer = new PooledBufferWriter(16);

        var span = writer.GetSpan(4);
        span[0] = 1;
        span[1] = 2;
        span[2] = 3;
        span[3] = 4;
        writer.Advance(4);

        Assert.Equal([1, 2, 3, 4], writer.WrittenSpan.ToArray());
    }

    [Fact]
    public void Advance_MasAllaDeLaCapacidad_Lanza()
    {
        using var writer = new PooledBufferWriter(16);
        writer.GetSpan(4);

        Assert.Throws<InvalidOperationException>(() => writer.Advance(writer.Capacity + 1));
    }

    [Fact]
    public void ReserveYPatch_RellenanElHuecoReservado()
    {
        using var writer = new PooledBufferWriter(32);
        var slot = writer.Reserve(4);
        writer.WriteByte(0xAB);

        writer.PatchInt32(slot, 0x01020304);

        var written = writer.WrittenSpan.ToArray();
        Assert.Equal([0x04, 0x03, 0x02, 0x01, 0xAB], written);
    }

    [Fact]
    public void Enteros_SeEscribenEnLittleEndian()
    {
        using var writer = new PooledBufferWriter(32);
        writer.WriteInt32(1);
        writer.WriteInt64(1);

        var written = writer.WrittenSpan.ToArray();
        Assert.Equal((byte)1, written[0]);
        Assert.Equal((byte)1, written[4]);
        Assert.Equal(12, written.Length);
    }
}
