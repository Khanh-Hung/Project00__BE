namespace Domain.ValueObjects;

public sealed record CharacterVoiceProfile(
    string VoiceId,
    string? Language = "vi-VN",
    string? Gender = "Female",
    string? AgeRange = "YoungAdult",
    string? Tone = "Soft",
    string? SpeakingStyle = "Warm",
    string? Pace = "Normal",
    string? Pitch = "Normal",
    string? Description = null
)
{
    public CharacterVoiceProfile() : this("vi-VN-HoaiMyNeural") { }
}
