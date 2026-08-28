using Domain.Entities;
using Domain.Enums;
using Xunit;

namespace Tests.CharacterReaction;

public sealed class CharacterWorldEventTests
{
    [Fact]
    public void Create_ValidParameters_InstantiatesCharacterWorldEvent()
    {
        var charId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var evt = CharacterWorldEvent.Create(
            characterId: charId,
            eventType: CharacterWorldEventType.UserMessage,
            sourceType: "Chat",
            sourceId: "msg-123",
            occurredAt: now,
            payloadJson: "{\"text\":\"Hello Valerius!\"}",
            correlationId: "corr-456"
        );

        Assert.NotEqual(Guid.Empty, evt.Id);
        Assert.Equal(charId, evt.CharacterId);
        Assert.Equal(CharacterWorldEventType.UserMessage, evt.EventType);
        Assert.Equal("Chat", evt.SourceType);
        Assert.Equal("msg-123", evt.SourceId);
        Assert.Equal(now, evt.OccurredAt);
        Assert.Equal("{\"text\":\"Hello Valerius!\"}", evt.PayloadJson);
        Assert.Equal("corr-456", evt.CorrelationId);
        Assert.Equal(1, evt.Version);
    }

    [Fact]
    public void Create_EmptyCharacterId_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            CharacterWorldEvent.Create(
                characterId: Guid.Empty,
                eventType: CharacterWorldEventType.SystemEvent,
                sourceType: "System"
            )
        );
    }

    [Fact]
    public void Create_EmptySourceType_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            CharacterWorldEvent.Create(
                characterId: Guid.NewGuid(),
                eventType: CharacterWorldEventType.SystemEvent,
                sourceType: "   "
            )
        );
    }
}
