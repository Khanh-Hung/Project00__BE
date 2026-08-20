using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Common.DateTimes;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.UserProfile.Commands.UpdateUserProfile;

public sealed class UpdateUserProfileHandler : IRequestHandler<UpdateUserProfileCommand, Result<UserProfileDto>>
{
    private readonly IUnitOfWork _unitOfWork;

    public UpdateUserProfileHandler(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserProfileDto>> Handle(UpdateUserProfileCommand command, CancellationToken cancellationToken)
    {
        var profileRepo = _unitOfWork.GetRepository<Domain.Entities.UserProfile>();
        var profiles = await profileRepo.GetAllAsync(ct: cancellationToken);
        var profile = profiles.FirstOrDefault(p => p.UserId == command.UserId);

        var now = Clock.Now;
        var req = command.Request;

        if (profile == null)
        {
            profile = Domain.Entities.UserProfile.Create(
                userId: command.UserId,
                displayName: req.DisplayName,
                avatarUrl: req.AvatarUrl,
                bio: req.Bio,
                interests: req.Interests,
                personalityTraits: req.PersonalityTraits,
                statusMessage: req.StatusMessage
            );
            await profileRepo.AddAsync(profile, cancellationToken);
        }
        else
        {
            profile.Update(
                displayName: req.DisplayName,
                avatarUrl: req.AvatarUrl,
                bio: req.Bio,
                interests: req.Interests,
                personalityTraits: req.PersonalityTraits,
                statusMessage: req.StatusMessage,
                updatedAt: now
            );
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
