namespace Sitim.Core.Entities;

/// <summary>
/// AI Model metadata stored in database
/// Actual model files stored in MinIO
/// </summary>
public class AIModel
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// Human-readable model name
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Detailed description
    /// </summary>
    public string? Description { get; set; }
    
    /// <summary>
    /// Task type (e.g., "retinopathy_classification", "lung_segmentation")
    /// </summary>
    public string Task { get; set; } = string.Empty;
    
    /// <summary>
    /// Model version (e.g., "v1", "v2", "fl_round_10")
    /// </summary>
    public string Version { get; set; } = string.Empty;
    
    /// <summary>
    /// MinIO object name (e.g., "retinopathy_pretrained_v1.onnx")
    /// </summary>
    public string StorageFileName { get; set; } = string.Empty;
    
    /// <summary>
    /// Model accuracy on validation set (0.0 to 1.0)
    /// </summary>
    public decimal? Accuracy { get; set; }
    
    /// <summary>
    /// Whether this model is currently active for inference
    /// </summary>
    public bool IsActive { get; set; }
    
    /// <summary>
    /// Number of classes for classification tasks
    /// </summary>
    public int? NumClasses { get; set; }
    
    /// <summary>
    /// Expected input shape (e.g., "[1, 3, 512, 512]")
    /// </summary>
    public string? InputShape { get; set; }
    
    /// <summary>
    /// Training source (e.g., "pretrained", "federated_learning", "transfer_learning")
    /// </summary>
    public string? TrainingSource { get; set; }
    
    /// <summary>
    /// DICOM modalities supported by this model (comma-separated)
    /// Examples: "OT" (eye), "CR,DX" (radiography), "CT" (CT scans)
    /// Null = legacy model, not configured for filtering
    /// </summary>
    public string? TargetModality { get; set; }
    
    /// <summary>
    /// JSON array of anatomical regions analyzed
    /// Example: ["Optic Disc", "Macula", "Blood Vessels"]
    /// </summary>
    public string? SupportedRegions { get; set; }
    
    /// <summary>
    /// JSON array of detectable pathologies
    /// Example: ["Diabetic Retinopathy", "Glaucoma"]
    /// </summary>
    public string? DetectablePathologies { get; set; }
    
    /// <summary>
    /// JSON array of output class names for result interpretation
    /// Example: ["No DR", "Mild DR", "Moderate DR", "Severe DR", "Proliferative DR"]
    /// Used by MapToDto to label predictions without hardcoding
    /// </summary>
    public string? ClassNames { get; set; }
    
    /// <summary>
    /// JSON array of severity levels corresponding to class indices
    /// Example: ["None", "Mild", "Moderate", "Severe", "Proliferative"]
    /// Maps to each class for diagnosis interpretation
    /// </summary>
    public string? ClassSeverities { get; set; }
    
    /// <summary>
    /// JSON 2D array of clinical recommendations per class
    /// Example: [["Rec1", "Rec2"], ["Rec3", "Rec4"], ...]
    /// Each inner array contains recommendations for that class
    /// </summary>
    public string? ClassRecommendations { get; set; }
    
    /// <summary>
    /// Number of output classes (should match ClassNames.Length)
    /// Example: 5 for DR, 2 for pneumonia detection
    /// Used for validation and preprocessing
    /// </summary>
    public int? NumOutputClasses { get; set; }
    
    /// <summary>
    /// ✅ SCALABILITY FIX #4: Preprocessing parameters moved from hardcoded to database
    /// JSON array of mean values for each channel normalization
    /// Example: [0.485, 0.456, 0.406] for ImageNet
    /// If null, defaults to [0, 0, 0] (no normalization)
    /// </summary>
    public string? PreprocessingMean { get; set; }
    
    /// <summary>
    /// JSON array of standard deviation values for each channel normalization
    /// Example: [0.229, 0.224, 0.225] for ImageNet
    /// If null, defaults to [1, 1, 1] (no normalization)
    /// </summary>
    public string? PreprocessingStd { get; set; }
    
    /// <summary>
    /// Target image size for resizing before inference
    /// Example: 512 for retinopathy (512x512), 256 for pneumonia detection, etc.
    /// If null, defaults to 512 (backward compatible)
    /// </summary>
    public int? PreprocessingImageSize { get; set; }
    
    /// <summary>
    /// Preprocessing method identifier
    /// Examples: "imagenet_norm", "dicom_hounsfield", "minmax", "zscore", "none"
    /// Allows selection of different preprocessing pipelines per model
    /// If null, defaults to "imagenet_norm" (ImageNet normalization)
    /// </summary>
    public string? PreprocessingMethod { get; set; }
    
    /// <summary>
    /// ONNX input tensor specification (JSON)
    /// Example: [{"name": "input", "shape": [1,3,512,512], "dtype": "float32"}]
    /// Enables validation of input tensor before inference
    /// Supports multiple inputs for complex models
    /// </summary>
    public string? OnnxInputSpec { get; set; }
    
    /// <summary>
    /// ONNX output tensor specification (JSON)
    /// Example: [{"name": "output", "shape": [1,5], "dtype": "float32"}]
    /// Enables parsing of multi-dimensional outputs (segmentation, etc.)
    /// Supports multiple outputs (detection + heatmaps, etc.)
    /// </summary>
    public string? OnnxOutputSpec { get; set; }
    
    public DateTime CreatedAt { get; set; }
    
    // Navigation properties
    public ICollection<AIAnalysisJob> AnalysisJobs { get; set; } = new List<AIAnalysisJob>();
}
