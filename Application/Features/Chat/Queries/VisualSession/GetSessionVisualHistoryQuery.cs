using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using MediatR;

namespace Application.Features.Chat.Queries.VisualSession;

public sealed record GetSessionVisualHistoryQuery(
    Guid SessionId,
    int? Limit = null
) : IRequest<Result<IReadOnlyList<VisualHistoryEntry>>>;

public sealed class GetSessionVisualHistoryHandler : IRequestHandler<GetSessionVisualHistoryQuery, Result<IReadOnlyList<VisualHistoryEntry>>>
{
    private readonly IVisualHistoryService _historyService;

    public GetSessionVisualHistoryHandler(IVisualHistoryService historyService)
    {
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
    }

    public async Task<Result<IReadOnlyList<VisualHistoryEntry>>> Handle(GetSessionVisualHistoryQuery request, CancellationToken cancellationToken)
    {
        var history = await _historyService.GetSessionVisualHistoryAsync(request.SessionId, request.Limit, cancellationToken);
        return Result<IReadOnlyList<VisualHistoryEntry>>.Success(history);
    }
}
