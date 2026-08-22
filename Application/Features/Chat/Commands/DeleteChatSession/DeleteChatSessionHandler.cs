using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.DeleteChatSession;

public sealed class DeleteChatSessionHandler : IRequestHandler<DeleteChatSessionCommand, Result<bool>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public DeleteChatSessionHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<bool>> Handle(DeleteChatSessionCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<ChatSession>();
        var session = await repo.GetByIdAsync(command.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Chat session with ID '{command.SessionId}' was not found.");
        }

        var currentUserId = _currentUserProvider.CurrentUserId;
        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            if (!string.IsNullOrEmpty(currentUserId) && Guid.TryParse(currentUserId, out var uid))
            {
                if (session.UserId.Value != uid)
                {
                    return Result<bool>.Failure(StatusCodes.Status403Forbidden, "You do not have access to this chat session.");
                }
            }
        }

        repo.Delete(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
