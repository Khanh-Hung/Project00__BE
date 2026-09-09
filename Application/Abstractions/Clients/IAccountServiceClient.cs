using System.Threading;
using System.Threading.Tasks;
using Application.DTOs;

namespace Application.Abstractions.Clients;

/// <summary>
/// Client interface for communicating with the external Account Service (G:\New folder (7))
/// </summary>
public interface IAccountServiceClient
{
    /// <summary>
    /// Checks the health/status of the Account Service via /api/v1/system/status
    /// </summary>
    Task<AccountSystemStatusDto?> GetSystemStatusAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the authenticated user's profile from Account Service via /api/v1/profile/me
    /// </summary>
    Task<AccountUserProfileDto?> GetUserProfileAsync(string bearerToken, CancellationToken ct = default);

    /// <summary>
    /// Gets the authenticated user's wallet status from Account Service via /api/v1/wallet/me
    /// </summary>
    Task<AccountWalletDto?> GetUserWalletAsync(string bearerToken, CancellationToken ct = default);
}
