using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Common.DateTimes;
using Domain.Entities;
using MediatR;

namespace Application.Features.UserProfile.Queries.GetUserProfile;

public sealed class GetUserProfileHandler : IRequestHandler<GetUserProfileQuery, Result<UserProfileDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public GetUserProfileHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserProfileDto>> Handle(GetUserProfileQuery query, CancellationToken cancellationToken)
    {
        var profileRepo = _unitOfWork.GetRepository<Domain.Entities.UserProfile>();
        var profiles = await profileRepo.GetAllAsync(ct: cancellationToken);
        var profile = profiles.FirstOrDefault(p => p.UserId == query.UserId);

        if (profile == null)
        {
            // Auto-initialize default profile for user
            profile = Domain.Entities.UserProfile.Create(
                userId: query.UserId,
                displayName: "Người Dùng Mới",
                avatarUrl: null,
                bio: "Chưa có lời giới thiệu.",
                interests: new List<string> { "Đọc Sách", "Nghe Nhạc", "Anime" },
                personalityTraits: new List<string> { "Thân thiện", "Tò mò" },
                statusMessage: "Đang khám phá thế giới..."
            );
            await profileRepo.AddAsync(profile, cancellationToken);
            await _unitOfWork.SaveChangesAsync(cancellationToken);
        }

        var dto = new UserProfileDto(
            Id: profile.Id,
            UserId: profile.UserId,
            DisplayName: profile.DisplayName,
            AvatarUrl: profile.AvatarUrl,
            Bio: profile.Bio,
            Interests: profile.GetInterests(),
            PersonalityTraits: profile.GetPersonalityTraits(),
            StatusMessage: profile.StatusMessage,
            CreatedAt: profile.CreatedAt,
            UpdatedAt: profile.UpdatedAt
        );

        return Result<UserProfileDto>.Success(dto);
    }
}
