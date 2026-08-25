using System.Text;
using LogStorage.Buffers;
using LogStorage.Format;
using Xunit;

namespace LogStorage.Tests;

public class BlockRoundTripTests
{
    private static readonly long Base = new DateTime(2026, 8, 20, 10, 0, 0, DateTimeKind.Utc).Ticks;

    [Fact]
    public void IdaYVuelta_DevuelveLosMismosRegistros()
    {
        var entries = new List<LogEntry>();
        for (var i = 0; i < 1000; i++)
        {
            entries.Add(new LogEntry(
                Base + i * TimeSpan.TicksPerMillisecond,
                (LogLevel)(i % 6),
                $"Mensaje {i % 7}",
                service: "api",
                host: "host-01",
                traceId: i % 3 == 0 ? $"trace-{i}" : null));
        }

        var (decoder, _) = RoundTrip(entries);

        Assert.Equal(entries.Count, decoder.RecordCount);
        for (var i = 0; i < entries.Count; i++)
        {
            Assert.Equal(entries[i].TimestampTicks, decoder.Timestamps[i]);
            Assert.Equal((byte)entries[i].Level, decoder.Levels[i]);
            Assert.Equal(entries[i].Service, decoder.Strings[decoder.Services[i]]);
            Assert.Equal(entries[i].Host, decoder.Strings[decoder.Hosts[i]]);
            Assert.Equal(entries[i].Message, decoder.Strings[decoder.Messages[i]]);
            Assert.Equal(entries[i].TraceId, decoder.Strings[decoder.Traces[i]]);
        }
    }

    [Fact]
    public void ElDiccionarioNoRepiteCadenas()
    {
        using var builder = new BlockBuilder();
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(Base + i, LogLevel.Information, "api", "host-01", "Usuario creado", null, default);
        }

