using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Queries.GetPublicCharacters;

public record GetPublicCharactersQuery(string? Category = null) : IRequest<Result<IReadOnlyList<CharacterDto>>>;
