using System.Globalization;
using System.Text;
using Cysharp.Text;
using Kokuban;

namespace LogStorage.Cli;

/// <summary>Presentación en consola: colores por nivel, tamaños legibles y tablas simples.</summary>
internal static class Output
{
    /// <summary>Pinta un nivel con su color: gris para trazas, rojo para errores.</summary>
    public static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => Chalk.BrightBlack + level.ToShortName(),
        LogLevel.Debug => Chalk.Gray + level.ToShortName(),
        LogLevel.Information => Chalk.BrightGreen + level.ToShortName(),
        LogLevel.Warning => Chalk.BrightYellow + level.ToShortName(),
        LogLevel.Error => Chalk.BrightRed + level.ToShortName(),
        LogLevel.Critical => Chalk.BgRed.BrightWhite + level.ToShortName(),
        _ => level.ToShortName(),
    };

    public static string Dim(string text) => Chalk.BrightBlack + text;

    public static string Accent(string text) => Chalk.BrightCyan + text;

    public static string Title(string text) => Chalk.BrightWhite.Bold + text;

    public static string Ok(string text) => Chalk.BrightGreen + text;

    public static string Warn(string text) => Chalk.BrightYellow + text;

    public static string Bad(string text) => Chalk.BrightRed + text;

    /// <summary>Una línea de log lista para leer: hora, nivel, servicio, traza y mensaje.</summary>
    public static string Record(in LogRecord record, bool showHost)
    {
        using var builder = ZString.CreateStringBuilder();

        builder.Append(Dim(record.TimestampUtc.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)));
        builder.Append(' ');
        builder.Append(Level(record.Level).PadRight(5 + AnsiOverhead(record.Level)));
        builder.Append(' ');
        builder.Append(Accent(record.Service));

        if (showHost && record.Host.Length > 0)
        {
            builder.Append('@');
            builder.Append(Dim(record.Host));
        }

        if (record.TraceId is { Length: > 0 } trace)
        {
            builder.Append(' ');
            builder.Append(Dim("[" + trace + "]"));
        }

        builder.Append("  ");
        builder.Append(record.Message);

        if (!record.Payload.IsEmpty)
        {
            builder.Append(' ');
            builder.Append(Dim(Encoding.UTF8.GetString(record.Payload.Span)));
        }

        return builder.ToString();
    }

    /// <summary>Bytes en la unidad que toque, para que un humano lo lea de un vistazo.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return ZString.Format("{0:0.##} {1}", value, units[unit]);
    }

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Imprime una tabla alineada, con la cabecera resaltada.</summary>
    public static void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++) widths[i] = headers[i].Length;
        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count && i < row.Length; i++) widths[i] = Math.Max(widths[i], row[i].Length);
        }

        using (var header = ZString.CreateStringBuilder())
        {
            for (var i = 0; i < headers.Count; i++)
            {
                header.Append(headers[i].PadRight(widths[i]));
                if (i < headers.Count - 1) header.Append("  ");
            }
            Console.WriteLine(Title(header.ToString()));
        }

        foreach (var row in rows)
        {
            using var line = ZString.CreateStringBuilder();
            for (var i = 0; i < headers.Count; i++)
            {
                var cell = i < row.Length ? row[i] : string.Empty;
                line.Append(cell.PadRight(widths[i]));
                if (i < headers.Count - 1) line.Append("  ");
            }
            Console.WriteLine(line.ToString());
        }
    }

    /// <summary>Caracteres invisibles que añade el coloreado, para que el PadRight cuadre.</summary>
    private static int AnsiOverhead(LogLevel level)
    {
        var colored = Level(level);
        return colored.Length - level.ToShortName().Length;
    }
}
