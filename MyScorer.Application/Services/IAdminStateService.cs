using MyScorer.Core.Models;

namespace MyScorer.Application.Services;

public interface IAdminStateService
{
    IReadOnlyList<SetupRecord> GetSetups(string? query);
    SetupRecord RegisterSetup(SetupRegistrationRequest request);
    IReadOnlyList<ClientRecord> GetClients(string? query);
    bool ValidateClientPassword(string setupId, string password);
    ClientRecord UpdateClient(string setupId, ClientUpdateRequest request);
    IReadOnlyList<MatchRecord> GetMatches(string setupId);
}
