using Application.Abstractions.Responses;
using MediatR;

namespace Application.Features.Characters.Commands.DeleteCharacter;

public record DeleteCharacterCommand(Guid Id) : IRequest<Result<bool>>;
