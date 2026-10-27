using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using System.IO.Compression;
using Sitim.Api.Security;

namespace Sitim.Api.Controllers
{
    /// <summary>
    /// MVP ingest endpoint:
    /// - accept one ZIP archive OR multiple DICOM files
    /// - upload instances into Orthanc (POST /instances)
    /// - apply institution label to studies in Orthanc
    /// - sync parent studies into our local PostgreSQL cache
    ///
    /// Why this exists:
    /// - In later steps, Blazor UI will upload through this API, not through Orthanc UI.
    /// </summary>
    [Authorize(Roles = SitimRoles.CanImport)]
    [ApiController]
    [Route("api/studies")]
    public sealed class StudiesImportController : ControllerBase
    {
        private const string PacsUnavailableMessage = "The PACS server (Orthanc) is currently unavailable.";

        private readonly IOrthancClientFactory _orthancFactory;
        private readonly IStudyCacheService _cache;
        private readonly AppDbContext _db;
        private readonly ITenantContext _tenantContext;

        public StudiesImportController(
            IOrthancClientFactory orthancFactory,
            IStudyCacheService cache,
            AppDbContext db,
            ITenantContext tenantContext)
        {
            _orthancFactory = orthancFactory;
            _cache = cache;
            _db = db;
            _tenantContext = tenantContext;
        }

        public sealed class ImportRequest
        {
            /// <summary>
            /// Upload a ZIP that contains multiple DICOM instances (recommended for a whole study export).
            /// </summary>
            public IFormFile? Archive { get; set; }
            /// <summary>
            /// Upload one or more DICOM instances directly (files).
            /// </summary>
            public List<IFormFile>? Files { get; set; }
        }

        public sealed record ImportResponse(
            int UploadedInstances,
            IReadOnlyList<string> OrthancStudyIds,
            int SyncedStudies,
            IReadOnlyList<string> Errors
        );
        /// <remarks>
        /// Orthanc can ingest DICOM instances through POST /instances (also works for ZIP uploads).
        /// In API we unpack ZIP ourselves to be more robust and to be able to report per-file errors.
        /// </remarks>
        [HttpPost("import")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = 1024L * 1024 * 1024)] // 1GB (for DEV/MVP)
        public async Task<ActionResult<ImportResponse>> Import([FromForm] ImportRequest req, CancellationToken ct)
        {
            if ((req.Files == null || req.Files.Count == 0) && req.Archive == null)
                return BadRequest("Provide either 'Archive' (zip) or one or more 'Files' (dicom).");

            var orthanc = await _orthancFactory.CreateClientForCurrentTenantAsync(ct);
            if (orthanc is null)
                return StatusCode(503, new { error = "Orthanc unavailable", message = PacsUnavailableMessage });

            var errors = new List<string>();
            var parentStudies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uploadedInstances = 0;

            // 1) ZIP archive path (contains multiple instances)
            if (req.Archive != null && req.Archive.Length > 0)
            {
                if (!req.Archive.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return BadRequest("Archive must be a .zip file.");

                await using var zipStream = req.Archive.OpenReadStream();
                using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

                foreach (var entry in zip.Entries)
                {
                    if (ct.IsCancellationRequested) break;

                    // skip folders
                    if (string.IsNullOrWhiteSpace(entry.Name))
                        continue;

                    // quick filter: ignore common non-dicom files
                    var lower = entry.Name.ToLowerInvariant();
                    if (lower.EndsWith(".txt") || lower.EndsWith(".json") || lower.EndsWith(".xml"))
                        continue;

                    try
                    {
                        await using var entryStream = entry.Open();
                        var result = await orthanc.UploadInstanceAsync(entryStream, ct);
                        uploadedInstances++;

                        if (!string.IsNullOrWhiteSpace(result.ParentStudy))
                            parentStudies.Add(result.ParentStudy!);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"ZIP entry '{entry.FullName}': {ex.Message}");
                    }
                }
            }

            // 2) Direct DICOM files
            if (req.Files != null)
            {
                foreach (var f in req.Files.Where(f => f != null && f.Length > 0))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await using var s = f.OpenReadStream();
                        var result = await orthanc.UploadInstanceAsync(s, ct);
                        uploadedInstances++;

                        if (!string.IsNullOrWhiteSpace(result.ParentStudy))
                            parentStudies.Add(result.ParentStudy!);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"File '{f.FileName}': {ex.Message}");
                    }
                }
            }

            // 3) Sync uploaded studies into our local DB cache
            var syncedStudies = 0;
            foreach (var studyId in parentStudies)
            {
                try
                {
                    await _cache.SyncFromOrthancAsync(studyId, ct);
                    syncedStudies++;
                }
                catch (Exception ex)
                {
                    errors.Add($"Sync study '{studyId}': {ex.Message}");
                }
            }

            return Ok(new ImportResponse(
                UploadedInstances: uploadedInstances,
                OrthancStudyIds: parentStudies.ToList(),
                SyncedStudies: syncedStudies,
                Errors: errors
            ));
        }
    }
}
