using Application.Common.Exceptions;
using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.VisualContinuity;

public sealed class VisualContinuityFailureTests
{
    [Fact]
    public async Task ContinuityResolver_NullRequest_ThrowsArgumentNullException()
    {
        var resolver = new VisualContinuityResolver(NullLogger<VisualContinuityResolver>.Instance);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            resolver.ResolveAsync(null!));
    }

    [Fact]
    public async Task ContinuityResolver_NullIntent_ThrowsArgumentException()
    {
        var resolver = new VisualContinuityResolver(NullLogger<VisualContinuityResolver>.Instance);
        var context = new SceneCompositionContext(Guid.NewGuid());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(new VisualContinuityRequest(null!, context)));
    }
}
