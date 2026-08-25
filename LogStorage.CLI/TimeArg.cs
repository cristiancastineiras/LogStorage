using System.Globalization;

namespace LogStorage.Cli;

/// <summary>
/// Interpreta los argumentos de fecha del CLI. Además de ISO 8601 admite atajos
/// cómodos de teclear: <c>now</c>, <c>today</c>, <c>yesterday</c>, <c>-2h</c>, <c>-30m</c>, <c>-7d</c>.
/// </summary>
public static class TimeArg
{
    /// <summary>Convierte el texto a un instante UTC. Devuelve null si el texto está vacío.</summary>
    /// <exception cref="FormatException">Si el texto no se entiende.</exception>
    public static DateTimeOffset? Parse(string? text, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (TryParse(text, out var value)) return value;

        throw new FormatException(
            $"No entiendo '{text}' en --{parameterName}. Usa 2026-08-20, 2026-08-20T10:15:00, now, today, yesterday, tomorrow o un relativo como -2h / -30m / -7d.");
    }

    public static bool TryParse(string? text, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var span = text.AsSpan().Trim();
        var now = DateTimeOffset.UtcNow;

        if (span.Equals("now", StringComparison.OrdinalIgnoreCase))
        {
            value = now;
            return true;
        }

        if (span.Equals("today", StringComparison.OrdinalIgnoreCase))
        {
            value = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            return true;
        }

        if (span.Equals("yesterday", StringComparison.OrdinalIgnoreCase))
        {
            value = new DateTimeOffset(now.UtcDateTime.Date.AddDays(-1), TimeSpan.Zero);
            return true;
        }

        if (span.Equals("tomorrow", StringComparison.OrdinalIgnoreCase))
        {
            value = new DateTimeOffset(now.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);
            return true;
        }

        if (TryParseRelative(span, now, out value)) return true;

        if (DateTimeOffset.TryParse(span, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Convierte a día UTC, útil para los comandos que trabajan por días.</summary>
    public static DateOnly? ParseDay(string? text, string parameterName)
    {
        var value = Parse(text, parameterName);
        return value is null ? null : DateOnly.FromDateTime(value.Value.UtcDateTime);
    }

    private static bool TryParseRelative(ReadOnlySpan<char> span, DateTimeOffset now, out DateTimeOffset value)
    {
        value = default;
        if (span.Length < 3) return false;
        if (span[0] != '-' && span[0] != '+') return false;

        var unit = char.ToLowerInvariant(span[^1]);
        if (!double.TryParse(span[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;

        value = unit switch
        {
            's' => now.AddSeconds(amount),
            'm' => now.AddMinutes(amount),
            'h' => now.AddHours(amount),
            'd' => now.AddDays(amount),
            'w' => now.AddDays(amount * 7),
            _ => default,
        };

        return value != default;
    }
}
