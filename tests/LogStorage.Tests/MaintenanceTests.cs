using Xunit;

namespace LogStorage.Tests;

public class MaintenanceTests
{
    private static readonly DateOnly Day = new(2026, 8, 20);
    private static readonly DateTimeOffset Base = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private static async Task Seed(LogStore store, int count = 500, DateTimeOffset? start = null)
    {
        var from = start ?? Base;
        for (var i = 0; i < count; i++)
        {
            store.WriteAt(from.AddSeconds(i), (LogLevel)(i % 6), $"Mensaje {i % 20}", i % 2 == 0 ? "ventas" : "compras", "host-01");
        }
        await store.FlushAsync();
        // Cerramos el segmento: un fichero abierto no se puede borrar al archivar.
        await store.SealAsync();
    }

    [Fact]
    public async Task Verify_DiceQueTodoEstaBien()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store);

        var report = store.Maintenance.Verify();

        Assert.True(report.Ok, string.Join("; ", report.Problems));
        Assert.True(report.Segments >= 1);
        Assert.True(report.Blocks >= 1);
        Assert.Equal(500, report.Records);
    }

    [Fact]
    public async Task Verify_DetectaBytesCorruptos()
    {
        using var temp = new TempStore();
        await using (var store = temp.Open())
        {
            await Seed(store, 200);
        }

        // Estropeamos un byte del interior del primer segmento.
        var segment = temp.Segments()[0];
        using (var file = new FileStream(segment, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Seek(120, SeekOrigin.Begin);
            var b = file.ReadByte();
            file.Seek(120, SeekOrigin.Begin);
            file.WriteByte((byte)(b ^ 0xFF));
        }

        var report = new LogStoreMaintenance(temp.Options()).Verify();

        Assert.False(report.Ok);
        Assert.Contains(report.Problems, p => p.Contains("huella", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Reindex_ResumeLoQueHayEnDisco()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 750);

        var manifest = await store.Maintenance.ReindexAsync();

        Assert.Single(manifest.Days);
        Assert.Equal("2026-08-20", manifest.Days[0].Date);
        Assert.Equal(750, manifest.Days[0].RecordCount);
        Assert.Equal(750, manifest.TotalRecords);
        Assert.True(manifest.Days[0].SegmentBytes > 0);
        Assert.False(manifest.Days[0].Archived);
        Assert.True(File.Exists(Path.Combine(temp.Root, "manifest.idx")));
    }

    [Fact]
    public async Task GetManifest_LeeElGuardadoSinVolverAEscanear()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 100);
        await store.Maintenance.ReindexAsync();

        var manifest = await store.Maintenance.GetManifestAsync();

        Assert.Equal(100, manifest.TotalRecords);
    }

    [Fact]
    public async Task ArchivarUnDia_CreaElParquetYBorraLosSegmentos()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 1000);

        var result = await store.Maintenance.ArchiveDayAsync(Day);

        Assert.Equal(1000, result.RecordCount);
        Assert.NotNull(result.ArchiveFile);
        Assert.True(File.Exists(result.ArchiveFile));
        Assert.Empty(temp.Segments());
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "2026", "08", "20")));
    }

    [Fact]
    public async Task DespuesDeArchivar_LasConsultasSiguenFuncionando()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 1000);

        var before = await LogStoreTests.Collect(store, new LogQuery());
        await store.Maintenance.ArchiveDayAsync(Day);
        var after = await LogStoreTests.Collect(store, new LogQuery());

        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].TimestampTicks, after[i].TimestampTicks);
            Assert.Equal(before[i].Level, after[i].Level);
            Assert.Equal(before[i].Service, after[i].Service);
            Assert.Equal(before[i].Host, after[i].Host);
            Assert.Equal(before[i].Message, after[i].Message);
        }
    }

    [Fact]
    public async Task DespuesDeArchivar_LosFiltrosSiguenAplicandose()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 1000);
        await store.Maintenance.ArchiveDayAsync(Day);

        Assert.Equal(500, await store.CountAsync(new LogQuery { Service = "ventas" }));
        Assert.Equal(332, await store.CountAsync(new LogQuery { MinLevel = LogLevel.Error }));
        Assert.Equal(50, await store.CountAsync(new LogQuery { Contains = "Mensaje 7" }));
        Assert.Equal(10, (await LogStoreTests.Collect(store, new LogQuery { Limit = 10 })).Count);
    }

    [Fact]
    public async Task DespuesDeArchivar_ElRangoDeFechasSigueFiltrando()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 1000);
        await store.Maintenance.ArchiveDayAsync(Day);

        var records = await LogStoreTests.Collect(store, LogQuery.Between(Base.AddSeconds(100), Base.AddSeconds(110)));

        Assert.Equal(10, records.Count);
        Assert.Equal(Base.AddSeconds(100).UtcTicks, records[0].TimestampTicks);
    }

    [Fact]
    public async Task ArchivarSinBorrar_DejaLosSegmentosYNoDuplicaResultados()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 300);

        await store.Maintenance.ArchiveDayAsync(Day, deleteSegments: false);

        Assert.NotEmpty(temp.Segments());
        // Con segmentos presentes se leen los segmentos y se ignora el Parquet: nada duplicado.
        Assert.Equal(300, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task ArchivarUnDiaSinDatos_NoHaceNada()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();

        var result = await store.Maintenance.ArchiveDayAsync(new DateOnly(2020, 1, 1));

        Assert.Null(result.ArchiveFile);
        Assert.Equal(0, result.RecordCount);
    }

    [Fact]
    public async Task ArchiveOlderThan_ArchivaSoloLosDiasAnteriores()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 100, new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero));
        await Seed(store, 100, new DateTimeOffset(2026, 8, 19, 10, 0, 0, TimeSpan.Zero));
        await Seed(store, 100, new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));

        var summary = await store.Maintenance.ArchiveOlderThanAsync(new DateOnly(2026, 8, 20));

        Assert.Equal(2, summary.Days);
        Assert.Equal(200, summary.RecordCount);
        Assert.False(Directory.Exists(Path.Combine(temp.Root, "2026", "08", "18")));
        Assert.True(Directory.Exists(Path.Combine(temp.Root, "2026", "08", "20")));
        Assert.Equal(300, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task PruneSegments_BorraLoAnteriorAlCorte()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 50, new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero));
        await Seed(store, 50, new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));

        var result = store.Maintenance.PruneSegmentsOlderThan(new DateOnly(2026, 8, 20));

        Assert.Equal(1, result.Files);
        Assert.True(result.BytesFreed > 0);
        Assert.Equal(50, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task PruneArchive_BorraLosParquetAntiguos()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 50, new DateTimeOffset(2026, 8, 18, 10, 0, 0, TimeSpan.Zero));
        await store.Maintenance.ArchiveDayAsync(new DateOnly(2026, 8, 18));

        var result = store.Maintenance.PruneArchiveOlderThan(new DateOnly(2026, 8, 19));

        Assert.Equal(1, result.Files);
        Assert.Equal(0, await store.CountAsync(new LogQuery()));
    }

    [Fact]
    public async Task Reindex_MarcaLosDiasArchivados()
    {
        using var temp = new TempStore();
        await using var store = temp.Open();
        await Seed(store, 200);
        await store.Maintenance.ArchiveDayAsync(Day);

        var manifest = await store.Maintenance.ReindexAsync();

        Assert.Single(manifest.Days);
        Assert.True(manifest.Days[0].Archived);
        Assert.Equal(0, manifest.Days[0].SegmentCount);
        Assert.Equal(200, manifest.Days[0].RecordCount);
        Assert.True(manifest.Days[0].ArchiveBytes > 0);
    }
}
