using System.Text.RegularExpressions;
using Application.Interfaces;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Services;

public sealed class VoicePromptCompiler : IVoicePromptCompiler
{
    private static readonly Regex InnerThoughtsRegex = new(@"💭\s*(\*.*?\*|\(.*?\)|[^\n*]+)", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ActionAsterisksRegex = new(@"\*.*?\*", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex WhitespaceCleanupRegex = new(@"\s+", RegexOptions.Compiled);

    public string ExtractCleanDialogueText(string rawReply)
    {
        if (string.IsNullOrWhiteSpace(rawReply)) return string.Empty;

        // 1. Strip inner thoughts 💭 *(...)*
        var noThoughts = InnerThoughtsRegex.Replace(rawReply, " ");

        // 2. Strip actions *[tag]...* and *...*
        var noActions = ActionAsterisksRegex.Replace(noThoughts, " ");

        // 3. Clean and normalize whitespace
        var cleaned = WhitespaceCleanupRegex.Replace(noActions, " ").Trim();

        // 4. Remove leading/trailing quotation marks if isolated
        if (cleaned.StartsWith('"') && cleaned.EndsWith('"') && cleaned.Length > 2)
        {
            cleaned = cleaned.Substring(1, cleaned.Length - 2).Trim();
        }

        return cleaned;
    }

    public VoiceProviderRequest CompileVoiceRequest(VoiceContext context)
    {
        var voice = context.Voice ?? new CharacterVoiceProfile();
        var cleanedText = ExtractCleanDialogueText(context.RawText);

        // If dialogue text is completely empty (e.g. character only did an action), fallback to a quiet whisper
        if (string.IsNullOrWhiteSpace(cleanedText))
        {
            cleanedText = "...";
        }

        var expression = MapMoodToVoiceExpression(context.Mood, context.MoodIntensity, context.AffectionScore);

        return new VoiceProviderRequest(
            CleanedText: cleanedText,
            VoiceId: voice.VoiceId,
            Language: voice.Language ?? "vi-VN",
            Expression: expression
        );
    }

    private static VoiceExpression MapMoodToVoiceExpression(CharacterMood mood, int intensity, int affectionScore)
    {
        var isHighIntensity = intensity >= 70;
        var isIntimate = affectionScore >= 70;

        return mood switch
        {
            CharacterMood.Happy => new VoiceExpression(
                Rate: isHighIntensity ? 1.15 : 1.05,
                Pitch: isHighIntensity ? 2.5 : 1.0,
                Volume: "Standard",
                EmotionTag: "Happy"
            ),
            CharacterMood.Excited => new VoiceExpression(
                Rate: isHighIntensity ? 1.25 : 1.15,
                Pitch: isHighIntensity ? 4.0 : 2.5,
                Volume: "Loud",
                EmotionTag: "Excited"
            ),
            CharacterMood.Sad => new VoiceExpression(
                Rate: isHighIntensity ? 0.80 : 0.88,
                Pitch: isHighIntensity ? -2.0 : -1.0,
                Volume: "Soft",
                EmotionTag: "Sad"
            ),
            CharacterMood.Angry => new VoiceExpression(
                Rate: isHighIntensity ? 1.15 : 1.05,
                Pitch: isHighIntensity ? 1.5 : 0.5,
                Volume: "Loud",
                EmotionTag: "Angry"
            ),
            CharacterMood.Anxious => new VoiceExpression(
                Rate: isHighIntensity ? 1.15 : 1.05,
                Pitch: isHighIntensity ? 2.0 : 1.0,
                Volume: "Soft",
                EmotionTag: "Anxious"
            ),
            CharacterMood.Embarrassed => new VoiceExpression(
                Rate: isHighIntensity ? 0.85 : 0.92,
                Pitch: isHighIntensity ? 2.5 : 1.5,
                Volume: "Soft",
                EmotionTag: "Embarrassed"
            ),
            CharacterMood.Curious => new VoiceExpression(
                Rate: 1.02,
                Pitch: 1.5,
                Volume: "Standard",
                EmotionTag: "Curious"
            ),
            CharacterMood.Affectionate => new VoiceExpression(
                Rate: isIntimate ? 0.85 : 0.90,
                Pitch: -0.5,
                Volume: isIntimate ? "Whisper" : "Soft",
                EmotionTag: "Affectionate"
            ),
            CharacterMood.Playful => new VoiceExpression(
                Rate: 1.12,
                Pitch: 2.0,
                Volume: "Standard",
                EmotionTag: "Playful"
            ),
            _ => new VoiceExpression(
                Rate: 1.0,
                Pitch: 0.0,
                Volume: "Standard",
                EmotionTag: "Neutral"
            )
        };
    }
}
