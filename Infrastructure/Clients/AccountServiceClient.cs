using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Application.Abstractions.Clients;
using Application.DTOs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients;

/// <summary>
/// HTTP client implementation for communicating with the external Account Service
/// </summary>
public sealed class AccountServiceClient : IAccountServiceClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AccountServiceClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public AccountServiceClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AccountServiceClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var baseUrl = configuration["Services:AccountService:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl) && _httpClient.BaseAddress == null)
        {
            _httpClient.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }

        var timeoutSeconds = configuration.GetValue<int>("Services:AccountService:TimeoutSeconds", 15);
        _httpClient.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
    }

    public async Task<AccountSystemStatusDto?> GetSystemStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync("api/v1/system/status", ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AccountServiceClient] System status check returned HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<AccountSystemStatusDto>(content, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AccountServiceClient] Failed to reach Account Service system status endpoint.");
            return null;
        }
    }

    public async Task<AccountUserProfileDto?> GetUserProfileAsync(string bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("Bearer token cannot be null or whitespace.", nameof(bearerToken));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/profile/me");
            var cleanToken = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..].Trim()
                : bearerToken.Trim();

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AccountServiceClient] Get profile returned HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<AccountUserProfileDto>(content, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AccountServiceClient] Failed to retrieve user profile from Account Service.");
            return null;
        }
    }

    public async Task<AccountWalletDto?> GetUserWalletAsync(string bearerToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bearerToken))
            throw new ArgumentException("Bearer token cannot be null or whitespace.", nameof(bearerToken));

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/wallet/me");
            var cleanToken = bearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? bearerToken["Bearer ".Length..].Trim()
                : bearerToken.Trim();

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);

            using var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[AccountServiceClient] Get wallet returned HTTP {StatusCode}", response.StatusCode);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<AccountWalletDto>(content, JsonOptions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AccountServiceClient] Failed to retrieve user wallet from Account Service.");
            return null;
        }
    }
}
