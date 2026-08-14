using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Characters.Commands.CreateCharacter;

public record CreateCharacterCommand(CreateCharacterRequest Request) : IRequest<Result<CharacterDto>>;
