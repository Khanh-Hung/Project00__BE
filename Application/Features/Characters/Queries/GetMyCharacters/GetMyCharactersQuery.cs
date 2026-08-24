using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Queries.GetMyCharacters;

public sealed record GetMyCharactersQuery() : IRequest<Result<IReadOnlyList<CharacterDto>>>;
