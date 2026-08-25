using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace LogStorage.Tray;

/// <summary>Estado del servicio tal y como lo enseña el panel.</summary>
public enum ServiceState
{
    /// <summary>No hay ningún servicio con ese nombre registrado.</summary>
    NotInstalled,

    /// <summary>Instalado pero parado.</summary>
    Stopped,

    /// <summary>Corriendo.</summary>
    Running,

    /// <summary>Arrancando.</summary>
    Starting,

    /// <summary>Parando.</summary>
    Stopping,

    /// <summary>Pausado o en un estado que no distinguimos.</summary>
    Other,
}

/// <summary>
/// Arranca, para, instala y desinstala el servicio de mantenimiento.
/// </summary>
/// <remarks>
/// <para>
/// Primero intenta la vía directa con <see cref="ServiceController"/>. Si el usuario no es
/// administrador salta <see cref="Win32Exception"/> con "acceso denegado", y entonces se
/// reintenta lanzando <c>sc.exe</c> con el verbo <c>runas</c>, que es lo que dispara el
/// diálogo de UAC. Así, quien ya va elevado no ve ningún aviso, y quien no, ve exactamente
/// uno.
/// </para>
/// <para>
/// Instalar y desinstalar siempre pasan por <c>sc.exe</c> elevado: crear un servicio no se
/// puede hacer de otra forma sin elevación.
/// </para>
/// </remarks>
public static class ServiceManager
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Estado actual del servicio.</summary>
    public static ServiceState GetState()
    {
        try
        {
            using var controller = new ServiceController(Paths.ServiceName);
            return controller.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
                ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
                _ => ServiceState.Other,
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController lanza esto cuando el servicio no existe.
            return ServiceState.NotInstalled;
        }
    }

    /// <summary>Arranca el servicio y espera a que esté corriendo.</summary>
    public static Task StartAsync() => Task.Run(() =>
    {
        try
        {
            using var controller = new ServiceController(Paths.ServiceName);
            if (controller.Status != ServiceControllerStatus.Running)
            {
                controller.Start();
                controller.WaitForStatus(ServiceControllerStatus.Running, Timeout);
            }
        }
        catch (Exception ex) when (NeedsElevation(ex))
        {
            RunElevated($"start {Paths.ServiceName}");
            WaitFor(ServiceControllerStatus.Running);
        }
    });

    /// <summary>Para el servicio y espera a que esté parado.</summary>
    public static Task StopAsync() => Task.Run(() =>
    {
        try
        {
            using var controller = new ServiceController(Paths.ServiceName);
            if (controller.Status != ServiceControllerStatus.Stopped)
            {
                controller.Stop();
                controller.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
            }
        }
        catch (Exception ex) when (NeedsElevation(ex))
        {
            RunElevated($"stop {Paths.ServiceName}");
            WaitFor(ServiceControllerStatus.Stopped);
        }
    });

    /// <summary>Para y vuelve a arrancar.</summary>
    public static async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Registra el servicio apuntando al ejecutable del worker y a la carpeta de datos.
    /// </summary>
    /// <remarks>
    /// El <c>--contentRoot</c> es lo que hace que el worker encuentre su <c>appsettings.json</c>
    /// en <c>%ProgramData%</c> en vez de en <c>C:\Windows\System32</c>, que es el directorio
    /// de trabajo de cualquier servicio.
    /// </remarks>
    public static Task InstallAsync(string workerExecutable) => Task.Run(() =>
    {
        var binPath = $"\\\"{workerExecutable}\\\" --contentRoot \\\"{Paths.DataDirectory}\\\"";

        RunElevated($"create {Paths.ServiceName} binPath= \"{binPath}\" start= auto DisplayName= \"{Paths.ServiceDisplayName}\"");
        RunElevated($"description {Paths.ServiceName} \"Archiva y poda el almacen de logs de LogStorage.\"");
    });

    /// <summary>Para y borra el servicio.</summary>
    public static Task UninstallAsync() => Task.Run(() =>
    {
        RunElevated($"stop {Paths.ServiceName}", ignoreFailure: true);
        RunElevated($"delete {Paths.ServiceName}");
    });

    /// <summary>Cierto si el proceso actual ya va elevado.</summary>
    public static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool NeedsElevation(Exception ex)
        => ex is Win32Exception or UnauthorizedAccessException
        || (ex is InvalidOperationException && ex.InnerException is Win32Exception);

    private static void RunElevated(string arguments, bool ignoreFailure = false)
    {
        var info = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = true,   // obligatorio para que "runas" dispare el UAC
            Verb = IsElevated() ? string.Empty : "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException("No se ha podido lanzar sc.exe.");

        process.WaitForExit((int)Timeout.TotalMilliseconds);

        if (!ignoreFailure && process.HasExited && process.ExitCode != 0)
        {
            throw new InvalidOperationException($"sc.exe {arguments.Split(' ')[0]} devolvió el código {process.ExitCode}.");
        }
    }

    private static void WaitFor(ServiceControllerStatus status)
    {
        using var controller = new ServiceController(Paths.ServiceName);
        controller.WaitForStatus(status, Timeout);
    }
}
