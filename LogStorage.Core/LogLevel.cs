namespace LogStorage;

/// <summary>
/// Severidad de un registro. Los valores coinciden con
/// <c>Microsoft.Extensions.Logging.LogLevel</c> para poder convertir con un simple cast.
/// </summary>
public enum LogLevel : byte
{
    Trace = 0,
    Debug = 1,
    Information = 2,
    Warning = 3,
    Error = 4,
    Critical = 5,
    None = 6,
}

/// <summary>Utilidades de presentación para <see cref="LogLevel"/> sin asignaciones.</summary>
public static class LogLevels
{
    private static readonly string[] Names = ["TRACE", "DEBUG", "INFO", "WARN", "ERROR", "FATAL", "NONE"];

    /// <summary>Nombre corto en mayúsculas, cacheado (no asigna).</summary>
    public static string ToShortName(this LogLevel level)
        => (byte)level < Names.Length ? Names[(byte)level] : "?";

    /// <summary>Convierte texto ("warn", "Warning", "3"…) a <see cref="LogLevel"/>.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out LogLevel level)
    {
        level = LogLevel.Trace;
        if (text.IsEmpty) return false;

        if (byte.TryParse(text, out var numeric) && numeric <= (byte)LogLevel.None)
        {
            level = (LogLevel)numeric;
            return true;
        }

        Span<char> upper = stackalloc char[16];
        if (text.Length > upper.Length) return false;
        var length = text.ToUpperInvariant(upper);
        var value = upper[..length];

        if (value.SequenceEqual("TRACE") || value.SequenceEqual("TRC") || value.SequenceEqual("VERBOSE")) { level = LogLevel.Trace; return true; }
        if (value.SequenceEqual("DEBUG") || value.SequenceEqual("DBG")) { level = LogLevel.Debug; return true; }
        if (value.SequenceEqual("INFO") || value.SequenceEqual("INFORMATION") || value.SequenceEqual("INF")) { level = LogLevel.Information; return true; }
        if (value.SequenceEqual("WARN") || value.SequenceEqual("WARNING") || value.SequenceEqual("WRN")) { level = LogLevel.Warning; return true; }
        if (value.SequenceEqual("ERROR") || value.SequenceEqual("ERR")) { level = LogLevel.Error; return true; }
        if (value.SequenceEqual("FATAL") || value.SequenceEqual("CRITICAL") || value.SequenceEqual("CRIT")) { level = LogLevel.Critical; return true; }
        if (value.SequenceEqual("NONE")) { level = LogLevel.None; return true; }

        return false;
    }
}
