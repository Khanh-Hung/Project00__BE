using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Auth.Queries.GetCurrentUser;

public sealed class GetCurrentUserHandler : IRequestHandler<GetCurrentUserQuery, Result<UserDto>>
{
    private readonly IIdentityUnitOfWork _unitOfWork;

    public GetCurrentUserHandler(IIdentityUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<UserDto>> Handle(GetCurrentUserQuery query, CancellationToken cancellationToken)
    {
        var userRepo = _unitOfWork.GetRepository<User>();
        var user = await userRepo.GetByIdAsync(query.UserId, cancellationToken);
        if (user == null)
        {
            return Result<UserDto>.Failure(StatusCodes.Status404NotFound, "User was not found.");
        }

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
