using System.Text.Json;
using Application.Common;
using Application.Enums;
using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Services;

/// <summary>
/// Translates a QualityMitigationAction into an adjusted GenerationProfile and derived seed.
/// Completely decoupled from model execution and evaluation engines.
/// </summary>
public static class IdentityMitigationProfileResolver
{
    public static (GenerationProfile Profile, long DerivedSeed) ResolveMitigation(
        VisualSnapshot snapshot,
        QualityMitigationAction action,
        int attemptNumber,
        long baseSeed)
    {
        if (action == QualityMitigationAction.Pass)
        {
            return (snapshot.GenerationProfile, baseSeed);
        }

        long derivedSeed = DeterministicSeedDerivation.Derive(baseSeed, attemptNumber);

        float slot1Weight = 0.60f;
        float slot1EndAt = 0.85f;
        float slot2Weight = 0.12f;
        float slot2EndAt = 0.25f;
        string weightType = "style transfer";

        // Read existing values from snapshot profile if available
        if (!string.IsNullOrWhiteSpace(snapshot.GenerationProfile.ParametersJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(snapshot.GenerationProfile.ParametersJson);
                if (doc.RootElement.TryGetProperty("ipAdapter", out var ipProp))
                {
                    if (ipProp.TryGetProperty("weight", out var w)) slot1Weight = (float)w.GetDouble();
                    if (ipProp.TryGetProperty("endAt", out var e)) slot1EndAt = (float)e.GetDouble();
                }
            }
            catch
            {
                // Fallback to defaults
            }
        }

        if (action == QualityMitigationAction.RetryAttenuated)
        {
            // Boost canonical reference authority slightly and halve continuity authority
            slot1Weight = Math.Clamp(slot1Weight + 0.05f, 0.50f, 0.85f);
            slot2Weight = 0.06f;
            slot2EndAt = 0.15f;
            weightType = "style transfer";
        }
        else if (action == QualityMitigationAction.RetryIsolated)
        {
            // Isolate canonical reference authority completely by zeroing Slot 2
            slot1Weight = Math.Clamp(slot1Weight + 0.10f, 0.50f, 0.90f);
            slot2Weight = 0.0f;
            slot2EndAt = 0.0f;
            weightType = "style transfer";
        }

        var parametersJson = JsonSerializer.Serialize(new
        {
            ipAdapter = new
            {
                weight = slot1Weight,
                endAt = slot1EndAt
            },
            sceneContinuity = new
            {
                weight = slot2Weight,
                endAt = slot2EndAt,
                weightType = weightType
            }
        });

        var adjustedProfile = GenerationProfile.CreateDefault(
            workflow: snapshot.GenerationProfile.Workflow,
            workflowVersion: snapshot.GenerationProfile.WorkflowVersion,
            parametersJson: parametersJson
        );

        return (adjustedProfile, derivedSeed);
    }
}
