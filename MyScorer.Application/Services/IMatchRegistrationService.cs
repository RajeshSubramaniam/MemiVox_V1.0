using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public interface IMatchRegistrationService
{
    IReadOnlyList<MatchRegistrationRecord> GetRegistrationsForSetup(string setupId);
    MatchRegistrationRecord? GetActiveRegistration(string setupId);
    MatchRegistrationRecord CreateRegistration(string setupId, CreateMatchRegistrationRequest request);
    void CompleteActiveRegistration(string setupId);
}
