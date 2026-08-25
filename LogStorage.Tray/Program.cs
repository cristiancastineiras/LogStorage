namespace LogStorage.Tray;

/// <summary>Punto de entrada del panel de control.</summary>
internal static class Program
{
    /// <summary>
    /// Argumentos admitidos:
    /// <list type="bullet">
    ///   <item><c>--tray</c>: arranca minimizado en la bandeja (lo usa el arranque de sesión).</item>
    ///   <item><c>--data &lt;carpeta&gt;</c>: usa otra carpeta de datos en vez de <c>%ProgramData%\LogStorage</c>.</item>
    /// </list>
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--data", StringComparison.OrdinalIgnoreCase))
            {
                Paths.UseDataDirectory(args[i + 1]);
            }
        }

        // Una sola instancia: dos paneles escribiendo el mismo appsettings.json es pedir guerra.
        using var single = new Mutex(initiallyOwned: true, @"Local\LogStorage.Tray.SingleInstance", out var isFirst);
        if (!isFirst) return;

        ApplicationConfiguration.Initialize();

        using var form = new MainForm();
        if (!args.Any(argument => argument.Equals("--tray", StringComparison.OrdinalIgnoreCase)))
        {
            form.Show();
        }

        // El bucle de mensajes no se ata a la ventana: cerrarla la esconde en la bandeja y la
        // aplicación sigue viva hasta que se elige "Salir".
        Application.Run(new ApplicationContext());
    }
}
