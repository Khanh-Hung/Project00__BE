using Application.DTOs;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Application.Services;

public sealed class CharacterVisualProfileService : ICharacterVisualProfileService
{
    private readonly ProjectDbContext _dbContext;
    private readonly ILogger<CharacterVisualProfileService> _logger;

    public CharacterVisualProfileService(
        ProjectDbContext dbContext,
        ILogger<CharacterVisualProfileService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<CharacterVisualProfile?> GetCurrentProfileAsync(Guid characterId, CancellationToken ct = default)
    {
        return await _dbContext.CharacterVisualProfiles
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);
    }

    public async Task<CharacterVisualProfile> CreateProfileAsync(
        Guid characterId,
        string? hair = null,
        string? eye = null,
        string? skin = null,
        string? body = null,
        string? features = null,
        CancellationToken ct = default)
    {
        var existing = await _dbContext.CharacterVisualProfiles
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        if (existing != null)
        {
            return existing;
        }

        var profile = new CharacterVisualProfile(
            characterId: characterId,
            hairDescription: hair,
            eyeDescription: eye,
            skinDescription: skin,
            bodyDescription: body,
            distinguishingFeatures: features,
            visualVersion: 1,
            now: DateTime.UtcNow
        );

        await _dbContext.CharacterVisualProfiles.AddAsync(profile, ct);
        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualProfileService] Created Visual Profile for CharacterId={CharacterId} (Version={Version})",
            characterId, profile.VisualVersion);

        return profile;
    }

    public async Task<CharacterVisualProfile> UpdateAppearanceAsync(
        Guid characterId,
        string? hair,
        string? eye,
        string? skin,
        string? body,
        string? features,
        CancellationToken ct = default)
    {
        var profile = await _dbContext.CharacterVisualProfiles
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        var now = DateTime.UtcNow;

        if (profile == null)
        {
            profile = new CharacterVisualProfile(
                characterId: characterId,
                hairDescription: hair,
                eyeDescription: eye,
                skinDescription: skin,
                bodyDescription: body,
                distinguishingFeatures: features,
                visualVersion: 1,
                now: now
            );
            await _dbContext.CharacterVisualProfiles.AddAsync(profile, ct);
        }
        else
        {
            profile.UpdateAppearance(hair, eye, skin, body, features, now);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualProfileService] Updated Appearance for CharacterId={CharacterId} (New Version={Version})",
            characterId, profile.VisualVersion);

        return profile;
    }

    public async Task<CharacterVisualProfile> SetPrimaryReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        var profile = await _dbContext.CharacterVisualProfiles
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        var now = DateTime.UtcNow;

        if (profile == null)
        {
            profile = new CharacterVisualProfile(
                characterId: characterId,
                primaryReferenceId: referenceId,
                faceReferenceId: referenceId,
                visualVersion: 1,
                now: now
            );
            await _dbContext.CharacterVisualProfiles.AddAsync(profile, ct);
        }
        else
        {
            profile.SetPrimaryReference(referenceId, now);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualProfileService] Set PrimaryReferenceId={ReferenceId} for CharacterId={CharacterId} (Version={Version})",
            referenceId, characterId, profile.VisualVersion);

        return profile;
    }

    public async Task<CharacterVisualProfile> SetFaceReferenceAsync(Guid characterId, Guid referenceId, CancellationToken ct = default)
    {
        var profile = await _dbContext.CharacterVisualProfiles
            .FirstOrDefaultAsync(p => p.CharacterId == characterId, ct);

        var now = DateTime.UtcNow;

        if (profile == null)
        {
            profile = new CharacterVisualProfile(
                characterId: characterId,
                faceReferenceId: referenceId,
                visualVersion: 1,
                now: now
            );
            await _dbContext.CharacterVisualProfiles.AddAsync(profile, ct);
        }
        else
        {
            profile.SetFaceReference(referenceId, now);
        }

        await _dbContext.SaveChangesAsync(ct);

        _logger.LogInformation("[CharacterVisualProfileService] Set FaceReferenceId={ReferenceId} for CharacterId={CharacterId} (Version={Version})",
            referenceId, characterId, profile.VisualVersion);

        return profile;
    }
}
