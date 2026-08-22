using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Lorebook.Queries.GetCharacterLorebook;

public sealed record GetCharacterLorebookQuery(Guid? CharacterId) : IRequest<Result<List<LorebookEntryDto>>>;

public sealed class GetCharacterLorebookHandler : IRequestHandler<GetCharacterLorebookQuery, Result<List<LorebookEntryDto>>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public GetCharacterLorebookHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public async Task<Result<List<LorebookEntryDto>>> Handle(GetCharacterLorebookQuery query, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserProvider.CurrentUserId;

        if (query.CharacterId.HasValue)
        {
            var charRepo = _unitOfWork.GetRepository<Character>();
            var character = await charRepo.GetByIdAsync(query.CharacterId.Value, cancellationToken);
            if (character == null)
            {
                return Result<List<LorebookEntryDto>>.Failure(StatusCodes.Status404NotFound, $"Character '{query.CharacterId.Value}' was not found.");
            }

            if (!character.IsPublic)
            {
                if (string.IsNullOrEmpty(currentUserId) ||
                    (!string.IsNullOrEmpty(character.CreatedBy) &&
                     character.CreatedBy != "system" &&
                     !string.Equals(character.CreatedBy, currentUserId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Result<List<LorebookEntryDto>>.Failure(StatusCodes.Status404NotFound, $"Character '{query.CharacterId.Value}' was not found.");
                }
            }
        }

        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        var entries = await repo.GetAllAsync(
            e => (query.CharacterId == null || e.CharacterId == query.CharacterId || e.CharacterId == null),
            cancellationToken);

        var dtoList = entries
            .OrderByDescending(e => e.Priority)
            .ThenBy(e => e.Title)
            .Select(entry => new LorebookEntryDto(
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
            ))
            .ToList();

        return Result<List<LorebookEntryDto>>.Success(dtoList);
    }
}
