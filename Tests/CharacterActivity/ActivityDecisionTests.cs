using Application.Contracts.Activities;
using Application.Services;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class ActivityDecisionTests
{
    private readonly CharacterActivityDecisionService _service;

    public ActivityDecisionTests()
    {
        _service = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);
    }

    [Fact]
    public async Task TimeOfDay_Night_SelectsSleepingOrResting()
    {
        var charId = Guid.NewGuid();
        var nightTime = new DateTime(2026, 8, 28, 23, 30, 0, DateTimeKind.Utc); // 23:30

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: nightTime,
            CurrentLocation: "Bedchamber",
            TimeBucket: "2026-08-28T23:00"
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.True(candidate.ActivityType == CharacterActivityType.Sleeping || candidate.ActivityType == CharacterActivityType.Relaxing);
    }

    [Fact]
    public async Task TimeOfDay_Morning_SelectsGettingReadyOrBreakfast()
    {
        var charId = Guid.NewGuid();
        var morningTime = new DateTime(2026, 8, 28, 7, 30, 0, DateTimeKind.Utc); // 07:30

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: morningTime,
            CurrentLocation: "Quarters",
            TimeBucket: "2026-08-28T07:00"
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.True(candidate.ActivityType == CharacterActivityType.GettingReady || candidate.ActivityType == CharacterActivityType.Eating);
    }

    [Fact]
    public async Task Personality_Scholar_BiasesTowardsReadingOrWorking()
    {
        var charId = Guid.NewGuid();
        var afternoonTime = new DateTime(2026, 8, 28, 15, 0, 0, DateTimeKind.Utc); // 15:00

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: afternoonTime,
            CurrentLocation: "Grand Library",
            TimeBucket: "2026-08-28T15:00",
            PersonalityPrompt: "Scholarly, arcane researcher, intellectual archivist"
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.True(candidate.ActivityType == CharacterActivityType.Reading || candidate.ActivityType == CharacterActivityType.Working);
    }

    [Fact]
    public async Task ActiveGoal_Exploration_BiasesTowardsExploring()
    {
        var charId = Guid.NewGuid();
        var morningTime = new DateTime(2026, 8, 28, 10, 0, 0, DateTimeKind.Utc); // 10:00

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: morningTime,
            CurrentLocation: "Ancient Ruins",
            TimeBucket: "2026-08-28T10:00",
            ActiveGoals: new[] { "Explore the uncharted northern catacombs" }
        );

        var candidate = await _service.DecideAsync(request);

        Assert.NotNull(candidate);
        Assert.True(candidate.ActivityType == CharacterActivityType.Exploring || candidate.ActivityType == CharacterActivityType.Working);
    }
}
