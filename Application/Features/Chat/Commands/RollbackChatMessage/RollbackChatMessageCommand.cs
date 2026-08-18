using Application.Abstractions.Responses;
using MediatR;

namespace Application.Features.Chat.Commands.RollbackChatMessage;

public sealed record RollbackChatMessageCommand(Guid SessionId, Guid MessageId) : IRequest<Result<bool>>;
