using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Lorebook.Commands.DeleteLorebookEntry;

public sealed record DeleteLorebookEntryCommand(Guid EntryId) : IRequest<Result<bool>>;

public sealed class DeleteLorebookEntryHandler : IRequestHandler<DeleteLorebookEntryCommand, Result<bool>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public DeleteLorebookEntryHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<bool>> Handle(DeleteLorebookEntryCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        var entry = await repo.GetByIdAsync(command.EntryId, cancellationToken);
        if (entry == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Lorebook entry '{command.EntryId}' was not found.");
        }

        if (entry.CharacterId.HasValue)
        {
            var characterRepo = _unitOfWork.GetRepository<Character>();
            var character = await characterRepo.GetByIdAsync(entry.CharacterId.Value, cancellationToken);
            if (character != null && !string.IsNullOrEmpty(character.CreatedBy) && character.CreatedBy != "system")
            {
                var currentUserId = _currentUserProvider.CurrentUserId;
                if (string.IsNullOrEmpty(currentUserId) || !string.Equals(character.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<bool>.Failure(StatusCodes.Status403Forbidden, "You do not have permission to delete this lorebook entry.");
                }
            }
        }

        repo.Delete(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
