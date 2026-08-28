using Application.Abstractions.Auth;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Infrastructure.Persistence;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetSessionVisualHistoryQuery(
    Guid SessionId,
    int? Limit = null
) : IRequest<Result<IReadOnlyList<VisualHistoryEntry>>>;

public sealed class GetSessionVisualHistoryHandler : IRequestHandler<GetSessionVisualHistoryQuery, Result<IReadOnlyList<VisualHistoryEntry>>>
{
    private readonly CoreDbContext _dbContext;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IVisualHistoryService _historyService;

    public GetSessionVisualHistoryHandler(
        CoreDbContext dbContext,
        ICurrentUserProvider currentUserProvider,
        IVisualHistoryService historyService)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _currentUserProvider = currentUserProvider ?? throw new ArgumentNullException(nameof(currentUserProvider));
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
    }

    public async Task<Result<IReadOnlyList<VisualHistoryEntry>>> Handle(GetSessionVisualHistoryQuery request, CancellationToken cancellationToken)
    {
        // 1. Resolve Authenticated User & Check Ownership
        if (string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) || !Guid.TryParse(_currentUserProvider.CurrentUserId, out var currentUserId))
        {
            return Result<IReadOnlyList<VisualHistoryEntry>>.Failure(
                StatusCodes.Status401Unauthorized,
                "Authentication is required to view visual session history.");
        }

        var session = await _dbContext.ChatSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);

        if (session == null)
        {
            return Result<IReadOnlyList<VisualHistoryEntry>>.Failure(
                StatusCodes.Status404NotFound,
                $"Chat session '{request.SessionId}' was not found.");
        }

        if (session.UserId != currentUserId)
        {
            return Result<IReadOnlyList<VisualHistoryEntry>>.Failure(
                StatusCodes.Status403Forbidden,
                "You do not have permission to access visual history for this session.");
        }

        var history = await _historyService.GetSessionVisualHistoryAsync(request.SessionId, request.Limit, cancellationToken);
        return Result<IReadOnlyList<VisualHistoryEntry>>.Success(history);
    }
}
