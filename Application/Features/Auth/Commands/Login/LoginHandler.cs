using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Auth.Commands.Login;

public sealed class LoginHandler : IRequestHandler<LoginCommand, Result<AuthResponse>>
{
    private readonly IIdentityUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;

    public LoginHandler(
        IIdentityUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
    }

    public async Task<Result<AuthResponse>> Handle(LoginCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        var userRepo = _unitOfWork.GetRepository<User>();

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        var user = await userRepo.GetAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (user == null)
        {
            return Result<AuthResponse>.Failure(StatusCodes.Status401Unauthorized, "Invalid email or password.");
        }

        var isPasswordValid = _passwordHasher.VerifyPassword(req.Password, user.PasswordHash);
        if (!isPasswordValid)
        {
            return Result<AuthResponse>.Failure(StatusCodes.Status401Unauthorized, "Invalid email or password.");
        }

        var token = _jwtTokenGenerator.GenerateToken(user);
        var userDto = new UserDto(
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

        return Result<AuthResponse>.Success(new AuthResponse(token, userDto));
    }
}
