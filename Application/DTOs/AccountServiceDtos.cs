using System;

namespace Application.DTOs;

/// <summary>
/// Health and system status response from external Account Service
/// </summary>
public record AccountSystemStatusDto(
    string Status,
    string Service,
    DateTime Timestamp
);

/// <summary>
/// User profile returned by external Account Service (/api/v1/profile/me)
/// </summary>
public record AccountUserProfileDto(
    Guid UserId,
    string Email,
    string Username,
    string DisplayName,
    string AvatarUrl,
    DateOnly? DateOfBirth,
    string? Gender,
    bool IsEmailVerified,
    string Role
);

/// <summary>
/// Wallet balance and status returned by external Account Service (/api/v1/wallet/me)
/// </summary>
public record AccountWalletDto(
    Guid Id,
    Guid UserId,
    decimal Balance,
    string Currency,
    string Status
);
