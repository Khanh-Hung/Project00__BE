using System.Text;
using Application.Common;
using Domain.Enums;
using Xunit;

namespace Project.Tests;

public sealed class IncrementalJsonReplyExtractorTests
{
    [Fact]
    public void Emits_Clean_Tokens_Without_JSON_Leakage_On_Standard_Stream()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var chunks = new[]
        {
            "```json\n{\n  \"reply\": \"Chào ",
            "bạn, mình là Aeloria. ",
            "Rất vui được gặp bạn!\",\n",
            "  \"mood\": \"Happy\",\n",
            "  \"moodIntensity\": 85,\n",
            "  \"affectionDelta\": 3,\n",
            "  \"event\": {\"key\": \"FIRST_MEETING\", \"context\": \"Met in tavern\"}\n}\n```"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Chào bạn, mình là Aeloria. Rất vui được gặp bạn!", fullDialogue);

        // Assert no JSON leakage in ANY emitted token
        foreach (var token in emittedTokens)
        {
            Assert.DoesNotContain("{", token);
            Assert.DoesNotContain("}", token);
            Assert.DoesNotContain("\"reply\"", token);
            Assert.DoesNotContain("\"mood\"", token);
            Assert.DoesNotContain("```", token);
        }

        // Test StructuredTurnParser on raw buffer
        var rawText = extractor.GetFullRawAccumulatedText();
        var turnResult = StructuredTurnParser.Parse(rawText);

        Assert.Equal("Chào bạn, mình là Aeloria. Rất vui được gặp bạn!", turnResult.Reply);
        Assert.Equal(CharacterMood.Happy, turnResult.Mood);
        Assert.Equal(85, turnResult.MoodIntensity);
        Assert.Equal(3, turnResult.AffectionDelta);
        Assert.NotNull(turnResult.Event);
        Assert.Equal("FIRST_MEETING", turnResult.Event.Key);
        Assert.Equal("Met in tavern", turnResult.Event.Context);
    }

    [Fact]
    public void Handles_Arbitrary_Field_Ordering_With_Long_Thinking_Prefix()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var longThought = new string('A', 500); // 500 characters to surpass old 80-char limit

        var chunks = new[]
        {
            "{\n  \"thought\": \"",
            longThought,
            "\",\n  \"mood\": \"Curious\",\n",
            "  \"moodIntensity\": 70,\n",
            "  \"affectionDelta\": 2,\n",
            "  \"reply\": \"Ngươi thực sự ",
            "muốn tìm hiểu về thánh tích này sao?\"\n}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Ngươi thực sự muốn tìm hiểu về thánh tích này sao?", fullDialogue);

        var rawText = extractor.GetFullRawAccumulatedText();
        var turnResult = StructuredTurnParser.Parse(rawText);

