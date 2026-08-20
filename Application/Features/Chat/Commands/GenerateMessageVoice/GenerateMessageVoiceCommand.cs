using Application.Abstractions.Responses;
using Application.Interfaces;
using MediatR;

namespace Application.Features.Chat.Commands.GenerateMessageVoice;

public sealed record GenerateMessageVoiceCommand(
    Guid SessionId,
    Guid MessageId
) : IRequest<Result<VoiceGenerationResult>>;
