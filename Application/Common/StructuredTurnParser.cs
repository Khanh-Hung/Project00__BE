using System.Text.Json;
using Application.DTOs;
using Domain.Enums;

namespace Application.Common;

/// <summary>
/// Conservative, fail-safe parser for LLM turn responses.
/// Enforces a strict 3-tier parsing hierarchy:
/// 1. Official JSON parsing with tolerant options (trailing commas, comments).
/// 2. Safe structural sanitization (markdown codeblocks, outer brace extraction, unclosed brace repair).
/// 3. Fail-safe deterministic fallback that NEVER mutates character affection or unlocks events erroneously.
/// </summary>
public static class StructuredTurnParser
{
    private static readonly JsonDocumentOptions TolerantJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static RoleplayTurnResult Parse(string rawText, string? fallbackReply = null)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new RoleplayTurnResult(fallbackReply ?? string.Empty, CharacterMood.Neutral, 50, 0, null);
        }

        // Tier 1: Direct Parsing
        if (TryParseJson(rawText, out var tier1Result))
        {
            return tier1Result!;
        }

        // Tier 2: Safe Structural Sanitization
        var sanitized = SanitizeJson(rawText);
        if (TryParseJson(sanitized, out var tier2Result))
        {
            return tier2Result!;
        }

        // Tier 2b: Repair truncated JSON by closing unclosed braces
        var repaired = RepairUnclosedBraces(sanitized);
        if (TryParseJson(repaired, out var tier2bResult))
        {
            return tier2bResult!;
        }

        // Tier 3: Fail-Safe Fallback (Strictly preserves state invariants: 0 delta, null event, false walkout)
        return new RoleplayTurnResult(
            Reply: fallbackReply ?? ExtractCleanReplyFallback(rawText),
            Mood: CharacterMood.Neutral,
            MoodIntensity: 50,
            AffectionDelta: 0,
            Event: null,
            HasWalkedOut: false,
            WalkOutReason: null
        );
    }

    private static bool TryParseJson(string json, out RoleplayTurnResult? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json, TolerantJsonOptions);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return false;

            // Extract reply
            string reply = string.Empty;
            if (root.TryGetProperty("reply", out var replyProp))
            {
                reply = replyProp.GetString() ?? string.Empty;
            }
            else if (root.TryGetProperty("Reply", out var replyPropUpper))
            {
                reply = replyPropUpper.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(reply))
            {
                return false;
            }

            // Extract mood safely
            var mood = CharacterMood.Neutral;
            if ((root.TryGetProperty("mood", out var moodProp) || root.TryGetProperty("Mood", out moodProp)) &&
                moodProp.ValueKind == JsonValueKind.String &&
                Enum.TryParse<CharacterMood>(moodProp.GetString(), true, out var parsedMood))
            {
                mood = parsedMood;
            }

            // Extract moodIntensity safely
            int intensity = 50;
            if ((root.TryGetProperty("moodIntensity", out var intProp) || root.TryGetProperty("MoodIntensity", out intProp)))
            {
                if (intProp.ValueKind == JsonValueKind.Number && intProp.TryGetInt32(out var parsedInt))
                {
                    intensity = Math.Clamp(parsedInt, 0, 100);
                }
            }

            // Extract affectionDelta safely (clamped to [-5, 5])
            int delta = 0;
            if ((root.TryGetProperty("affectionDelta", out var delProp) || root.TryGetProperty("AffectionDelta", out delProp)))
            {
                if (delProp.ValueKind == JsonValueKind.Number && delProp.TryGetInt32(out var parsedDelta))
                {
                    delta = Math.Clamp(parsedDelta, -5, 5);
                }
            }

            // Extract event safely
            RelationshipEventProposal? proposal = null;
            if ((root.TryGetProperty("event", out var evtProp) || root.TryGetProperty("Event", out evtProp)) &&
                evtProp.ValueKind == JsonValueKind.Object)
            {
                string? key = null;
                if ((evtProp.TryGetProperty("key", out var kProp) || evtProp.TryGetProperty("Key", out kProp)) &&
                    kProp.ValueKind == JsonValueKind.String)
                {
                    key = kProp.GetString();
                }

                string context = string.Empty;
                if ((evtProp.TryGetProperty("context", out var cProp) || evtProp.TryGetProperty("Context", out cProp)) &&
                    cProp.ValueKind == JsonValueKind.String)
                {
                    context = cProp.GetString() ?? string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    proposal = new RelationshipEventProposal(key.Trim(), context.Trim());
                }
            }

            // Extract walkout safely
            bool hasWalkedOut = false;
            if ((root.TryGetProperty("hasWalkedOut", out var woProp) || root.TryGetProperty("HasWalkedOut", out woProp)))
            {
                if (woProp.ValueKind == JsonValueKind.True)
                {
                    hasWalkedOut = true;
                }
                else if (woProp.ValueKind == JsonValueKind.String && bool.TryParse(woProp.GetString(), out var bw))
                {
                    hasWalkedOut = bw;
                }
            }

            string? walkOutReason = null;
            if ((root.TryGetProperty("walkOutReason", out var wrProp) || root.TryGetProperty("WalkOutReason", out wrProp)) &&
                wrProp.ValueKind == JsonValueKind.String)
            {
                walkOutReason = wrProp.GetString();
            }

            result = new RoleplayTurnResult(reply, mood, intensity, delta, proposal, hasWalkedOut, walkOutReason);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeJson(string raw)
    {
        var cleaned = raw.Trim();

        // Strip markdown code fences
        if (cleaned.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(7);
        }
        else if (cleaned.StartsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(3);
        }

        if (cleaned.EndsWith("```", StringComparison.OrdinalIgnoreCase))
        {
            cleaned = cleaned.Substring(0, cleaned.Length - 3);
        }
        cleaned = cleaned.Trim();

        // Extract outermost object if surrounded by prose
        int firstBrace = cleaned.IndexOf('{');
        int lastBrace = cleaned.LastIndexOf('}');
        if (firstBrace != -1 && lastBrace != -1 && lastBrace > firstBrace)
        {
            cleaned = cleaned.Substring(firstBrace, lastBrace - firstBrace + 1);
        }

        return cleaned;
    }

    private static string RepairUnclosedBraces(string json)
    {
        int openBraces = json.Count(c => c == '{');
        int closeBraces = json.Count(c => c == '}');

        if (openBraces > closeBraces)
        {
            return json + new string('}', openBraces - closeBraces);
        }

        return json;
    }

    private static string ExtractCleanReplyFallback(string raw)
    {
        var cleaned = SanitizeJson(raw);
        // If it starts with non-JSON text, return as is
        return cleaned;
    }
}
