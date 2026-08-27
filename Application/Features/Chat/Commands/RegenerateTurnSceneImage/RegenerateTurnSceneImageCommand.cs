using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Commands.RegenerateTurnSceneImage;

public sealed record RegenerateTurnSceneImageCommand(
    Guid SessionId,
    Guid TurnId,
    Guid? RequestId = null
) : IRequest<Result<TriggerSceneImageResponse>>;
