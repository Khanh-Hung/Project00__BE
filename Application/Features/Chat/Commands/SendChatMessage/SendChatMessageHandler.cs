using Application.Abstractions.Auth;
using Application.Abstractions.Responses;
using Application.Common;
using Application.Common.Exceptions;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Commands.SendChatMessage;

public sealed class SendChatMessageHandler : IRequestHandler<SendChatMessageCommand, Result<SendMessageResponse>>
{
    private readonly ICharacterRuntime _characterRuntime;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<SendChatMessageHandler> _logger;

    public SendChatMessageHandler(
        ICharacterRuntime characterRuntime,
        ICurrentUserProvider currentUserProvider,
        ILogger<SendChatMessageHandler> logger)
    {
        _characterRuntime = characterRuntime;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task<Result<SendMessageResponse>> Handle(SendChatMessageCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;

        Guid effectiveUserId = Guid.Empty;
        if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
        {
            effectiveUserId = uid;
        }

        var turnRequest = new CharacterTurnRequest(
            UserId: effectiveUserId,
            CharacterId: Guid.Empty, // Will be resolved by Runtime via Session
            SessionId: req.SessionId,
            UserMessage: req.Content,
            TurnId: Guid.NewGuid()
        );

        CharacterTurnResult turnResult;
        try
        {
            turnResult = await _characterRuntime.ProcessTurnAsync(turnRequest, cancellationToken);
        }
        catch (KeyNotFoundException ex)
        {
            return Result<SendMessageResponse>.Failure(StatusCodes.Status404NotFound, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Result<SendMessageResponse>.Failure(StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (CharacterTurnConcurrencyException ex)
        {
            _logger.LogWarning(ex, "Character turn concurrency conflict for Turn {TurnId}", turnRequest.TurnId);
            return Result<SendMessageResponse>.Failure(StatusCodes.Status409Conflict, ex.Message);
        }

        var (level, stageName, _) = RelationshipStageResolver.Resolve(turnResult.Relationship.AffectionScore);

        return Result<SendMessageResponse>.Success(new SendMessageResponse(
            UserMessage: new ChatMessageDto(Guid.NewGuid(), MessageRole.User, req.Content, Clock.Now),
            AssistantMessage: new ChatMessageDto(turnResult.MessageId, MessageRole.Assistant, turnResult.Reply, Clock.Now),
            AffectionScore: turnResult.Relationship.AffectionScore,
            RelationshipLevel: level,
            RelationshipStage: stageName,
            CurrentMood: turnResult.Mood,
            MoodIntensity: turnResult.MoodIntensity,
            AffectionDelta: turnResult.AffectionDelta,
            LevelUp: false,
            UnlockedEvent: null
        ));
    }
}