        Assert.Equal("Ngươi thực sự muốn tìm hiểu về thánh tích này sao?", turnResult.Reply);
        Assert.Equal(CharacterMood.Curious, turnResult.Mood);
        Assert.Equal(70, turnResult.MoodIntensity);
        Assert.Equal(2, turnResult.AffectionDelta);
    }

    [Fact]
    public void Handles_Arbitrary_Field_Ordering_With_Fields_After_Reply()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var chunks = new[]
        {
            "{\n  \"reply\": \"Đây là câu trả lời đầu tiên.\",\n",
            "  \"sceneDelta\": {\"location\": \"Library\"},\n",
            "  \"mood\": \"Playful\",\n",
            "  \"affectionDelta\": 1\n}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Đây là câu trả lời đầu tiên.", fullDialogue);

        var turnResult = StructuredTurnParser.Parse(extractor.GetFullRawAccumulatedText());
        Assert.Equal("Đây là câu trả lời đầu tiên.", turnResult.Reply);
        Assert.Equal(CharacterMood.Playful, turnResult.Mood);
        Assert.Equal(1, turnResult.AffectionDelta);
    }

    [Fact]
    public void Decodes_Unicode_Escapes_And_Vietnamese_Characters()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var chunks = new[]
        {
            "{\"reply\": \"Xin ch\\u00e0o \\u0111\\u1ea1i hi\\u1ec7p! C\\u1ea3m \\u01a1n v\\u00ec \\u0111\\u00e3 gh\\u00e9 th\\u0103m.\"}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Xin chào đại hiệp! Cảm ơn vì đã ghé thăm.", fullDialogue);
    }

    [Fact]
    public void Decodes_UTF16_Surrogate_Pairs_For_Emojis()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        // \uD83C\uDF38 is 🌸, \uD83D\uDE0A is 😊
        var chunks = new[]
        {
            "{\"reply\": \"Hoa anh \\u0111\\u00e0o \\uD83C\\uDF38 v\\u00e0 n\\u1ee5 c\\u01b0\\u1eddi \\uD83D\\uDE0A\"}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Hoa anh đào 🌸 và nụ cười 😊", fullDialogue);
    }

    [Fact]
    public void Decodes_Unicode_And_Surrogate_Pairs_Split_Across_Arbitrary_Chunk_Boundaries()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        // Fragmented stream splitting \u and hex digits across boundaries
        var chunks = new[]
        {
            "{\"reply\": \"Ch\\u",
            "00",
            "e0o \\uD8",
            "3C\\u",
            "DF38 b\\u",
            "1ea1n \\uD83D\\u",
            "DE0A!\"}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Chào 🌸 bạn 😊!", fullDialogue);
    }

    [Fact]
    public void Decodes_Standard_Escapes_Split_Across_Chunks()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var chunks = new[]
        {
            "{\"reply\": \"Line 1\\",
            "nLine 2 with \\\"quotes\\\" and backslash \\",
            "\\ finished.\"}"
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Line 1\nLine 2 with \"quotes\" and backslash \\ finished.", fullDialogue);
    }

    [Fact]
    public void Handles_Pure_PlainText_NonJson_Stream_Gracefully()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var chunks = new[]
        {
            "Xin ",
            "chào bạn, ",
            "ta là một pháp sư cổ xưa ",
            "đến từ phương Bắc."
        };

        var emittedTokens = new List<string>();
        foreach (var chunk in chunks)
        {
            emittedTokens.AddRange(extractor.PushChunk(chunk));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Xin chào bạn, ta là một pháp sư cổ xưa đến từ phương Bắc.", fullDialogue);

        // Verify fail-safe StructuredTurnParser maintains state invariants (0 delta, neutral mood, null event)
        var turnResult = StructuredTurnParser.Parse(extractor.GetFullRawAccumulatedText());
        Assert.Equal("Xin chào bạn, ta là một pháp sư cổ xưa đến từ phương Bắc.", turnResult.Reply);
        Assert.Equal(CharacterMood.Neutral, turnResult.Mood);
        Assert.Equal(50, turnResult.MoodIntensity);
        Assert.Equal(0, turnResult.AffectionDelta);
        Assert.Null(turnResult.Event);
        Assert.False(turnResult.HasWalkedOut);
    }

    [Fact]
    public void Handles_Truncated_JSON_Repairs_Braces_Safely()
    {
        var rawTruncated = "```json\n{\n  \"reply\": \"Ta đang lắng nghe câu chuyện của ngươi...\",\n  \"mood\": \"Curious\",\n  \"affectionDelta\": 3";
        var turnResult = StructuredTurnParser.Parse(rawTruncated);

        Assert.Equal("Ta đang lắng nghe câu chuyện của ngươi...", turnResult.Reply);
        Assert.Equal(CharacterMood.Curious, turnResult.Mood);
        Assert.Equal(3, turnResult.AffectionDelta);
    }

    [Fact]
    public void Handles_Severely_Corrupted_JSON_Without_Mutating_Character_State()
    {
        var severelyCorrupted = "```json\n{ unquoted_key_invalid ::: 1234, ??? [[[\n```";
        var turnResult = StructuredTurnParser.Parse(severelyCorrupted, fallbackReply: "Fallback dialogue");

        // Invariant: Malformed JSON must NEVER guess state or cause non-zero affection delta!
        Assert.Equal("Fallback dialogue", turnResult.Reply);
        Assert.Equal(CharacterMood.Neutral, turnResult.Mood);
        Assert.Equal(50, turnResult.MoodIntensity);
        Assert.Equal(0, turnResult.AffectionDelta);
        Assert.Null(turnResult.Event);
        Assert.False(turnResult.HasWalkedOut);
    }

    [Fact]
    public void Handles_Single_Character_And_Empty_Chunks()
    {
        var extractor = new IncrementalJsonReplyExtractor();
        var fullJson = "{\"reply\": \"Từng ký tự một.\"}\n";

        var emittedTokens = new List<string>();
        extractor.PushChunk(string.Empty);
        extractor.PushChunk(null);

        foreach (char c in fullJson)
        {
            emittedTokens.AddRange(extractor.PushChunk(c.ToString()));
        }

        var fullDialogue = string.Join("", emittedTokens);
        Assert.Equal("Từng ký tự một.", fullDialogue);
    }
}
