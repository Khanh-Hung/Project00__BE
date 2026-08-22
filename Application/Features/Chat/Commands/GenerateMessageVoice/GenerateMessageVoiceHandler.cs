using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Application.Features.Chat.Commands.GenerateMessageVoice;

public sealed class GenerateMessageVoiceHandler : IRequestHandler<GenerateMessageVoiceCommand, Result<VoiceGenerationResult>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly IVoicePromptCompiler _voiceCompiler;
    private readonly IVoiceGenerationService _voiceService;
    private readonly ILogger<GenerateMessageVoiceHandler> _logger;

    public GenerateMessageVoiceHandler(
        IUnitOfWork unitOfWork,
        ICurrentUserProvider currentUserProvider,
        IVoicePromptCompiler voiceCompiler,
        IVoiceGenerationService voiceService,
        ILogger<GenerateMessageVoiceHandler> logger)
    {
        _unitOfWork = unitOfWork;
        _currentUserProvider = currentUserProvider;
        _voiceCompiler = voiceCompiler;
        _voiceService = voiceService;
        _logger = logger;
    }

    public async Task<Result<VoiceGenerationResult>> Handle(GenerateMessageVoiceCommand command, CancellationToken cancellationToken)
    {
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();
        var relationshipRepo = _unitOfWork.GetRepository<CharacterRelationship>();

        // 1. Fetch ChatSession
        var session = await sessionRepo.GetByIdAsync(command.SessionId, cancellationToken);
        if (session == null)
        {
            return Result<VoiceGenerationResult>.Failure(StatusCodes.Status404NotFound, $"Chat session '{command.SessionId}' was not found.");
        }

        // 2. Resolve Effective User & Enforce Ownership
        Guid? effectiveUserId = null;
        if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
        {
            effectiveUserId = uid;
        }

        if (session.UserId.HasValue && session.UserId.Value != Guid.Empty)
        {
            if (!effectiveUserId.HasValue || session.UserId.Value != effectiveUserId.Value)
            {
                return Result<VoiceGenerationResult>.Failure(StatusCodes.Status403Forbidden, "You do not have permission to access this chat session.");
            }
        }

        // 3. Find Target Message
        var message = session.Messages.FirstOrDefault(m => m.Id == command.MessageId);
        if (message == null)
        {
            return Result<VoiceGenerationResult>.Failure(StatusCodes.Status404NotFound, $"Message '{command.MessageId}' was not found in session.");
        }

        // 4. Fetch Character
        var character = await characterRepo.GetByIdAsync(session.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<VoiceGenerationResult>.Failure(StatusCodes.Status404NotFound, $"Character '{session.CharacterId}' was not found.");
        }

        // 5. Fetch Relationship
        CharacterRelationship? relationship = null;
        if (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty)
        {
            relationship = await relationshipRepo.GetAsync(
                r => r.UserId == effectiveUserId.Value && r.CharacterId == character.Id,
                cancellationToken);
        }

        var voiceProfile = character.VoiceProfile ?? new CharacterVoiceProfile();
        var mood = relationship?.CurrentMood ?? CharacterMood.Neutral;
        var intensity = relationship?.MoodIntensity ?? 50;
        var affection = relationship?.AffectionScore ?? character.DefaultAffectionScore;
        var stage = relationship != null ? RelationshipStageResolver.Resolve(relationship.AffectionScore, character.CustomMilestonesJson).ToString() : "Neutral";

        var context = new VoiceContext(
            Voice: voiceProfile,
            Mood: mood,
            MoodIntensity: intensity,
            AffectionScore: affection,
            RelationshipStage: stage,
            RawText: message.Content
        );

        // 6. Compile Voice Request (Clean dialogue + compute expression)
        var voiceRequest = _voiceCompiler.CompileVoiceRequest(context);

        // 7. Call Voice Generation Service (Provider-agnostic)
        var voiceResult = await _voiceService.GenerateVoiceAsync(voiceRequest, cancellationToken);

        return Result<VoiceGenerationResult>.Success(voiceResult);
    }
}
