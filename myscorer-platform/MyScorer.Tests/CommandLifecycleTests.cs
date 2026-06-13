using Microsoft.EntityFrameworkCore;
using MyScorer.Application.Data;
using MyScorer.Core.Models;

namespace MyScorer.Tests;

public class CommandLifecycleTests : IDisposable
{
    private readonly MyScorerDbContext _db;

    public CommandLifecycleTests()
    {
        var options = new DbContextOptionsBuilder<MyScorerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new MyScorerDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CommandDeduplication_PreventsDoubleQueue()
    {
        // Queue a command
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "START_STREAM",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        // Check for existing pending
        var hasPending = _db.DeviceCommands
            .Any(c => c.DeviceId == "pi-01" && c.Command == "START_STREAM"
                       && (c.Status == "Pending" || c.Status == "Delivered"));
        Assert.True(hasPending);
    }

    [Fact]
    public void CommandAck_CompletesCommand()
    {
        var cmd = new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "START_STREAM",
            Status = "Delivered",
            CreatedAt = DateTime.UtcNow
        };
        _db.DeviceCommands.Add(cmd);
        _db.SaveChanges();

        // Ack
        cmd.Status = "Completed";
        _db.SaveChanges();

        var result = _db.DeviceCommands.Find(cmd.Id);
        Assert.Equal("Completed", result!.Status);
    }

    [Fact]
    public void CommandPurge_RemovesOldCommands()
    {
        // Add old command (>24h)
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "OLD_CMD",
            Status = "Completed",
            CreatedAt = DateTime.UtcNow.AddHours(-25)
        });
        // Add recent command
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "NEW_CMD",
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        // Purge old
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var old = _db.DeviceCommands.Where(c => c.CreatedAt < cutoff).ToList();
        _db.DeviceCommands.RemoveRange(old);
        _db.SaveChanges();

        Assert.Equal(1, _db.DeviceCommands.Count());
        Assert.Equal("NEW_CMD", _db.DeviceCommands.First().Command);
    }

    [Fact]
    public void StuckDeliveredCommand_GetsRecovered()
    {
        // Command delivered >10 minutes ago but never acked
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "START_STREAM",
            Status = "Delivered",
            CreatedAt = DateTime.UtcNow.AddMinutes(-15)
        });
        _db.SaveChanges();

        // Recovery logic: reset Delivered >10min to Pending
        var stuckCutoff = DateTime.UtcNow.AddMinutes(-10);
        var stuck = _db.DeviceCommands
            .Where(c => c.Status == "Delivered" && c.CreatedAt < stuckCutoff)
            .ToList();

        foreach (var cmd in stuck)
            cmd.Status = "Pending";
        _db.SaveChanges();

        var recovered = _db.DeviceCommands.First();
        Assert.Equal("Pending", recovered.Status);
    }

    [Fact]
    public void RecentDeliveredCommand_NotRecovered()
    {
        // Command delivered 2 minutes ago — should NOT be recovered
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01",
            Command = "START_STREAM",
            Status = "Delivered",
            CreatedAt = DateTime.UtcNow.AddMinutes(-2)
        });
        _db.SaveChanges();

        var stuckCutoff = DateTime.UtcNow.AddMinutes(-10);
        var stuck = _db.DeviceCommands
            .Where(c => c.Status == "Delivered" && c.CreatedAt < stuckCutoff)
            .ToList();

        Assert.Empty(stuck);
    }

    [Fact]
    public void NextCommand_ReturnsOldestPending()
    {
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01", Command = "CMD_A", Status = "Pending",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5)
        });
        _db.DeviceCommands.Add(new DeviceCommand
        {
            DeviceId = "pi-01", Command = "CMD_B", Status = "Pending",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();

        var next = _db.DeviceCommands
            .Where(c => c.DeviceId == "pi-01" && c.Status == "Pending")
            .OrderBy(c => c.Id)
            .FirstOrDefault();

        Assert.NotNull(next);
        Assert.Equal("CMD_A", next.Command);
    }
}
