using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sitim.Api.Services;
using Sitim.Core.Contracts.AI;
using Sitim.Core.Entities;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using Sitim.Infrastructure.Services;
using System.Security.Claims;
using System.Text.Json;

namespace Sitim.Api.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class AIAnalysisController : ControllerBase
{
    private readonly AIAnalysisJobScheduler _scheduler;
    private readonly IAIInferenceService _inferenceService;
    private readonly IAIModelSelectorService _modelSelector;
    private readonly IModelStorageService _modelStorage;
    private readonly AppDbContext _context;
    private readonly ILogger<AIAnalysisController> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public AIAnalysisController(
        AIAnalysisJobScheduler scheduler,
        IAIInferenceService inferenceService,
        IAIModelSelectorService modelSelector,
        IModelStorageService modelStorage,
        AppDbContext context,
        ILogger<AIAnalysisController> logger,
        IHttpContextAccessor httpContextAccessor)
    {
        _scheduler = scheduler;
        _inferenceService = inferenceService;
        _modelSelector = modelSelector;
        _modelStorage = modelStorage;
        _context = context;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// Start an AI analysis job (runs in background via Hangfire).
    /// Returns job ID for polling job status.
    /// </summary>
    [HttpPost("analyze")]
    public async Task<ActionResult<StartAnalysisResponseDto>> AnalyzeStudy(
        [FromBody] AnalyzeStudyRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation(
                "Received AI analysis request for study {StudyId}",
                request.StudyId);

            // Get current user ID from claims
            var userIdClaim = _httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
                throw new InvalidOperationException("User not authenticated");

            // Get study
            var study = await _context.ImagingStudies.FirstOrDefaultAsync(s => s.Id == request.StudyId, cancellationToken);
            if (study == null)
                return NotFound(new { error = "Study not found" });

            // Get user for name
            var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
            var userName = user?.UserName ?? "Unknown";

            // Create job record with Status="Queued"
            var job = new AIAnalysisJob
            {
                Id = Guid.NewGuid(),
                StudyId = request.StudyId,
                ModelId = request.ModelId ?? Guid.Empty,
                PerformedByUserId = userId,
                PerformedByUserName = userName,
                Status = "Queued",
                CreatedAtUtc = DateTime.UtcNow
            };

            _context.AIAnalysisJobs.Add(job);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created AI analysis job {JobId} with status Queued", job.Id);

            // Enqueue background job via Hangfire
            await _scheduler.EnqueueAsync(job.Id, cancellationToken);

            _logger.LogInformation("Enqueued AI analysis job {JobId} for Hangfire execution", job.Id);

            // Return job ID + status (client will poll for results)
            return Accepted(new StartAnalysisResponseDto(
                JobId: job.Id,
                Status: "Queued",
                CreatedAt: job.CreatedAtUtc
            ));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Invalid analysis request");
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start AI analysis for study {StudyId}", request.StudyId);
            return StatusCode(500, new { error = "Failed to start analysis. Please try again later." });
        }
    }

    /// <summary>
    /// List analysis jobs visible in current tenant context.
    /// Includes both running and completed analyses.
    /// </summary>
    [HttpGet("jobs")]
    public async Task<ActionResult<List<AIAnalysisJobListItemDto>>> GetJobs([FromQuery] int limit = 100, CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        // Join with ImagingStudies (which has tenant query filter) to enforce tenant isolation.
        var jobs = await (
            from job in _context.AIAnalysisJobs
            join study in _context.ImagingStudies on job.StudyId equals study.Id
            join patient in _context.Patients on study.PatientDbId equals patient.Id into patientJoin
            from patient in patientJoin.DefaultIfEmpty()
            join model in _context.AIModels on job.ModelId equals model.Id into modelJoin
            from model in modelJoin.DefaultIfEmpty()
            orderby job.CreatedAtUtc descending
            select new AIAnalysisJobListItemDto(
                job.Id,
                job.StudyId,
                study.OrthancStudyId,
                patient != null ? patient.PatientName : null,
                study.StudyDate,
                study.ModalitiesInStudy ?? Array.Empty<string>(),
                job.Status,
                job.CreatedAtUtc,
                job.StartedAtUtc,
                job.FinishedAtUtc,
                model != null ? model.Name : null,
                job.PredictionClass,
                job.Confidence,
                job.ProcessingTimeMs,
                job.ErrorMessage
            )
        )
        .Take(limit)
        .ToListAsync(cancellationToken);

        return Ok(jobs);
    }

