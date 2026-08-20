using Application.Common;

namespace Application.Interfaces;

public interface IPromptCompiler
{
    string CompileSystemPrompt(RoleplayContext context);
    List<object> CompileConversationContents(RoleplayContext context);
}
