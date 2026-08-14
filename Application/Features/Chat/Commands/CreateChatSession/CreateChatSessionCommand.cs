using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Commands.CreateChatSession;

public record CreateChatSessionCommand(CreateSessionRequest Request) : IRequest<Result<ChatSessionDto>>;
