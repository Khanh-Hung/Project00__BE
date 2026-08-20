using Application.Common;

namespace Application.Interfaces;

public interface IRoleplayContextEngine
{
    Task<RoleplayContext> BuildContextAsync(
        Guid sessionId,
        string userMessage,
        Guid? currentUserId = null,
        CancellationToken ct = default);
}