    /// <summary>
    /// List analysis jobs for one study.
    /// Running/queued jobs are returned first, followed by completed/failed.
    /// </summary>
    [HttpGet("studies/{studyId:guid}/jobs")]
    public async Task<ActionResult<List<AIAnalysisJobListItemDto>>> GetStudyJobs(Guid studyId, CancellationToken cancellationToken = default)
    {
        var studyExists = await _context.ImagingStudies.AnyAsync(s => s.Id == studyId, cancellationToken);
        if (!studyExists)
            return NotFound(new { error = "Study not found" });

        var jobs = await (
            from job in _context.AIAnalysisJobs
            join study in _context.ImagingStudies on job.StudyId equals study.Id
            join patient in _context.Patients on study.PatientDbId equals patient.Id into patientJoin
            from patient in patientJoin.DefaultIfEmpty()
            join model in _context.AIModels on job.ModelId equals model.Id into modelJoin
            from model in modelJoin.DefaultIfEmpty()
            where job.StudyId == studyId
            select new AIAnalysisJobListItemDto(
                job.Id,
                job.StudyId,
                study.OrthancStudyId,
                patient != null ? patient.PatientName : null,
                study.StudyDate,
                study.ModalitiesInStudy ?? Array.Empty<string>(),
                job.Status,
                job.CreatedAtUtc,
                job.StartedAtUtc,
                job.FinishedAtUtc,
                model != null ? model.Name : null,
                job.PredictionClass,
                job.Confidence,
                job.ProcessingTimeMs,
                job.ErrorMessage
            )
        ).ToListAsync(cancellationToken);

        var ordered = jobs
            .OrderBy(j => j.Status switch
            {
                "Running" => 0,
                "Queued" => 1,
                "Completed" => 2,
                "Failed" => 3,
                _ => 4
            })
            .ThenByDescending(j => j.CreatedAt)
            .ToList();

        return Ok(ordered);
    }

    /// <summary>
    /// Get AI analysis job status and results (for polling)
    /// </summary>
    [HttpGet("jobs/{jobId:guid}")]
    public async Task<ActionResult<AIAnalysisJobStatusDto>> GetJobStatus(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.AIAnalysisJobs
            .Include(j => j.Model)
            .Include(j => j.Study)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return NotFound(new { error = "Job not found" });

        if (job.Study == null)
            return NotFound(new { error = "Job not found" });

        return Ok(new AIAnalysisJobStatusDto(
            Id: job.Id,
            StudyId: job.StudyId,
            OrthancStudyId: job.Study.OrthancStudyId,
            Status: job.Status,
            CreatedAt: job.CreatedAtUtc,
            StartedAt: job.StartedAtUtc,
            FinishedAt: job.FinishedAtUtc,
            ModelName: job.Model?.Name,
            PredictionClass: job.PredictionClass,
            Confidence: job.Confidence,
            ProcessingTimeMs: job.ProcessingTimeMs,
            ErrorMessage: job.ErrorMessage
        ));
    }

    /// <summary>
    /// Get full analysis results with diagnosis, severity, and recommendations.
    /// Used after job completion to display comprehensive results to user.
    /// </summary>
    [HttpGet("jobs/{jobId:guid}/results")]
    public async Task<ActionResult<AIAnalysisResultDto>> GetAnalysisResults(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.AIAnalysisJobs
            .Include(j => j.Model)
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return NotFound(new { error = "Job not found" });

        var canAccessStudy = await _context.ImagingStudies
            .AnyAsync(s => s.Id == job.StudyId, cancellationToken);
        if (!canAccessStudy)
            return NotFound(new { error = "Job not found" });

        if (job.Status != "Completed")
            return BadRequest(new { error = "Job not completed yet", status = job.Status });

        var result = await MapToDto(job);
        return Ok(result);
    }

