using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Auth.Commands.UpdateProfile;

public sealed record UpdateProfileCommand(Guid UserId, UpdateProfileRequest Request) : IRequest<Result<UserDto>>;
