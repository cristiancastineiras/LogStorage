using LogStorage.Storage;
using Microsoft.Extensions.Options;
using ZLogger;

namespace LogStorage.Worker;

/// <summary>
/// Servicio de fondo que mantiene el almacén: archiva días cerrados a Parquet,
/// aplica la retención y reconstruye el manifiesto.
/// </summary>
/// <remarks>
/// No escribe logs en el almacén ni toca los segmentos que otro proceso esté escribiendo:
/// solo trabaja con días ya cerrados, así que puede convivir con la aplicación que ingesta.
/// </remarks>
public sealed class MaintenanceService(
    IOptions<MaintenanceOptions> options,
    ILogger<MaintenanceService> logger,
    StatusReporter? status = null) : BackgroundService
{
    private readonly MaintenanceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _options.Validate();

        var maintenance = new LogStoreMaintenance(new LogStoreOptions
        {
            RootPath = _options.RootPath,
            CompressionLevel = _options.CompressionLevel,
            ReadOnly = true,
        });

        logger.ZLogInformation($"Mantenimiento en marcha sobre '{_options.RootPath}' cada {_options.Interval}.");
        Report(new WorkerStatus
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            NextCycleUtc = DateTimeOffset.UtcNow + _options.InitialDelay,
            Ok = true,
            Message = "En marcha, esperando al primer ciclo.",
            RootPath = _options.RootPath,
        });

        if (_options.InitialDelay > TimeSpan.Zero)
        {
            await Task.Delay(_options.InitialDelay, stoppingToken).ConfigureAwait(false);
        }

        using var timer = new PeriodicTimer(_options.Interval);
        do
        {
            try
            {
                var cycle = await RunCycleAsync(maintenance, stoppingToken).ConfigureAwait(false);
                Report(Describe(cycle, maintenance, ok: true, error: null));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Un ciclo fallido no debe tumbar el worker: se reintenta en el siguiente.
                logger.ZLogError(ex, $"El ciclo de mantenimiento ha fallado. Se reintentará en {_options.Interval}.");
                Report(Describe(CycleReport.Empty, maintenance, ok: false, error: ex));
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));

        logger.ZLogInformation($"Mantenimiento detenido.");
        Report(new WorkerStatus
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            Ok = true,
            Message = "Detenido.",
            RootPath = _options.RootPath,
        });
    }

    /// <summary>Un ciclo completo: archivar, aplicar retención y reindexar.</summary>
    public async Task<CycleReport> RunCycleAsync(LogStoreMaintenance maintenance, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var started = TimeProvider.System.GetTimestamp();
        var report = new CycleReport();

        if (_options.ArchiveAfterDays > 0)
        {
            var cutoff = today.AddDays(-_options.ArchiveAfterDays);
            var summary = await maintenance.ArchiveOlderThanAsync(
                cutoff,
                deleteSegments: true,
                progress: new SyncProgress<ArchiveResult>(LogArchived),
                cancellationToken).ConfigureAwait(false);

            report.DaysArchived = summary.Days;

            if (summary.Days > 0)
            {
                logger.ZLogInformation($"Archivados {summary.Days} días ({summary.RecordCount:N0} registros): {Mb(summary.SegmentBytes)} MB de segmentos -> {Mb(summary.ArchiveBytes)} MB de Parquet.");
            }
        }

        if (_options.SegmentRetentionDays > 0)
        {
            var result = maintenance.PruneSegmentsOlderThan(today.AddDays(-_options.SegmentRetentionDays));
            report.FilesPruned += result.Files;
            if (result.Files > 0) logger.ZLogWarning($"Retención: borrados {result.Files} segmentos sin archivar ({Mb(result.BytesFreed)} MB).");
        }

        if (_options.ArchiveRetentionDays > 0)
        {
            var result = maintenance.PruneArchiveOlderThan(today.AddDays(-_options.ArchiveRetentionDays));
            report.FilesPruned += result.Files;
            if (result.Files > 0) logger.ZLogInformation($"Retención: borrados {result.Files} Parquet caducados ({Mb(result.BytesFreed)} MB).");
        }

        if (_options.Verify)
        {
            var verification = maintenance.Verify(cancellationToken: cancellationToken);
            report.Problems = verification.Problems.Count;
            if (verification.Ok) logger.ZLogInformation($"Integridad correcta: {verification.Segments} segmentos, {verification.Blocks} bloques, {verification.Records:N0} registros.");
            else foreach (var problem in verification.Problems) logger.ZLogError($"Integridad: {problem}");
        }

        if (_options.Reindex)
        {
            var manifest = await maintenance.ReindexAsync(cancellationToken).ConfigureAwait(false);
            logger.ZLogInformation($"Manifiesto: {manifest.Days.Count} días, {manifest.TotalRecords:N0} registros, {Mb(manifest.TotalBytes)} MB.");
        }

        report.Elapsed = TimeProvider.System.GetElapsedTime(started);
        logger.ZLogDebug($"Ciclo de mantenimiento completado en {report.Elapsed.TotalMilliseconds:N0} ms.");
        return report;
    }

    /// <summary>Compone el retrato que lee el panel de control.</summary>
    private WorkerStatus Describe(CycleReport cycle, LogStoreMaintenance maintenance, bool ok, Exception? error)
    {
        var now = DateTimeOffset.UtcNow;
        var manifest = TryGetManifest(maintenance);
        var days = manifest?.Days;

        var message = error is not null
            ? $"El ciclo falló: {error.Message}"
            : cycle.Problems > 0
                ? $"Ciclo completado con {cycle.Problems} problemas de integridad."
                : Summarize(cycle);

        return new WorkerStatus
        {
            UpdatedUtc = now,
            LastCycleUtc = now,
            NextCycleUtc = now + _options.Interval,
            Ok = ok && cycle.Problems == 0,
            Message = message,
            DurationMs = cycle.Elapsed.TotalMilliseconds,
            DaysArchived = cycle.DaysArchived,
            FilesPruned = cycle.FilesPruned,
            TotalDays = days?.Count ?? 0,
            TotalRecords = manifest?.TotalRecords ?? 0,
            TotalBytes = manifest?.TotalBytes ?? 0,
            OldestDay = days is { Count: > 0 } ? days[0].Date : null,
            NewestDay = days is { Count: > 0 } ? days[^1].Date : null,
            RootPath = _options.RootPath,
        };
    }

    private static string Summarize(CycleReport cycle)
    {
        if (cycle.DaysArchived == 0 && cycle.FilesPruned == 0) return "Ciclo completado. Nada que hacer.";
        var parts = new List<string>(2);
        if (cycle.DaysArchived > 0) parts.Add($"{cycle.DaysArchived} días archivados");
        if (cycle.FilesPruned > 0) parts.Add($"{cycle.FilesPruned} ficheros borrados por retención");
        return "Ciclo completado: " + string.Join(", ", parts) + ".";
    }

    private StoreManifest? TryGetManifest(LogStoreMaintenance maintenance)
    {
        try
        {
            return maintenance.GetManifestAsync(rebuildIfMissing: false).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // El manifiesto es un extra para el panel: si no se puede leer, no pasa nada.
            return null;
        }
    }

    private void Report(WorkerStatus workerStatus) => status?.Write(workerStatus);

    private void LogArchived(ArchiveResult result)
    {
        logger.ZLogInformation($"{result.Day:yyyy-MM-dd}: {result.RecordCount:N0} registros a Parquet ({Mb(result.SegmentBytes)} -> {Mb(result.ArchiveBytes)} MB).");
        if (result.SegmentsKept > 0)
        {
            logger.ZLogWarning($"{result.Day:yyyy-MM-dd}: {result.SegmentsKept} segmentos no se pudieron borrar (¿los tiene abiertos otro proceso?).");
        }
    }

    private static double Mb(long bytes) => Math.Round(bytes / (1024d * 1024d), 2);
}

/// <summary>Qué hizo un ciclo de mantenimiento.</summary>
public sealed class CycleReport
{
    /// <summary>Un ciclo que no hizo nada.</summary>
    public static CycleReport Empty => new();

    /// <summary>Días pasados a Parquet.</summary>
    public int DaysArchived { get; set; }

    /// <summary>Ficheros borrados por retención (segmentos + Parquet).</summary>
    public int FilesPruned { get; set; }

    /// <summary>Problemas encontrados al verificar, si <c>Verify</c> estaba activado.</summary>
    public int Problems { get; set; }

    /// <summary>Lo que tardó el ciclo.</summary>
    public TimeSpan Elapsed { get; set; }
}
