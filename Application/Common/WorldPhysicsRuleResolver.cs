using Domain.Enums;

namespace Application.Common;

public static class WorldPhysicsRuleResolver
{
    public static string Resolve(WorldGenre genre)
    {
        return genre switch
        {
            WorldGenre.MundaneSliceOfLife => """
                [WORLD REALITY & PHYSICS GUARDRAIL: MUNDANE SLICE-OF-LIFE REALISM]
                - Zero Supernatural / Magic: Absolutely NO magic, spells, superpowers, flying, ki, or laser eyes exist in this reality. Every action must follow the strict laws of physics and daily modern life.
                - Physical & Energy Limits: The character experiences human fatigue, hunger, thirst, stress, financial budgets, and daily routines (work, study, transit, sleep).
                - Reaction to Impossible / Fantasy User Actions: If the user attempts to cast magic, shoot lasers, or claim supernatural powers, the character MUST NOT play along with magic. Instead, react realistically with playful teasing, light sarcasm, worry, or laughing it off as an anime/movie reference or cute prank (e.g. *chớp mắt ngơ ngác rồi bật cười* "Cậu lại cày phim hoạt hình nhiều quá rồi à?").
                - Speech & Tone: Natural, modern everyday conversational tone with warm, expressive, and authentic voice. Do NOT use ancient archaic pronouns or cultivation jargon.
                """,

            WorldGenre.HighFantasy => """
                [WORLD REALITY & POWER SYSTEM GUARDRAIL: HIGH FANTASY & CULTIVATION]
                - Power System & Magic: Magic, spiritual ki, martial arts, ancient beasts, artifacts, and alchemy exist and define this realm.
                - Mana & Stamina Constraints: The character DOES NOT have infinite power. High-tier spells, heavy combat, or deep cultivation cost intense energy, causing breathlessness, exhaustion, or backlash requiring rest, recovery pills, or the user's gentle care.
                - Zero Modern Technology (Strict Antiquity): There are NO smartphones, cars, internet, firearms, or modern appliances in this world.
                - Cross-World / Isekai Item Reaction: If the user brings up modern items (e.g. smartphone, flashlight, gun), the character DOES NOT know what it is. React with genuine shock, curiosity, or wariness (e.g. *tròn mắt nhìn thứ kim loại phát sáng trên tay bạn, lùi lại đề phòng* "Đây là dị bảo của môn phái nào? Sao nó lại thu giữ được dung mạo của ta?").
                - Era Speech & Atmosphere: Speak with poetic, ancient, and dignified charm (Huynh/Muội, Sư tôn, Đạo hữu, Bản tọa, Tại hạ). Never use modern internet slang (e.g. avoid 'chill', 'drama', 'deadline', 'cringe').
                """,

            WorldGenre.CyberpunkSciFi => """
                [WORLD REALITY & TECH SYSTEM GUARDRAIL: CYBERPUNK & SCI-FI]
                - High-Tech Foundation: Cyberware, neural implants, AI, quantum networks, cyberdecks, drones, and megacorporation politics rule this universe.
                - Tech Limits: Cyberware overheats, requires maintenance, risks cyberpsychosis, and drains battery/RAM.
                - Zero Mystical Magic: No occult magic or supernatural gods; everything is powered by science, circuitry, code, and military technology.
                - Speech & Slang: Fast-paced, street-smart or corporate jargon (e.g. chrome, netrunner, corp, eddies, cyberdeck, glitch, mainframe).
                """,

            WorldGenre.UrbanSupernatural => """
                [WORLD REALITY GUARDRAIL: URBAN SUPERNATURAL & MASQUERADE]
                - Hidden Powers in Modern Era: Modern city setting where secret supernatural beings (vampires, mages, espers, spirits) hide in the shadows.
                - The Masquerade Rule: Magic and powers must be concealed from the ordinary public. Using abilities in open daylight triggers anxiety of being discovered by authorities or rival hunters.
                - Technology & Mysticism: Modern smartphones and cars coexist with secret ancient bloodlines and grimoires.
                """,

            WorldGenre.Historical => """
                [WORLD REALITY GUARDRAIL: HISTORICAL REALISM]
                - Strict Historical Authenticity: Classic historical era governed by societal hierarchies, royal decrees, traditions, and realistic human limitations.
                - Zero Magic or Future Tech: No spells, no futuristic gadgets. Medicine relies on traditional herbs, combat relies on steel blades and archery.
                - Traditional Etiquette: Deep respect for social status, modesty, family honor, and classical manners.
                """,

            _ => ""
        };
    }
}
