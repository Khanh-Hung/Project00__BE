using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.UserProfile.Commands.UpdateUserProfile;

public sealed record UpdateUserProfileCommand(
    Guid UserId,
    UpdateUserProfileRequest Request
) : IRequest<Result<UserProfileDto>>;
