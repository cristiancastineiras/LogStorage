namespace LogStorage.Worker;

/// <summary>Nombres compartidos entre el worker, el instalador y el panel de control.</summary>
/// <remarks>
/// Si cambias <see cref="ServiceName"/> hay que cambiarlo también en
/// <c>installer/LogStorage.iss</c> y en <c>LogStorage.Tray/Paths.cs</c>, o el panel no
/// encontrará el servicio.
/// </remarks>
public static class WorkerConstants
{
    /// <summary>Nombre del servicio de Windows (el que usa <c>sc.exe</c>).</summary>
    public const string ServiceName = "LogStorageMaintenance";

    /// <summary>Nombre visible del servicio en el gestor de servicios.</summary>
    public const string DisplayName = "LogStorage Maintenance";

    /// <summary>Fichero donde el worker deja el resultado del último ciclo.</summary>
    public const string StatusFileName = "status.json";
}
