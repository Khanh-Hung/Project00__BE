using Application.Abstractions.Responses;
using Application.DTOs;
using MediatR;

namespace Application.Features.Auth.Queries.GetCurrentUser;

public record GetCurrentUserQuery(Guid UserId) : IRequest<Result<UserDto>>;
