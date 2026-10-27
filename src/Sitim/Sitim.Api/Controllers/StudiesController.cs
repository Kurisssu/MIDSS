using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Sitim.Api.Options;
using Sitim.Core.Models;
using Sitim.Core.Services;
using Sitim.Api.Security;
using Microsoft.AspNetCore.Authorization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Sitim.Api.Controllers
{
    [Authorize(Roles = SitimRoles.AnyStaff)]
    [ApiController]
    [Route("api/studies")]
    public sealed class StudiesController : ControllerBase
    {
        private const string PacsUnavailableMessage = "The PACS server (Orthanc) is currently unavailable.";

        private readonly IOrthancClientFactory _orthancFactory;
        private readonly OhifOptions _ohif;
        private readonly IStudyCacheService _cache;
        private readonly IViewerTokenService _viewerTokenService;
        private readonly ITenantContext _tenantContext;

        public StudiesController(
            IOrthancClientFactory orthancFactory,
            IOptions<OhifOptions> ohif,
            IStudyCacheService cache,
            IViewerTokenService viewerTokenService,
            ITenantContext tenantContext)
        {
            _orthancFactory = orthancFactory;
            _ohif = ohif.Value;
            _cache = cache;
            _viewerTokenService = viewerTokenService;
            _tenantContext = tenantContext;
        }

        /// <summary>
        /// MVP: returns a list of studies from Orthanc.
        /// For small thesis datasets, we also fetch minimal tags (1 call per study).
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<IReadOnlyList<StudySummary>>> GetStudies(CancellationToken ct)
        {
            var orthanc = await _orthancFactory.CreateClientForCurrentTenantAsync(ct);
            if (orthanc is null)
                return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });

            IReadOnlyList<string> ids;
            try
            {
                ids = await orthanc.GetStudyIdsAsync(ct);
            }
            catch (HttpRequestException)
            {
                return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });
            }

            // Fetch minimal details with limited parallelism (avoid hammering Orthanc)
            var results = new List<StudySummary>(ids.Count);
            var gate = new SemaphoreSlim(5);

            var tasks = ids.Select(async id =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    var d = await orthanc.GetStudyAsync(id, ct);
                    if (d is null) return null;

                    return new StudySummary(
                        OrthancStudyId: d.OrthancStudyId,
                        StudyInstanceUid: d.StudyInstanceUid,
                        PatientId: d.PatientId,
                        PatientName: d.PatientName,
                        StudyDate: d.StudyDate,
                        ModalitiesInStudy: d.ModalitiesInStudy
                    );
                }
                catch (HttpRequestException)
                {
                    return null;
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            var summaries = await Task.WhenAll(tasks);
            results.AddRange(summaries.Where(s => s is not null)!);

            results.Sort((a, b) => string.Compare(b.StudyDate, a.StudyDate, StringComparison.Ordinal));

            return Ok(results);
        }

        [HttpGet("{orthancStudyId}")]
        public async Task<ActionResult<StudyDetails>> GetStudy(string orthancStudyId, CancellationToken ct)
        {
            var orthanc = await _orthancFactory.CreateClientForCurrentTenantAsync(ct);
            if (orthanc is null)
                return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });

            OrthancStudyDetails? d;
            try
            {
                d = await orthanc.GetStudyAsync(orthancStudyId, ct);
            }
            catch (HttpRequestException)
            {
                return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });
            }

            if (d is null) return NotFound("Study not found in Orthanc.");

            return Ok(new StudyDetails(
                OrthancStudyId: d.OrthancStudyId,
                StudyInstanceUid: d.StudyInstanceUid,
                PatientId: d.PatientId,
                PatientName: d.PatientName,
                StudyDate: d.StudyDate,
                ModalitiesInStudy: d.ModalitiesInStudy,
                SeriesOrthancIds: d.SeriesOrthancIds
            ));
        }

        [HttpGet("{orthancStudyId}/viewer-link")]
        public async Task<ActionResult<object>> GetViewerLink(string orthancStudyId, CancellationToken ct)
        {
            // Use local cache first to enforce tenant scoping via query filters.
            var local = await _cache.GetLocalAsync(orthancStudyId, ct);
            if (local is null)
                return NotFound("Study not found.");

            var studyUid = local.StudyInstanceUid;
            if (string.IsNullOrWhiteSpace(studyUid))
            {
                // Try to refresh from Orthanc if DB record has missing UID.
                try
                {
                    var synced = await _cache.SyncFromOrthancAsync(orthancStudyId, ct);
                    studyUid = synced?.StudyInstanceUid;
                }
                catch (HttpRequestException)
                {
                    return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });
                }
            }

            if (string.IsNullOrWhiteSpace(studyUid))
                return Problem(statusCode: 500, title: "StudyInstanceUID is missing for this study.");

            var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!Guid.TryParse(userIdClaim, out var userId))
                return Unauthorized();

            // For SuperAdmin: use the study's InstitutionId (from cache which returns ImagingStudy entity)
            // For regular users: use tenant context InstitutionId
            // Get InstitutionId from the actual study record (GetLocalAsync returns StudyDetails, need DB entity)
            var studyEntity = await _cache.GetStudyEntityAsync(orthancStudyId, ct);
            if (studyEntity is null)
                return NotFound("Study not found.");

            var tokenInstitutionId = _tenantContext.IsSuperAdmin 
                ? studyEntity.InstitutionId 
                : _tenantContext.InstitutionId;

            var (viewerToken, _) = _viewerTokenService.CreateViewerToken(
                userId: userId,
                institutionId: tokenInstitutionId,
                studyInstanceUid: studyUid,
                isSuperAdmin: _tenantContext.IsSuperAdmin);

            var baseUrl = (_ohif.BaseUrl ?? "").TrimEnd('/');
            var uid = Uri.EscapeDataString(studyUid);
            var token = Uri.EscapeDataString(viewerToken);

            var url = $"{baseUrl}/viewer?StudyInstanceUIDs={uid}&viewerToken={token}";

            return Ok(new { url });
        }
    }
}
