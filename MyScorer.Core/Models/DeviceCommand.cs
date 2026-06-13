namespace MyScorer.Core.Models;

public class DeviceCommand
{
    public int Id { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty; // START_STREAM, STOP_STREAM
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "Pending"; // Pending, Delivered, Completed
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeviceCommandRequest
{
    public string DeviceId { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
}

public class DeviceCommandAckRequest
{
    public int CommandId { get; set; }
    public string Status { get; set; } = "Delivered"; // Delivered or Completed
}
