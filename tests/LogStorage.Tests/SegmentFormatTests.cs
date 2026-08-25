using LogStorage.Compression;
using LogStorage.Format;
using Xunit;

namespace LogStorage.Tests;

public class SegmentFormatTests
{
    [Fact]
    public void CabeceraDeSegmento_IdaYVuelta()
    {
        var header = new SegmentHeader
        {
            Version = SegmentFormat.Version,
            Codec = CompressionCodec.Zstd,
            Flags = SegmentFormat.FlagSealed,
            BlockCount = 42,
            MinTicks = 1000,
            MaxTicks = 2000,
            RecordCount = 123456,
            CreatedTicks = 999,
        };

        Span<byte> buffer = stackalloc byte[SegmentFormat.HeaderSize];
        header.WriteTo(buffer);
        var read = SegmentHeader.ReadFrom(buffer);

        Assert.Equal(header.Version, read.Version);
        Assert.Equal(header.Codec, read.Codec);
        Assert.Equal(header.BlockCount, read.BlockCount);
        Assert.Equal(header.MinTicks, read.MinTicks);
        Assert.Equal(header.MaxTicks, read.MaxTicks);
        Assert.Equal(header.RecordCount, read.RecordCount);
        Assert.Equal(header.CreatedTicks, read.CreatedTicks);
        Assert.True(read.IsSealed);
    }

    [Fact]
    public void CabeceraDeSegmento_ConFirmaMala_Lanza()
    {
        var buffer = new byte[SegmentFormat.HeaderSize];
        Assert.Throws<InvalidDataException>(() => SegmentHeader.ReadFrom(buffer));
    }

    [Fact]
    public void CabeceraDeSegmento_DeVersionFutura_Lanza()
    {
        var header = SegmentHeader.CreateNew(CompressionCodec.Zstd);
        header.Version = SegmentFormat.Version + 1;

        var buffer = new byte[SegmentFormat.HeaderSize];
        header.WriteTo(buffer);

        var error = Assert.Throws<InvalidDataException>(() => SegmentHeader.ReadFrom(buffer));
        Assert.Contains("versión", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescriptorDeBloque_IdaYVuelta()
    {
        var descriptor = new BlockDescriptor(64, 1234, 5678, 99, 100, 200, 0xDEADBEEFCAFEF00D);

        Span<byte> buffer = stackalloc byte[SegmentFormat.BlockHeaderSize];
        descriptor.WriteHeader(buffer);

        Assert.True(BlockDescriptor.TryReadHeader(buffer, 64, out var read));
        Assert.Equal(descriptor.CompressedLength, read.CompressedLength);
        Assert.Equal(descriptor.RawLength, read.RawLength);
        Assert.Equal(descriptor.RecordCount, read.RecordCount);
        Assert.Equal(descriptor.MinTicks, read.MinTicks);
        Assert.Equal(descriptor.MaxTicks, read.MaxTicks);
        Assert.Equal(descriptor.Hash, read.Hash);
        Assert.Equal(64 + SegmentFormat.BlockHeaderSize, read.PayloadOffset);
        Assert.Equal(SegmentFormat.BlockHeaderSize + 1234, read.TotalLength);
    }

    [Fact]
    public void DescriptorDeBloque_SinFirma_DevuelveFalse()
    {
        var buffer = new byte[SegmentFormat.BlockHeaderSize];
        Assert.False(BlockDescriptor.TryReadHeader(buffer, 0, out _));
    }

    [Fact]
    public void DescriptorDeBloque_Truncado_DevuelveFalse()
    {
        var descriptor = new BlockDescriptor(64, 10, 10, 1, 0, 0, 0);
        var buffer = new byte[SegmentFormat.BlockHeaderSize];
        descriptor.WriteHeader(buffer);

        Assert.False(BlockDescriptor.TryReadHeader(buffer.AsSpan(0, 20), 64, out _));
    }
}
