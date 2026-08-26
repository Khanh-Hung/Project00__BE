using System.Text.Json.Nodes;
using Application.Common;
using Application.Enums;
using Application.Services;
using Domain.Entities;
using Domain.ValueObjects;
using Xunit;

namespace Tests.IdentityQualityGuard;

public sealed class IdentityMitigationProfileResolverTests
{
    [Fact]
    public void IdentityMitigationProfileResolver_Attempt1_Pass_KeepsOriginalProfileAndBaseSeed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        long baseSeed = 555555L;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed)
        );

        var (profile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.Pass, attemptNumber: 1, baseSeed: baseSeed);

        Assert.Equal(snapshot.GenerationProfile, profile);
        Assert.Equal(baseSeed, derivedSeed);
    }

    [Fact]
    public void IdentityMitigationProfileResolver_Attempt2_RetryAttenuated_AppliesAttenuationAndDerivedSeed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        long baseSeed = 123456789L;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed)
        );

        var (mitigatedProfile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryAttenuated, attemptNumber: 2, baseSeed: baseSeed);

        var root = JsonNode.Parse(mitigatedProfile.ParametersJson!)!.AsObject();
        var ip = root["ipAdapter"]!.AsObject();
        var cont = root["sceneContinuity"]!.AsObject();

        Assert.Equal(0.65f, (float)ip["weight"]!, 2);
        Assert.Equal(0.85f, (float)ip["endAt"]!, 2);
        Assert.Equal(0.06f, (float)cont["weight"]!, 2);
        Assert.Equal(0.15f, (float)cont["endAt"]!, 2);
        Assert.Equal("style transfer", (string)cont["weightType"]!);

        long expectedSeed = DeterministicSeedDerivation.Derive(baseSeed, 2);
        Assert.Equal(expectedSeed, derivedSeed);
    }

    [Fact]
    public void IdentityMitigationProfileResolver_Attempt3_RetryIsolated_AppliesIsolationAndDerivedSeed()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        long baseSeed = 123456789L;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed)
        );

        var (mitigatedProfile, derivedSeed) = IdentityMitigationProfileResolver.ResolveMitigation(
            snapshot, QualityMitigationAction.RetryIsolated, attemptNumber: 3, baseSeed: baseSeed);

        var root = JsonNode.Parse(mitigatedProfile.ParametersJson!)!.AsObject();
        var ip = root["ipAdapter"]!.AsObject();
        var cont = root["sceneContinuity"]!.AsObject();

        Assert.Equal(0.70f, (float)ip["weight"]!, 2);
        Assert.Equal(0.85f, (float)ip["endAt"]!, 2);
        Assert.Equal(0.00f, (float)cont["weight"]!, 2);
        Assert.Equal(0.00f, (float)cont["endAt"]!, 2);

        long expectedSeed = DeterministicSeedDerivation.Derive(baseSeed, 3);
        Assert.Equal(expectedSeed, derivedSeed);
    }

    [Fact]
    public void IdentityMitigationProfileResolver_AttemptsAreDeterministicAndDistinct()
    {
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        long baseSeed = 100000L;

        var snapshot = new VisualSnapshot(
            TurnId: turnId,
            SessionId: sessionId,
            CharacterId: Guid.NewGuid(),
            SceneRevision: 1,
            VisualIdentity: null,
            SceneState: new SessionSceneState("courtyard", "standing"),
            TransientState: null,
            GenerationProfile: GenerationProfile.CreateDefault(seed: baseSeed)
        );

        var (prof1, seed1) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.Pass, 1, baseSeed);
        var (prof2, seed2) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryAttenuated, 2, baseSeed);
        var (prof3, seed3) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryIsolated, 3, baseSeed);

        Assert.Equal(baseSeed, seed1);
        Assert.NotEqual(seed1, seed2);
        Assert.NotEqual(seed2, seed3);
        Assert.NotEqual(seed1, seed3);

        // Re-executing produces identical profiles and seeds
        var (_, seed2Re) = IdentityMitigationProfileResolver.ResolveMitigation(snapshot, QualityMitigationAction.RetryAttenuated, 2, baseSeed);
        Assert.Equal(seed2, seed2Re);
    }

    [Fact]
    public void WithConditioningOverride_Preserves_All_Unrelated_Parameters()
    {
        var customJson = """
        {
          "steps": 25,
          "cfg": 7.5,
          "sampler": "euler_ancestral",
          "scheduler": "normal",
          "lora": { "name": "valerius_v1", "strength": 0.8 },
          "customRoot": "must-preserve",
          "ipAdapter": {
            "weight": 0.60,
            "startAt": 0.0,
            "endAt": 0.80,
            "custom": "must-preserve"
          },
          "sceneContinuity": {
            "weight": 0.12,
            "endAt": 0.25,
            "weightType": "linear",
            "custom": "must-preserve"
          }
        }
        """;

        var profile = GenerationProfile.CreateDefault(seed: 1000L, parametersJson: customJson);

        var overridden = profile.WithConditioningOverride(
            slot1Weight: 0.65f,
            slot1EndAt: 0.85f,
            slot2Weight: 0.06f,
            slot2EndAt: 0.15f,
            weightType: "style transfer",
            newSeed: 2000L
        );

        Assert.Equal(2000L, overridden.Seed);

        var root = JsonNode.Parse(overridden.ParametersJson!)!.AsObject();
        Assert.Equal(25, (int)root["steps"]!);
        Assert.Equal(7.5, (double)root["cfg"]!, 2);
        Assert.Equal("euler_ancestral", (string)root["sampler"]!);
        Assert.Equal("normal", (string)root["scheduler"]!);
        Assert.Equal("valerius_v1", (string)root["lora"]!["name"]!);
        Assert.Equal("must-preserve", (string)root["customRoot"]!);

        var ip = root["ipAdapter"]!.AsObject();
        Assert.Equal(0.65f, (float)ip["weight"]!, 2);
        Assert.Equal(0.85f, (float)ip["endAt"]!, 2);
        Assert.Equal("must-preserve", (string)ip["custom"]!);
        Assert.Equal(0.0, (double)ip["startAt"]!, 2);

        var cont = root["sceneContinuity"]!.AsObject();
        Assert.Equal(0.06f, (float)cont["weight"]!, 2);
        Assert.Equal(0.15f, (float)cont["endAt"]!, 2);
        Assert.Equal("style transfer", (string)cont["weightType"]!);
        Assert.Equal("must-preserve", (string)cont["custom"]!);
    }

    [Fact]
    public void WithConditioningOverride_MalformedJson_FailsFast()
    {
        var malformedJson = "{ invalid_json: 123 ";
        var profile = GenerationProfile.CreateDefault(seed: 1000L, parametersJson: malformedJson);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            profile.WithConditioningOverride(0.65f, 0.85f, 0.06f, 0.15f, "style transfer", 2000L));

        Assert.Contains("ParametersJson is malformed or invalid JSON", ex.Message);
    }
}
