using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Chat.Commands.SendChatMessage;

public record SendChatMessageCommand(SendMessageRequest Request) : IRequest<Result<SendMessageResponse>>;
