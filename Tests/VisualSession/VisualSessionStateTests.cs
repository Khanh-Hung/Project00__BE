using Domain.Entities;
using Xunit;

namespace Tests.VisualSession;

public sealed class VisualSessionStateTests
{
    [Fact]
    public void Constructor_InitializesDefaultRevisionAndTimestamps()
    {
        var sessionId = Guid.NewGuid();
        var state = new VisualSessionState(sessionId);

        Assert.Equal(sessionId, state.SessionId);
        Assert.Equal(sessionId, state.Id);
        Assert.Null(state.CurrentImageId);
        Assert.Null(state.CurrentGenerationJobId);
        Assert.Equal(1, state.VisualRevision);
        Assert.NotNull(state.UpdatedAt);
    }

    [Fact]
    public void PromoteArtifact_AdvancesRevision_AndReturnsNewRevision()
    {
        var sessionId = Guid.NewGuid();
        var state = new VisualSessionState(sessionId, visualRevision: 1);

        var artifactId1 = Guid.NewGuid();
        var jobId1 = Guid.NewGuid();
        var promotionTime1 = DateTime.UtcNow;

        var rev1 = state.PromoteArtifact(artifactId1, jobId1, promotionTime1);

        Assert.Equal(2, rev1);
        Assert.Equal(2, state.VisualRevision);
        Assert.Equal(artifactId1, state.CurrentImageId);
        Assert.Equal(jobId1, state.CurrentGenerationJobId);

        var artifactId2 = Guid.NewGuid();
        var jobId2 = Guid.NewGuid();
        var promotionTime2 = DateTime.UtcNow.AddMinutes(1);

        var rev2 = state.PromoteArtifact(artifactId2, jobId2, promotionTime2);

        Assert.Equal(3, rev2);
        Assert.Equal(3, state.VisualRevision);
        Assert.Equal(artifactId2, state.CurrentImageId);
        Assert.Equal(jobId2, state.CurrentGenerationJobId);
    }

    [Fact]
    public void PromoteArtifact_WithEmptyGuid_ThrowsArgumentException()
    {
        var state = new VisualSessionState(Guid.NewGuid());
        var now = DateTime.UtcNow;

        Assert.Throws<ArgumentException>(() => state.PromoteArtifact(Guid.Empty, Guid.NewGuid(), now));
        Assert.Throws<ArgumentException>(() => state.PromoteArtifact(Guid.NewGuid(), Guid.Empty, now));
    }

    [Fact]
    public void ClearCurrent_NullifiesCurrentReferences_AndUpdatesTimestamp()
    {
        var sessionId = Guid.NewGuid();
        var artifactId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        var state = new VisualSessionState(sessionId, artifactId, jobId, visualRevision: 4);
        state.ClearCurrent(now);

        Assert.Null(state.CurrentImageId);
        Assert.Null(state.CurrentGenerationJobId);
        Assert.Equal(4, state.VisualRevision); // Revision count does not decrement
        Assert.Equal(now, state.UpdatedAt);
    }
}
