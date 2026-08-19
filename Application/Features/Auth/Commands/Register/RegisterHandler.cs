using Application.Abstractions.Data;
using Application.Abstractions.Responses;
using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Application.Features.Auth.Commands.Register;

public sealed class RegisterHandler : IRequestHandler<RegisterCommand, Result<AuthResponse>>
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;

    public RegisterHandler(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
    }

    public async Task<Result<AuthResponse>> Handle(RegisterCommand command, CancellationToken cancellationToken)
    {
        var req = command.Request;
        var userRepo = _unitOfWork.GetRepository<User>();

        var normalizedEmail = req.Email.Trim().ToLowerInvariant();
        var existing = await userRepo.GetAsync(u => u.Email == normalizedEmail, cancellationToken);
        if (existing != null)
        {
            return Result<AuthResponse>.Failure(StatusCodes.Status409Conflict, "Email is already in use. Please log in or choose a different email.");
        }

        var passwordHash = _passwordHasher.HashPassword(req.Password);
        
        // Extract UserName from email prefix if not provided
        var rawUserName = req.UserName;
        if (string.IsNullOrWhiteSpace(rawUserName))
        {
            var prefix = normalizedEmail.Split('@')[0];
            rawUserName = User.NormalizeUserName(prefix);
        }
        else
        {
            rawUserName = User.NormalizeUserName(rawUserName);
        }

        // Check if UserName already exists; if so, append random suffix
        var existingUserWithSameName = await userRepo.GetAsync(u => u.UserName == rawUserName, cancellationToken);
        if (existingUserWithSameName != null)
        {
            rawUserName = $"{rawUserName}_{Guid.NewGuid().ToString("N")[..4]}";
        }

        var displayName = string.IsNullOrWhiteSpace(req.DisplayName) ? "User" : req.DisplayName.Trim();
        var user = new User(normalizedEmail, passwordHash, rawUserName, displayName, req.AvatarUrl);

        await userRepo.AddAsync(user, cancellationToken);
        await _unitOfWork.SaveChangesAsync(cancellationToken);

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
