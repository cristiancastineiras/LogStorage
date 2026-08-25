using System.Runtime.Versioning;

namespace LogStorage.Worker;

/// <summary>
/// Engancha el registro de eventos de Windows cuando el worker corre como servicio.
/// </summary>
/// <remarks>
/// Está en su propia clase marcada con <see cref="SupportedOSPlatformAttribute"/> porque el
/// analizador de plataformas no sigue un <c>if (OperatingSystem.IsWindows())</c> hasta dentro
/// de la lambda de configuración. Aquí la promesa queda escrita en el tipo.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsEventLog
{
    /// <summary>Manda los mensajes del worker al registro de eventos, sección Aplicación.</summary>
    public static void Add(ILoggingBuilder logging)
        => logging.AddEventLog(settings =>
        {
            settings.SourceName = WorkerConstants.ServiceName;
            settings.LogName = "Application";
        });
}