    /// <summary>
    /// Cancel a running analysis job.
    /// Only works for jobs in Queued or Running status.
    /// </summary>
    [HttpPost("jobs/{jobId:guid}/cancel")]
    public async Task<ActionResult<object>> CancelAnalysisJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.AIAnalysisJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return NotFound(new { error = "Job not found" });

        var canAccessStudy = await _context.ImagingStudies
            .AnyAsync(s => s.Id == job.StudyId, cancellationToken);
        if (!canAccessStudy)
            return NotFound(new { error = "Job not found" });

        if (job.Status == "Completed" || job.Status == "Failed")
            return BadRequest(new { error = "Cannot cancel completed or failed job", status = job.Status });

        // Update job status to Failed
        job.Status = "Failed";
        job.ErrorMessage = "Cancelled by user";
        job.FinishedAtUtc = DateTime.UtcNow;

        _context.AIAnalysisJobs.Update(job);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Analysis job {JobId} cancelled by user", jobId);

        return Ok(new { message = "Job cancelled", jobId, status = "Failed" });
    }

    /// <summary>
    /// Delete a completed analysis job record.
    /// Only works for jobs that are Completed or Failed.
    /// </summary>
    [HttpDelete("jobs/{jobId:guid}")]
    public async Task<ActionResult<object>> DeleteAnalysisJob(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await _context.AIAnalysisJobs
            .FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);

        if (job == null)
            return NotFound(new { error = "Job not found" });

        var canAccessStudy = await _context.ImagingStudies
            .AnyAsync(s => s.Id == job.StudyId, cancellationToken);
        if (!canAccessStudy)
            return NotFound(new { error = "Job not found" });

        if (job.Status != "Completed" && job.Status != "Failed")
            return BadRequest(new { error = "Cannot delete running or queued job", status = job.Status });

        _context.AIAnalysisJobs.Remove(job);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Analysis job {JobId} deleted", jobId);

        return Ok(new { message = "Job deleted", jobId });
    }

    /// <summary>
    /// Get AI models compatible with a study's modality.
    /// System detects DICOM modality and returns filtered models.
    /// </summary>
    [HttpGet("models-for-study/{studyId:guid}")]
    public async Task<ActionResult<List<AIModelSelectionDto>>> GetModelsForStudy(
        Guid studyId,
        CancellationToken cancellationToken)
    {
        try
        {
            // Get study
            var study = await _context.ImagingStudies
                .FirstOrDefaultAsync(s => s.Id == studyId, cancellationToken);
            
            if (study == null)
                return NotFound(new { error = "Study not found" });

            // Try to detect modality from study metadata
            // ModalitiesInStudy is string[] array
            if (study.ModalitiesInStudy == null || study.ModalitiesInStudy.Length == 0)
            {
                _logger.LogWarning("Study {StudyId} has no modality information", studyId);
                return BadRequest(new { error = "Study has no modality information" });
            }

            var validModalities = study.ModalitiesInStudy
                .Select(m => m?.Trim().ToUpperInvariant())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .Where(ModalityDetector.IsValidModality)
                .ToList();

            if (!validModalities.Any())
            {
                _logger.LogWarning(
                    "Study {StudyId} has no supported modalities for model matching. Modalities: {Modalities}",
                    studyId,
                    string.Join(", ", study.ModalitiesInStudy));
                return BadRequest(new { error = "No AI models available for this study modality set" });
            }

            // Collect compatible models for all modalities found in study.
            var modelMap = new Dictionary<Guid, AIModel>();
            foreach (var modality in validModalities)
            {
                var modalityModels = await _modelSelector.GetModelsByModalityAsync(modality!, cancellationToken);
                foreach (var model in modalityModels)
                {
                    if (!modelMap.TryGetValue(model.Id, out var existing) ||
                        (model.Accuracy ?? 0) > (existing.Accuracy ?? 0))
                    {
                        modelMap[model.Id] = model;
                    }
                }
            }

            var models = modelMap.Values
                .OrderByDescending(m => m.Accuracy ?? 0)
                .ToList();

            if (!models.Any())
            {
                _logger.LogInformation(
                    "No models found for study {StudyId} across modalities {Modalities}",
                    studyId,
                    string.Join(", ", validModalities));
                return Ok(new List<AIModelSelectionDto>()); // Return empty list
            }

            // Map to DTO
            var dtos = models.Select(m => new AIModelSelectionDto(
                Id: m.Id,
                Name: m.Name,
                Version: m.Version,
                Task: m.Task,
                Accuracy: m.Accuracy,
                TargetModality: m.TargetModality,
                Description: m.Description
            )).ToList();

            _logger.LogInformation(
                "Found {Count} models for study {StudyId} (modalities: {Modalities})",
                dtos.Count,
                studyId,
                string.Join(", ", validModalities));

            return Ok(dtos);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching models for study {StudyId}", studyId);
            return StatusCode(500, new { error = "Failed to fetch models" });
        }
    }

    /// <summary>
    /// Get analysis history for a study
    /// </summary>
    [HttpGet("studies/{studyId:guid}/history")]
    public async Task<ActionResult<List<AIAnalysisResultDto>>> GetStudyHistory(Guid studyId)
    {
        var results = await _inferenceService.GetStudyAnalysisHistoryAsync(studyId);
        var dtos = new List<AIAnalysisResultDto>();

        foreach (var result in results)
        {
            dtos.Add(await MapToDto(result));
        }

        return Ok(dtos);
    }

    /// <summary>
    /// Get available AI models
    /// </summary>
    [HttpGet("models")]
    public async Task<ActionResult<List<AIModelDto>>> GetModels()
    {
        var models = await _context.AIModels
            .OrderByDescending(m => m.IsActive)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => new AIModelDto(
                m.Id,
                m.Name,
                m.Description,
                m.Task,
                m.Version,
                m.StorageFileName,
                m.Accuracy,
                m.IsActive,
                m.NumClasses,
                m.InputShape,
                m.TrainingSource,
                m.CreatedAt,
                m.TargetModality,
                m.ClassNames,
                m.ClassSeverities,
                m.ClassRecommendations,
                m.SupportedRegions,
                m.DetectablePathologies,
                m.PreprocessingMethod,
                m.PreprocessingMean,
                m.PreprocessingStd,
                m.PreprocessingImageSize,
                m.OnnxInputSpec,
                m.OnnxOutputSpec
            ))
            .ToListAsync();

        return Ok(models);
    }

    /// <summary>
    /// Get active model for a specific task
    /// </summary>
    [HttpGet("models/active/{task}")]
    public async Task<ActionResult<AIModelDto>> GetActiveModel(string task)
    {
        var model = await _context.AIModels
            .Where(m => m.Task == task && m.IsActive)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        if (model == null)
            return NotFound(new { error = $"No active model found for task: {task}" });

        return Ok(new AIModelDto(
            model.Id,
            model.Name,
            model.Description,
            model.Task,
            model.Version,
            model.StorageFileName,
            model.Accuracy,
            model.IsActive,
            model.NumClasses,
            model.InputShape,
            model.TrainingSource,
            model.CreatedAt,
            model.TargetModality,
            model.ClassNames,
            model.ClassSeverities,
            model.ClassRecommendations,
            model.SupportedRegions,
            model.DetectablePathologies,
            model.PreprocessingMethod,
            model.PreprocessingMean,
            model.PreprocessingStd,
            model.PreprocessingImageSize,
            model.OnnxInputSpec,
            model.OnnxOutputSpec
        ));
    }

    /// <summary>
    /// Upload a new AI model (Admin/SuperAdmin only)
    /// </summary>
    [HttpPost("models/upload")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    [RequestSizeLimit(500_000_000)] // 500 MB limit
    public async Task<ActionResult<AIModelDto>> UploadModel([FromForm] UploadModelRequest request)
    {
        if (request.ModelFile == null || request.ModelFile.Length == 0)
            return BadRequest(new { error = "Model file is required" });

        if (!request.ModelFile.FileName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only ONNX model files are supported" });

        try
        {
            // Validate uniqueness
            var existingModel = await _context.AIModels
                .FirstOrDefaultAsync(m => m.Task == request.Task && m.Version == request.Version);

            if (existingModel != null)
                return BadRequest(new { error = $"Model with task '{request.Task}' and version '{request.Version}' already exists" });

            // Generate unique filename
            var fileExtension = Path.GetExtension(request.ModelFile.FileName);
            var sanitizedName = string.Join("_", request.Name.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"{sanitizedName}_v{request.Version}_{Guid.NewGuid():N}{fileExtension}";

            // Upload to MinIO
            _logger.LogInformation("Uploading model file {FileName} ({Size} bytes)", fileName, request.ModelFile.Length);
            
            using var stream = request.ModelFile.OpenReadStream();
            var storageUrl = await _modelStorage.UploadModelAsync(
                fileName,
                stream,
                "application/octet-stream"
            );

            // Create database record
            var model = new AIModel
            {
                Id = Guid.NewGuid(),
                Name = request.Name,
                Description = request.Description,
                Task = request.Task,
                Version = request.Version,
                StorageFileName = fileName,
                Accuracy = request.Accuracy,
                IsActive = request.IsActive,
                NumClasses = request.NumClasses,
                InputShape = request.InputShape,
                TrainingSource = request.TrainingSource,
                
                // Clinical metadata
                TargetModality = request.TargetModality,
                SupportedRegions = request.SupportedRegions,
                DetectablePathologies = request.DetectablePathologies,
                ClassNames = request.ClassNames,
                ClassSeverities = request.ClassSeverities,
                ClassRecommendations = request.ClassRecommendations,
                
                // Preprocessing configuration
                PreprocessingMethod = request.PreprocessingMethod,
                PreprocessingMean = request.PreprocessingMean,
                PreprocessingStd = request.PreprocessingStd,
                PreprocessingImageSize = request.PreprocessingImageSize,
                
                // ONNX specifications
                OnnxInputSpec = request.OnnxInputSpec,
                OnnxOutputSpec = request.OnnxOutputSpec,
                
                CreatedAt = DateTime.UtcNow
            };

            _context.AIModels.Add(model);
            await _context.SaveChangesAsync();

            _logger.LogInformation(
                "AI model uploaded successfully: {ModelName} v{Version} (Task: {Task})",
                model.Name, model.Version, model.Task
            );

            return CreatedAtAction(
                nameof(GetModels),
                new { id = model.Id },
                new AIModelDto(
                    model.Id,
                    model.Name,
                    model.Description,
                    model.Task,
                    model.Version,
                    model.StorageFileName,
                    model.Accuracy,
                    model.IsActive,
                    model.NumClasses,
                    model.InputShape,
                    model.TrainingSource,
                    model.CreatedAt,
                    model.TargetModality,
                    model.ClassNames,
                    model.ClassSeverities,
                    model.ClassRecommendations,
                    model.SupportedRegions,
                    model.DetectablePathologies,
                    model.PreprocessingMethod,
                    model.PreprocessingMean,
                    model.PreprocessingStd,
                    model.PreprocessingImageSize,
                    model.OnnxInputSpec,
                    model.OnnxOutputSpec
                )
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload AI model");
            return StatusCode(500, new { error = "Failed to upload model. Please try again." });
        }
    }

    /// <summary>
    /// Update a model's interpretation/clinical metadata (Admin/SuperAdmin only).
    /// Lets an admin set or correct the per-class names, severities and clinical
    /// recommendations used to render analysis results — including for pre-loaded
    /// models that were imported without this metadata. The ONNX file is untouched.
    /// </summary>
    [HttpPut("models/{id:guid}/metadata")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult<AIModelDto>> UpdateModelMetadata(
        Guid id,
        [FromBody] UpdateModelMetadataRequest request,
        CancellationToken cancellationToken)
    {
        var model = await _context.AIModels.FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (model == null)
            return NotFound(new { error = "Model not found" });

        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });

        // Per-class arrays must line up by index so result interpretation stays consistent.
        var classCount = request.ClassNames?.Length ?? 0;
        if (classCount > 0)
        {
            if (request.ClassSeverities is { Length: var sev } && sev != classCount)
                return BadRequest(new { error = "ClassSeverities length must match ClassNames length." });
            if (request.ClassRecommendations is { Length: var rec } && rec != classCount)
                return BadRequest(new { error = "ClassRecommendations length must match ClassNames length." });
        }

        model.Name = request.Name.Trim();
        model.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        model.TargetModality = string.IsNullOrWhiteSpace(request.TargetModality) ? null : request.TargetModality.Trim();

        model.ClassNames = classCount > 0 ? JsonSerializer.Serialize(request.ClassNames) : null;
        model.ClassSeverities = request.ClassSeverities is { Length: > 0 }
            ? JsonSerializer.Serialize(request.ClassSeverities) : null;
        model.ClassRecommendations = request.ClassRecommendations is { Length: > 0 }
            ? JsonSerializer.Serialize(request.ClassRecommendations) : null;

        if (classCount > 0)
        {
            model.NumClasses = classCount;
            model.NumOutputClasses = classCount;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Model {ModelId} metadata updated ({Classes} classes) by admin", id, classCount);

        return Ok(new AIModelDto(
            model.Id,
            model.Name,
            model.Description,
            model.Task,
            model.Version,
            model.StorageFileName,
            model.Accuracy,
            model.IsActive,
            model.NumClasses,
            model.InputShape,
            model.TrainingSource,
            model.CreatedAt,
            model.TargetModality,
            model.ClassNames,
            model.ClassSeverities,
            model.ClassRecommendations,
            model.SupportedRegions,
            model.DetectablePathologies,
            model.PreprocessingMethod,
            model.PreprocessingMean,
            model.PreprocessingStd,
            model.PreprocessingImageSize,
            model.OnnxInputSpec,
            model.OnnxOutputSpec));
    }

    /// <summary>
    /// Toggle model active status (Admin/SuperAdmin only)
    /// </summary>
    [HttpPatch("models/{id:guid}/toggle")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult> ToggleModelStatus(Guid id)
    {
        var model = await _context.AIModels.FindAsync(id);
        if (model == null)
            return NotFound(new { error = "Model not found" });

        model.IsActive = !model.IsActive;
        await _context.SaveChangesAsync();

        _logger.LogInformation("Model {ModelId} status toggled to {IsActive}", id, model.IsActive);
        return Ok(new { isActive = model.IsActive });
    }

    /// <summary>
    /// Delete a model (Admin/SuperAdmin only)
    /// </summary>
    [HttpDelete("models/{id:guid}")]
    [Authorize(Roles = "SuperAdmin,Admin")]
    public async Task<ActionResult> DeleteModel(Guid id)
    {
        var model = await _context.AIModels.FindAsync(id);
        if (model == null)
            return NotFound(new { error = "Model not found" });

        // Check if model is being used
        var hasAnalyses = await _context.AIAnalysisJobs.AnyAsync(r => r.ModelId == id);
        if (hasAnalyses)
            return BadRequest(new { error = "Cannot delete model that has been used for analyses" });

        try
        {
            // Delete from storage
            await _modelStorage.DeleteModelAsync(model.StorageFileName);

            // Delete from database
            _context.AIModels.Remove(model);
            await _context.SaveChangesAsync();

            _logger.LogInformation("Model {ModelId} deleted successfully", id);
            return NoContent();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete model {ModelId}", id);
            return StatusCode(500, new { error = "Failed to delete model" });
        }
    }

    private async Task<AIAnalysisResultDto> MapToDto(Core.Entities.AIAnalysisJob result)
    {
        // Load Model relationship if not already loaded
        if (result.Model == null)
        {
            await _context.Entry(result)
                .Reference(r => r.Model)
                .LoadAsync();
        }

        // Get username
        var userName = result.PerformedByUserName ?? 
            await _context.Users
                .Where(u => u.Id == result.PerformedByUserId)
                .Select(u => u.UserName)
                .FirstOrDefaultAsync() ?? "Unknown";

        // Parse probabilities
        var probabilities = result.Probabilities != null
            ? JsonSerializer.Deserialize<float[]>(result.Probabilities) ?? Array.Empty<float>()
            : Array.Empty<float>();

        // Get class names from database metadata
        var classNames = result.Model?.ClassNames != null
            ? JsonSerializer.Deserialize<string[]>(result.Model.ClassNames) ?? Array.Empty<string>()
            : Array.Empty<string>();

        // If no metadata, fallback to generic names (backward compatible)
        if (classNames.Length == 0)
        {
            classNames = Enumerable.Range(0, Math.Max(probabilities.Length, 1))
                .Select(i => $"Class {i}")
                .ToArray();
        }

        // Generic probability mapping (works for ANY model)
        var allProbabilities = probabilities
            .Select((prob, idx) => new ClassProbability(
                classNames.ElementAtOrDefault(idx) ?? $"Class {idx}",
                (decimal)prob
            ))
            .ToList();

        // Get diagnosis and severity from database
        var classSeverities = result.Model?.ClassSeverities != null
            ? JsonSerializer.Deserialize<string[]>(result.Model.ClassSeverities) ?? Array.Empty<string>()
            : Array.Empty<string>();

        var diagnosis = classNames.ElementAtOrDefault(result.PredictionClass ?? -1) ?? "Unknown Diagnosis";
        var severity = classSeverities.ElementAtOrDefault(result.PredictionClass ?? -1) ?? "Unknown";

        // Get recommendations from database
        var classRecommendations = result.Model?.ClassRecommendations != null
            ? JsonSerializer.Deserialize<string[][]>(result.Model.ClassRecommendations) ?? Array.Empty<string[]>()
            : Array.Empty<string[]>();

        var recommendations = (result.PredictionClass != null && 
                              result.PredictionClass >= 0 && 
                              result.PredictionClass < classRecommendations.Length)
            ? classRecommendations[result.PredictionClass.Value].ToList()
            : new List<string> { "No specific recommendations available" };

        return new AIAnalysisResultDto(
            result.Id,
            result.Model?.Name ?? "Unknown Model",
            result.Model?.Version ?? "v1",
            result.PredictionClass,
            result.Confidence ?? 0,
            diagnosis,
            severity,
            recommendations,
            allProbabilities,
            result.ProcessingTimeMs ?? 0,
            result.FinishedAtUtc ?? result.CreatedAtUtc,
            userName
        );
    }
}

public record UploadModelRequest
{
    public IFormFile ModelFile { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Task { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public decimal? Accuracy { get; set; }
    public bool IsActive { get; set; } = true;
    public int? NumClasses { get; set; }
    public string? InputShape { get; set; }
    public string? TrainingSource { get; set; }
    
    // Modality filtering
    public string? TargetModality { get; set; }
    
    // Clinical metadata
    public string? SupportedRegions { get; set; }
    public string? DetectablePathologies { get; set; }
    
    // Result interpretation (JSON arrays)
    public string? ClassNames { get; set; }
    public string? ClassSeverities { get; set; }
    public string? ClassRecommendations { get; set; }
    
    // Preprocessing configuration (JSON format for arrays)
    public string? PreprocessingMethod { get; set; }
    public string? PreprocessingMean { get; set; }
    public string? PreprocessingStd { get; set; }
    public int? PreprocessingImageSize { get; set; }
    
    // ONNX specifications (JSON format)
    public string? OnnxInputSpec { get; set; }
    public string? OnnxOutputSpec { get; set; }
    
    // Output configuration
    public int? NumOutputClasses { get; set; }
}
