using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Commands.DeleteCharacter;

public sealed class DeleteCharacterHandler : IRequestHandler<DeleteCharacterCommand, Result<bool>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public DeleteCharacterHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<bool>> Handle(DeleteCharacterCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var character = await repo.GetByIdAsync(command.Id, cancellationToken);
        if (character == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{command.Id}' was not found.");
        }

        var currentUserId = _currentUserProvider.CurrentUserId;
        if (!string.IsNullOrEmpty(character.CreatedBy) && character.CreatedBy != "system")
        {
            if (string.IsNullOrEmpty(currentUserId) || !string.Equals(character.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase))
            {
                return Result<bool>.Failure(StatusCodes.Status403Forbidden, "You do not have permission to delete this character.");
            }
        }

        repo.Delete(character);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
