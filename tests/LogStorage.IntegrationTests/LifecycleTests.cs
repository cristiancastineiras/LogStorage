using LogStorage.Tests;
using Xunit;

namespace LogStorage.IntegrationTests;

/// <summary>
/// Recorre el ciclo de vida completo tal y como pasaría en producción:
/// se ingesta durante varios días, se consulta, se archiva a Parquet y se aplica retención.
/// </summary>
public class LifecycleTests
{
    private static readonly string[] Templates =
    [
        "Usuario creado", "Pedido creado", "Pedido enviado", "Pago aceptado", "Pago rechazado",
    ];

    private static readonly string[] Services = ["api", "ventas", "pagos"];

    [Fact]
    public async Task CicloCompleto_IngestarConsultarArchivarYPurgar()
    {
        using var temp = new TempStore("lifecycle");
        var day0 = new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);
        const int perDay = 20_000;
        const int days = 5;

        // --- Ingesta de cinco días ---
        await using (var store = temp.Open(o => o.BlockSizeBytes = 64 * 1024))
        {
            var random = new Random(7);
            for (var day = 0; day < days; day++)
            {
                for (var i = 0; i < perDay; i++)
                {
                    store.WriteAt(
                        day0.AddDays(day).AddSeconds(i * 4),
                        (LogLevel)random.Next(0, 6),
                        Templates[random.Next(Templates.Length)],
                        Services[random.Next(Services.Length)],
                        "host-01");
                }
            }
            await store.FlushAsync();
        }

        var total = (long)perDay * days;

        // --- Cada día tiene su carpeta y nada se ha cruzado de día ---
        for (var day = 0; day < days; day++)
        {
            var date = DateOnly.FromDateTime(day0.AddDays(day).UtcDateTime);
            Assert.True(Directory.Exists(Path.Combine(temp.Root, $"{date.Year:D4}", $"{date.Month:D2}", $"{date.Day:D2}")));
        }

