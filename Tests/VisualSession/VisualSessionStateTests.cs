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
    public void PromoteArtifact_AdvancesRevision_AndSetsNewCurrentArtifact()
    {
        var sessionId = Guid.NewGuid();
        var state = new VisualSessionState(sessionId, visualRevision: 2);

        var artifactId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var promotionTime = DateTime.UtcNow;

        state.PromoteArtifact(artifactId, jobId, promotionTime);

        Assert.Equal(artifactId, state.CurrentImageId);
        Assert.Equal(jobId, state.CurrentGenerationJobId);
        Assert.Equal(3, state.VisualRevision);
        Assert.Equal(promotionTime, state.UpdatedAt);
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
