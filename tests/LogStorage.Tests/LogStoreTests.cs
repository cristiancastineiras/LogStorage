using Xunit;

namespace LogStorage.Tests;

public class LogStoreTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task EscribirYBuscar_DevuelveLoEscrito()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 500; i++)
        {
            store.WriteAt(Base.AddSeconds(i), (LogLevel)(i % 6), $"Mensaje {i}", "api", "host-01");
        }
        await store.FlushAsync();

        var records = await Collect(store, new LogQuery());

        Assert.Equal(500, records.Count);
        Assert.Equal("Mensaje 0", records[0].Message);
        Assert.Equal("Mensaje 499", records[^1].Message);
        Assert.Equal("api", records[0].Service);
        Assert.Equal("host-01", records[0].Host);
    }

    [Fact]
    public async Task ResultadosOrdenadosPorTiempoAscendente()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 300; i++) store.WriteAt(Base.AddMilliseconds(i * 37), LogLevel.Information, $"m{i}");
        await store.FlushAsync();

        var records = await Collect(store, new LogQuery());

        for (var i = 1; i < records.Count; i++)
        {
            Assert.True(records[i].TimestampTicks >= records[i - 1].TimestampTicks);
        }
    }

    [Fact]
    public async Task Descending_DevuelveDelMasNuevoAlMasViejo()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 300; i++) store.WriteAt(Base.AddSeconds(i), LogLevel.Information, $"m{i}");
        await store.FlushAsync();

        var records = await Collect(store, new LogQuery { Descending = true, Limit = 5 });

        Assert.Equal(5, records.Count);
        Assert.Equal("m299", records[0].Message);
        Assert.Equal("m295", records[4].Message);
    }

    [Fact]
    public async Task FiltroPorRango_DevuelveSoloLaVentanaPedida()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 600; i++) store.WriteAt(Base.AddMinutes(i), LogLevel.Information, $"m{i}");
        await store.FlushAsync();

        var records = await Collect(store, LogQuery.Between(Base.AddMinutes(100), Base.AddMinutes(110)));

        Assert.Equal(10, records.Count);
        Assert.Equal("m100", records[0].Message);
        Assert.Equal("m109", records[^1].Message);
    }

    [Fact]
    public async Task FiltroPorNivelServicioYTexto()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 400; i++)
        {
            store.WriteAt(Base.AddSeconds(i), i % 4 == 0 ? LogLevel.Error : LogLevel.Debug,
                i % 3 == 0 ? "Pedido creado" : "Usuario creado",
                i % 2 == 0 ? "ventas" : "compras");
        }
        await store.FlushAsync();

        Assert.Equal(100, await store.CountAsync(new LogQuery { MinLevel = LogLevel.Error }));
        Assert.Equal(200, await store.CountAsync(new LogQuery { Service = "ventas" }));
        Assert.Equal(134, await store.CountAsync(new LogQuery { Contains = "pedido" }));
        Assert.Equal(0, await store.CountAsync(new LogQuery { Service = "inexistente" }));
        Assert.Equal(0, await store.CountAsync(new LogQuery { Contains = "no aparece nunca" }));
    }

    [Fact]
    public async Task Limit_CortaLosResultados()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 1000; i++) store.WriteAt(Base.AddSeconds(i), LogLevel.Information, $"m{i}");
        await store.FlushAsync();

        Assert.Equal(7, (await Collect(store, new LogQuery { Limit = 7 })).Count);
        Assert.Empty(await Collect(store, new LogQuery { Limit = 0 }));
    }

    [Fact]
    public async Task VariosDias_SeGuardanEnSuCarpetaYNingunBloqueCruzaLaMedianoche()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 300; i++)
        {
            store.WriteAt(new DateTimeOffset(2026, 8, 19, 23, 59, 0, TimeSpan.Zero).AddSeconds(i * 2), LogLevel.Information, $"m{i}");
        }
        await store.FlushAsync();

        Assert.True(Directory.Exists(Path.Combine(temp.Root, "2026", "08", "19")));
        Assert.True(Directory.Exists(Path.Combine(temp.Root, "2026", "08", "20")));

        var day19 = await store.CountAsync(LogQuery.OnDay(new DateOnly(2026, 8, 19)));
        var day20 = await store.CountAsync(LogQuery.OnDay(new DateOnly(2026, 8, 20)));
        Assert.Equal(300, day19 + day20);
        Assert.True(day19 > 0 && day20 > 0);
    }

    [Fact]
    public async Task RotaDeSegmentoAlSuperarElTamanoMaximo()
    {
        using var temp = new TempStore();
        await using var store = temp.Open(o =>
        {
            o.BlockSizeBytes = 4096;
            o.MaxSegmentBytes = 8192;
        });

        for (var i = 0; i < 50_000; i++)
        {
            store.WriteAt(Base.AddMilliseconds(i), LogLevel.Information, $"mensaje irrepetible numero {i}");
        }
        await store.FlushAsync();

        Assert.True(temp.Segments().Length > 1, "esperaba varios segmentos tras superar el tamaño máximo");
        Assert.Equal(50_000, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task Payload_SobreviveAlaIdaYVuelta()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        byte[] payload = [1, 2, 3, 4, 5];
        store.Write(LogLevel.Error, "con datos", payload: payload);
        await store.FlushAsync();

        var records = await Collect(store, new LogQuery());

        Assert.Single(records);
        Assert.Equal(payload, records[0].Payload.ToArray());
    }

    [Fact]
    public async Task Atajos_UsanElServicioYHostPorDefecto()
    {
        using var temp = new TempStore();
        await using var store = temp.Open(o =>
        {
            o.DefaultService = "mi-servicio";
            o.DefaultHost = "mi-host";
        });

        store.Information("hola");
        store.Error("adios");
        await store.FlushAsync();

        var records = await Collect(store, new LogQuery());

        Assert.Equal(2, records.Count);
        Assert.All(records, r => Assert.Equal("mi-servicio", r.Service));
        Assert.All(records, r => Assert.Equal("mi-host", r.Host));
        Assert.Equal(LogLevel.Information, records[0].Level);
        Assert.Equal(LogLevel.Error, records[1].Level);
    }

    [Fact]
    public async Task Stats_CuentaLoQueDeVerdadSeHaEscrito()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        for (var i = 0; i < 1000; i++) store.WriteAt(Base.AddSeconds(i), LogLevel.Information, "repetido");
        await store.FlushAsync();

        var stats = store.Stats;
        Assert.Equal(1000, stats.RecordsWritten);
        Assert.True(stats.BlocksWritten >= 1);
        Assert.True(stats.BytesWritten > 0);
    }

    [Fact]
    public async Task SinFlush_AlCerrarTambienSeGuardaTodo()
    {
        using var temp = new TempStore();

        await using (var store = temp.Open())
        {
            for (var i = 0; i < 250; i++) store.WriteAt(Base.AddSeconds(i), LogLevel.Information, $"m{i}");
        }

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        Assert.Equal(250, await reader.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task ModoSoloLectura_NoDejaEscribir()
    {
        using var temp = new TempStore();
        await using (var store = temp.Open())
        {
            store.Information("algo");
        }

        await using var readOnly = LogStore.OpenReadOnly(temp.Root);
        Assert.Throws<InvalidOperationException>(() => readOnly.Information("no debería"));
        Assert.Equal(1, await readOnly.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task AlmacenVacio_DevuelveCero()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        Assert.Equal(0, await store.CountAsync(new LogQuery()));
        Assert.Empty(await Collect(store, LogQuery.LastHours(24)));
    }

    [Fact]
    public async Task SealAsync_CierraElSegmentoYAbreOtro()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        store.WriteAt(Base, LogLevel.Information, "uno");
        await store.SealAsync();
        store.WriteAt(Base.AddSeconds(1), LogLevel.Information, "dos");
        await store.FlushAsync();

        Assert.Equal(2, temp.Segments().Length);
        Assert.Equal(2, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task VariosHilosEscribiendoALaVez_NoSePierdeNingunRegistro()
    {
        using var temp = new TempStore();
        await using var store = temp.Open(o => o.BlockSizeBytes = 16 * 1024);

        const int threads = 8;
        const int perThread = 5_000;

        await Task.WhenAll(Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < perThread; i++)
            {
                store.WriteAt(Base.AddMilliseconds(i), LogLevel.Information, $"hilo {t} mensaje {i}", $"svc-{t}");
            }
        })));

        await store.FlushAsync();

        Assert.Equal(threads * perThread, await store.CountAsync(new LogQuery()));
        for (var t = 0; t < threads; t++)
        {
            Assert.Equal(perThread, await store.CountAsync(new LogQuery { Service = $"svc-{t}" }));
        }
    }

    [Fact]
    public async Task OpcionesInvalidas_Lanzan()
    {
        using var temp = new TempStore();

        Assert.Throws<ArgumentOutOfRangeException>(() => LogStore.Open(temp.Options(o => o.BlockSizeBytes = 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogStore.Open(temp.Options(o => o.CompressionLevel = 99)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogStore.Open(temp.Options(o => o.MaxSegmentBytes = 4L * 1024 * 1024 * 1024)));
        Assert.Throws<ArgumentOutOfRangeException>(() => LogStore.Open(temp.Options(o =>
        {
            o.BlockSizeBytes = 1024 * 1024;
            o.MaxSegmentBytes = 4096; // más pequeño que un bloque
        })));
        Assert.Throws<ArgumentException>(() => LogStore.Open(new LogStoreOptions { RootPath = "  " }));

        await Task.CompletedTask;
    }

    [Fact]
    public async Task DisposeDosVeces_NoRompe()
    {
        using var temp = new TempStore();
        var store = temp.Open();
        store.Information("hola");

        await store.DisposeAsync();
        await store.DisposeAsync();
    }

    internal static async Task<List<LogRecord>> Collect(ILogStore store, LogQuery query)
    {
        var result = new List<LogRecord>();
        await foreach (var record in store.QueryAsync(query)) result.Add(record);
        return result;
    }
}
