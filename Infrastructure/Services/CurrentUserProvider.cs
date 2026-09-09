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

            // 1. Prioritize dedicated "userId" claim (emitted by external Account service)
            var userId = user.FindFirstValue("userId");
            if (!string.IsNullOrEmpty(userId) && Guid.TryParse(userId, out _))
                return userId;

            // 2. Check standard NameIdentifier / sub claims if they contain a valid Guid
            var nameId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrEmpty(nameId) && Guid.TryParse(nameId, out _))
                return nameId;

            var sub = user.FindFirstValue("sub");
            if (!string.IsNullOrEmpty(sub) && Guid.TryParse(sub, out _))
                return sub;

            // 3. Fallback to raw values
            return userId ?? nameId ?? sub;
        }
    }
}
