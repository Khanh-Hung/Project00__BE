using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.DTOs;
using Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients;

public sealed class AccountServiceClient : IAccountServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountServiceClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AccountServiceClient(HttpClient httpClient, ILogger<AccountServiceClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<AccountUserDto?> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return null;

        try
        {
            var response = await _httpClient.GetAsync($"api/v1/users/{userId}", ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AccountService returned non-success code {StatusCode} for user {UserId}", response.StatusCode, userId);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<AccountUserDto>(JsonOptions, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch user {UserId} from AccountService", userId);
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<Guid, AccountUserDto>> GetUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds == null || userIds.Count == 0)
        {
            return new Dictionary<Guid, AccountUserDto>();
        }

        var distinctIds = userIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinctIds.Count == 0)
        {
            return new Dictionary<Guid, AccountUserDto>();
        }

        try
        {
            var requestPayload = new { UserIds = distinctIds };
            var response = await _httpClient.PostAsJsonAsync("api/v1/users/batch", requestPayload, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("AccountService returned non-success code {StatusCode} for batch users lookup", response.StatusCode);
                return new Dictionary<Guid, AccountUserDto>();
            }

            var users = await response.Content.ReadFromJsonAsync<List<AccountUserDto>>(JsonOptions, ct);
            if (users == null)
            {
                return new Dictionary<Guid, AccountUserDto>();
            }

            return users.ToDictionary(u => u.UserId, u => u);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to batch fetch {Count} users from AccountService", distinctIds.Count);
            return new Dictionary<Guid, AccountUserDto>();
        }
    }
}
