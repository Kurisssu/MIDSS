using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sitim.Core.Entities;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;

namespace Sitim.Infrastructure.Services;

public class AIModelSelectorService : IAIModelSelectorService
{
    private readonly AppDbContext _context;
    private readonly ILogger<AIModelSelectorService> _logger;

    public AIModelSelectorService(AppDbContext context, ILogger<AIModelSelectorService> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<List<AIModel>> GetModelsByModalityAsync(
        string modality,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modality))
        {
            _logger.LogWarning("GetModelsByModality called with empty modality");
            return new List<AIModel>();
        }

        var upperModality = modality.ToUpper().Trim();

        // Query: Get all active models where:
        // 1. TargetModality is not null AND contains this modality, OR
        // 2. TargetModality is null (legacy models - no filtering)
        var models = await _context.AIModels
            .Where(m => m.IsActive)
            .Where(m => string.IsNullOrWhiteSpace(m.TargetModality) ||  // Legacy models (accept all)
                        m.TargetModality.Contains(upperModality))       // Or explicitly supports this modality
            .OrderByDescending(m => m.Accuracy ?? 0)
            .ToListAsync(cancellationToken);

        _logger.LogInformation(
            "Found {Count} active models for modality {Modality}. Query considered: " +
            "NULL TargetModality (legacy) OR containing '{Modality}'",
            models.Count,
            upperModality,
            upperModality);

        return models;
    }

    public async Task<AIModel?> GetBestModelForModalityAsync(
        string modality,
        CancellationToken cancellationToken = default)
    {
        var models = await GetModelsByModalityAsync(modality, cancellationToken);
        return models.FirstOrDefault();
    }

    public bool IsModelCompatibleWithModality(AIModel model, string modality)
    {
        if (model == null || string.IsNullOrWhiteSpace(modality))
            return false;

        // If TargetModality is null, model accepts all modalities (legacy compatibility)
        if (string.IsNullOrWhiteSpace(model.TargetModality))
            return true;

        // Otherwise, check if TargetModality contains this modality
        var upperModality = modality.ToUpper().Trim();
        return model.TargetModality.Contains(upperModality);
    }
}
