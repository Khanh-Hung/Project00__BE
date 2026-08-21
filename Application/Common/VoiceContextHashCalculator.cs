using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Common;

/// <summary>
/// Deterministic canonical hash calculator for voice requests.
/// Serves as the primary idempotency identity (ContextHash) for AudioArtifacts.
/// </summary>
public static class VoiceContextHashCalculator
{
    private record CanonicalVoiceJson(
        string VoiceId,
        string Language,
        string Text,
        string Mood,
        int Intensity,
        double Rate,
        double Pitch,
        string Volume,
        string Emotion
    );

    public static string ComputeHash(VoiceProviderRequest request, CharacterMood? mood = null, int? moodIntensity = null)
    {
        var expr = request.Expression ?? new VoiceExpression();

        var canonicalObj = new CanonicalVoiceJson(
            VoiceId: request.VoiceId ?? string.Empty,
            Language: request.Language ?? "vi-VN",
            Text: request.CleanedText ?? string.Empty,
            Mood: mood?.ToString() ?? "Neutral",
            Intensity: moodIntensity ?? 50,
            Rate: Math.Round(expr.Rate, 2),
            Pitch: Math.Round(expr.Pitch, 2),
            Volume: expr.Volume ?? "Standard",
            Emotion: expr.EmotionTag ?? "Neutral"
        );

        var json = JsonSerializer.Serialize(canonicalObj, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
