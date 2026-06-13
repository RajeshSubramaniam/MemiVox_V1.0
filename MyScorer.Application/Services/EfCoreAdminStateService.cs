using Microsoft.EntityFrameworkCore;
using MyScorer.Application.Data;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public class EfCoreAdminStateService : IAdminStateService
{
    private readonly MyScorerDbContext _context;

    public EfCoreAdminStateService(MyScorerDbContext context)
    {
        _context = context;

        if (!_context.Setups.Any())
        {
            _context.Setups.AddRange(new[]
            {
                new SetupRecord { SetupId = "23082201", StartDate = new DateTime(2023, 8, 22), OwnerName = "Alex Carter", OwnerEmail = "alex@example.com", CameraSerialNumber = "CAM-001", YoloSerialNumber = "YLB-001", PowerBankSerialNumber = "PWR-001", Status = "Active" },
                new SetupRecord { SetupId = "23082202", StartDate = new DateTime(2023, 9, 10), OwnerName = "Sam Lee", OwnerEmail = "sam@example.com", CameraSerialNumber = "CAM-002", YoloSerialNumber = "YLB-002", PowerBankSerialNumber = "PWR-002", Status = "Active" }
            });

            _context.Clients.AddRange(new[]
            {
                new ClientRecord { Name = "Alex Carter", EmailId = "alex@example.com", SetupId = "23082201", StartDate = new DateTime(2023, 8, 22), Status = "Active", Password = BCrypt.Net.BCrypt.HashPassword("demo123") },
                new ClientRecord { Name = "Sam Lee", EmailId = "sam@example.com", SetupId = "23082202", StartDate = new DateTime(2023, 9, 10), Status = "Active", Password = BCrypt.Net.BCrypt.HashPassword("demo456") }
            });

            _context.Matches.AddRange(new[]
            {
                new MatchRecord { SetupId = "23082201", Date = new DateTime(2024, 4, 10), MatchUrl = "https://playhq.com/match/23082201", ProviderType = "PlayHQ", Status = "Completed" },
                new MatchRecord { SetupId = "23082201", Date = new DateTime(2024, 5, 12), MatchUrl = "https://cricheroes.in/match/23082201", ProviderType = "Cricheroes", Status = "Active" },
                new MatchRecord { SetupId = "23082202", Date = new DateTime(2024, 5, 18), MatchUrl = "https://playhq.com/match/23082202", ProviderType = "PlayHQ", Status = "Completed" }
            });

            _context.SaveChanges();
        }
    }

    public IReadOnlyList<SetupRecord> GetSetups(string? query)
    {
        return _context.Setups
            .Where(x => string.IsNullOrWhiteSpace(query) ||
                        x.SetupId.Contains(query) ||
                        x.OwnerName.Contains(query) ||
                        x.OwnerEmail.Contains(query))
            .OrderBy(x => x.SetupId)
            .ToList();
    }

    public SetupRecord RegisterSetup(SetupRegistrationRequest request)
    {
        // Auto-generate SetupId if not provided: increment from the latest existing one
        var setupId = request.SetupId?.Trim();
        if (string.IsNullOrWhiteSpace(setupId))
        {
            var latestId = _context.Setups
                .OrderByDescending(s => s.SetupId)
                .Select(s => s.SetupId)
                .FirstOrDefault();

            if (latestId != null && long.TryParse(latestId, out var num))
                setupId = (num + 1).ToString();
            else
                setupId = DateTime.Today.ToString("yyMMdd") + "01";
        }

        // Validate duplicate
        if (_context.Setups.Any(s => s.SetupId == setupId))
            throw new ArgumentException($"Setup ID '{setupId}' already exists.");

        var setup = new SetupRecord
        {
            SetupId = setupId,
            StartDate = request.StartDate,
            OwnerName = request.OwnerName,
            OwnerEmail = request.OwnerEmail,
            CameraSerialNumber = request.CameraSerialNumber,
            YoloSerialNumber = request.YoloSerialNumber,
            PowerBankSerialNumber = request.PowerBankSerialNumber,
            Status = request.Status
        };

        // Use provided password or default "change-me"
        var passwordHash = !string.IsNullOrWhiteSpace(request.Password)
            ? BCrypt.Net.BCrypt.HashPassword(request.Password)
            : BCrypt.Net.BCrypt.HashPassword("change-me");

        _context.Setups.Add(setup);
        _context.Clients.Add(new ClientRecord
        {
            Name = request.OwnerName,
            EmailId = request.OwnerEmail,
            SetupId = setup.SetupId,
            StartDate = request.StartDate,
            Status = request.Status,
            Password = passwordHash
        });

        _context.SaveChanges();
        return setup;
    }

    public IReadOnlyList<ClientRecord> GetClients(string? query)
    {
        return _context.Clients
            .Where(x => string.IsNullOrWhiteSpace(query) ||
                        x.Name.Contains(query) ||
                        x.EmailId.Contains(query) ||
                        x.SetupId.Contains(query))
            .OrderBy(x => x.Name)
            .ToList();
    }

    public bool ValidateClientPassword(string setupId, string password)
    {
        if (string.IsNullOrWhiteSpace(setupId) || string.IsNullOrWhiteSpace(password))
        {
            return false;
        }

        var client = _context.Clients.FirstOrDefault(x => x.SetupId == setupId);
        return client != null && BCrypt.Net.BCrypt.Verify(password, client.Password);
    }

    public ClientRecord UpdateClient(string setupId, ClientUpdateRequest request)
    {
        var client = _context.Clients.FirstOrDefault(x => x.SetupId == setupId);
        if (client == null)
        {
            throw new KeyNotFoundException("Client not found for setup ID.");
        }

        client.EmailId = string.IsNullOrWhiteSpace(request.EmailId) ? client.EmailId : request.EmailId;
        client.Password = string.IsNullOrWhiteSpace(request.Password) ? client.Password : BCrypt.Net.BCrypt.HashPassword(request.Password);
        _context.SaveChanges();
        return client;
    }

    public IReadOnlyList<MatchRecord> GetMatches(string setupId)
    {
        return _context.Matches
            .Where(x => x.SetupId == setupId)
            .OrderByDescending(x => x.Date)
            .ToList();
    }
}