        await using (var store = LogStore.OpenReadOnly(temp.Root))
        {
            Assert.Equal(total, await store.CountAsync(new LogQuery()));
            Assert.Equal(perDay, await store.CountAsync(LogQuery.OnDay(new DateOnly(2026, 8, 17))));

            // --- Integridad ---
            var report = store.Maintenance.Verify();
            Assert.True(report.Ok, string.Join("; ", report.Problems));
            Assert.Equal(total, report.Records);

            // --- Archivado de los tres primeros días ---
            var summary = await store.Maintenance.ArchiveOlderThanAsync(new DateOnly(2026, 8, 18));
            Assert.Equal(3, summary.Days);
            Assert.Equal(perDay * 3, summary.RecordCount);

            // Los datos siguen ahí: tres días en Parquet y dos en segmentos.
            Assert.Equal(total, await store.CountAsync(new LogQuery()));
            Assert.Equal(perDay, await store.CountAsync(LogQuery.OnDay(new DateOnly(2026, 8, 16))));
            Assert.Equal(perDay, await store.CountAsync(LogQuery.OnDay(new DateOnly(2026, 8, 19))));

            // --- Los filtros funcionan igual sobre Parquet que sobre segmentos ---
            var pagosArchivados = await store.CountAsync(new LogQuery
            {
                Service = "pagos",
                From = day0,
                To = day0.AddDays(3),
            });
            var pagosCalientes = await store.CountAsync(new LogQuery
            {
                Service = "pagos",
                From = day0.AddDays(3),
            });
            Assert.True(pagosArchivados > 0);
            Assert.True(pagosCalientes > 0);
            Assert.Equal(await store.CountAsync(new LogQuery { Service = "pagos" }), pagosArchivados + pagosCalientes);

            // --- Manifiesto coherente ---
            var manifest = await store.Maintenance.ReindexAsync();
            Assert.Equal(days, manifest.Days.Count);
            Assert.Equal(total, manifest.TotalRecords);
            Assert.Equal(3, manifest.Days.Count(d => d.Archived));

            // --- Retención: se borra el Parquet más viejo ---
            var pruned = store.Maintenance.PruneArchiveOlderThan(new DateOnly(2026, 8, 16));
            Assert.Equal(1, pruned.Files);
            Assert.Equal(total - perDay, await store.CountAsync(new LogQuery()));
        }
    }

    [Fact]
    public async Task ReabrirElAlmacen_AnadeSegmentosSinPisarLosAnteriores()
    {
        using var temp = new TempStore("reopen");
        var start = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        for (var round = 0; round < 3; round++)
        {
            await using var store = temp.Open();
            for (var i = 0; i < 1000; i++)
            {
                store.WriteAt(start.AddMinutes(round * 60).AddSeconds(i), LogLevel.Information, $"ronda {round} registro {i}");
            }
            await store.FlushAsync();
        }

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        Assert.Equal(3, temp.Segments().Length);
        Assert.Equal(3000, await reader.CountAsync(new LogQuery()));
        Assert.Equal(1000, await reader.CountAsync(new LogQuery { Contains = "ronda 1 " }));
    }

    [Fact]
    public async Task SegmentoCortadoPorUnApagon_SigueSiendoLegibleHastaElUltimoBloque()
    {
        using var temp = new TempStore("crash");
        var start = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        await using (var store = temp.Open(o => o.BlockSizeBytes = 8 * 1024))
        {
            for (var i = 0; i < 20_000; i++)
            {
                store.WriteAt(start.AddMilliseconds(i), LogLevel.Information, $"mensaje irrepetible {i}");
            }
            await store.FlushAsync();
        }

        var segment = temp.Segments()[^1];
        var originalLength = new FileInfo(segment).Length;

        // Cortamos el último tercio del fichero, como si se hubiera ido la luz escribiendo.
        using (var file = new FileStream(segment, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(originalLength * 2 / 3);
        }

        await using var store2 = LogStore.OpenReadOnly(temp.Root);
        var survivors = await store2.CountAsync(new LogQuery());

        Assert.True(survivors > 0, "se ha perdido todo el segmento, debería sobrevivir lo escrito antes del corte");
        Assert.True(survivors < 20_000, "el test no ha llegado a truncar nada");

        // Y lo que sobrevive es válido, no basura.
        var report = store2.Maintenance.Verify();
        Assert.True(report.Records == survivors);
    }

    [Fact]
    public async Task LeerMientrasSeEscribe_NoRompeNiBloquea()
    {
        using var temp = new TempStore("concurrent");
        var start = new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero);

        await using var store = temp.Open(o => o.BlockSizeBytes = 16 * 1024);
        using var stop = new CancellationTokenSource();

        var reader = Task.Run(async () =>
        {
            var reads = 0;
            await using var readOnly = LogStore.OpenReadOnly(temp.Root);
            while (!stop.Token.IsCancellationRequested)
            {
                await readOnly.CountAsync(new LogQuery { Limit = 500 });
                reads++;
            }
            return reads;
        });

        for (var i = 0; i < 50_000; i++)
        {
            store.WriteAt(start.AddMilliseconds(i), LogLevel.Information, $"mensaje {i % 100}");
            if (i % 10_000 == 0) await store.FlushAsync();
        }
        await store.FlushAsync();

        await stop.CancelAsync();
        var reads = await reader;

        Assert.True(reads > 0, "el lector no llegó a consultar nada");
        Assert.Equal(50_000, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task VolumenGrande_RotaSegmentosYMantieneLaIntegridad()
    {
        using var temp = new TempStore("volume");
        var start = new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        const int count = 300_000;

        await using (var store = temp.Open(o =>
        {
            // Bloques y segmentos muy pequeños a propósito: con 300.000 registros que
            // comprimen a ~1 byte cada uno, con los valores normales cabrían todos en un
            // solo segmento y no se probaría la rotación.
            o.BlockSizeBytes = 64 * 1024;
            o.MaxSegmentBytes = 128 * 1024;
        }))
        {
            var random = new Random(11);
            for (var i = 0; i < count; i++)
            {
                store.WriteAt(
                    start.AddMilliseconds(i * 250),
                    (LogLevel)random.Next(0, 6),
                    Templates[random.Next(Templates.Length)],
                    Services[random.Next(Services.Length)]);
            }
            await store.FlushAsync();
        }

        Assert.True(temp.Segments().Length > 1, "esperaba rotación de segmentos");

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        Assert.Equal(count, await reader.CountAsync(new LogQuery()));

        var report = reader.Maintenance.Verify();
        Assert.True(report.Ok, string.Join("; ", report.Problems));
        Assert.Equal(count, report.Records);

        // Con 300.000 registros de cinco plantillas y tres servicios, el diccionario hace su trabajo.
        Assert.True(temp.TotalBytes() / (double)count < 5, $"esperaba menos de 5 bytes por registro, salieron {temp.TotalBytes() / (double)count:F2}");
    }
}
