namespace MyScorer.Core.Models;

public class SetupRecord
{
    public string SetupId { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string CameraSerialNumber { get; set; } = string.Empty;
    public string YoloSerialNumber { get; set; } = string.Empty;
    public string PowerBankSerialNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
}

public class SetupRegistrationRequest
{
    public string SetupId { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerEmail { get; set; } = string.Empty;
    public string CameraSerialNumber { get; set; } = string.Empty;
    public string YoloSerialNumber { get; set; } = string.Empty;
    public string PowerBankSerialNumber { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string Password { get; set; } = string.Empty;
}

public class ClientRecord
{
    public string Name { get; set; } = string.Empty;
    public string EmailId { get; set; } = string.Empty;
    public string SetupId { get; set; } = string.Empty;
    public DateTime StartDate { get; set; } = DateTime.Today;
    public string Status { get; set; } = "Active";
    public string Password { get; set; } = "changeme";
}

public class ClientUpdateRequest
{
    public string EmailId { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class MatchRecord
{
    public int Id { get; set; }
    public string SetupId { get; set; } = string.Empty;
    public DateTime Date { get; set; } = DateTime.Today;
    public string MatchUrl { get; set; } = string.Empty;
    public string ProviderType { get; set; } = "Manual";
    public string Status { get; set; } = "Active";
}
