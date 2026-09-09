using Application.Abstractions.Auth;
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
    private readonly IIdentityUnitOfWork _identityUnitOfWork;
    private readonly ICurrentUserProvider _currentUserProvider;

    public UpdateUserProfileHandler(
        IUnitOfWork unitOfWork,
        IIdentityUnitOfWork identityUnitOfWork,
        ICurrentUserProvider currentUserProvider)
    {
        _unitOfWork = unitOfWork;
        _identityUnitOfWork = identityUnitOfWork;
        _currentUserProvider = currentUserProvider;
    }

    public UpdateUserProfileHandler(IUnitOfWork unitOfWork, ICurrentUserProvider currentUserProvider)
        : this(unitOfWork, null!, currentUserProvider)
    {
    }

    public async Task<Result<UserProfileDto>> Handle(UpdateUserProfileCommand command, CancellationToken cancellationToken)
    {
        var currentUserId = _currentUserProvider.CurrentUserId;
        if (!string.IsNullOrEmpty(currentUserId) && Guid.TryParse(currentUserId, out var loggedInGuid))
        {
            if (loggedInGuid != command.UserId)
            {
                return Result<UserProfileDto>.Failure(StatusCodes.Status403Forbidden, "You do not have permission to update this user profile.");
            }
        }

        var profileRepo = _unitOfWork.GetRepository<Domain.Entities.UserProfile>();
        var profiles = await profileRepo.GetAllAsync(ct: cancellationToken);
        var profile = profiles.FirstOrDefault(p => p.UserId == command.UserId);

        var now = Clock.Now;
        var req = command.Request;

        if (profile == null)
        {
            profile = Domain.Entities.UserProfile.Create(
                userId: command.UserId,
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
                bio: req.Bio,
                interests: req.Interests,
                personalityTraits: req.PersonalityTraits,
                statusMessage: req.StatusMessage,
                updatedAt: now
            );
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        User? user = null;
        if (_identityUnitOfWork != null)
        {
            var userRepo = _identityUnitOfWork.GetRepository<User>();
            user = await userRepo.GetByIdAsync(command.UserId, cancellationToken);
        }

        var dto = new UserProfileDto(
            Id: profile.Id,
            UserId: profile.UserId,
            DisplayName: user?.DisplayName ?? "Người Dùng",
            AvatarUrl: user?.AvatarUrl,
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
