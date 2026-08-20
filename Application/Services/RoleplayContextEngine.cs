using Application.Abstractions.Auth;
using Application.Abstractions.Data;
using Application.Common;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class RoleplayContextEngine : IRoleplayContextEngine
{
    private const int MaxRecentMessagesBudget = 10;
    private const int MaxMemoriesBudget = 6;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMemoryService _memoryService;
    private readonly ICurrentUserProvider _currentUserProvider;
    private readonly ILogger<RoleplayContextEngine> _logger;

    public RoleplayContextEngine(
        IUnitOfWork unitOfWork,
        IMemoryService memoryService,
        ICurrentUserProvider currentUserProvider,
        ILogger<RoleplayContextEngine> logger)
    {
        _unitOfWork = unitOfWork;
        _memoryService = memoryService;
        _currentUserProvider = currentUserProvider;
        _logger = logger;
    }

    public async Task<RoleplayContext> BuildContextAsync(
        Guid sessionId,
        string userMessage,
        Guid? currentUserId = null,
        CancellationToken ct = default)
    {
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();
        var characterRepo = _unitOfWork.GetRepository<Character>();

        // 1. Fetch ChatSession
        var session = await sessionRepo.GetByIdAsync(sessionId, ct);
        if (session == null)
        {
            throw new KeyNotFoundException($"Chat session with ID '{sessionId}' was not found.");
        }

        // 2. Fetch Character
        var character = await characterRepo.GetByIdAsync(session.CharacterId, ct);
        if (character == null)
        {
            throw new KeyNotFoundException($"Character with ID '{session.CharacterId}' for session '{sessionId}' was not found.");
        }

        // 3. Resolve Effective User ID with Strict User Ownership Enforcement
        Guid? effectiveUserId = currentUserId;
        if (!effectiveUserId.HasValue || effectiveUserId.Value == Guid.Empty)
        {
            if (!string.IsNullOrEmpty(_currentUserProvider.CurrentUserId) && Guid.TryParse(_currentUserProvider.CurrentUserId, out var uid))
            {
                effectiveUserId = uid;
            }
            else
            {
                effectiveUserId = session.UserId;
            }
        }

        // Strict Ownership Check: Prevent cross-tenant access to another user's session
        if (session.UserId.HasValue && effectiveUserId.HasValue && session.UserId.Value != effectiveUserId.Value)
        {
            _logger.LogWarning("Unauthorized session access attempt. Session User: {SessionUserId}, Request User: {RequestUserId}",
                session.UserId.Value, effectiveUserId.Value);
            throw new UnauthorizedAccessException("You do not have permission to access this chat session.");
        }

        // 4. Retrieve or initialize CharacterRelationship
        CharacterRelationship? relationship = null;
        if (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty)
        {
            var defaultMood = Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var dm)
                ? dm
                : CharacterMood.Neutral;

            relationship = await _unitOfWork.Relationships.GetOrCreateAsync(
                effectiveUserId.Value,
                character.Id,
                character.DefaultAffectionScore,
                defaultMood,
                ct);

            // Soften transient mood after > 24 hours of inactivity
            relationship.SoftenMoodIfInactive(Clock.Now, TimeSpan.FromHours(24), defaultMood);
        }

        // 5. Retrieve Relevant Memories within Budget (Max 6)
        IReadOnlyList<CharacterMemory> relevantMemories = Array.Empty<CharacterMemory>();
        if (effectiveUserId.HasValue && effectiveUserId.Value != Guid.Empty)
        {
            try
            {
                relevantMemories = await _memoryService.GetRelevantMemoriesAsync(
                    effectiveUserId.Value,
                    character.Id,
                    maxCount: MaxMemoriesBudget,
                    ct: ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Memory retrieval failed for Character {CharacterId}, User {UserId}. Degraded roleplay context created without memories.",
                    character.Id,
                    effectiveUserId.Value);
            }
        }

        // 6. Dynamic Token Budget Pruning for Working Memory / History (Max 10 messages and max 2,400 history tokens)
        var messageWindow = session.Messages.TakeLast(MaxRecentMessagesBudget).ToList();
        var boundedMessages = new List<ChatMessage>();
        var accumulatedTokens = 0;
        const int MaxHistoryTokenBudget = 2400;

        // Iterate backwards (newest to oldest) to preserve the most recent context within budget
        for (int i = messageWindow.Count - 1; i >= 0; i--)
        {
            var msg = messageWindow[i];
            var msgTokens = TokenEstimator.Estimate(msg.Content);
            if (accumulatedTokens + msgTokens > MaxHistoryTokenBudget && boundedMessages.Count > 0)
            {
                break;
            }

            boundedMessages.Insert(0, msg);
            accumulatedTokens += msgTokens;
        }

        return new RoleplayContext(
            character,
            relationship,
            relevantMemories,
            boundedMessages,
            userMessage,
            session
        );
    }
}
