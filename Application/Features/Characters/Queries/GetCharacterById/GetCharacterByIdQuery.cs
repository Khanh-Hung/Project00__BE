using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Queries.GetCharacterById;

public record GetCharacterByIdQuery(Guid Id) : IRequest<Result<CharacterDto>>;
