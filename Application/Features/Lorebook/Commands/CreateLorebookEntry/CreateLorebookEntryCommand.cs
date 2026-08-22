using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Lorebook.Commands.CreateLorebookEntry;

public sealed record CreateLorebookEntryCommand(CreateLorebookEntryRequest Request) : IRequest<Result<LorebookEntryDto>>;

public sealed class CreateLorebookEntryHandler : IRequestHandler<CreateLorebookEntryCommand, Result<LorebookEntryDto>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public CreateLorebookEntryHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<LorebookEntryDto>> Handle(CreateLorebookEntryCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;

        if (string.IsNullOrWhiteSpace(req.Title))
        {
            return Result<LorebookEntryDto>.Failure(StatusCodes.Status400BadRequest, "Lorebook entry title cannot be empty.");
        }

        if (string.IsNullOrWhiteSpace(req.Content))
        {
            return Result<LorebookEntryDto>.Failure(StatusCodes.Status400BadRequest, "Lorebook entry content cannot be empty.");
        }

        if (req.CharacterId.HasValue)
        {
            var characterRepo = _unitOfWork.GetRepository<Character>();
            var character = await characterRepo.GetByIdAsync(req.CharacterId.Value, cancellationToken);
            if (character == null)
            {
                return Result<LorebookEntryDto>.Failure(StatusCodes.Status404NotFound, $"Character '{req.CharacterId.Value}' was not found.");
            }

            var currentUserId = _currentUserProvider.CurrentUserId;
            if (!string.IsNullOrEmpty(character.CreatedBy) && character.CreatedBy != "system")
            {
                if (string.IsNullOrEmpty(currentUserId) || !string.Equals(character.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase))
                {
                    return Result<LorebookEntryDto>.Failure(StatusCodes.Status403Forbidden, "You do not have permission to add lorebook entries to this character.");
                }
            }
        }

        var entry = new LorebookEntry(
            characterId: req.CharacterId,
            title: req.Title.Trim(),
            content: req.Content.Trim(),
            keywords: req.Keywords,
            category: req.Category,
            isConstant: req.IsConstant,
            priority: req.Priority
        );

        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        await repo.AddAsync(entry, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = new LorebookEntryDto(
            entry.Id,
            entry.CharacterId,
            entry.Title,
            entry.Content,
            entry.Keywords,
            entry.Category,
            entry.IsConstant,
            entry.Priority,
            entry.IsEnabled,
            entry.CreatedAt
        );

        return Result<LorebookEntryDto>.Success(dto);
    }
}
