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

    public DeleteLorebookEntryHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<bool>> Handle(DeleteLorebookEntryCommand command, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        var entry = await repo.GetByIdAsync(command.EntryId, cancellationToken);
        if (entry == null)
        {
            return Result<bool>.Failure(StatusCodes.Status404NotFound, $"Lorebook entry '{command.EntryId}' was not found.");
        }

        repo.Delete(entry);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        return Result<bool>.Success(true);
    }
}
