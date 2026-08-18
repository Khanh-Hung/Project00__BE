using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Infrastructure.Services;

public class JwtTokenGenerator : IJwtTokenGenerator
{
    private readonly IConfiguration _configuration;

    public JwtTokenGenerator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string GenerateToken(User user)
    {
        var secret = _configuration["Jwt:Secret"] ?? "SuperSecretKeyForNyxorisRoleplayPlatformDev2026!@#";
        var issuer = _configuration["Jwt:Issuer"] ?? "NyxorisAuth";
        var audience = _configuration["Jwt:Audience"] ?? "NyxorisRoleplay";
        var expirationHours = int.TryParse(_configuration["Jwt:ExpirationHours"], out var hours) ? hours : 72;

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.UserName),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString())
        };

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            expires: Clock.Now.AddHours(expirationHours),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
