using System.Collections.Concurrent;
using Microsoft.AspNetCore.Mvc;
using MyScorer.Application.Data;
using MyScorer.Core;
using MyScorer.Core.Models;

namespace MyScorer.Api.Controllers;

[ApiController]
[Route("api/device")]
public class DeviceController : ControllerBase
{
    // Legacy in-memory heartbeats (kept for backward compatibility)
    private static readonly ConcurrentDictionary<string, DeviceHeartbeat> _heartbeats = new();

    private readonly MyScorerDbContext _context;

    public DeviceController(MyScorerDbContext context)
    {
        _context = context;
    }

    // ─── Legacy endpoints (backward compatible) ───

    [HttpPost("heartbeat")]
    public IActionResult Heartbeat([FromBody] StreamingDeviceHeartbeatRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) || !Validation.IdPattern().IsMatch(request.DeviceId))
            return BadRequest(new { message = "Invalid DeviceId format." });

        // Persist to DB
        var device = _context.StreamingDevices.Find(request.DeviceId);
        if (device == null)
        {
            device = new StreamingDevice { DeviceId = request.DeviceId };
            _context.StreamingDevices.Add(device);
        }

        device.Name = request.Name;
        device.DeviceType = request.DeviceType;
        device.IsOnline = true;
        device.LastSeen = DateTime.UtcNow;
        device.AtemConnected = request.AtemConnected;
        device.StreamActive = request.StreamActive;
        device.NetworkStatus = request.NetworkStatus;

        _context.SaveChanges();

        // Also maintain legacy in-memory store
        _heartbeats[request.DeviceId] = new DeviceHeartbeat
        {
            SetupId = request.DeviceId,
            ObsRunning = false,
            CameraDetected = request.AtemConnected,
            IsStreaming = request.StreamActive,
            ReceivedAt = DateTime.UtcNow
        };

        return Ok(new { received = true });
    }

    [HttpGet("{deviceId}/status")]
    public IActionResult GetStatus(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !Validation.IdPattern().IsMatch(deviceId))
            return BadRequest(new { message = "Invalid DeviceId format." });

        // Try DB first (new model)
        var device = _context.StreamingDevices.Find(deviceId);
        if (device != null)
        {
            var age = DateTime.UtcNow - device.LastSeen;
            device.IsOnline = age.TotalSeconds < 30;
            return Ok(new
            {
                device.DeviceId,
                device.Name,
                device.DeviceType,
                device.IsOnline,
                device.LastSeen,
                device.AtemConnected,
                device.StreamActive,
                device.NetworkStatus,
                // Legacy compat fields
                SetupId = device.DeviceId,
                ObsRunning = false,
                CameraDetected = device.AtemConnected,
                IsStreaming = device.StreamActive,
                ReceivedAt = device.LastSeen
            });
        }

        // Fallback to legacy in-memory
        if (_heartbeats.TryGetValue(deviceId, out var heartbeat))
        {
            var age = DateTime.UtcNow - heartbeat.ReceivedAt;
            return Ok(new
            {
                heartbeat.SetupId,
                heartbeat.ObsRunning,
                heartbeat.CameraDetected,
                heartbeat.IsStreaming,
                heartbeat.ReceivedAt,
                isOnline = age.TotalSeconds < 15
            });
        }

        return Ok(new { deviceId, isOnline = false, message = "No heartbeat received." });
    }

    // ─── Command system ───

    [HttpPost("command")]
    public IActionResult QueueCommand([FromBody] DeviceCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) || !Validation.IdPattern().IsMatch(request.DeviceId))
            return BadRequest(new { message = "Invalid DeviceId format." });

        var validCommands = new[] { "START_STREAM", "STOP_STREAM" };
        if (!validCommands.Contains(request.Command, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Invalid command. Use START_STREAM or STOP_STREAM." });

        // Idempotency: don't queue if same command already pending
        var hasPending = _context.DeviceCommands.Any(c =>
            c.DeviceId == request.DeviceId &&
            c.Command == request.Command.ToUpperInvariant() &&
            c.Status == "Pending");

        if (hasPending)
            return Ok(new { message = "Command already pending.", queued = false });

        // If START_STREAM and device already streaming, skip
        if (request.Command.Equals("START_STREAM", StringComparison.OrdinalIgnoreCase))
        {
            var device = _context.StreamingDevices.Find(request.DeviceId);
            if (device is { StreamActive: true, IsOnline: true })
                return Ok(new { message = "Stream already active.", queued = false });
        }

        var cmd = new DeviceCommand
        {
            DeviceId = request.DeviceId,
            Command = request.Command.ToUpperInvariant(),
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _context.DeviceCommands.Add(cmd);
        _context.SaveChanges();

        return Ok(new { message = "Command queued.", queued = true, commandId = cmd.Id, requestId = cmd.RequestId });
    }

    [HttpGet("{deviceId}/next-command")]
    public IActionResult GetNextCommand(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !Validation.IdPattern().IsMatch(deviceId))
            return BadRequest(new { message = "Invalid DeviceId format." });

        var cmd = _context.DeviceCommands
            .Where(c => c.DeviceId == deviceId && c.Status == "Pending")
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefault();

        if (cmd == null)
            return Ok(new { hasCommand = false });

        // Mark as delivered
        cmd.Status = "Delivered";
        _context.SaveChanges();

        return Ok(new
        {
            hasCommand = true,
            commandId = cmd.Id,
            command = cmd.Command,
            requestId = cmd.RequestId
        });
    }

    [HttpPost("command/ack")]
    public IActionResult AcknowledgeCommand([FromBody] DeviceCommandAckRequest request)
    {
        var cmd = _context.DeviceCommands.Find(request.CommandId);
        if (cmd == null)
            return NotFound(new { message = "Command not found." });

        var validStatuses = new[] { "Delivered", "Completed" };
        if (!validStatuses.Contains(request.Status, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { message = "Status must be Delivered or Completed." });

        cmd.Status = request.Status;
        _context.SaveChanges();

        return Ok(new { message = "Command acknowledged.", commandId = cmd.Id, status = cmd.Status });
    }

    // ─── Streaming devices list (admin use) ───

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        var devices = _context.StreamingDevices.OrderBy(d => d.DeviceId).ToList();

        // Update online status based on last seen
        foreach (var d in devices)
            d.IsOnline = (DateTime.UtcNow - d.LastSeen).TotalSeconds < 30;

        return Ok(devices);
    }

    // ─── Maintenance (called by MaintenanceService) ───

    internal static int EvictStaleHeartbeats()
    {
        var evicted = 0;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        foreach (var key in _heartbeats.Keys.ToList())
        {
            if (_heartbeats.TryGetValue(key, out var hb) && hb.ReceivedAt < cutoff)
            {
                if (_heartbeats.TryRemove(key, out _))
                    evicted++;
            }
        }
        return evicted;
    }
}
