using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Infrastructure.Services;
using Microsoft.AspNetCore.Http;
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
}
