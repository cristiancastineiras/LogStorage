using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LogStorage.Tray;

/// <summary>
/// Los iconos de la bandeja, dibujados en tiempo de ejecución.
/// </summary>
/// <remarks>
/// Tres puntos de color en lugar de tres ficheros <c>.ico</c> en el repositorio: no hay
/// binarios que mantener, se ven bien en cualquier DPI y el color se decide en un sitio.
/// </remarks>
public static partial class TrayIcons
{
    /// <summary>Verde: el servicio está corriendo.</summary>
    public static Icon Running { get; } = Dot(Color.FromArgb(46, 160, 67));

    /// <summary>Rojo: el servicio está parado.</summary>
    public static Icon Stopped { get; } = Dot(Color.FromArgb(218, 54, 51));

    /// <summary>Ámbar: en transición o instalado a medias.</summary>
    public static Icon Busy { get; } = Dot(Color.FromArgb(210, 153, 34));

    /// <summary>Gris: el servicio no está instalado.</summary>
    public static Icon Unknown { get; } = Dot(Color.FromArgb(125, 133, 144));

    /// <summary>El icono que corresponde a un estado.</summary>
    public static Icon For(ServiceState state) => state switch
    {
        ServiceState.Running => Running,
        ServiceState.Stopped => Stopped,
        ServiceState.Starting or ServiceState.Stopping => Busy,
        _ => Unknown,
    };

    /// <summary>El color que corresponde a un estado, para el punto de la ventana.</summary>
    public static Color ColorFor(ServiceState state) => state switch
    {
        ServiceState.Running => Color.FromArgb(46, 160, 67),
        ServiceState.Stopped => Color.FromArgb(218, 54, 51),
        ServiceState.Starting or ServiceState.Stopping => Color.FromArgb(210, 153, 34),
        _ => Color.FromArgb(125, 133, 144),
    };

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);

    private static Icon Dot(Color color)
    {
        const int size = 32;

        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            var body = new Rectangle(3, 3, size - 7, size - 7);

            using var fill = new SolidBrush(color);
            graphics.FillEllipse(fill, body);

            using var edge = new Pen(ControlPaint.Dark(color, 0.15f), 2f);
            graphics.DrawEllipse(edge, body);
        }

        // GetHicon devuelve una copia del icono; hay que clonarlo antes de destruir el handle
        // o el Icon resultante apunta a un handle que Windows ya no conoce.
        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
