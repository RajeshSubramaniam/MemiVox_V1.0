namespace MyScorer.Core.Models;

public class DeviceHeartbeat
{
    public string SetupId { get; set; } = string.Empty;
    public bool ObsRunning { get; set; }
    public bool CameraDetected { get; set; }
    public bool IsStreaming { get; set; }
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public class DeviceHeartbeatRequest
{
    public string SetupId { get; set; } = string.Empty;
    public bool ObsRunning { get; set; }
    public bool CameraDetected { get; set; }
    public bool IsStreaming { get; set; }
}
