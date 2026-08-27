using Application.DTOs;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Tests.SceneComposition;

public sealed class VisualContextResolverTests
{
    [Fact]
    public async Task ResolveVisualContext_PrioritizesCanonicalReferenceOverVisualMemory()
    {
        var resolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var charId = Guid.NewGuid();

        var canonicalRef = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canonical_prime.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true,
            priority: 100
        );

        var olderMemory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 1,
            artifactId: Guid.NewGuid(),
            context: "Old Scene"
        );

        var scene = new SceneSpecification(charId, "Throne Room", "Speaking to council");
        var context = new SceneCompositionContext(
            CharacterId: charId,
            CanonicalVisualReference: canonicalRef,
            RelevantVisualMemories: new[] { olderMemory }
        );

        var result = await resolver.ResolveVisualContextAsync(charId, scene, context);

        Assert.NotNull(result.CanonicalIdentityReference);
        Assert.Equal(canonicalRef.ReferenceUrl, result.CanonicalIdentityReference.ReferenceUrl);
        Assert.True(result.CanonicalIdentityReference.IsCanonical);
    }

    [Fact]
    public async Task ResolveVisualContext_BoundsMemorySelectionToMaxThree_EvenWithManyMemories()
    {
        var resolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var charId = Guid.NewGuid();

        var memories = new List<CharacterVisualMemory>();
        for (int i = 1; i <= 50; i++)
        {
            memories.Add(new CharacterVisualMemory(
                characterId: charId,
                visualProfileVersion: 1,
                sceneRevision: i,
                artifactId: Guid.NewGuid(),
                context: i % 2 == 0 ? "Library" : "Courtyard",
                identityScore: 0.8f + (i * 0.002f)
            ));
        }

        var scene = new SceneSpecification(charId, "Library", "Reading");
        var context = new SceneCompositionContext(
            CharacterId: charId,
            RelevantVisualMemories: memories
        );

        var result = await resolver.ResolveVisualContextAsync(charId, scene, context);

        // Invariant: Bounded to maximum 3 relevant visual memories
        Assert.True(result.RelevantOlderMemories.Count <= 3);
        Assert.Equal(3, result.RelevantOlderMemories.Count);

        // Verified that library memories are prioritized
        Assert.All(result.RelevantOlderMemories, m => Assert.Contains("Library", m.Context));
    }

    [Fact]
    public async Task ResolveVisualContext_CanonicalIdentityCannotBeOverriddenBySceneContext()
    {
        var resolver = new VisualContextResolver(NullLogger<VisualContextResolver>.Instance);
        var charId = Guid.NewGuid();

        var canonicalRef = new CharacterVisualReference(
            characterId: charId,
            referenceUrl: "https://cdn.project00.ai/canonical_immutable.png",
            type: VisualReferenceType.Canonical,
            status: VisualReferenceStatus.Active,
            isCanonical: true
        );

        var conflictingMemory = new CharacterVisualMemory(
            characterId: charId,
            visualProfileVersion: 1,
            sceneRevision: 10,
            artifactId: Guid.NewGuid(),
            context: "Completely Different Environment",
            identityScore: 0.99f
        );

        var scene = new SceneSpecification(charId, "Volcanic Crater", "Fighting dragon");
        var context = new SceneCompositionContext(
            CharacterId: charId,
            CanonicalVisualReference: canonicalRef,
            RelevantVisualMemories: new[] { conflictingMemory }
        );

        var result = await resolver.ResolveVisualContextAsync(charId, scene, context);

        Assert.Equal(canonicalRef.ReferenceUrl, result.CanonicalIdentityReference?.ReferenceUrl);
    }
}
