using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Characters.Commands.DeleteCharacter;

public sealed class DeleteCharacterHandler : IRequestHandler<DeleteCharacterCommand, Result<bool>>
{
    private readonly IUnitOfWork _unitOfWork;

    public DeleteCharacterHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<bool>> Handle(DeleteCharacterCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var character = await repo.GetByIdAsync(command.Id, cancellationToken);
        if (character == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{command.Id}' was not found.");
        }

        repo.Delete(character);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
