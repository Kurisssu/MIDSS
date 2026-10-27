using Sitim.Core.Entities;

namespace Sitim.Core.Services;

/// <summary>
/// Selects AI models based on DICOM modality and accuracy
/// </summary>
public interface IAIModelSelectorService
{
    /// <summary>
    /// Get all active models supporting the given modality
    /// Ordered by accuracy (highest first)
    /// </summary>
    Task<List<AIModel>> GetModelsByModalityAsync(string modality, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the best (highest accuracy) model for the given modality
    /// </summary>
    Task<AIModel?> GetBestModelForModalityAsync(string modality, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validate if model supports the given modality
    /// </summary>
    bool IsModelCompatibleWithModality(AIModel model, string modality);
}
