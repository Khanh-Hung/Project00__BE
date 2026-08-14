using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Domain.Entities;
using MediatR;

namespace Application.Features.Chat.Commands.DeleteChatSession;

public sealed class DeleteChatSessionHandler : IRequestHandler<DeleteChatSessionCommand, Result<bool>>
{
    private readonly IUnitOfWork _unitOfWork;

    public DeleteChatSessionHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<bool>> Handle(DeleteChatSessionCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<ChatSession>();
        var session = await repo.GetByIdAsync(command.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Chat session with ID '{command.SessionId}' was not found.");
        }

        repo.Delete(session);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
