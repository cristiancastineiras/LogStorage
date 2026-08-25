using System.Text;
using System.Text.Json;
using LogStorage.Cli;
using LogStorage.Tests;
using Xunit;

namespace LogStorage.IntegrationTests;

/// <summary>Ejercita los comandos de <c>logtool</c> tal y como los invoca el CLI.</summary>
public class CliTests
{
    private static readonly DateTimeOffset Base = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    private static async Task Seed(TempStore temp, int count = 500)
    {
        await using var store = temp.Open();
        for (var i = 0; i < count; i++)
        {
            store.WriteAt(
                Base.AddSeconds(i),
                i % 10 == 0 ? LogLevel.Error : LogLevel.Information,
                i % 3 == 0 ? "Pedido creado" : "Usuario creado",
                i % 2 == 0 ? "ventas" : "compras",
                "host-01");
        }
        await store.FlushAsync();
        await store.SealAsync();
    }

    [Fact]
    public async Task Search_PintaLosResultadosYRespetaLosFiltros()
    {
        using var temp = new TempStore("cli-search");
        await Seed(temp);

        var output = await CaptureAsync(() => new QueryCommands().Search(root: temp.Root, level: "error", limit: 10));

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
        Assert.All(lines, line => Assert.Contains("ERROR", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_ConJson_SacaNdjsonValido()
    {
        using var temp = new TempStore("cli-json");
        await Seed(temp, 20);

        var output = await CaptureAsync(() => new QueryCommands().Search(root: temp.Root, json: true, limit: 5));

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);

        using var document = JsonDocument.Parse(lines[0]);
        // i=0 => Error y servicio "ventas" (ver Seed).
        Assert.Equal("ERROR", document.RootElement.GetProperty("level").GetString());
        Assert.Equal("ventas", document.RootElement.GetProperty("service").GetString());
        Assert.Equal("Pedido creado", document.RootElement.GetProperty("message").GetString());
        Assert.Equal("host-01", document.RootElement.GetProperty("host").GetString());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("INFO", second.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public async Task Search_ConFechaMalEscrita_ExplicaElFormato()
    {
        using var temp = new TempStore("cli-badtime");
        await Seed(temp, 10);

        var error = await Assert.ThrowsAsync<FormatException>(
            () => new QueryCommands().Search(root: temp.Root, from: "el martes pasado"));

        Assert.Contains("--from", error.Message, StringComparison.Ordinal);
        Assert.Contains("yesterday", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tail_MuestraLosUltimosEnOrdenCronologico()
    {
        using var temp = new TempStore("cli-tail");
        await Seed(temp);

        var output = await CaptureAsync(() => new QueryCommands().Tail(root: temp.Root, lines: 5));

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        // El último registro es el más reciente, así que va al final.
        Assert.Contains(Base.AddSeconds(499).UtcDateTime.ToString("HH:mm:ss"), lines[^1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ndjson")]
    [InlineData("csv")]
    [InlineData("json")]
    public async Task Export_EscribeElFicheroEnElFormatoPedido(string format)
    {
        using var temp = new TempStore("cli-export-" + format);
        await Seed(temp, 100);

        var target = Path.Combine(temp.Root, "salida." + format);
        await CaptureAsync(() => new QueryCommands().Export(output: target, root: temp.Root, format: format));

        Assert.True(File.Exists(target));
        var content = await File.ReadAllTextAsync(target, Encoding.UTF8);

        switch (format)
        {
            case "csv":
                var csvLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                Assert.Equal(101, csvLines.Length); // cabecera + 100
                Assert.StartsWith("timestamp,level,service", csvLines[0], StringComparison.Ordinal);
                break;
            case "json":
                using (var document = JsonDocument.Parse(content))
                {
                    Assert.Equal(100, document.RootElement.GetArrayLength());
                }
                break;
            default:
                Assert.Equal(100, content.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
                break;
        }
    }

    [Fact]
    public async Task Export_ConFormatoDesconocido_Avisa()
    {
        using var temp = new TempStore("cli-export-bad");
        await Seed(temp, 5);

        var error = await Assert.ThrowsAsync<ArgumentException>(
            () => new QueryCommands().Export(output: Path.Combine(temp.Root, "x.txt"), root: temp.Root, format: "xml"));

        Assert.Contains("ndjson", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stats_Y_Ls_ResumenElAlmacen()
    {
        using var temp = new TempStore("cli-stats");
        await Seed(temp, 1000);

        var stats = await CaptureAsync(() => new QueryCommands().Stats(root: temp.Root, refresh: true));
        Assert.Contains("1,000", stats, StringComparison.Ordinal);
        Assert.Contains("2026-08-20", stats, StringComparison.Ordinal);

        var ls = await CaptureAsync(() => new StoreCommands().Ls(root: temp.Root));
        Assert.Contains("2026-08-20", ls, StringComparison.Ordinal);
        Assert.Contains("segmentos", ls, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Info_DetallaElSegmentoYSusBloques()
    {
        using var temp = new TempStore("cli-info");
        await Seed(temp, 2000);

        var segment = temp.Segments()[0];
        var output = await CaptureAsync(() =>
        {
            new StoreCommands().Info(segment, blocks: true);
            return Task.CompletedTask;
        });

        Assert.Contains("Formato", output, StringComparison.Ordinal);
        Assert.Contains("Zstd", output, StringComparison.Ordinal);
        Assert.Contains("cerrado", output, StringComparison.Ordinal);
        Assert.Contains("REGISTROS", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyYReindex_TerminanSinProblemas()
    {
        using var temp = new TempStore("cli-verify");
        await Seed(temp, 500);

        var verify = await CaptureAsync(() =>
        {
            new StoreCommands().Verify(root: temp.Root);
            return Task.CompletedTask;
        });
        Assert.Contains("Todo correcto", verify, StringComparison.Ordinal);
        Assert.Equal(0, Environment.ExitCode);

        var reindex = await CaptureAsync(() => new StoreCommands().Reindex(root: temp.Root));
        Assert.Contains("Manifiesto reconstruido", reindex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchiveYPrune_MuevenYBorranLosDatos()
    {
        using var temp = new TempStore("cli-archive");
        await Seed(temp, 500);

        var archive = await CaptureAsync(() => new StoreCommands().Archive(root: temp.Root, before: "2026-08-21"));
        Assert.Contains("1 días archivados", archive, StringComparison.Ordinal);
        Assert.Empty(temp.Segments());

        await using (var reader = LogStore.OpenReadOnly(temp.Root))
        {
            Assert.Equal(500, await reader.CountAsync(new LogQuery()));
        }

        var prune = await CaptureAsync(() =>
        {
            new StoreCommands().Prune(before: "2026-08-21", root: temp.Root, archive: true, yes: true);
            return Task.CompletedTask;
        });
        Assert.Contains("Borrados", prune, StringComparison.Ordinal);

        await using var empty = LogStore.OpenReadOnly(temp.Root);
        Assert.Equal(0, await empty.CountAsync(new LogQuery()));
    }

    [Theory]
    [InlineData("now")]
    [InlineData("today")]
    [InlineData("yesterday")]
    [InlineData("tomorrow")]
    [InlineData("-2h")]
    [InlineData("-30m")]
    [InlineData("-7d")]
    [InlineData("2026-08-20")]
    [InlineData("2026-08-20T10:15:00")]
    public void TimeArg_EntiendeLosAtajos(string text)
        => Assert.True(TimeArg.TryParse(text, out _));

    [Theory]
    [InlineData("el martes")]
    [InlineData("")]
    [InlineData("-2x")]
    public void TimeArg_RechazaLoQueNoEntiende(string text)
        => Assert.False(TimeArg.TryParse(text, out _));

    /// <summary>Ejecuta un comando capturando lo que escribe en la consola.</summary>
    private static async Task<string> CaptureAsync(Func<Task> action)
    {
        var original = Console.Out;
        var buffer = new StringWriter();
        Console.SetOut(buffer);
        try
        {
            await action();
        }
        finally
        {
            Console.SetOut(original);
        }
        return buffer.ToString().Replace("\r", string.Empty);
    }
}
