using Midss.Core.Contracts.AI;
using Midss.Core.Entities;

namespace Midss.Core.Services;

/// <summary>
/// Service for AI model inference on DICOM studies
/// </summary>
public interface IAIInferenceService
{
    /// <summary>
    /// Run AI inference on a DICOM study
    /// </summary>
    /// <param name="studyId">Study to analyze</param>
    /// <param name="modelId">Model to use (required - doctor must select from filtered list via GetModelsForStudy)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Analysis result</returns>
    Task<AIAnalysisJob> AnalyzeStudyAsync(
        Guid studyId,
        Guid? modelId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get analysis history for a study
    /// </summary>
    Task<List<AIAnalysisJob>> GetStudyAnalysisHistoryAsync(Guid studyId);

    /// <summary>
    /// Execute an AI analysis job (called from Hangfire background worker).
    /// Loads job from database, runs inference, and updates job record with results/status.
    /// </summary>
    Task ExecuteAnalysisJobAsync(Guid analysisJobId, CancellationToken cancellationToken);
}

// AIAnalysisResultDto and ClassProbability moved to Midss.Core.Contracts.AI
// (see Midss.Core/Contracts/AI/AIAnalysisContracts.cs)
