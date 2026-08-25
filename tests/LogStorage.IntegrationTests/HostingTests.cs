using System.Text.Json;
using LogStorage.Logging;
using LogStorage.Tests;
using LogStorage.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;
using MelLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace LogStorage.IntegrationTests;

/// <summary>Integración con el contenedor de dependencias, con <c>ILogger</c> y con el worker.</summary>
public class HostingTests
{
    [Fact]
    public async Task AddLogStorage_RegistraElAlmacenComoSingleton()
    {
        using var temp = new TempStore("di");

        var services = new ServiceCollection();
        services.AddLogStorage(options =>
        {
            options.RootPath = temp.Root;
            options.DefaultService = "desde-di";
            options.AutoFlushInterval = TimeSpan.Zero;
        });

        await using var provider = services.BuildServiceProvider();

        var store = provider.GetRequiredService<ILogStore>();
        Assert.Same(store, provider.GetRequiredService<LogStore>());
        Assert.NotNull(provider.GetRequiredService<LogStoreMaintenance>());
        Assert.Equal("desde-di", store.Options.DefaultService);

        store.Write(LogLevel.Warning, "hola desde el contenedor");
        await store.FlushAsync();

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        var records = await Collect(reader, new LogQuery());
        Assert.Single(records);
        Assert.Equal("desde-di", records[0].Service);
    }

    [Fact]
    public async Task AddLogStorage_DesdeConfiguracion()
    {
        using var temp = new TempStore("di-config");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LogStorage:RootPath"] = temp.Root,
                ["LogStorage:CompressionLevel"] = "5",
                ["LogStorage:DefaultService"] = "config",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogStorage(configuration.GetSection("LogStorage"));

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<ILogStore>();

        Assert.Equal(5, store.Options.CompressionLevel);
        Assert.Equal("config", store.Options.DefaultService);
        Assert.Equal(Path.GetFullPath(temp.Root), store.Options.RootPath);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task ILogger_EscribeEnElAlmacenGuardandoLaPlantilla()
    {
        using var temp = new TempStore("ilogger");

        var services = new ServiceCollection();
        services.AddLogStorage(options =>
        {
            options.RootPath = temp.Root;
            options.AutoFlushInterval = TimeSpan.Zero;
        });
        services.AddLogging(logging =>
        {
            logging.SetMinimumLevel(MelLogLevel.Trace);
            logging.AddLogStorage(o =>
            {
                o.MinimumLevel = LogLevel.Debug;
                o.Service = "tienda";
            });
        });

        await using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<HostingTests>>();

        for (var i = 0; i < 100; i++) logger.LogInformation("Pedido {PedidoId} creado por {Usuario}", i, "ana");
        logger.LogTrace("esto no debería guardarse");
        logger.LogError(new InvalidOperationException("boom"), "Fallo al procesar {PedidoId}", 42);

        await provider.GetRequiredService<ILogStore>().FlushAsync();

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        var records = await Collect(reader, new LogQuery());

        Assert.Equal(101, records.Count);
        Assert.All(records, r => Assert.Equal("tienda", r.Service));

        // La plantilla se guarda tal cual: cien registros comparten una sola cadena.
        var pedidos = records.Where(r => r.Message == "Pedido {PedidoId} creado por {Usuario}").ToList();
        Assert.Equal(100, pedidos.Count);

        // Los valores viajan en el payload como JSON.
        using var payload = JsonDocument.Parse(pedidos[7].Payload);
        Assert.Equal(7, payload.RootElement.GetProperty("PedidoId").GetInt32());
        Assert.Equal("ana", payload.RootElement.GetProperty("Usuario").GetString());
        Assert.Contains("HostingTests", payload.RootElement.GetProperty("category").GetString());

        // La excepción también.
        var error = records.Single(r => r.Level == LogLevel.Error);
        using var errorPayload = JsonDocument.Parse(error.Payload);
        Assert.Equal("System.InvalidOperationException", errorPayload.RootElement.GetProperty("exception").GetString());
        Assert.Equal("boom", errorPayload.RootElement.GetProperty("exceptionMessage").GetString());
    }

    [Fact]
    public async Task ILogger_SinPlantilla_GuardaElTextoYaFormateado()
    {
        using var temp = new TempStore("ilogger-plano");

        var services = new ServiceCollection();
        services.AddLogStorage(options =>
        {
            options.RootPath = temp.Root;
            options.AutoFlushInterval = TimeSpan.Zero;
        });
        services.AddLogging(logging => logging.AddLogStorage(o =>
        {
            o.UseMessageTemplate = false;
            o.IncludeParameters = false;
        }));

        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ILogger<HostingTests>>().LogInformation("Pedido {PedidoId} creado", 99);
        await provider.GetRequiredService<ILogStore>().FlushAsync();

        await using var reader = LogStore.OpenReadOnly(temp.Root);
        var records = await Collect(reader, new LogQuery());

        Assert.Single(records);
        Assert.Equal("Pedido 99 creado", records[0].Message);
        Assert.True(records[0].Payload.IsEmpty);
    }

    [Fact]
    public async Task Worker_EjecutaUnCicloDeMantenimientoCompleto()
    {
        using var temp = new TempStore("worker");
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Dos días viejos (para archivar) y hoy (que se queda como está).
        await using (var store = temp.Open())
        {
            foreach (var offset in new[] { -5, -4, 0 })
            {
                var day = new DateTimeOffset(today.AddDays(offset).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).AddHours(10);
                for (var i = 0; i < 500; i++) store.WriteAt(day.AddSeconds(i), LogLevel.Information, $"mensaje {i % 10}");
            }
            await store.FlushAsync();
            await store.SealAsync();
        }

        var options = new MaintenanceOptions
        {
            RootPath = temp.Root,
            ArchiveAfterDays = 2,
            ArchiveRetentionDays = 4,
            Reindex = true,
            Verify = true,
        };

        var service = new MaintenanceService(Options.Create(options), new LoggerFactory().CreateLogger<MaintenanceService>());
        var maintenance = new LogStoreMaintenance(new LogStoreOptions { RootPath = temp.Root, ReadOnly = true });

        await service.RunCycleAsync(maintenance, CancellationToken.None);

        // El día -5 se archivó y luego se purgó por retención; el -4 quedó en Parquet; hoy sigue en segmentos.
        await using var reader = LogStore.OpenReadOnly(temp.Root);
        Assert.Equal(0, await reader.CountAsync(LogQuery.OnDay(today.AddDays(-5))));
        Assert.Equal(500, await reader.CountAsync(LogQuery.OnDay(today.AddDays(-4))));
        Assert.Equal(500, await reader.CountAsync(LogQuery.OnDay(today)));

        Assert.True(File.Exists(Path.Combine(temp.Root, "manifest.idx")));
    }

    [Fact]
    public void Worker_RechazaUnaConfiguracionPeligrosa()
    {
        var options = new MaintenanceOptions
        {
            RootPath = "logs",
            ArchiveAfterDays = 7,
            SegmentRetentionDays = 3, // borraría segmentos que aún no se han archivado
        };

        var error = Assert.Throws<ArgumentException>(options.Validate);
        Assert.Contains("SegmentRetentionDays", error.Message, StringComparison.Ordinal);
    }

    private static async Task<List<LogRecord>> Collect(ILogStore store, LogQuery query)
    {
        var result = new List<LogRecord>();
        await foreach (var record in store.QueryAsync(query)) result.Add(record);
        return result;
    }
}
