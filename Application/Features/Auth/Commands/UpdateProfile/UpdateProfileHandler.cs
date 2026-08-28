using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Auth.Commands.UpdateProfile;

public sealed class UpdateProfileHandler : IRequestHandler<UpdateProfileCommand, Result<UserDto>>
{
    private readonly IIdentityUnitOfWork _unitOfWork;

    public UpdateProfileHandler(IIdentityUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserDto>> Handle(UpdateProfileCommand command, CancellationToken cancellationToken)
    {
        var userRepo = _unitOfWork.GetRepository<User>();
        var user = await userRepo.GetByIdAsync(command.UserId, cancellationToken);
        if (user == null)
        {
            return Result<UserDto>.Failure(StatusCodes.Status404NotFound, "User was not found.");
        }

        var req = command.Request;

        // 1. Update DisplayName and AvatarUrl
        var newDisplayName = req.DisplayName ?? user.DisplayName;
        var newAvatarUrl = req.AvatarUrl ?? user.AvatarUrl;
        user.UpdateProfile(newDisplayName, newAvatarUrl);

        // 2. Update UserName if requested and different from current
        if (!string.IsNullOrWhiteSpace(req.UserName))
        {
            var normalizedUserName = User.NormalizeUserName(req.UserName);
            if (!string.Equals(normalizedUserName, user.UserName, StringComparison.OrdinalIgnoreCase))
            {
                // Check 14-day cooldown
                if (!user.CanChangeUserName(14))
                {
                    var nextDate = user.GetNextUserNameChangeDate(14);
                    var daysLeft = nextDate.HasValue ? (int)Math.Ceiling((nextDate.Value - DateTime.UtcNow).TotalDays) : 14;
                    return Result<UserDto>.Failure(StatusCodes.Status400BadRequest, $"You can only change your UserName once every 14 days. Please try again in {daysLeft} days.");
                }

                // Check username uniqueness
                var duplicate = await userRepo.GetAsync(u => u.UserName == normalizedUserName && u.Id != user.Id, cancellationToken);
                if (duplicate != null)
                {
                    return Result<UserDto>.Failure(StatusCodes.Status409Conflict, $"UserName '@{normalizedUserName}' is already taken. Please choose a different name.");
                }

                try
                {
                    user.UpdateUserName(normalizedUserName, 14);
                }
                catch (Exception ex)
                {
                    return Result<UserDto>.Failure(StatusCodes.Status400BadRequest, ex.Message);
                }
            }
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken);

        var dto = new UserDto(
            user.Id,
            user.Email,
            user.UserName,
            user.DisplayName,
            user.AvatarUrl,
            user.CreatedAt,
            user.LastUserNameChangedAt,
            user.CanChangeUserName(),
            user.GetNextUserNameChangeDate()
        );

        return Result<UserDto>.Success(dto);
    }
}
