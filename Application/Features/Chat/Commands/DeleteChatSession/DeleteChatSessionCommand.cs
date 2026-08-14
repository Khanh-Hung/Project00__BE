using Application.Abstractions.Responses;
using MediatR;

namespace Application.Features.Chat.Commands.DeleteChatSession;

public record DeleteChatSessionCommand(Guid SessionId) : IRequest<Result<bool>>;
