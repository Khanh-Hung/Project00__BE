using System.Security.Claims;
using Application.Abstractions.Auth;
using Microsoft.AspNetCore.Http;

namespace Infrastructure.Services;

public class CurrentUserProvider : ICurrentUserProvider
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserProvider(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public string? CurrentUserId
    {
        get
        {
            var user = _httpContextAccessor.HttpContext?.User;
            if (user == null) return null;

            return user.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? user.FindFirstValue("sub")
                ?? user.FindFirstValue("userId");
        }
    }
}
