using System.Globalization;

namespace LogStorage.Tray;

/// <summary>Foto del almacén: lo que el panel enseña en el bloque "Almacén".</summary>
/// <param name="Exists">La carpeta existe en disco.</param>
/// <param name="HasManifest">Hay un <c>manifest.idx</c> legible.</param>
/// <param name="Days">Días con datos.</param>
/// <param name="Records">Registros totales.</param>
/// <param name="Bytes">Bytes en disco.</param>
/// <param name="OldestDay">Día más antiguo con datos.</param>
/// <param name="NewestDay">Día más reciente con datos.</param>
public sealed record StoreSnapshot(
    bool Exists,
    bool HasManifest,
    int Days,
    long Records,
    long Bytes,
    string? OldestDay,
    string? NewestDay)
{
    /// <summary>Lo que se enseña cuando no hay nada que enseñar.</summary>
    public static StoreSnapshot Missing { get; } = new(false, false, 0, 0, 0, null, null);

    /// <summary>Bytes por registro, la métrica que hace que todo esto tenga gracia.</summary>
    public double BytesPerRecord => Records == 0 ? 0 : (double)Bytes / Records;
}

/// <summary>Lee el estado del almacén sin escribir en él.</summary>
/// <remarks>
/// Siempre en solo lectura y sin reconstruir el manifiesto: el panel no debe escribir nada
/// dentro del almacén por su cuenta. Reconstruirlo es una acción explícita del usuario
/// (<see cref="RefreshAsync"/>), no un efecto secundario de abrir una ventana.
/// </remarks>
public static class StoreReader
{
    /// <summary>Lee el manifiesto guardado. No toca disco más allá de eso.</summary>
    public static Task<StoreSnapshot> ReadAsync(string rootPath, CancellationToken cancellationToken = default)
        => LoadAsync(rootPath, rebuild: false, cancellationToken);

    /// <summary>Reconstruye el manifiesto recorriendo los segmentos y devuelve la foto nueva.</summary>
    public static Task<StoreSnapshot> RefreshAsync(string rootPath, CancellationToken cancellationToken = default)
        => LoadAsync(rootPath, rebuild: true, cancellationToken);

    /// <summary>Comprueba la integridad de todos los segmentos.</summary>
    public static Task<VerifyReport> VerifyAsync(string rootPath, CancellationToken cancellationToken = default)
        => Task.Run(() => Maintenance(rootPath).Verify(cancellationToken: cancellationToken), cancellationToken);

    private static async Task<StoreSnapshot> LoadAsync(string rootPath, bool rebuild, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return StoreSnapshot.Missing;

        var maintenance = Maintenance(rootPath);
        var manifest = rebuild
            ? await maintenance.ReindexAsync(cancellationToken).ConfigureAwait(false)
            : await maintenance.GetManifestAsync(rebuildIfMissing: false, cancellationToken).ConfigureAwait(false);

        var days = manifest.Days;
        return new StoreSnapshot(
            Exists: true,
            HasManifest: rebuild || days.Count > 0 || File.Exists(Path.Combine(rootPath, "manifest.idx")),
            Days: days.Count,
            Records: manifest.TotalRecords,
            Bytes: manifest.TotalBytes,
            OldestDay: days.Count > 0 ? days[0].Date : null,
            NewestDay: days.Count > 0 ? days[^1].Date : null);
    }

    private static LogStoreMaintenance Maintenance(string rootPath)
        => new(new LogStoreOptions { RootPath = rootPath, ReadOnly = true });
}

/// <summary>Formatos cortos para la ventana.</summary>
public static class Format
{
    /// <summary>Bytes en la unidad que toque.</summary>
    public static string Bytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return string.Create(CultureInfo.InvariantCulture, $"{size:0.##} {units[unit]}");
    }

    /// <summary>Números grandes con separador de millar y sufijo cuando pasan del millón.</summary>
    public static string Count(long value) => value switch
    {
        >= 1_000_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000_000d:0.##} G"),
        >= 1_000_000 => string.Create(CultureInfo.InvariantCulture, $"{value / 1_000_000d:0.##} M"),
        _ => value.ToString("N0", CultureInfo.InvariantCulture),
    };

    /// <summary>Un instante UTC en hora local y en lenguaje de andar por casa.</summary>
    public static string Moment(DateTimeOffset? value)
    {
        if (value is null) return "nunca";

        var local = value.Value.ToLocalTime();
        var age = DateTimeOffset.Now - local;

        if (age < TimeSpan.Zero) return local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (age < TimeSpan.FromMinutes(1)) return "hace un momento";
        if (age < TimeSpan.FromHours(1)) return $"hace {(int)age.TotalMinutes} min";
        if (local.Date == DateTimeOffset.Now.Date) return "hoy " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
        if (local.Date == DateTimeOffset.Now.Date.AddDays(-1)) return "ayer " + local.ToString("HH:mm", CultureInfo.CurrentCulture);
        return local.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);
    }

    /// <summary>Cuánto falta para un instante futuro.</summary>
    public static string Countdown(DateTimeOffset? value)
    {
        if (value is null) return "-";

        var remaining = value.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "en cualquier momento";
        if (remaining < TimeSpan.FromMinutes(1)) return $"en {(int)remaining.TotalSeconds} s";
        if (remaining < TimeSpan.FromHours(1)) return $"en {(int)remaining.TotalMinutes} min";
        return $"en {remaining.TotalHours:0.#} h";
    }
}
