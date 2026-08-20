using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;

namespace Application.Features.Lorebook.Queries.GetCharacterLorebook;

public sealed record GetCharacterLorebookQuery(Guid? CharacterId) : IRequest<Result<List<LorebookEntryDto>>>;

public sealed class GetCharacterLorebookHandler : IRequestHandler<GetCharacterLorebookQuery, Result<List<LorebookEntryDto>>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetCharacterLorebookHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<List<LorebookEntryDto>>> Handle(GetCharacterLorebookQuery query, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<LorebookEntry>();
        var entries = await repo.FindAsync(
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
