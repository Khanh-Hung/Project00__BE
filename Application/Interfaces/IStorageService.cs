namespace Application.Interfaces;

public interface IStorageService
{
    Task<string> SaveImageAsync(byte[] imageBytes, string fileName, string contentType = "image/jpeg", CancellationToken ct = default);
    Task<string> SaveBase64ImageAsync(string base64Data, string fileName, CancellationToken ct = default);
    Task<bool> DeleteFileAsync(string fileUrl, CancellationToken ct = default);
}
