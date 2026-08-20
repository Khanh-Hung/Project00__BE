using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.UserProfile.Queries.GetUserProfile;

public sealed record GetUserProfileQuery(Guid UserId) : IRequest<Result<UserProfileDto>>;
