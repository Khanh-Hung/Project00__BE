using Domain.Entities;
using Domain.Enums;

namespace Infrastructure.LLM.Prompts;

public static class SuggestionPrompts
{
    public static string BuildSuggestionSystemPrompt(Character character, IReadOnlyCollection<ChatMessage> history)
    {
        var recentHistory = history.TakeLast(6).ToList();
        var historySummary = recentHistory.Count > 0
            ? string.Join("\n", recentHistory.Select(m => $"{(m.Role == MessageRole.Assistant ? character.Name : "User")}: {m.Content}"))
            : $"{character.Name} (Lời chào mở đầu): {character.Greeting}";

        return $"""
            You are a master interactive roleplay storyteller.
            The user is roleplaying with character: {character.Name} ({character.Category} - {character.Title}).
            
            Personality Lore:
            {character.PersonalityPrompt}
            
            Opening / Conversation Context:
            {historySummary}
            
            Task:
            Based on {character.Name}'s greeting/dialogue and category ({character.Category}), generate exactly 3 diverse, creative, and engaging options for how the user could respond next in Vietnamese:
            1. Option 1 (Affectionate / Compliant / Gentle): A warm, friendly, accepting, or romantic reaction appropriate to {character.Name}'s personality.
            2. Option 2 (Defiant / Mysterious / Bold / Teasing): A witty, bold, teasing, or questioning reaction.
            3. Option 3 (Observant / Inquisitive / Roleplay Action): A curious question or subtle physical action fitting the scene.
            
            Formatting Rules:
            - Output ONLY a valid JSON array of 3 strings in Vietnamese, e.g. ["*Khẽ mỉm cười nhìn {character.Name}...*", "*Ngạc nhiên nhìn lại...*", "*Điềm tĩnh đáp lời...*"].
            - Wrap actions in *asterisks*.
            - Keep each suggestion concise, vivid, natural, and directly relevant to {character.Name}.
            - Do not output markdown fences or explanatory text.
            """;
    }
}
