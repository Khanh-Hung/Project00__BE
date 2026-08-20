using Domain.ValueObjects;

namespace Application.Interfaces;

public sealed record VoiceGenerationRequest(
    string CleanedText,
    string VoiceId,
    string? Language = "vi-VN",
    VoiceExpression? Expression = null
);

public sealed record VoiceGenerationResult(
    string AudioUrl,
    string AudioFormat = "audio/mpeg",
    int? DurationSeconds = null
);

public interface IVoicePromptCompiler
{
    string ExtractCleanDialogueText(string rawReply);
    VoiceGenerationRequest CompileVoiceRequest(VoiceContext context);
}
