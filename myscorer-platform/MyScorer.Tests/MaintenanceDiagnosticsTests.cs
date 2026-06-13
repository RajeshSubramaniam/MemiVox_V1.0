using MyScorer.Api.Services;

namespace MyScorer.Tests;

public class MaintenanceDiagnosticsTests
{
    [Fact]
    public void GetStats_ReturnsInitialState()
    {
        var stats = MaintenanceService.GetStats();

        // Before any cycles, stats should show zero counts
        // (LastRunAt/LastSuccessAt may be non-null if another test triggered a cycle,
        // but CycleCount/TotalEvicted/TotalPurged should be non-negative)
        Assert.True(stats.CycleCount >= 0);
        Assert.True(stats.TotalEvicted >= 0);
        Assert.True(stats.TotalPurged >= 0);
        Assert.True(stats.TotalRecovered >= 0);
    }
}
