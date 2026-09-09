using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.DTOs;
using Application.Exceptions;
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
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AccountUserDto?> GetUserAsync(Guid userId, CancellationToken ct = default)
    {
        if (userId == Guid.Empty) return null;

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync($"api/v1/users/{userId}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Timeout occurred while querying Account Service for user {UserId}", userId);
            throw new AccountServiceUnavailableException($"Request to Account Service timed out for user '{userId}'.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure while communicating with Account Service for user {UserId}", userId);
            throw new AccountServiceUnavailableException($"Failed to communicate with Account Service for user '{userId}'. Service may be unreachable or offline.", ex);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError("Authentication/authorization failed when calling Account Service for user {UserId}. Status: {StatusCode}", userId, response.StatusCode);
            throw new AccountServiceException($"Unauthorized or forbidden access when contacting Account Service (Status: {(int)response.StatusCode}). Check service credentials.", response.StatusCode);
        }

        if ((int)response.StatusCode >= 500)
        {
            _logger.LogError("Account Service returned 5xx server error for user {UserId}. Status: {StatusCode}", userId, response.StatusCode);
            throw new AccountServiceUnavailableException($"Account Service encountered an internal server error (Status: {(int)response.StatusCode}) while looking up user '{userId}'.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Account Service returned unexpected non-success code {StatusCode} for user {UserId}", response.StatusCode, userId);
            throw new AccountServiceException($"Account Service returned unexpected status code {(int)response.StatusCode} for user '{userId}'.", response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<AccountUserDto>(JsonOptions, ct);
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

        HttpResponseMessage response;
        try
        {
            var requestPayload = new { UserIds = distinctIds };
            response = await _httpClient.PostAsJsonAsync("api/v1/users/batch", requestPayload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogError(ex, "Timeout occurred while batch querying Account Service for {Count} users", distinctIds.Count);
            throw new AccountServiceUnavailableException($"Batch request to Account Service timed out for {distinctIds.Count} users.", ex);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Network failure while batch querying Account Service for {Count} users", distinctIds.Count);
            throw new AccountServiceUnavailableException($"Failed to communicate with Account Service during batch lookup for {distinctIds.Count} users. Service may be unreachable or offline.", ex);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
        {
            _logger.LogError("Authentication/authorization failed during batch lookup in Account Service. Status: {StatusCode}", response.StatusCode);
            throw new AccountServiceException($"Unauthorized or forbidden access during batch lookup in Account Service (Status: {(int)response.StatusCode}). Check service credentials.", response.StatusCode);
        }

        if ((int)response.StatusCode >= 500)
        {
            _logger.LogError("Account Service returned 5xx server error during batch lookup. Status: {StatusCode}", response.StatusCode);
            throw new AccountServiceUnavailableException($"Account Service encountered an internal server error (Status: {(int)response.StatusCode}) during batch lookup.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Account Service returned unexpected non-success code {StatusCode} during batch lookup", response.StatusCode);
            throw new AccountServiceException($"Account Service returned unexpected status code {(int)response.StatusCode} during batch lookup.", response.StatusCode);
        }

        var users = await response.Content.ReadFromJsonAsync<List<AccountUserDto>>(JsonOptions, ct);
        if (users == null)
        {
            return new Dictionary<Guid, AccountUserDto>();
        }

        return users.ToDictionary(u => u.UserId, u => u);
    }
}
