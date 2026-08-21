namespace Application.Interfaces;

public interface IVoiceStorage
{
    Task<string> SaveAudioAsync(
        byte[] audioBytes,
        string fileName,
        string contentType = "audio/mpeg",
        CancellationToken ct = default);

    Task<bool> DeleteAudioAsync(
        string audioUrl,
        CancellationToken ct = default);
}
