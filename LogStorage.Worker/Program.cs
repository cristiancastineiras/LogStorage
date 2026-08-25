using LogStorage.Worker;
using Microsoft.Extensions.Hosting.WindowsServices;
using ZLogger;

var builder = Host.CreateApplicationBuilder(args);

// Permite instalarlo como servicio de Windows (sc.exe create ... ). Fuera de Windows, o
// lanzado desde una consola, no hace nada y arranca como aplicación normal.
builder.Services.AddWindowsService(options => options.ServiceName = WorkerConstants.ServiceName);

builder.Services
    .AddOptions<MaintenanceOptions>()
    .Bind(builder.Configuration.GetSection("LogStorage"))
    .ValidateOnStart();

// El fichero de estado vive junto al appsettings.json (el content root), que es lo que el
// instalador apunta con --contentRoot a C:\ProgramData\LogStorage.
builder.Services.AddSingleton(new StatusReporter(builder.Environment.ContentRootPath));

builder.Services.AddHostedService<MaintenanceService>();

// El worker escribe sus propios mensajes por consola con ZLogger (sin asignaciones al formatear).
// Los logs de la aplicación van al almacén; los del worker, no: así no se muerde la cola.
builder.Logging.ClearProviders();
builder.Logging.AddZLoggerConsole(options =>
{
    options.UsePlainTextFormatter(formatter =>
    {
        formatter.SetPrefixFormatter($"{0:yyyy-MM-dd HH:mm:ss} [{1:short}] ", (in MessageTemplate template, in LogInfo info)
            => template.Format(info.Timestamp, info.LogLevel));
    });
});

// Como servicio no hay consola que mirar: los mensajes van al registro de eventos de Windows.
if (OperatingSystem.IsWindows() && WindowsServiceHelpers.IsWindowsService())
{
    WindowsEventLog.Add(builder.Logging);
}

await builder.Build().RunAsync();
