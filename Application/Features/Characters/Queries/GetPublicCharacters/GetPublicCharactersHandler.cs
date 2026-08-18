using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;

namespace Application.Features.Characters.Queries.GetPublicCharacters;

public sealed class GetPublicCharactersHandler : IRequestHandler<GetPublicCharactersQuery, Result<IReadOnlyList<CharacterDto>>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetPublicCharactersHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<IReadOnlyList<CharacterDto>>> Handle(GetPublicCharactersQuery query, CancellationToken cancellationToken)
    {
        var repo = _unitOfWork.GetRepository<Character>();
        var characters = await repo.GetAllAsync(
            c => c.IsPublic && (string.IsNullOrWhiteSpace(query.Category) || c.Category.ToLower() == query.Category.ToLower()),
            cancellationToken);

        var userRepo = _unitOfWork.GetRepository<User>();
        var users = await userRepo.GetAllAsync(ct: cancellationToken);
        var userMap = users.ToDictionary(u => u.Id.ToString(), u => u);

        var dtos = characters.Select(c =>
        {
            User? creator = null;
            if (!string.IsNullOrEmpty(c.CreatedBy))
            {
                userMap.TryGetValue(c.CreatedBy, out creator);
            }

            return new CharacterDto(
                c.Id,
                c.Name,
                c.Title,
                c.AvatarUrl,
                c.PersonalityPrompt,
                c.Greeting,
                c.Category,
                c.Tags,
                c.IsPublic,
                c.CreatedAt,
                c.CreatedBy,
                creator?.UserName ?? (c.CreatedBy == "system" ? "Hệ thống" : null),
                creator?.AvatarUrl
            );
        }).ToList();

        return Result<IReadOnlyList<CharacterDto>>.Success(dtos);
    }
}
