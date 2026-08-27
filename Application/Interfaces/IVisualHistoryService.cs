using Application.DTOs;

namespace Application.Interfaces;

/// <summary>
/// Service providing visual history inspection across chat turns for a session.
/// </summary>
public interface IVisualHistoryService
{
    Task<IReadOnlyList<VisualHistoryEntry>> GetSessionVisualHistoryAsync(
        Guid sessionId,
        int? limit = null,
        CancellationToken ct = default);
}
