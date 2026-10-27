namespace Sitim.Core.Contracts.AI;

/// <summary>Full AI model row — returned by /api/ai/models endpoints.</summary>
/// <remarks>
/// Server-side is the source of truth: contains all clinical/preprocessing metadata.
/// Client-side deserialization will simply ignore any field it doesn't care about.
/// </remarks>
public sealed record AIModelDto(
    Guid Id,
    string Name,
    string? Description,
    string Task,
    string Version,
    string StorageFileName,
    decimal? Accuracy,
    bool IsActive,
    int? NumClasses,
    string? InputShape,
    string? TrainingSource,
    DateTime CreatedAt,
    // Clinical metadata
    string? TargetModality = null,
    string? ClassNames = null,
    string? ClassSeverities = null,
    string? ClassRecommendations = null,
    string? SupportedRegions = null,
    string? DetectablePathologies = null,
    // Preprocessing (stored as JSON strings on the entity)
    string? PreprocessingMethod = null,
    string? PreprocessingMean = null,
    string? PreprocessingStd = null,
    int? PreprocessingImageSize = null,
    // ONNX specifications
    string? OnnxInputSpec = null,
    string? OnnxOutputSpec = null);

/// <summary>
/// Editable interpretation/clinical metadata for an existing AI model.
/// Sent by the model-management UI to PUT /api/ai/models/{id}/metadata.
/// Arrays are aligned by class index: ClassNames[i], ClassSeverities[i] and
/// ClassRecommendations[i] all describe output class i.
/// </summary>
public sealed record UpdateModelMetadataRequest(
    string Name,
    string? Description,
    string? TargetModality,
    string[]? ClassNames,
    string[]? ClassSeverities,
    string[][]? ClassRecommendations);

/// <summary>Lightweight model entry used in the model-selection dialog (UI).</summary>
public sealed record AIModelSelectionDto(
    Guid Id,
    string Name,
    string Version,
    string Task,
    decimal? Accuracy,
    string? TargetModality,
    string? Description)
{
    /// <summary>Human-readable label for dropdowns / lists.</summary>
    public string Label => $"{Name} (v{Version})";
}

/// <summary>Compact model descriptor used by federated learning publishing pipeline.</summary>
public sealed record ModelDefinitionDto(
    Guid Id,
    string Name,
    string Task,
    string Version,
    bool IsActive,
    string StorageFileName,
    decimal? Accuracy,
    string? TrainingSource,
    string? TargetModality,
    DateTime CreatedAt);
