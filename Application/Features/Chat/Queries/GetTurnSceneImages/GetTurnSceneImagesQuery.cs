using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Queries.GetTurnSceneImages;

public sealed record GetTurnSceneImagesQuery(
    Guid SessionId,
    Guid TurnId
) : IRequest<Result<List<SceneImageDto>>>;
