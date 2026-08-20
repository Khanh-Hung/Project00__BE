using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Common.DateTimes;
using Domain.Entities;
using Domain.Enums;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Chat.Commands.GenerateProactiveReachout;

public sealed class GenerateProactiveReachoutHandler : IRequestHandler<GenerateProactiveReachoutCommand, Result<ProactiveReachoutResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILLMService _llmService;

    public GenerateProactiveReachoutHandler(IUnitOfWork unitOfWork, ILLMService llmService)
    {
        _unitOfWork = unitOfWork;
        _llmService = llmService;
    }

    public async Task<Result<ProactiveReachoutResponse>> Handle(GenerateProactiveReachoutCommand command, CancellationToken cancellationToken)
    {
        var characterRepo = _unitOfWork.GetRepository<Character>();
        var profileRepo = _unitOfWork.GetRepository<Domain.Entities.UserProfile>();
        var sessionRepo = _unitOfWork.GetRepository<ChatSession>();

        var character = await characterRepo.GetByIdAsync(command.Request.CharacterId, cancellationToken);
        if (character == null)
        {
            return Result<ProactiveReachoutResponse>.Failure(StatusCodes.Status404NotFound, $"Character with ID '{command.Request.CharacterId}' was not found.");
        }

        var profiles = await profileRepo.GetAllAsync(ct: cancellationToken);
        var profile = profiles.FirstOrDefault(p => p.UserId == command.Request.UserId);

        if (profile == null)
        {
            profile = Domain.Entities.UserProfile.Create(
                userId: command.Request.UserId,
                displayName: "Người Dùng",
                avatarUrl: null,
                bio: "Thích khám phá những điều thú vị và tìm kiếm bạn bè.",
                interests: new List<string> { "Đọc Sách", "Nghe Nhạc", "Anime" },
                personalityTraits: new List<string> { "Thân thiện", "Tò mò" },
                statusMessage: "Đang lướt trang cá nhân..."
            );
            await profileRepo.AddAsync(profile, cancellationToken);
        }

        // 1. Generate Proactive Reachout Message from AI embodying the Character reading the User's Profile
        var aiResult = await _llmService.GenerateProactiveReachoutAsync(character, profile, cancellationToken);

        // 2. Create new ChatSession with this proactive opening message
        var session = new ChatSession(
            character.Id,
            command.Request.UserId,
            $"Trò chuyện cùng {character.Name}"
        );

        session.AddAssistantMessage(aiResult.OpeningMessage);
        await sessionRepo.AddAsync(session, cancellationToken);

        // 3. Initialize or retrieve relationship
        var defaultMood = Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var dm)
            ? dm
            : CharacterMood.Neutral;

        await _unitOfWork.Relationships.GetOrCreateAsync(
            command.Request.UserId,
            character.Id,
            character.DefaultAffectionScore,
            defaultMood,
            cancellationToken
        );

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var response = new ProactiveReachoutResponse(
            SessionId: session.Id,
            CharacterId: character.Id,
            CharacterName: character.Name,
            CharacterAvatar: character.AvatarUrl,
            OpeningMessage: aiResult.OpeningMessage,
            MatchReason: aiResult.MatchReason,
            CreatedAt: session.CreatedAt
        );

        return Result<ProactiveReachoutResponse>.Success(response);
    }
}
