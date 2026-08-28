using Application.Contracts.Activities;
using Application.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.CharacterActivities;

public sealed class ActivityDeterminismTests
{
    [Fact]
    public async Task SameInput_ProducesIdenticalCandidateAndFingerprint_Across10Executions()
    {
        var service = new CharacterActivityDecisionService(NullLogger<CharacterActivityDecisionService>.Instance);

        var charId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var fixedTime = new DateTime(2026, 8, 28, 14, 0, 0, DateTimeKind.Utc);
        var timeBucket = "2026-08-28T14:00";

        var request = new CharacterActivityDecisionRequest(
            CharacterId: charId,
            CurrentTime: fixedTime,
            CurrentLocation: "Observatory",
            TimeBucket: timeBucket,
            PersonalityPrompt: "Observant stargazer scholar",
            SceneRevision: 3
        );

        var firstResult = await service.DecideAsync(request);
        Assert.NotNull(firstResult);

        for (int i = 0; i < 9; i++)
        {
            var iterationResult = await service.DecideAsync(request);
            Assert.NotNull(iterationResult);

            Assert.Equal(firstResult.ActivityType, iterationResult.ActivityType);
            Assert.Equal(firstResult.Location, iterationResult.Location);
            Assert.Equal(firstResult.ShouldCreateVisualMoment, iterationResult.ShouldCreateVisualMoment);
            Assert.Equal(firstResult.ActionHint, iterationResult.ActionHint);
            Assert.Equal(firstResult.DecisionFingerprint, iterationResult.DecisionFingerprint);
        }
    }
}
