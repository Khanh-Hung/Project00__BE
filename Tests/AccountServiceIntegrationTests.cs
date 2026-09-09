using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Application.DTOs;
using Infrastructure.Clients;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Project.Tests;

public class AccountServiceIntegrationTests
{
    private const string AccountSecret = "oracle-tarot-super-secret-key-256-bits-minimum-required-for-hmac-sha-algorithm-security";
    private const string AccountIssuer = "AccountService";
    private const string AccountAudience = "NyxorisClient";

    private string GenerateAccountServiceToken(Guid userId, string email, string role = "USER", bool expired = false)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.InboundClaimTypeMap.Clear();
        tokenHandler.OutboundClaimTypeMap.Clear();
        var key = Encoding.UTF8.GetBytes(AccountSecret);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, email),
            new("userId", userId.ToString()),
            new("username", "testuser"),
            new("displayName", "Test User"),
            new("avatarUrl", "https://example.com/avatar.jpg"),
            new("role", role.ToUpperInvariant()),
            new("isEmailVerified", "true"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            NotBefore = expired ? DateTime.UtcNow.AddMinutes(-30) : DateTime.UtcNow,
            Expires = expired ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(60),
            Issuer = AccountIssuer,
            Audience = AccountAudience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    [Fact]
    public void AccountJwtToken_ValidTokenEmittedByAccountService_ParsedCorrectlyByCurrentUserProvider()
    {
        var expectedUserId = Guid.NewGuid();
        var email = "user@example.com";
        var rawJwt = GenerateAccountServiceToken(expectedUserId, email);

        // 1. Validate token with BE TokenValidationParameters matching Account configuration
        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.InboundClaimTypeMap.Clear();
        tokenHandler.OutboundClaimTypeMap.Clear();

        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = AccountIssuer,
            ValidAudience = AccountAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AccountSecret)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        var principal = tokenHandler.ValidateToken(rawJwt, validationParams, out var validatedToken);
        Assert.NotNull(validatedToken);

        // 2. Set up HttpContext with validated principal
        var httpContext = new DefaultHttpContext { User = principal };
        var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };

        // 3. Test CurrentUserProvider resolves userId Guid (NOT the email from sub)
        var provider = new CurrentUserProvider(httpContextAccessor);
        var currentUserId = provider.CurrentUserId;

        Assert.NotNull(currentUserId);
        Assert.True(Guid.TryParse(currentUserId, out var parsedGuid), "CurrentUserId must be parseable as Guid.");
        Assert.Equal(expectedUserId, parsedGuid);
        Assert.NotEqual(email, currentUserId);
    }

    [Fact]
    public void AccountJwtToken_ExpiredToken_FailsValidation()
    {
        var userId = Guid.NewGuid();
        var expiredJwt = GenerateAccountServiceToken(userId, "expired@example.com", expired: true);

        var tokenHandler = new JwtSecurityTokenHandler();
        tokenHandler.InboundClaimTypeMap.Clear();
        tokenHandler.OutboundClaimTypeMap.Clear();

        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = AccountIssuer,
            ValidAudience = AccountAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AccountSecret)),
            ClockSkew = TimeSpan.Zero
        };

        Assert.ThrowsAny<SecurityTokenException>(() =>
            tokenHandler.ValidateToken(expiredJwt, validationParams, out _));
    }

    [Fact]
    public async Task AccountServiceClient_GetSystemStatusAsync_ReturnsStatus()
    {
        var responseJson = """
        {
            "status": "Healthy",
            "service": "AccountService",
            "timestamp": "2026-09-09T10:00:00Z"
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            Assert.Equal("http://localhost:5000/api/v1/system/status", request.RequestUri?.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var config = new ConfigurationBuilder().Build();
        var client = new AccountServiceClient(httpClient, config, NullLogger<AccountServiceClient>.Instance);

        var status = await client.GetSystemStatusAsync();

        Assert.NotNull(status);
        Assert.Equal("Healthy", status.Status);
        Assert.Equal("AccountService", status.Service);
    }

    [Fact]
    public async Task AccountServiceClient_GetUserProfileAsync_SendsBearerAndDeserializes()
    {
        var expectedUserId = Guid.NewGuid();
        var responseJson = $$"""
        {
            "userId": "{{expectedUserId}}",
            "email": "alice@wonderland.com",
            "username": "alice",
            "displayName": "Alice In Wonderland",
            "avatarUrl": "https://example.com/alice.png",
            "role": "USER",
            "isEmailVerified": true
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            Assert.Equal("http://localhost:5000/api/v1/profile/me", request.RequestUri?.ToString());
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("my-jwt-token-123", request.Headers.Authorization.Parameter);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var config = new ConfigurationBuilder().Build();
        var client = new AccountServiceClient(httpClient, config, NullLogger<AccountServiceClient>.Instance);

        var profile = await client.GetUserProfileAsync("Bearer my-jwt-token-123");

        Assert.NotNull(profile);
        Assert.Equal(expectedUserId, profile.UserId);
        Assert.Equal("alice@wonderland.com", profile.Email);
        Assert.Equal("Alice In Wonderland", profile.DisplayName);
        Assert.True(profile.IsEmailVerified);
    }

    [Fact]
    public async Task AccountServiceClient_GetUserWalletAsync_SendsBearerAndDeserializes()
    {
        var expectedWalletId = Guid.NewGuid();
        var expectedUserId = Guid.NewGuid();
        var responseJson = $$"""
        {
            "id": "{{expectedWalletId}}",
            "userId": "{{expectedUserId}}",
            "balance": 1500.75,
            "currency": "VND",
            "status": "Active"
        }
        """;

        var handler = new MockHttpMessageHandler(async request =>
        {
            Assert.Equal("http://localhost:5000/api/v1/wallet/me", request.RequestUri?.ToString());
            Assert.NotNull(request.Headers.Authorization);
            Assert.Equal("Bearer", request.Headers.Authorization.Scheme);
            Assert.Equal("wallet-token", request.Headers.Authorization.Parameter);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        });

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var config = new ConfigurationBuilder().Build();
        var client = new AccountServiceClient(httpClient, config, NullLogger<AccountServiceClient>.Instance);

        var wallet = await client.GetUserWalletAsync("wallet-token");

        Assert.NotNull(wallet);
        Assert.Equal(expectedWalletId, wallet.Id);
        Assert.Equal(expectedUserId, wallet.UserId);
        Assert.Equal(1500.75m, wallet.Balance);
        Assert.Equal("VND", wallet.Currency);
        Assert.Equal("Active", wallet.Status);
    }

    [Fact]
    public async Task AccountServiceClient_GracefulFallback_OnHttpError()
    {
        var handler = new MockHttpMessageHandler(async _ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        var config = new ConfigurationBuilder().Build();
        var client = new AccountServiceClient(httpClient, config, NullLogger<AccountServiceClient>.Instance);

        var status = await client.GetSystemStatusAsync();
        var profile = await client.GetUserProfileAsync("token");
        var wallet = await client.GetUserWalletAsync("token");

        Assert.Null(status);
        Assert.Null(profile);
        Assert.Null(wallet);
    }

    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public MockHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request);
        }
    }
}
