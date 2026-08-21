using Application.Common;
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
    public void TryUnlockEvent_EventKeyExceeding100Chars_RejectsAndReturnsFalse()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);
        var longKey = new string('A', 150);

        var unlocked = rel.TryUnlockEvent(longKey, "Valid context");

        // Per domain invariant, invalid event key length is strictly rejected (not silently truncated)
        Assert.False(unlocked);
        Assert.Empty(rel.Events);
    }

    [Fact]
    public void TryUnlockEvent_ContextExceeding500Chars_RejectsAndReturnsFalse()
    {
        var rel = CharacterRelationship.Create(_testCharacterId, _testUserId);
        var longContext = new string('B', 550);

        var unlocked = rel.TryUnlockEvent("ValidKey", longContext);

        // Per domain invariant, invalid context length is strictly rejected
        Assert.False(unlocked);
        Assert.Empty(rel.Events);
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

    [Theory]
    [InlineData(-100, -4, "Kẻ Thù Truyền Kiếp")]
    [InlineData(-76, -4, "Kẻ Thù Truyền Kiếp")]
    [InlineData(-75, -3, "Căm Ghét & Khinh Bỉ")]
    [InlineData(-51, -3, "Căm Ghét & Khinh Bỉ")]
    [InlineData(-50, -2, "Ác Cảm & Đề Phòng")]
    [InlineData(-26, -2, "Ác Cảm & Đề Phòng")]
    [InlineData(-25, 1, "Người Lạ")]
    [InlineData(0, 1, "Người Lạ")]
    [InlineData(1, 2, "Người Quen & Cởi Mở")]
    [InlineData(25, 2, "Người Quen & Cởi Mở")]
    [InlineData(26, 3, "Bạn Thân Thiết")]
    [InlineData(50, 3, "Bạn Thân Thiết")]
    [InlineData(51, 4, "Tri Kỷ & Rung Động")]
    [InlineData(75, 4, "Tri Kỷ & Rung Động")]
    [InlineData(76, 5, "Gắn Kết Linh Hồn")]
    [InlineData(100, 5, "Gắn Kết Linh Hồn")]
    public void RelationshipStageResolver_BoundaryScores_CalculateExactLevelAndName(
        int score, int expectedLevel, string expectedName)
    {
        var (level, name, _) = RelationshipStageResolver.Resolve(score, customMilestonesJson: null);

        Assert.Equal(expectedLevel, level);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void RelationshipStageResolver_CalculatesAndResolvesCustomMilestonesCorrectly()
    {
        var customMilestonesJson = @"[
            { ""Name"": ""Oan Gia"", ""Description"": ""Luôn cãi vã"", ""MinScore"": -100, ""MaxScore"": -1 },
            { ""Name"": ""Bạn Đồng Hành"", ""Description"": ""Cùng nhau phiêu lưu"", ""MinScore"": 0, ""MaxScore"": 50 },
            { ""Name"": ""Bạn Đời Tri Kỷ"", ""Description"": ""Không thể tách rời"", ""MinScore"": 51, ""MaxScore"": 100 }
        ]";

        var (lvl1, name1, guide1) = RelationshipStageResolver.Resolve(30, customMilestonesJson);
        Assert.Equal("Bạn Đồng Hành", name1);
        Assert.Equal("Cùng nhau phiêu lưu", guide1);

        var (lvl2, name2, guide2) = RelationshipStageResolver.Resolve(75, customMilestonesJson);
        Assert.Equal("Bạn Đời Tri Kỷ", name2);

        // Fallback to default if no custom milestones
        var (lvl3, name3, _) = RelationshipStageResolver.Resolve(0, null);
        Assert.Equal("Người Lạ", name3);
    }

    [Fact]
    public void ChatSession_DoesNotContainRelationshipStateProperties_EnsuresSingleSourceOfTruth()
    {
        var sessionType = typeof(ChatSession);

        Assert.Null(sessionType.GetProperty("AffectionScore"));
        Assert.Null(sessionType.GetProperty("RelationshipLevel"));
        Assert.Null(sessionType.GetProperty("CurrentMood"));
        Assert.Null(sessionType.GetMethod("UpdateAffection"));
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
}
