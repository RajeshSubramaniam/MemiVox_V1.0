using MyScorer.Application.Data;
using MyScorer.Application.Services;
using MyScorer.Application.Services.Providers;
using MyScorer.Api.Controllers;

namespace MyScorer.Api.Services;

/// <summary>
/// Periodic background service that prevents unbounded memory and database growth.
/// Runs every 5 minutes to:
///   - Evict stale entries from all in-memory caches
///   - Purge completed/stale device commands older than 24 hours
///   - Recover stuck "Delivered" commands older than 10 minutes
///   - Log memory usage for observability
/// </summary>
public sealed class MaintenanceService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MaintenanceService> _logger;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Observable state for diagnostics
    private static DateTime? _lastRunAt;
    private static DateTime? _lastSuccessAt;
    private static int _cycleCount;
    private static int _totalEvicted;
    private static int _totalPurged;
    private static int _totalRecovered;

    public static (DateTime? LastRunAt, DateTime? LastSuccessAt, int CycleCount,
                    int TotalEvicted, int TotalPurged, int TotalRecovered) GetStats()
        => (_lastRunAt, _lastSuccessAt, _cycleCount, _totalEvicted, _totalPurged, _totalRecovered);

    public MaintenanceService(IServiceProvider services, ILogger<MaintenanceService> logger)
    {
        _services = services;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay first run to let startup complete
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            _lastRunAt = DateTime.UtcNow;
            try
            {
                var evicted = EvictStaleCaches();
                var purged = await PurgeOldCommandsAsync(stoppingToken);
                var recovered = await RecoverStuckCommandsAsync(stoppingToken);

                _cycleCount++;
                _totalEvicted += evicted;
                _totalPurged += purged;
                _totalRecovered += recovered;
                _lastSuccessAt = DateTime.UtcNow;

                var memMB = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
                _logger.LogInformation(
                    "Maintenance[#{Cycle}]: evicted={Evicted} cache, purged={Purged} cmds, recovered={Recovered} stuck, mem={MemMB:F1}MB",
                    _cycleCount, evicted, purged, recovered, memMB);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Maintenance cycle failed");
            }

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private int EvictStaleCaches()
    {
        var total = 0;

        // Score extraction cache (static ConcurrentDictionary)
        total += ScoreExtractionService.EvictStaleEntries();

        // Legacy in-memory heartbeats (static ConcurrentDictionary)
        total += DeviceController.EvictStaleHeartbeats();

        // Rate-limit tracking (static ConcurrentDictionary)
        total += SetupController.EvictStaleRateLimits();

        // PlayHQ spectator WebSocket cache (singleton instance)
        var spectator = _services.GetService<PlayHqSpectatorService>();
        if (spectator != null)
            total += spectator.EvictStaleCache();

        // In-memory match state (singleton instance)
        var matchState = _services.GetService<IMatchStateService>();
        if (matchState is InMemoryMatchStateService inMemory)
            total += inMemory.EvictStaleEntries();

        return total;
    }

    private async Task<int> PurgeOldCommandsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyScorerDbContext>();

        var cutoff = DateTime.UtcNow.AddHours(-24);

        // Batch delete to avoid loading all rows into memory
        const int batchSize = 500;
        var totalPurged = 0;
        int deleted;
        do
        {
            var batch = db.DeviceCommands
                .Where(c => c.CreatedAt < cutoff)
                .Take(batchSize)
                .ToList();

            if (batch.Count == 0)
                break;

            db.DeviceCommands.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            totalPurged += batch.Count;
            deleted = batch.Count;
        }
        while (deleted == batchSize);

        return totalPurged;
    }

    /// <summary>
    /// Recover commands stuck in "Delivered" state for over 10 minutes.
    /// This happens when a Pi polls a command but crashes before ack.
    /// Reset them to "Pending" so they get re-delivered.
    /// </summary>
    private async Task<int> RecoverStuckCommandsAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MyScorerDbContext>();

        var stuckCutoff = DateTime.UtcNow.AddMinutes(-10);
        var stuck = db.DeviceCommands
            .Where(c => c.Status == "Delivered" && c.CreatedAt < stuckCutoff)
            .ToList();

        foreach (var cmd in stuck)
            cmd.Status = "Pending";

        if (stuck.Count > 0)
            await db.SaveChangesAsync(ct);

        return stuck.Count;
    }
}
