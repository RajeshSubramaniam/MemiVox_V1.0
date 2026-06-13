using MyScorer.Application.Data;
using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public class EfCoreMatchRegistrationService : IMatchRegistrationService
{
    private readonly MyScorerDbContext _context;

    public EfCoreMatchRegistrationService(MyScorerDbContext context)
    {
        _context = context;
    }

    public IReadOnlyList<MatchRegistrationRecord> GetRegistrationsForSetup(string setupId)
    {
        if (string.IsNullOrWhiteSpace(setupId))
        {
            throw new ArgumentException("SetupId is required.", nameof(setupId));
        }

        return _context.Matches
            .Where(x => x.SetupId == setupId)
            .OrderByDescending(x => x.Date)
            .AsEnumerable()
            .Select(ToRegistrationRecord)
            .ToList();
    }

    public MatchRegistrationRecord? GetActiveRegistration(string setupId)
    {
        if (string.IsNullOrWhiteSpace(setupId))
        {
            throw new ArgumentException("SetupId is required.", nameof(setupId));
        }

        var active = _context.Matches
            .Where(x => x.SetupId == setupId && x.Status == "Active")
            .OrderByDescending(x => x.Date)
            .FirstOrDefault();

        return active != null ? ToRegistrationRecord(active) : null;
    }

    public MatchRegistrationRecord CreateRegistration(string setupId, CreateMatchRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(setupId))
        {
            throw new ArgumentException("SetupId is required.", nameof(setupId));
        }

        if (string.IsNullOrWhiteSpace(request.MatchUrl))
        {
            throw new ArgumentException("MatchUrl is required.", nameof(request));
        }

        foreach (var existing in _context.Matches.Where(x => x.SetupId == setupId && x.Status == "Active"))
        {
            existing.Status = "Completed";
        }

        var record = new MatchRecord
        {
            SetupId = setupId,
            Date = DateTime.UtcNow.Date,
            MatchUrl = request.MatchUrl.Trim(),
            ProviderType = string.IsNullOrWhiteSpace(request.ProviderType) ? "Manual" : request.ProviderType,
            Status = "Active"
        };

        _context.Matches.Add(record);
        _context.SaveChanges();

        return ToRegistrationRecord(record);
    }

    public void CompleteActiveRegistration(string setupId)
    {
        var active = _context.Matches
            .Where(x => x.SetupId == setupId && x.Status == "Active")
            .ToList();

        foreach (var match in active)
        {
            match.Status = "Completed";
        }

        if (active.Count > 0)
            _context.SaveChanges();
    }

    private static MatchRegistrationRecord ToRegistrationRecord(MatchRecord r) => new()
    {
        SetupId = r.SetupId,
        ProviderType = r.ProviderType,
        MatchUrl = r.MatchUrl,
        CreatedAt = r.Date,
        IsActive = r.Status == "Active"
    };
}