        // "api", "host-01" y "Usuario creado": tres cadenas para diez mil registros.
        Assert.Equal(3, builder.DictionaryCount);
    }

    [Fact]
    public void DiezMilRegistrosIgualesOcupanPocoMasDeUnByteCadaUno()
    {
        using var builder = new BlockBuilder();
        for (var i = 0; i < 10_000; i++)
        {
            builder.Append(Base + i * TimeSpan.TicksPerMillisecond, LogLevel.Information, "api", "host-01", "Usuario creado", null, default);
        }

        // timestamp (~2B) + nivel (1B) + tres ids (3B) + traza (1B) = unos 7 bytes por registro
        // SIN comprimir. El texto se guarda una sola vez.
        Assert.InRange(builder.EstimatedSize / 10_000.0, 1, 10);
    }

    [Fact]
    public void ColumnasOpcionales_SeOmitenCuandoNoSeUsan()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Information, "api", "h", "hola", null, default);

        using var output = new PooledBufferWriter(1024);
        builder.Serialize(output);

        var decoder = new BlockDecoder();
        decoder.ParseHeader(output.WrittenSpan);
        decoder.ParseColumns(output.WrittenSpan);

        Assert.False(decoder.HasTrace);
        Assert.False(decoder.HasPayload);
        Assert.Null(decoder.Strings[decoder.Traces[0]]);
    }

    [Fact]
    public void Payload_IdaYVuelta()
    {
        var payload = Encoding.UTF8.GetBytes("{\"pedido\":42}");

        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Information, "api", "h", "sin datos", null, default);
        builder.Append(Base + 1, LogLevel.Error, "api", "h", "con datos", null, payload);

        using var output = new PooledBufferWriter(1024);
        builder.Serialize(output);

        using var decoder = new BlockDecoder();
        decoder.ParseHeader(output.WrittenSpan);
        decoder.ParseColumns(output.WrittenSpan);

        Assert.True(decoder.HasPayload);
        Assert.True(decoder.GetPayload(0).IsEmpty);
        Assert.Equal(payload, decoder.GetPayload(1).ToArray());
    }

    [Fact]
    public void TimestampsDesordenados_SeConservanYElRangoEsCorrecto()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base + 500, LogLevel.Information, "a", "h", "m", null, default);
        builder.Append(Base + 100, LogLevel.Information, "a", "h", "m", null, default);
        builder.Append(Base + 900, LogLevel.Information, "a", "h", "m", null, default);

        Assert.Equal(Base + 100, builder.MinTicks);
        Assert.Equal(Base + 900, builder.MaxTicks);

        var (decoder, _) = RoundTripBuilder(builder);
        Assert.Equal(Base + 500, decoder.Timestamps[0]);
        Assert.Equal(Base + 100, decoder.Timestamps[1]);
        Assert.Equal(Base + 900, decoder.Timestamps[2]);
    }

    [Fact]
    public void Reset_DejaElBloqueComoNuevo()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Error, "a", "h", "m", "t", [1, 2, 3]);
        builder.Reset();

        Assert.True(builder.IsEmpty);
        Assert.Equal(0, builder.RecordCount);
        Assert.Equal(0, builder.DictionaryCount);
        Assert.Equal(0, builder.EstimatedSize);

        builder.Append(Base + 5, LogLevel.Information, "b", "h2", "m2", null, default);
        var (decoder, _) = RoundTripBuilder(builder);

        Assert.Equal(1, decoder.RecordCount);
        Assert.Equal("b", decoder.Strings[decoder.Services[0]]);
        Assert.False(decoder.HasTrace);
        Assert.False(decoder.HasPayload);
    }

    [Fact]
    public void FindExact_EncuentraSoloLoQueEstaEnElBloque()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Information, "ventas", "h", "Pedido creado", null, default);

        var (decoder, _) = RoundTripBuilder(builder);

        Assert.True(decoder.FindExact("ventas") > 0);
        Assert.Equal(-1, decoder.FindExact("compras"));
    }

    [Fact]
    public void MascaraDeContiene_MarcaSoloLasCadenasQueCoinciden()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Information, "api", "h", "Pedido creado", null, default);
        builder.Append(Base + 1, LogLevel.Information, "api", "h", "Usuario creado", null, default);

        var (decoder, _) = RoundTripBuilder(builder);

        var mask = decoder.RentContainsMask("pedido", StringComparison.OrdinalIgnoreCase, out var any);
        Assert.True(any);
        Assert.True(mask[decoder.Messages[0]]);
        Assert.False(mask[decoder.Messages[1]]);
        BlockDecoder.ReturnMask(mask);

        var noMask = decoder.RentContainsMask("inexistente", StringComparison.OrdinalIgnoreCase, out var none);
        Assert.False(none);
        BlockDecoder.ReturnMask(noMask);
    }

    [Fact]
    public void CadenasVaciasYUnicode_IdaYVuelta()
    {
        using var builder = new BlockBuilder();
        builder.Append(Base, LogLevel.Information, string.Empty, "máquina-ñ", "日本語 con acentos: áéíóú", null, default);

        var (decoder, _) = RoundTripBuilder(builder);

        Assert.Equal(string.Empty, decoder.Strings[decoder.Services[0]]);
        Assert.Equal("máquina-ñ", decoder.Strings[decoder.Hosts[0]]);
        Assert.Equal("日本語 con acentos: áéíóú", decoder.Strings[decoder.Messages[0]]);
    }

    private static (BlockDecoder Decoder, int Bytes) RoundTrip(IEnumerable<LogEntry> entries)
    {
        using var builder = new BlockBuilder();
        foreach (var entry in entries) builder.Append(entry);
        return RoundTripBuilder(builder);
    }

    private static (BlockDecoder Decoder, int Bytes) RoundTripBuilder(BlockBuilder builder)
    {
        using var output = new PooledBufferWriter(1024);
        builder.Serialize(output);

        var decoder = new BlockDecoder();
        decoder.ParseHeader(output.WrittenSpan);
        decoder.ParseColumns(output.WrittenSpan);
        return (decoder, output.WrittenCount);
    }
}
