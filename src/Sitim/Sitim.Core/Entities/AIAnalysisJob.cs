namespace Sitim.Core.Entities;

/// <summary>
/// Background job for AI inference on a DICOM study.
/// Tracks job execution via Hangfire with status and lifecycle events.
/// </summary>
public class AIAnalysisJob
{
    public Guid Id { get; set; }
    
    /// <summary>
    /// Study that was analyzed
    /// </summary>
    public Guid StudyId { get; set; }
    public ImagingStudy Study { get; set; } = null!;
    
    /// <summary>
    /// Model used for analysis
    /// </summary>
    public Guid ModelId { get; set; }
    public AIModel Model { get; set; } = null!;
    
    /// <summary>
    /// Hangfire job ID (for tracking and cancellation)
    /// </summary>
    public string? HangfireJobId { get; set; }
    
    /// <summary>
    /// Job status: Queued, Running, Completed, Failed
    /// </summary>
    public string Status { get; set; } = "Queued";
    
    /// <summary>
    /// User ID who triggered the analysis
    /// </summary>
    public Guid PerformedByUserId { get; set; }
    
    /// <summary>
    /// User's display name at time of analysis (for audit trail)
    /// </summary>
    public string? PerformedByUserName { get; set; }
    
    /// <summary>
    /// When the job was created
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }
    
    /// <summary>
    /// When job execution started
    /// </summary>
    public DateTime? StartedAtUtc { get; set; }
    
    /// <summary>
    /// When job execution finished (success or failure)
    /// </summary>
    public DateTime? FinishedAtUtc { get; set; }
    
    // ──── Result Fields (populated when Status = "Completed") ──────────────
    
    /// <summary>
    /// Predicted class (for classification tasks)
    /// Example: 0=No DR, 1=Mild, 2=Moderate, 3=Severe, 4=Proliferative
    /// </summary>
    public int? PredictionClass { get; set; }
    
    /// <summary>
    /// Confidence score (0.0 to 1.0)
    /// </summary>
    public decimal? Confidence { get; set; }
    
    /// <summary>
    /// All class probabilities as JSON array
    /// Example: [0.95, 0.02, 0.01, 0.01, 0.01]
    /// </summary>
    public string? Probabilities { get; set; }
    
    /// <summary>
    /// Processing time in milliseconds
    /// </summary>
    public int? ProcessingTimeMs { get; set; }
    
    /// <summary>
    /// Error message if job failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}
