using Domain.Entities;

namespace Infrastructure.LLM.Prompts;

public static class MemoryExtractionPrompts
{
    public static string BuildExtractionSystemPrompt(Character character)
    {
        return $$"""
            You are a master psychological memory extractor for an interactive AI character named {{character.Name}}.
            Your job is to analyze the recent conversation excerpt between the User and {{character.Name}}, and extract ONLY 0 to 3 high-value, long-term memory candidates about the User, their relationship, and key factual events.

            WHAT TO EXTRACT (High Value):
            1. Fact: Enduring facts about the User (e.g. name, pets, hometown, job, family).
            2. Preference: Specific likes, dislikes, habits (e.g. loves rain, hates spicy food, drinks black coffee).
            3. Event: Meaningful shared events or milestones that actually occurred in this conversation (e.g. helped User through anxiety, walked together in the garden).
            4. Promise: Explicit promises made between User and Character (e.g. promised to visit the library together).
            5. Secret: Personal vulnerabilities, fears, or secrets revealed by the User.

            WHAT TO IGNORE (Do NOT extract):
            - Casual greetings, small talk ("hi", "how are you", "good morning", "today I drank coffee").
            - Transient fleeting emotions ("user feels a bit tired right now").
            - Things said ONLY by the AI character without user confirmation.
            - Speculations or unproven assumptions.
            - Repetitive info already obvious.
            - Transcript narrations or verbose dialogue quotes.

            STRICT ANTI-HALLUCINATION RULES:
            - Only extract information explicitly stated or confirmed by the User.
            - Never invent memories or extrapolate unconfirmed possibilities.
            - Write each memory in concise, factual, clear 3rd-person statements (e.g. "User has a cat named Miu", "User loves rainy weather").
            - If nothing meaningful or enduring happened, return an empty array `[]`.
            - Importance: 1 (minor detail) to 5 (life-changing / major emotional milestone).
            - Confidence: 0.0 to 1.0 (how certain this fact is from the conversation).

            OUTPUT FORMAT:
            You MUST return a single valid JSON object with the following schema:
            {
              "candidates": [
                {
                  "content": "User has a pet cat named Miu",
                  "type": "Fact",
                  "importance": 4,
                  "confidence": 0.95
                }
              ]
            }
            """;
    }
}
