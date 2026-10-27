namespace Sitim.Core.Contracts.AI;

/// <summary>Body for POST /api/ai/analyze (kicks off a background analysis job).</summary>
public sealed record AnalyzeStudyRequest(Guid StudyId, Guid? ModelId = null);

/// <summary>Response from POST /api/ai/analyze — contains the new job's id for polling.</summary>
public sealed record StartAnalysisResponseDto(
    Guid JobId,
    string Status,
    DateTime CreatedAt);

/// <summary>Live status of a single analysis job (polled while it's Queued/Running).</summary>
public sealed record AIAnalysisJobStatusDto(
    Guid Id,
    Guid StudyId,
    string OrthancStudyId,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? ModelName,
    int? PredictionClass,
    decimal? Confidence,
    int? ProcessingTimeMs,
    string? ErrorMessage);

/// <summary>Row in /api/ai/jobs listing — adds study context columns over the status DTO.</summary>
public sealed record AIAnalysisJobListItemDto(
    Guid Id,
    Guid StudyId,
    string OrthancStudyId,
    string? PatientName,
    string? StudyDate,
    IReadOnlyList<string> ModalitiesInStudy,
    string Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? ModelName,
    int? PredictionClass,
    decimal? Confidence,
    int? ProcessingTimeMs,
    string? ErrorMessage);

/// <summary>Full analysis result with clinical interpretation — returned by /api/ai/jobs/{id}/results.</summary>
public sealed record AIAnalysisResultDto(
    Guid Id,
    string ModelName,
    string ModelVersion,
    int? PredictionClass,
    decimal Confidence,
    string Diagnosis,
    string Severity,
    List<string> Recommendations,
    List<ClassProbability> AllProbabilities,
    int ProcessingTimeMs,
    DateTime PerformedAt,
    string PerformedByUserName);

/// <summary>One (class name, probability) tuple in an AI prediction distribution.</summary>
public sealed record ClassProbability(string ClassName, decimal Probability);
