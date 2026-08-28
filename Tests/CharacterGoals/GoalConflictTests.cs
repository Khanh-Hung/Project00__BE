using Domain.Entities;
using Domain.Enums;
using Domain.Policies;
using Xunit;

namespace Tests.CharacterGoals;

public sealed class GoalConflictTests
{
    [Fact]
    public void ConflictingGoals_ResolvesToSingleDeterministicWinner()
    {
        var charId = Guid.NewGuid();

        var goalA = new CharacterGoal(charId, "Painting Masterpiece", CharacterGoalType.Creative, 50, CharacterGoalPriority.Normal);
        var goalB = new CharacterGoal(charId, "Diplomatic Relationship Building", CharacterGoalType.Relationship, 50, CharacterGoalPriority.High);

        var winnerForSocializing = GoalConflictPolicy.ResolveGoalConflict(new[] { goalA, goalB }, CharacterActivityType.Socializing);
        Assert.NotNull(winnerForSocializing);
        Assert.Equal(goalB.Id, winnerForSocializing.Id);

        var winnerForArt = GoalConflictPolicy.ResolveGoalConflict(new[] { goalA, goalB }, CharacterActivityType.Custom);
        Assert.NotNull(winnerForArt);
        Assert.Equal(goalA.Id, winnerForArt.Id);
    }
}
