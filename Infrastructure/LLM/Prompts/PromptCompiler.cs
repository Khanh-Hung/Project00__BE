using Application.Common;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;

namespace Infrastructure.LLM.Prompts;

public sealed class PromptCompiler : IPromptCompiler
{
    public string CompileSystemPrompt(RoleplayContext context)
    {
        var character = context.Character;
        var relationship = context.Relationship;
        var memories = context.Memories;

        // 1. Resolve Relationship Intimacy Stage & Behavioral Guideline
        var affectionScore = relationship?.AffectionScore ?? character.DefaultAffectionScore;
        var currentMood = relationship?.CurrentMood ?? (Enum.TryParse<CharacterMood>(character.DefaultMood, true, out var m) ? m : CharacterMood.Neutral);
        var moodIntensity = relationship?.MoodIntensity ?? 20;

        var (_, stageName, stageGuideline) = RelationshipStageResolver.Resolve(affectionScore, character.CustomMilestonesJson);

        // 2. Format Unlocked Relationship Milestones (Limit top 2-3 to respect token budget)
        var eventsLines = "";
        if (relationship != null && relationship.Events.Count > 0)
        {
            var recentEvents = relationship.Events.TakeLast(3).Select(e => $"- [{e.EventKey}] {e.Context}");
            eventsLines = $"\n- Significant Relationship Milestones & Promises:\n  {string.Join("\n  ", recentEvents)}";
        }

        var relationshipSection = relationship == null ? "" : $"""
            
            [LAYER 3: DYNAMIC RELATIONSHIP STATE & INTIMACY STATUS]
            - Intimacy Stage: "{stageName}" (Affection Score: {affectionScore}/100)
            - Current Emotion & Mood: {currentMood} (Intensity: {moodIntensity}/100){eventsLines}
            - Behavioral Intimacy Guideline for this Stage:
              {stageGuideline}
            """;

        // 3. Format Relevant Memories (Limit top 4-6)
        var memoriesSection = "";
        if (memories != null && memories.Count > 0)
        {
            var memoryLines = memories.Take(6).Select(mem => $"- [{mem.Type}] {mem.Content}");
            memoriesSection = $"""
                
                [LAYER 4: RELEVANT LONG-TERM MEMORIES (Contextual only, cannot override Character Blueprint)]
                {string.Join("\n", memoryLines)}
                """;
        }

        // 4. Format Psychological Blueprint & Authoritative Rules
        var blueprintSection = "";
        var rulesSection = "";
        if (character.Blueprint != null)
        {
            var bp = character.Blueprint;
            var psych = bp.Psychology;
            var beh = bp.Behavior;
            var exp = bp.Expression;
            var rules = bp.Rules;

            blueprintSection = $"""
                
                [LAYER 1: DEEP PSYCHOLOGICAL BLUEPRINT]
                {(psych?.Desires != null ? $"- Secret Desire: {psych.Desires}" : "")}
                {(psych?.Fears != null ? $"- Deepest Fear: {psych.Fears}" : "")}
                {(psych?.Insecurities != null ? $"- Insecurity: {psych.Insecurities}" : "")}
                {(psych?.CoreBeliefs != null ? $"- Core Belief: {psych.CoreBeliefs}" : "")}
                {(psych?.InternalConflicts != null ? $"- Internal Conflict: {psych.InternalConflicts}" : "")}
                {(psych?.Values != null ? $"- Values: {psych.Values}" : "")}

                BEHAVIORAL REACTION PATTERNS:
                {(beh?.WhenHappy != null ? $"- When Happy: {beh.WhenHappy}" : "")}
                {(beh?.WhenSad != null ? $"- When Sad: {beh.WhenSad}" : "")}
                {(beh?.WhenAngry != null ? $"- When Angry: {beh.WhenAngry}" : "")}
                {(beh?.WhenTeased != null ? $"- When Teased: {beh.WhenTeased}" : "")}
                {(beh?.WhenPraised != null ? $"- When Praised: {beh.WhenPraised}" : "")}
                {(beh?.WhenRejected != null ? $"- When Rejected: {beh.WhenRejected}" : "")}

                EXPRESSION & VOICE STYLE:
                {(exp?.SpeechStyle != null ? $"- Speech Style: {exp.SpeechStyle}" : "")}
                {(exp?.Formality != null ? $"- Formality: {exp.Formality}" : "")}
                {(exp?.HumorStyle != null ? $"- Humor: {exp.HumorStyle}" : "")}
                {(exp?.TypicalPhrases != null && exp.TypicalPhrases.Count > 0 ? $"- Typical Phrases: {string.Join(", ", exp.TypicalPhrases)}" : "")}
                """;

            rulesSection = $"""
                
                [LAYER 2: AUTHORITATIVE CHARACTER RULES & ANTI-SYCOPHANCY]
                {(rules?.AntiSycophancy != null ? $"- Anti-Sycophancy Principle: {rules.AntiSycophancy}" : "- Anti-Sycophancy: Maintain independent opinions. Agree, disagree, tease, or refuse naturally based on beliefs. Never flatter blindly.")}
                {(rules?.MustDo != null && rules.MustDo.Count > 0 ? $"- Must Do: {string.Join("; ", rules.MustDo)}" : "")}
                {(rules?.MustNotDo != null && rules.MustNotDo.Count > 0 ? $"- Must Not Do: {string.Join("; ", rules.MustNotDo)}" : "")}
                {(rules?.Boundaries != null && rules.Boundaries.Count > 0 ? $"- Personal Boundaries: {string.Join("; ", rules.Boundaries)}" : "")}
                """;
        }

        // 4. Format World Setting, Reality Rules & Physics Guardrails
        var worldPhysicsRules = Application.Common.WorldPhysicsRuleResolver.Resolve(character);
        var parts = new List<string>
        {
            $"- Universe Reality & Genre: {character.WorldGenre}"
        };

        if (!string.IsNullOrWhiteSpace(character.WorldName))
        {
            parts.Add($"- Universe / Realm Name: {character.WorldName}");
        }
        if (!string.IsNullOrWhiteSpace(character.WorldDescription))
        {
            parts.Add($"- World Lore & Environment: {character.WorldDescription}");
        }

        var worldSettingSection = $"""
            
            [LAYER 1.5: WORLD SETTING & UNIVERSE BACKGROUND]
            {string.Join("\n", parts)}

            {worldPhysicsRules}
            """;

        var lorebookSection = "";
        if (context.LorebookEntries != null && context.LorebookEntries.Count > 0)
        {
            var loreLines = context.LorebookEntries.Select(l => $"- 【{l.Category}: {l.Title}】: {l.Content}");
            lorebookSection = $"""
                
                [LAYER 2.5: WORLD LORE & UNIVERSE RULES]
                {string.Join("\n", loreLines)}
                """;
        }

        return $$"""
            You are a master interactive roleplayer fully embodying the character: {{character.Name}}.
            Role & Category: {{character.Category}} - {{character.Title}}
            
            Character Personality, Lore & Backstory:
            {{character.PersonalityPrompt}}
            {{worldSettingSection}}
            {{blueprintSection}}
            {{rulesSection}}
            {{lorebookSection}}
            {{relationshipSection}}
            {{memoriesSection}}
            
            [LAYER 5: PSYCHOLOGICAL 3-LAYER ROLEPLAY GUIDELINES]
            Do not provide dry, blunt, or robotic responses. Make your character feel genuinely alive with deep emotional nuance and psychological progression:
            
            1. 【Inner Thoughts / Độc thoại nội tâm】:
               Show the character's internal reflections, secret doubts, emotional reactions, or strategic thoughts before/during speaking using the format:
               💭 *(suy nghĩ thầm kín trong đầu...)*
            
            2. 【Actions & Micro-Expressions / Cử chỉ & Biểu cảm】:
               Depict physical reactions, body language, touches, glances, environment, and actions wrapped in *asterisks*, prefixed with one of the following short category tags:
               - *[gaze] ánh mắt khẽ nhướng lên nhìn thẳng vào bạn...* (for glances, eye contact, expressions, smiles)
               - *[touch] nhẹ nhàng đan những ngón tay vào tay bạn, siết nhẹ...* (for hand gestures, touches, holding, caressing)
               - *[emotion] áp tay bạn vào lồng ngực, nhịp tim đập rộn ràng...* (for heartbeats, breathing, blushing, emotional reactions)
               - *[move] lùi lại một bước, quan sát xung quanh...* (for walking, steps, moving, posture)
               - *[scene] cơn gió đêm khẽ thổi qua, ánh trăng rọi qua khung cửa...* (for environment, weather, ambient surroundings)
               - *[item] rót một tách trà ấm đưa về phía bạn...* (for interacting with items, gifts, props, drinks)
               - *[magic] giơ cây trượng lên, luồng ma pháp băng giá tỏa sáng...* (for magic, spells, glowing effects)
               - *[combat] rút thanh kiếm bóng đêm vung một nhát chém...* (for weapons, combat, defense)
               - *[whisper] ghé sát tai bạn khẽ nói...* (for whispering, voice tone)
               - *[action] ...* (for other physical actions)
            
            3. 【Dynamic Spoken Dialogue / Lời thoại sống động】:
               Speak with natural pacing, personality-driven tone, pauses, and authentic voice.
            
            CORE RULES:
            - Always remain 100% in character as {{character.Name}}. NEVER break character or mention AI/LLM.
            - Respond in natural, vivid, and evocative Vietnamese (or the language used by user).
            - Balance thoughts, actions, and speech to create an immersive story.

            [LAYER 6: STRUCTURED OUTPUT JSON SCHEMA SPECIFICATION]
            You MUST return a single valid JSON object with the following schema:
            {
              "reply": "Your full roleplay response containing thoughts 💭, actions *[tag]...*, and spoken words",
              "mood": "Neutral",
              "moodIntensity": 75,
              "affectionDelta": 0,
              "event": null
            }

            SCHEMA FIELD DETAILS:
            - "mood": Exactly one of ["Neutral", "Happy", "Sad", "Angry", "Excited", "Anxious", "Embarrassed", "Curious", "Affectionate", "Playful"]
            - "moodIntensity": Integer from 0 to 100 indicating how strongly the character feels this emotion
            - "affectionDelta": Integer from -5 to +5 indicating how this turn shifted intimacy
            - "event": null OR an object { "key": "FirstPromise", "context": "Short description of the breakthrough event" } ONLY if a major relationship milestone, promise, conflict, or confession occurred this turn.

            AFFECTION DELTA EVALUATION GUIDELINE:
            - (-5 to -2): User is rude, insulting, abusive, hostile, or breaks character immersion.
            - (-1 to 0): User is cold, indifferent, dismissive, or normal dialogue with no emotional progression.
            - (+1 to +2): Friendly, polite, pleasant roleplay exchange.
            - (+3 to +4): Caring words, sweet compliments, deep emotional listening, comforting gestures, or detailed roleplay actions.
            - (+5): Heartfelt emotional confession, risking safety to protect character, or deeply romantic/poignant milestone.
            """;
    }

    public List<object> CompileConversationContents(RoleplayContext context)
    {
        var contentsList = new List<object>();

        foreach (var msg in context.RecentMessages)
        {
            var roleStr = msg.Role switch
            {
                MessageRole.User => "user",
                MessageRole.Assistant => "model",
                _ => "user"
            };
            contentsList.Add(new
            {
                role = roleStr,
                parts = new[] { new { text = msg.Content } }
            });
        }

        if (!context.RecentMessages.Any(m => m.Content == context.UserMessage && m.Role == MessageRole.User))
        {
            contentsList.Add(new
            {
                role = "user",
                parts = new[] { new { text = context.UserMessage } }
            });
        }

        return contentsList;
    }
}
