namespace MyScorer.Core.Models;

public class StreamingDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "raspberry-pi";
    public bool IsOnline { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public bool AtemConnected { get; set; }
    public bool StreamActive { get; set; }
    public string NetworkStatus { get; set; } = "unknown";
}

public class StreamingDeviceHeartbeatRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "raspberry-pi";
    public bool AtemConnected { get; set; }
    public bool StreamActive { get; set; }
    public string NetworkStatus { get; set; } = "unknown";
}
