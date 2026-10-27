using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sitim.Api.Security;
using Sitim.Core.Contracts.AI;
using Sitim.Infrastructure.Data;

namespace Sitim.Api.Controllers;

[Authorize(Roles = SitimRoles.AnyStaff)]
[ApiController]
[Route("api/models")]
public sealed class ModelsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ModelsController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Simple model registry endpoint for all AI models (uploaded + FL-published).
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<ModelDefinitionDto>>> List(
        [FromQuery] bool activeOnly = false,
        [FromQuery] string? task = null,
        [FromQuery] string? modality = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<Sitim.Core.Entities.AIModel> query = _db.AIModels.AsNoTracking();

        if (activeOnly)
            query = query.Where(m => m.IsActive);

        if (!string.IsNullOrWhiteSpace(task))
        {
            var taskFilter = task.Trim();
            query = query.Where(m => m.Task == taskFilter);
        }

        if (!string.IsNullOrWhiteSpace(modality))
        {
            var modalityFilter = modality.Trim().ToUpperInvariant();
            query = query.Where(m =>
                !string.IsNullOrWhiteSpace(m.TargetModality) &&
                m.TargetModality!.ToUpper().Contains(modalityFilter));
        }

        var models = await query
            .OrderByDescending(m => m.IsActive)
            .ThenByDescending(m => m.CreatedAt)
            .Select(m => new ModelDefinitionDto(
                m.Id,
                m.Name,
                m.Task,
                m.Version,
                m.IsActive,
                m.StorageFileName,
                m.Accuracy,
                m.TrainingSource,
                m.TargetModality,
                m.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(models);
    }
}

