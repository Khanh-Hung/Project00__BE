using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Commands.TriggerTurnSceneImage;

public sealed record TriggerTurnSceneImageGenerationCommand(
    Guid SessionId,
    Guid TurnId
) : IRequest<Result<TriggerSceneImageResponse>>;
