using ConsoleAppFramework;
using LogStorage.Cli;
using System.Diagnostics;

// logtool: la navaja suiza de LogStorage.
//   logtool search --from -2h --contains pedido --level warn
//   logtool ls
//   logtool archive --before today
//   logtool bench -n 2000000 -t 8

var app = ConsoleApp.Create();

app.UseFilter<ErrorFilter>();

app.Add<QueryCommands>();
app.Add<StoreCommands>();
app.Add<BenchCommands>();

await app.RunAsync(args);

// Mantener la ventana abierta cuando se ejecuta desde el depurador
if (Debugger.IsAttached)
{
    Console.WriteLine("Presione una tecla para salir...");
    Console.ReadKey(true);
}

/// <summary>
/// Convierte los errores esperables (rutas que no existen, argumentos mal escritos) en un
/// mensaje claro en rojo y código de salida 1, en vez de volcar una traza de excepción.
/// </summary>
internal sealed class ErrorFilter(ConsoleAppFilter next) : ConsoleAppFilter(next)
{
    public override async Task InvokeAsync(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        try
        {
            await Next.InvokeAsync(context, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine(Output.Dim("Cancelado."));
            Environment.ExitCode = 130;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or DirectoryNotFoundException or FileNotFoundException or InvalidDataException)
        {
            Console.Error.WriteLine(Output.Bad(ex.Message));
            Environment.ExitCode = 1;
        }
    }
}
