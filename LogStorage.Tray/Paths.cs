namespace LogStorage.Tray;

/// <summary>
/// Dónde está cada cosa. Todo lo que el panel necesita localizar en disco pasa por aquí.
/// </summary>
/// <remarks>
/// La carpeta de datos es <c>%ProgramData%\LogStorage</c>: ahí viven el
/// <c>appsettings.json</c> del worker y su <c>status.json</c>. El instalador la crea con
/// permisos de escritura para los usuarios, para que cambiar la configuración desde el
/// panel no exija elevar.
/// </remarks>
public static class Paths
{
    /// <summary>Nombre del servicio de Windows. Debe coincidir con <c>WorkerConstants.ServiceName</c>.</summary>
    public const string ServiceName = "LogStorageMaintenance";

    /// <summary>Nombre visible del servicio.</summary>
    public const string ServiceDisplayName = "LogStorage Maintenance";

    private static string? _dataDirectory;

    /// <summary>Carpeta de datos del worker. Se puede sobrescribir con <c>--data</c>.</summary>
    public static string DataDirectory => _dataDirectory ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "LogStorage");

    /// <summary>Permite apuntar a otra carpeta de datos (para desarrollo).</summary>
    public static void UseDataDirectory(string directory) => _dataDirectory = Path.GetFullPath(directory);

    /// <summary>Configuración del worker.</summary>
    public static string ConfigFile => Path.Combine(DataDirectory, "appsettings.json");

    /// <summary>Estado del último ciclo, escrito por el worker.</summary>
    public static string StatusFile => Path.Combine(DataDirectory, "status.json");

    /// <summary>Carpeta donde está instalado el panel (y, con él, el worker y logtool).</summary>
    public static string InstallDirectory => AppContext.BaseDirectory;

    /// <summary>Ejecutable del worker, si está junto al panel.</summary>
    public static string? WorkerExecutable => Find("LogStorage.Worker.exe");

    /// <summary>Ejecutable del CLI, si está junto al panel.</summary>
    public static string? LogToolExecutable => Find("logtool.exe");

    /// <summary>Busca un ejecutable junto al panel; en desarrollo, también en las carpetas de compilación hermanas.</summary>
    private static string? Find(string fileName)
    {
        var beside = Path.Combine(InstallDirectory, fileName);
        if (File.Exists(beside)) return beside;

        // Ejecutando desde bin\Debug\net10.0-windows: mira en el bin del proyecto hermano.
        var project = fileName.Equals("logtool.exe", StringComparison.OrdinalIgnoreCase)
            ? "LogStorage.CLI"
            : "LogStorage.Worker";

        DirectoryInfo? directory = new(InstallDirectory);
        for (var i = 0; i < 6 && directory is not null; i++, directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, project, "bin");
            if (!Directory.Exists(candidate)) continue;

            var found = Directory.EnumerateFiles(candidate, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (found is not null) return found;
        }

        return null;
    }
}
