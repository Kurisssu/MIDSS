using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sitim.Core.Services;
using Sitim.Api.Security;

namespace Sitim.Api.Controllers
{
    [Authorize(Roles = SitimRoles.AnyStaff)]
    [ApiController]
    [Route("api/local/studies")]
    public sealed class LocalStudiesController : ControllerBase
    {
        private readonly IStudyCacheService _cache;

        public LocalStudiesController(IStudyCacheService cache)
        {
            _cache = cache;
        }
        /// <summary>
        /// Returns studies that are already stored in PostgreSQL (no calls to Orthanc).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct)
            => Ok(await _cache.ListLocalAsync(ct));
        /// <summary>
        /// Returns a single study from PostgreSQL (no calls to Orthanc).
        /// </summary>
        [HttpGet("{orthancStudyId}")]
        public async Task<IActionResult> Get(string orthancStudyId, CancellationToken ct)
        {
            var s = await _cache.GetLocalAsync(orthancStudyId, ct);
            return s is null ? NotFound() : Ok(s);
        }
        /// <summary>
        /// Fetches the latest details from Orthanc and upserts into PostgreSQL.
        /// Use this after you upload/import a new study into Orthanc.
        /// </summary>
        [HttpGet("{orthancStudyId}/sync")]
        public async Task<IActionResult> Sync(string orthancStudyId, CancellationToken ct)
            => Ok(await _cache.SyncFromOrthancAsync(orthancStudyId, ct));
        /// <summary>
        /// Convenience endpoint: sync all studies from Orthanc into DB.
        /// </summary>
        [HttpPost("sync-all")]
        public async Task<IActionResult> SyncAll(CancellationToken ct)
        {
            var count = await _cache.SyncAllFromOrthancAsync(ct);
            return Ok(new { synced = count });
        }

        /// <summary>
        /// Permanently deletes a study from Orthanc and from the local DB.
        /// Also removes the patient record if it has no remaining studies.
        /// </summary>
        [HttpDelete("{orthancStudyId}")]
        [Authorize(Roles = "SuperAdmin,Admin")]
        public async Task<IActionResult> Delete(string orthancStudyId, CancellationToken ct)
        {
            var deleted = await _cache.DeleteStudyAsync(orthancStudyId, ct);
            return deleted ? NoContent() : NotFound();
        }
    }
}
