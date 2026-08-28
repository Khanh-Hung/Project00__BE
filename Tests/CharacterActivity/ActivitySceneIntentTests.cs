using Application.Contracts.Activities;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class ActivitySceneIntentTests
{
    [Fact]
    public void MapToSceneIntent_PreservesVisualHierarchy_AndPopulatesStructuredFields()
    {
        var charId = Guid.NewGuid();
        var activity = new CharacterActivity(
            characterId: charId,
            activityType: CharacterActivityType.GettingReady,
            location: "Crystal Sanctuary",
            timeBucket: "2026-08-28T08:00",
            decisionFingerprint: "fp123",
            source: CharacterActivitySource.Autonomous,
            shouldCreateVisualMoment: true,
            reason: "Morning grooming"
        );

        var candidate = new CharacterActivityCandidate(
            ActivityType: CharacterActivityType.GettingReady,
            Location: "Crystal Sanctuary",
            Reason: "Morning grooming",
            Priority: ActivityPriority.High,
            DurationMinutes: 30,
            ShouldCreateVisualMoment: true,
            Confidence: 0.95f,
            ActionHint: "brushing silver hair before crystal mirror",
            PoseHint: "standing upright facing mirror",
            OutfitHint: "Silk Robe"
        );

        var sceneIntent = CharacterActivitySceneIntentMapper.MapToSceneIntent(
            activity: activity,
            candidate: candidate
        );

        Assert.NotNull(sceneIntent);
        Assert.Equal(charId, sceneIntent.CharacterId);
        Assert.Equal("Crystal Sanctuary", sceneIntent.LocationHint);
        Assert.Equal("brushing silver hair before crystal mirror", sceneIntent.ActionHint);
        Assert.Equal("standing upright facing mirror", sceneIntent.PoseHint);
        Assert.Equal("Silk Robe", sceneIntent.OutfitHint);
        Assert.Equal("Poised and focused", sceneIntent.MoodHint);
    }
}
