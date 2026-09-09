using Application.DTOs;

namespace Application.Interfaces;

public interface IAccountServiceClient
{
    Task<AccountUserDto?> GetUserAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, AccountUserDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default);
}
