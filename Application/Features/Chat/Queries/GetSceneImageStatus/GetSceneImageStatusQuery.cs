using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Queries.GetSceneImageStatus;

public sealed record GetSceneImageStatusQuery(
    Guid GenerationRequestId
) : IRequest<Result<SceneImageStatusResponse>>;
