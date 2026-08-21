using Domain.ValueObjects;

namespace Application.Interfaces;

public interface IVoicePromptCompiler
{
    string ExtractCleanDialogueText(string rawReply);
    VoiceProviderRequest CompileVoiceRequest(VoiceContext context);
}
