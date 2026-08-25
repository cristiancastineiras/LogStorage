using Xunit;

namespace LogStorage.Tests;

public class LogLevelsTests
{
    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("WARNING", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("fatal", LogLevel.Critical)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("4", LogLevel.Error)]
    public void TryParse_AceptaLasFormasHabituales(string text, LogLevel expected)
    {
        Assert.True(LogLevels.TryParse(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("")]
    [InlineData("noexiste")]
    [InlineData("99")]
    [InlineData("una cadena larguísima que no cabe")]
    public void TryParse_RechazaLoQueNoEsUnNivel(string text)
        => Assert.False(LogLevels.TryParse(text, out _));

    [Fact]
    public void ToShortName_EsEstableYSinAsignar()
    {
        Assert.Equal("INFO", LogLevel.Information.ToShortName());
        Assert.Equal("WARN", LogLevel.Warning.ToShortName());
        Assert.Equal("FATAL", LogLevel.Critical.ToShortName());
        Assert.Same(LogLevel.Error.ToShortName(), LogLevel.Error.ToShortName());
    }

    [Fact]
    public void LosValoresCoincidenConMicrosoftExtensionsLogging()
    {
        Assert.Equal(0, (int)LogLevel.Trace);
        Assert.Equal(2, (int)LogLevel.Information);
        Assert.Equal(5, (int)LogLevel.Critical);
        Assert.Equal(6, (int)LogLevel.None);
    }
}

public class LogQueryTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static LogRecord Record(DateTimeOffset at, LogLevel level = LogLevel.Information, string service = "api", string message = "Pedido creado", string? traceId = null)
        => new(at.UtcTicks, level, service, "host-01", message, traceId, default);

    [Fact]
    public void SinFiltros_TodoCoincide()
        => Assert.True(new LogQuery().Matches(Record(Noon)));

    [Fact]
    public void Rango_EsInclusivoAbajoYExclusivoArriba()
    {
        var query = LogQuery.Between(Noon, Noon.AddHours(1));

        Assert.True(query.Matches(Record(Noon)));
        Assert.True(query.Matches(Record(Noon.AddMinutes(59))));
        Assert.False(query.Matches(Record(Noon.AddHours(1))));
        Assert.False(query.Matches(Record(Noon.AddSeconds(-1))));
    }

    [Fact]
    public void MinLevel_DejaPasarLoIgualOMasGrave()
    {
        var query = new LogQuery { MinLevel = LogLevel.Warning };

        Assert.False(query.Matches(Record(Noon, LogLevel.Information)));
        Assert.True(query.Matches(Record(Noon, LogLevel.Warning)));
        Assert.True(query.Matches(Record(Noon, LogLevel.Critical)));
    }

    [Fact]
    public void Contains_IgnoraMayusculasSalvoQueSePidaLoContrario()
    {
        Assert.True(new LogQuery { Contains = "PEDIDO" }.Matches(Record(Noon)));
        Assert.False(new LogQuery { Contains = "PEDIDO", CaseSensitive = true }.Matches(Record(Noon)));
        Assert.True(new LogQuery { Contains = "Pedido", CaseSensitive = true }.Matches(Record(Noon)));
    }

    [Fact]
    public void Overlaps_DetectaSolapeDeRangos()
    {
        var query = LogQuery.Between(Noon, Noon.AddHours(1));

        Assert.True(query.Overlaps(Noon.UtcTicks, Noon.AddMinutes(30).UtcTicks));
        Assert.True(query.Overlaps(Noon.AddMinutes(-30).UtcTicks, Noon.AddMinutes(5).UtcTicks));
        Assert.False(query.Overlaps(Noon.AddHours(2).UtcTicks, Noon.AddHours(3).UtcTicks));
        Assert.False(query.Overlaps(Noon.AddHours(-3).UtcTicks, Noon.AddHours(-2).UtcTicks));
    }

    [Fact]
    public void OnDay_CubreElDiaCompletoUtc()
    {
        var query = LogQuery.OnDay(new DateOnly(2026, 8, 20));

        Assert.True(query.Matches(Record(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero))));
        Assert.True(query.Matches(Record(new DateTimeOffset(2026, 8, 20, 23, 59, 59, TimeSpan.Zero))));
        Assert.False(query.Matches(Record(new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero))));
    }

    [Fact]
    public void FiltrosExactos_SonSensiblesAMayusculas()
    {
        Assert.True(new LogQuery { Service = "api" }.Matches(Record(Noon)));
        Assert.False(new LogQuery { Service = "API" }.Matches(Record(Noon)));
        Assert.True(new LogQuery { TraceId = "t1" }.Matches(Record(Noon, traceId: "t1")));
        Assert.False(new LogQuery { TraceId = "t1" }.Matches(Record(Noon)));
    }
}

public class LogEntryTests
{
    [Fact]
    public void ValoresNulos_SeNormalizanACadenaVacia()
    {
        var entry = new LogEntry(123, LogLevel.Information, null!);

        Assert.Equal(string.Empty, entry.Message);
        Assert.Equal(string.Empty, entry.Service);
        Assert.Equal(string.Empty, entry.Host);
        Assert.Null(entry.TraceId);
    }

    [Fact]
    public void TimestampUtc_EsSiempreUtc()
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new LogEntry(now, LogLevel.Error, "boom");

        Assert.Equal(DateTimeKind.Utc, entry.TimestampUtc.Kind);
        Assert.Equal(now.UtcTicks, entry.TimestampTicks);
    }
}
