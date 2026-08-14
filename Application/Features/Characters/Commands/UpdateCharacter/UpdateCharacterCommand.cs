using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Commands.UpdateCharacter;

public record UpdateCharacterCommand(Guid Id, UpdateCharacterRequest Request) : IRequest<Result<CharacterDto>>;
