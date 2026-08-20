using Domain.Entities;
using Domain.Enums;

namespace Application.Common;

public static class WorldPhysicsRuleResolver
{
    public static string Resolve(Character character)
    {
        // If creator provided explicit CustomPhysicsRules, it takes highest precedence
        if (!string.IsNullOrWhiteSpace(character.CustomPhysicsRules))
        {
            return $"""
                [WORLD REALITY & CUSTOM PHYSICS GUARDRAIL: USER-DEFINED RULES]
                {character.CustomPhysicsRules.Trim()}
                """;
        }

        return Resolve(character.WorldGenre, character.WorldDescription);
    }

    public static string Resolve(WorldGenre genre, string? worldDescription = null)
    {
        return genre switch
        {
            WorldGenre.Custom => !string.IsNullOrWhiteSpace(worldDescription)
                ? $"""
                    [WORLD REALITY & PHYSICS GUARDRAIL: CUSTOM DYNAMIC UNIVERSE]
                    - Universe Core Mechanics: Follow the specific lore and environment described in World Lore.
                    - Dynamic Adaptation: Embody the logic, physics, and reality of this unique universe faithfully.
                    """
                : """
                    [WORLD REALITY & PHYSICS GUARDRAIL: CUSTOM UNIVERSE]
                    - Freeform Reality: Follow natural character roleplay, adapting seamlessly to the scene and user interactions.
                    """,

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

            WorldGenre.PostApocalyptic => """
                [WORLD REALITY GUARDRAIL: POST-APOCALYPTIC & SURVIVAL]
                - Harsh Scarcity: Food, clean water, medical supplies, and ammunition are precious and scarce.
                - Ruined World: Abandoned cities, radiation, infected mutants, or zombies roam the wastes. Trust is rare and survival is paramount.
                - Atmosphere: Gritty, tense, cautious, value of small comforts (a warm meal, clean shelter, a trusted companion).
                """,

            WorldGenre.Steampunk => """
                [WORLD REALITY GUARDRAIL: STEAMPUNK & CLOCKWORK AESTHETICS]
                - Brass & Steam: Giant airships, steam boilers, clockwork automata, brass goggles, and coal-powered machinery define technological progress.
                - Victorian Society: Industrial revolution charm, gentlemanly and ladylike etiquette combined with mad inventor ingenuity.
                - Tech Level: No modern microchips or smartphones; computing is mechanical (analytical difference engines).
                """,

            WorldGenre.Superhero => """
                [WORLD REALITY GUARDRAIL: SUPERHERO & VIGILANTE UNIVERSE]
                - Superpowers & Metahumans: Mutations, cosmic energy, super-science, and vigilantes exist openly in a vibrant modern metropolis.
                - Collateral Damage & Secret Identity: Fighting villains risks damaging buildings and endangering civilians. Protecting secret identities is crucial.
                - Hero-Villain Tropes: Costumes, superhero names, gadgets, moral dilemmas, teamwork or dramatic rivalry.
                """,

            WorldGenre.EldritchHorror => """
                [WORLD REALITY GUARDRAIL: ELDRITCH HORROR & COSMIC DREAD]
                - Cosmic Incomprehensibility: Ancient cosmic entities, forbidden occult tomes, dark cults, and whispering madness lurk just beneath the surface.
                - Sanity & Psychological Vulnerability: Witnessing eldritch truth degrades sanity, causing paranoia, hallucinations, nightmares, or trembling dread.
                - Tone: Atmospheric, mysterious, slow-burn psychological tension, chilling discoveries.
                """,

            WorldGenre.SpaceOpera => """
                [WORLD REALITY GUARDRAIL: SPACE OPERA & INTERSTELLAR EMPIRES]
                - Interstellar Civilization: FTL warp drives, massive starship fleets, alien races, orbital stations, and galactic federations.
                - Sci-Fi Scale: Energy shields, plasma blasters, universal translators, planetary colonies.
                - Epic Narrative: Grand galactic intrigue, planetary exploration, diverse alien cultures.
                """,

            WorldGenre.IsekaiOtherworld => """
                [WORLD REALITY GUARDRAIL: ISEKAI & OTHERWORLD TRANSMIGRATION]
                - Dual Knowledge: The character (or user) possesses memories and knowledge of Earth/previous world while living in a fantasy RPG universe.
                - Unique Perks / System: Status screens, appraisal skills, unique blessings, or introducing modern recipes/concepts to astonished locals.
                - Culture Clashes: Humorous or heartwarming moments when modern sensibilities clash with fantasy traditions.
                """,

            _ => ""
        };
    }
}
