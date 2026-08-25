using System.Text.Json;
using LogStorage.Tests;
using LogStorage.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LogStorage.IntegrationTests;

/// <summary>
/// El fichero de estado que el worker deja en disco y que el panel de control lee.
/// </summary>
/// <remarks>
/// Es un contrato entre dos procesos que no comparten código: si el worker deja de escribir
/// alguno de estos campos, el panel se queda mudo sin que nada falle. De ahí que se
/// compruebe campo a campo con un deserializador ajeno al del worker, igual que hace el panel.
/// </remarks>
public class WorkerStatusTests
{
    // Las mismas opciones que usa LogStorage.Tray al leer el fichero.
    private static readonly JsonSerializerOptions PanelOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StatusReporter_EscribeUnJsonQueElPanelPuedeLeer()
    {
        using var temp = new TempStore("status");
        var reporter = new StatusReporter(temp.Root);

        reporter.Write(new WorkerStatus
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            LastCycleUtc = DateTimeOffset.UtcNow,
            Ok = true,
            Message = "Ciclo completado.",
            TotalDays = 3,
            TotalRecords = 1234,
            RootPath = temp.Root,
        });

        Assert.True(File.Exists(reporter.FilePath));

        var read = JsonSerializer.Deserialize<WorkerStatus>(File.ReadAllText(reporter.FilePath), PanelOptions);

        Assert.NotNull(read);
        Assert.True(read.Ok);
        Assert.Equal("Ciclo completado.", read.Message);
        Assert.Equal(3, read.TotalDays);
        Assert.Equal(1234L, read.TotalRecords);
        Assert.Equal(temp.Root, read.RootPath);
        Assert.NotNull(read.LastCycleUtc);
    }

    [Fact]
    public void StatusReporter_NoDejaFicherosTemporalesNiLanzaAlReescribir()
    {
        using var temp = new TempStore("status-rewrite");
        var reporter = new StatusReporter(temp.Root);

        for (var i = 0; i < 5; i++)
        {
            reporter.Write(new WorkerStatus { UpdatedUtc = DateTimeOffset.UtcNow, Message = $"ciclo {i}" });
        }

        var read = JsonSerializer.Deserialize<WorkerStatus>(File.ReadAllText(reporter.FilePath), PanelOptions);

        Assert.Equal("ciclo 4", read!.Message);
        Assert.Empty(Directory.GetFiles(temp.Root, "*.tmp"));
    }

    [Fact]
    public void StatusReporter_NoLanzaSiLaCarpetaNoExiste()
    {
        var missing = Path.Combine(Path.GetTempPath(), "logstorage-tests", $"no-existe-{Guid.NewGuid():N}");
        var reporter = new StatusReporter(missing);

        // El estado es cosmético: que no se pueda escribir jamás debe tumbar el mantenimiento.
        reporter.Write(new WorkerStatus { Message = "da igual" });

        Assert.False(File.Exists(reporter.FilePath));
    }

    [Fact]
    public async Task ElCicloDeMantenimiento_ResumeLoQueHaHecho()
    {
        using var temp = new TempStore("status-cycle");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var store = temp.Open())
        {
            foreach (var offset in new[] { -4, 0 })
            {
                var day = new DateTimeOffset(today.AddDays(offset).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(9);
                for (var i = 0; i < 200; i++) store.WriteAt(day.AddSeconds(i), LogLevel.Information, $"mensaje {i % 5}");
            }
            await store.FlushAsync();
            await store.SealAsync();
        }

        var options = new MaintenanceOptions
        {
            RootPath = temp.Root,
            ArchiveAfterDays = 2,
            Reindex = true,
        };

        var service = new MaintenanceService(Options.Create(options), new LoggerFactory().CreateLogger<MaintenanceService>());
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = temp.Root, ReadOnly = true });

        var report = await service.RunCycleAsync(maintenance, CancellationToken.None);

        Assert.Equal(1, report.DaysArchived);
        Assert.Equal(0, report.Problems);
        Assert.True(report.Elapsed > TimeSpan.Zero);
    }
}
