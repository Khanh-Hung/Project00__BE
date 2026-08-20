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

        // 3. Resolve Effective User ID with Strict User Isolation
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

        // 6. Slice Recent Conversation Messages within Budget (Max 10)
        var recentMessages = session.Messages.TakeLast(MaxRecentMessagesBudget).ToList();

        return new RoleplayContext(
            character,
            relationship,
            relevantMemories,
            recentMessages,
            userMessage,
            session
        );
    }
}
