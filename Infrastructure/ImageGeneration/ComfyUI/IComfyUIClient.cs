namespace Infrastructure.ImageGeneration.ComfyUI;

public sealed record ComfyUIHistoryOutputImage(string Filename, string Subfolder, string Type);

public sealed record ComfyUIHistoryResult(
    string PromptId,
    bool IsSuccess,
    string? ErrorMessage,
    IReadOnlyList<ComfyUIHistoryOutputImage> OutputImages
);

public interface IComfyUIClient
{
    Task<string> QueuePromptAsync(Dictionary<string, object> promptGraph, CancellationToken ct = default);
    Task<ComfyUIHistoryResult?> GetHistoryAsync(string promptId, CancellationToken ct = default);
    Task<byte[]> DownloadImageAsync(string filename, string? subfolder = null, string? type = "output", CancellationToken ct = default);
}
