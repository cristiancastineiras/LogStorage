using System.Text;
using LogStorage.Compression;
using LogStorage.Format;
using LogStorage.Storage;
using Xunit;

namespace LogStorage.Tests;

public class SegmentFileTests
{
    [Fact]
    public void EscribirYLeer_DevuelveLosMismosBloques()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "2026", "08", "20", "segment-00001.lsz");

        using (var writer = new SegmentFileWriter(path, CompressionCodec.None))
        {
            writer.WriteBlock("bloque-uno"u8, 10, 3, 100, 200);
            writer.WriteBlock("bloque-dos"u8, 10, 5, 300, 400);
            writer.Seal();
        }

        using var reader = SegmentFileReader.TryOpen(path);
        Assert.NotNull(reader);
        Assert.True(reader.Header.IsSealed);
        Assert.Equal(2, reader.Blocks.Count);
        Assert.Equal(8, reader.RecordCount);
        Assert.Equal(100, reader.MinTicks);
        Assert.Equal(400, reader.MaxTicks);
        Assert.Equal("bloque-uno", Encoding.UTF8.GetString(reader.BlockPayload(reader.Blocks[0])));
        Assert.Equal("bloque-dos", Encoding.UTF8.GetString(reader.BlockPayload(reader.Blocks[1])));
    }

    [Fact]
    public void SinSellar_LaCabeceraYaEstaAlDiaYSeLeeIgual()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "segment-00001.lsz");

        using (var writer = new SegmentFileWriter(path, CompressionCodec.None))
        {
            writer.WriteBlock("datos"u8, 5, 2, 10, 20);
            writer.Flush();

            // Sin cerrar el escritor: es justo lo que veríamos si el proceso muriese aquí.
            using var reader = SegmentFileReader.TryOpen(path);
            Assert.NotNull(reader);
            Assert.False(reader.Header.IsSealed);
            Assert.Single(reader.Blocks);
            Assert.Equal(2, reader.RecordCount);
        }
    }

    [Fact]
    public void ConColaTruncada_SeLeeHastaElUltimoBloqueIntegro()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "segment-00001.lsz");

        using (var writer = new SegmentFileWriter(path, CompressionCodec.None))
        {
            writer.WriteBlock("primero"u8, 7, 1, 10, 20);
            writer.WriteBlock("segundo"u8, 7, 1, 30, 40);
            writer.Seal();
        }

        // Simulamos un apagón a mitad de escribir el segundo bloque.
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(file.Length - 4);
        }

        using var reader = SegmentFileReader.TryOpen(path);
        Assert.NotNull(reader);
        Assert.Single(reader.Blocks);
        Assert.Equal("primero", Encoding.UTF8.GetString(reader.BlockPayload(reader.Blocks[0])));
    }

    [Fact]
    public void FicheroDemasiadoCorto_DevuelveNull()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "corto.lsz");
        File.WriteAllBytes(path, new byte[10]);

        Assert.Null(SegmentFileReader.TryOpen(path));
    }

    [Fact]
    public void FicheroInexistente_DevuelveNull()
        => Assert.Null(SegmentFileReader.TryOpen(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".lsz")));

    [Fact]
    public void SegmentoVacio_TieneCabeceraValidaYCeroBloques()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "segment-00001.lsz");

        using (var writer = new SegmentFileWriter(path, CompressionCodec.Zstd))
        {
            writer.Seal();
        }

        using var reader = SegmentFileReader.TryOpen(path);
        Assert.NotNull(reader);
        Assert.Empty(reader.Blocks);
        Assert.Equal(0, reader.RecordCount);
        Assert.Equal(CompressionCodec.Zstd, reader.Header.Codec);
        Assert.Equal(SegmentFormat.HeaderSize, new FileInfo(path).Length);
    }

    [Fact]
    public void SePuedeLeerMientrasSeEscribe()
    {
        using var temp = new TempStore();
        var path = Path.Combine(temp.Root, "segment-00001.lsz");

        using var writer = new SegmentFileWriter(path, CompressionCodec.None);
        writer.WriteBlock("uno"u8, 3, 1, 10, 20);
        writer.Flush();

        using (var reader = SegmentFileReader.TryOpen(path))
        {
            Assert.NotNull(reader);
            Assert.Single(reader.Blocks);
        }

        writer.WriteBlock("dos"u8, 3, 1, 30, 40);
        writer.Flush();

        using var second = SegmentFileReader.TryOpen(path);
        Assert.NotNull(second);
        Assert.Equal(2, second.Blocks.Count);
    }
}
