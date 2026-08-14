using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Queries.GetUserChatSessions;

public record GetUserChatSessionsQuery(Guid? UserId = null) : IRequest<Result<IReadOnlyList<ChatSessionListItemDto>>>;
