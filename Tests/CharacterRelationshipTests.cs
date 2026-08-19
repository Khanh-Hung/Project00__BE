using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Project.Tests;

public class CharacterRelationshipTests
{
    private readonly Guid _testCharacterId = Guid.NewGuid();
    private readonly Guid _testUserId = Guid.NewGuid();

    [Fact]
    public void Create_ValidParameters_InstantiatesCorrectly()
    {
        var rel = CharacterRelationship.Create(
            _testCharacterId,
            _testUserId,
            initialAffection: 10,
            initialMood: CharacterMood.Happy,
            initialMoodIntensity: 60);

        Assert.Equal(_testCharacterId, rel.CharacterId);
        Assert.Equal(_testUserId, rel.UserId);
        Assert.Equal(10, rel.AffectionScore);
        Assert.Equal(CharacterMood.Happy, rel.CurrentMood);
        Assert.Equal(60, rel.MoodIntensity);
        Assert.Empty(rel.Events);
    }

    [Fact]
    public void Create_EmptyGuids_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => CharacterRelationship.Create(Guid.Empty, _testUserId));
        Assert.Throws<ArgumentException>(() => CharacterRelationship.Create(_testCharacterId, Guid.Empty));
    }

    [Theory]
    [InlineData(10, 5, 15, 5)]
    [InlineData(95, 10, 100, 5)] // Upper clamp to 100
    [InlineData(-95, -10, -100, -5)] // Lower clamp to -100
    [InlineData(0, -30, -30, -30)]
    public void ApplyAffectionDelta_ValidAndClampedDeltas_UpdatesScoreAccurately(
        int initial, int delta, int expectedNewScore, int expectedActualDelta)
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId, initialAffection: initial);

        var (oldScore, newScore, actualDelta) = rel.ApplyAffectionDelta(delta);

        Assert.Equal(initial, oldScore);
        Assert.Equal(expectedNewScore, newScore);
        Assert.Equal(expectedActualDelta, actualDelta);
        Assert.Equal(expectedNewScore, rel.AffectionScore);
    }

    [Fact]
    public void UpdateMood_ValidMoodAndIntensity_SetsProperties()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);

        rel.UpdateMood(CharacterMood.Embarrassed, 85);

        Assert.Equal(CharacterMood.Embarrassed, rel.CurrentMood);
        Assert.Equal(85, rel.MoodIntensity);
    }

    [Theory]
    [InlineData(150, 100)] // Clamped to 100
    [InlineData(-20, 0)]   // Clamped to 0
    public void UpdateMood_OutofRangeIntensity_ClampsCorrectly(int inputIntensity, int expectedIntensity)
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);

        rel.UpdateMood(CharacterMood.Curious, inputIntensity);

        Assert.Equal(expectedIntensity, rel.MoodIntensity);
    }

    [Fact]
    public void TryUnlockEvent_NewEvent_AddsEventSuccessfully()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);

        var unlocked = rel.TryUnlockEvent("FirstPromise", "Luna promised to cook together");

        Assert.True(unlocked);
        Assert.Single(rel.Events);
        var ev = rel.Events.First();
        Assert.Equal("FirstPromise", ev.EventKey);
        Assert.Equal("Luna promised to cook together", ev.Context);
    }

    [Fact]
    public void TryUnlockEvent_EventKeyExceeding100Chars_TruncatesAndAddsSuccessfully()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);
        var longKey = new string('A', 150);

        var unlocked = rel.TryUnlockEvent(longKey, "Valid context");

        Assert.True(unlocked);
        Assert.Equal(100, rel.Events.First().EventKey.Length);
    }

    [Fact]
    public void TryUnlockEvent_DuplicateEventKey_ReturnsFalseAndDoesNotAdd()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);

        rel.TryUnlockEvent("FirstPromise", "Luna promised to cook together");
        var duplicateAttempt = rel.TryUnlockEvent("firstpromise", "Another promise text"); // Case-insensitive deduplication

        Assert.False(duplicateAttempt);
        Assert.Single(rel.Events);
    }

    [Fact]
    public void TryUnlockEvent_EmptyKeyOrContext_ReturnsFalse()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);

        Assert.False(rel.TryUnlockEvent("", "Some context"));
        Assert.False(rel.TryUnlockEvent("Key", ""));
        Assert.Empty(rel.Events);
    }

    [Fact]
    public void SoftenMoodIfInactive_InactiveExceedsThreshold_ResetsToDefaultMood()
    {
        var pastTime = DateTime.UtcNow.AddHours(-25);
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId, initialMood: CharacterMood.Angry, initialMoodIntensity: 90, initialTimestamp: pastTime);

        // Soften with threshold of 24h
        rel.SoftenMoodIfInactive(DateTime.UtcNow, TimeSpan.FromHours(24), CharacterMood.Neutral);

        Assert.Equal(CharacterMood.Neutral, rel.CurrentMood);
        Assert.Equal(20, rel.MoodIntensity);
    }

    [Fact]
    public void Events_CannotBeCastAndModifiedDirectly_MaintainsEncapsulation()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);
        rel.TryUnlockEvent("Event1", "Context 1");

        Assert.IsAssignableFrom<IReadOnlyCollection<RelationshipEvent>>(rel.Events);
        Assert.Throws<NotSupportedException>(() =>
        {
            if (rel.Events is IList<RelationshipEvent> list)
            {
                list.Add(new RelationshipEvent("Hack", "Hack", DateTime.UtcNow));
            }
            else
            {
                throw new NotSupportedException();
            }
        });
    }

    [Fact]
    public async Task MultipleChatSessions_SameUserAndCharacter_ShareSingleCharacterRelationship()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ProjectDbContext(options);
        var uow = new UnitOfWork(dbContext);

        var userId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        // 1. Session 1 loads & modifies relationship
        var rel1 = await uow.Relationships.GetOrCreateAsync(userId, characterId, initialAffection: 10);
        rel1.ApplyAffectionDelta(5); // 10 + 5 = 15
        await uow.SaveChangesAsync();

        // 2. Session 2 queries relationship for the same user & character
        var rel2 = await uow.Relationships.GetOrCreateAsync(userId, characterId);

        // Assert they share the exact same relationship state
        Assert.Equal(rel1.Id, rel2.Id);
        Assert.Equal(15, rel2.AffectionScore);

        // 3. Session 2 unlocks an event
        rel2.TryUnlockEvent("FirstMeeting", "Met in the old town plaza");
        await uow.SaveChangesAsync();

        // 4. Session 1 re-queries and sees the unlocked event
        var relAfter = await uow.Relationships.GetByPairAsync(userId, characterId);
        Assert.NotNull(relAfter);
        Assert.Single(relAfter.Events);
        Assert.Equal("FirstMeeting", relAfter.Events.First().EventKey);
    }

    [Fact]
    public async Task GetCharacterRelationshipHandler_Returns_Correct_Dto()
    {
        var options = new DbContextOptionsBuilder<ProjectDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ProjectDbContext(options);
        var uow = new UnitOfWork(dbContext);

        var userId = Guid.NewGuid();
        var characterId = Guid.NewGuid();

        var character = new Character("Luna", "Mage", "https://example.com/avatar.jpg", "Friendly", "Hello", "Fantasy", defaultAffectionScore: 25)
        {
            Id = characterId
        };
        dbContext.Characters.Add(character);
        await dbContext.SaveChangesAsync();

        var handler = new Application.Features.Chat.Queries.GetCharacterRelationship.GetCharacterRelationshipHandler(uow);
        var query = new Application.Features.Chat.Queries.GetCharacterRelationship.GetCharacterRelationshipQuery(characterId, userId);

        var result = await handler.Handle(query, CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal(characterId, result.Value.CharacterId);
        Assert.Equal(userId, result.Value.UserId);
        Assert.Equal(25, result.Value.AffectionScore);
        Assert.Equal(CharacterMood.Neutral, result.Value.CurrentMood);
    }
}
