using Microsoft.Extensions.Logging;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public class InMemoryMatchStateService : IMatchStateService
{
    private readonly object _lock = new();
    private readonly Dictionary<string, MatchSnapshot> _matches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<InMemoryMatchStateService> _logger;

    public InMemoryMatchStateService(ILogger<InMemoryMatchStateService> logger)
    {
        _logger = logger;
    }

    public MatchSnapshot GetMatch(string setupId)
    {
        lock (_lock)
        {
            if (_matches.TryGetValue(setupId, out var existing))
            {
                _logger.LogDebug("Match state requested for SetupId {SetupId}.", setupId);
                return existing;
            }

            var created = new MatchSnapshot
            {
                SetupId = setupId,
                TeamA = "Sydney CC",
                TeamB = "Parramatta CC",
                Runs = 120,
                Wickets = 3,
                Overs = "18.2",
                Status = "Live",
                UpdatedAt = DateTime.UtcNow
            };

            _matches[setupId] = created;
            _logger.LogInformation("Created default match state for SetupId {SetupId}.", setupId);
            return created;
        }
    }

    public MatchSnapshot UpdateMatch(string setupId, MatchSnapshot snapshot)
    {
        lock (_lock)
        {
            var updated = new MatchSnapshot
            {
                SetupId = string.IsNullOrWhiteSpace(snapshot.SetupId) ? setupId : snapshot.SetupId,
                TeamA = string.IsNullOrWhiteSpace(snapshot.TeamA) ? "Sydney CC" : snapshot.TeamA,
                TeamB = string.IsNullOrWhiteSpace(snapshot.TeamB) ? "Parramatta CC" : snapshot.TeamB,
                Runs = snapshot.Runs,
                Wickets = snapshot.Wickets,
                Overs = string.IsNullOrWhiteSpace(snapshot.Overs) ? "0.0" : snapshot.Overs,
                Status = string.IsNullOrWhiteSpace(snapshot.Status) ? "Live" : snapshot.Status,
                UpdatedAt = DateTime.UtcNow
            };

            _matches[setupId] = updated;
            _logger.LogInformation("Updated match state for SetupId {SetupId} to {Runs}/{Wickets} ({Overs}).", setupId, updated.Runs, updated.Wickets, updated.Overs);
            return updated;
        }
    }

    /// <summary>
    /// Remove entries not updated in the last hour. Called by MaintenanceService.
    /// </summary>
    public int EvictStaleEntries()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            var staleKeys = _matches.Where(kv => kv.Value.UpdatedAt < cutoff).Select(kv => kv.Key).ToList();
            foreach (var key in staleKeys)
                _matches.Remove(key);
            return staleKeys.Count;
        }
    }
}
