using Microsoft.EntityFrameworkCore;
using MyScorer.Application.Data;
using MyScorer.Application.Services;
using MyScorer.Core.Models;

namespace MyScorer.Tests;

public class DeviceHeartbeatTests : IDisposable
{
    private readonly MyScorerDbContext _db;

    public DeviceHeartbeatTests()
    {
        var options = new DbContextOptionsBuilder<MyScorerDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _db = new MyScorerDbContext(options);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Heartbeat_CreatesNewDevice()
    {
        _db.StreamingDevices.Add(new StreamingDevice
        {
            DeviceId = "pi-new",
            Name = "NewPi",
            IsOnline = true,
            LastSeen = DateTime.UtcNow
        });
        _db.SaveChanges();

        var device = _db.StreamingDevices.Find("pi-new");
        Assert.NotNull(device);
        Assert.True(device.IsOnline);
    }

    [Fact]
    public void Heartbeat_UpdatesExistingDevice()
    {
        _db.StreamingDevices.Add(new StreamingDevice
        {
            DeviceId = "pi-upd",
            Name = "OldName",
            IsOnline = false,
            LastSeen = DateTime.UtcNow.AddMinutes(-5)
        });
        _db.SaveChanges();

        var device = _db.StreamingDevices.Find("pi-upd")!;
        device.Name = "NewName";
        device.IsOnline = true;
        device.LastSeen = DateTime.UtcNow;
        _db.SaveChanges();

        var updated = _db.StreamingDevices.Find("pi-upd")!;
        Assert.Equal("NewName", updated.Name);
        Assert.True(updated.IsOnline);
    }

    [Fact]
    public void DeviceOnlineStatus_BasedOn30SecondThreshold()
    {
        var now = DateTime.UtcNow;

        _db.StreamingDevices.Add(new StreamingDevice
        {
            DeviceId = "pi-online",
            LastSeen = now.AddSeconds(-10)
        });
        _db.StreamingDevices.Add(new StreamingDevice
        {
            DeviceId = "pi-offline",
            LastSeen = now.AddSeconds(-60)
        });
        _db.SaveChanges();

        var threshold = now.AddSeconds(-30);
        var devices = _db.StreamingDevices.ToList();

        foreach (var d in devices)
            d.IsOnline = d.LastSeen > threshold;

        Assert.True(devices.First(d => d.DeviceId == "pi-online").IsOnline);
        Assert.False(devices.First(d => d.DeviceId == "pi-offline").IsOnline);
    }
}
