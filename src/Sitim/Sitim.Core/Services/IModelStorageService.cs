namespace Sitim.Core.Services;

/// <summary>
/// Service for managing AI model storage in MinIO object storage
/// </summary>
public interface IModelStorageService
{
    /// <summary>
    /// Upload a model file to MinIO storage
    /// </summary>
    /// <param name="fileName">Name of the file (e.g., "retinopathy_v1.onnx")</param>
    /// <param name="fileStream">File content stream</param>
    /// <param name="contentType">MIME type (default: application/octet-stream)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Object URL in MinIO</returns>
    Task<string> UploadModelAsync(
        string fileName, 
        Stream fileStream, 
        string contentType = "application/octet-stream",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a model file from MinIO storage
    /// </summary>
    /// <param name="fileName">Name of the file</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>File content stream</returns>
    Task<Stream> DownloadModelAsync(
        string fileName, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a model file exists in storage
    /// </summary>
    Task<bool> ModelExistsAsync(
        string fileName, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a model file from storage
    /// </summary>
    Task DeleteModelAsync(
        string fileName, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get presigned URL for model download (valid for limited time)
    /// </summary>
    /// <param name="fileName">Name of the file</param>
    /// <param name="expirySeconds">URL validity in seconds (default: 3600 = 1 hour)</param>
    /// <returns>Presigned URL</returns>
    Task<string> GetModelDownloadUrlAsync(
        string fileName, 
        int expirySeconds = 3600);

    /// <summary>
    /// List all models in storage
    /// </summary>
    Task<List<string>> ListModelsAsync(CancellationToken cancellationToken = default);
}
