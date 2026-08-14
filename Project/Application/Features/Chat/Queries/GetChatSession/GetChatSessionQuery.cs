using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Queries.GetChatSession;

public record GetChatSessionQuery(Guid SessionId) : IRequest<Result<ChatSessionDto>>;
