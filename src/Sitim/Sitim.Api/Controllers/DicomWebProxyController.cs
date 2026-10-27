using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sitim.Api.Options;
using Sitim.Api.Security;
using Sitim.Infrastructure.Data;

namespace Sitim.Api.Controllers
{
    /// <summary>
    /// Secure DICOMweb proxy for OHIF.
    /// Requests are authorized by a short-lived viewer token that allows access
    /// to exactly one StudyInstanceUID and one institution (except SuperAdmin).
    /// </summary>
    [AllowAnonymous]
    [ApiController]
    [Route("api/dicomweb")]
    public sealed class DicomWebProxyController : ControllerBase
    {
        private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Connection",
            "Transfer-Encoding",
            "Keep-Alive",
            "Proxy-Authenticate",
            "Proxy-Authorization",
            "TE",
            "Trailer",
            "Upgrade",
            "Content-Length"
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IViewerTokenService _viewerTokenService;
        private readonly OrthancOptions _orthancOptions;
        private readonly AppDbContext _db;

        public DicomWebProxyController(
            IHttpClientFactory httpClientFactory,
            IViewerTokenService viewerTokenService,
            Microsoft.Extensions.Options.IOptions<OrthancOptions> orthancOptions,
            AppDbContext db)
        {
            _httpClientFactory = httpClientFactory;
            _viewerTokenService = viewerTokenService;
            _orthancOptions = orthancOptions.Value;
            _db = db;
        }

        /// <summary>
        /// Get Institution's Orthanc URL from database.
        /// </summary>
        private async Task<string?> GetInstitutionOrthancUrlAsync(Guid institutionId, CancellationToken ct)
        {
            var inst = await _db.Institutions
                .AsNoTracking()
                .Where(i => i.Id == institutionId)
                .Select(i => i.OrthancBaseUrl)
                .FirstOrDefaultAsync(ct);
            return inst;
        }

        [HttpGet]
        public Task<IActionResult> ProxyRoot(CancellationToken ct)
            => ProxyGet("studies", ct);

        [HttpGet("{**path}")]
        public async Task<IActionResult> ProxyGet(string? path, CancellationToken ct)
        {
            var token = ExtractViewerToken();
            if (string.IsNullOrWhiteSpace(token))
                return Unauthorized(new { error = "viewer_token_missing", message = "Viewer token is required." });

            var payload = _viewerTokenService.ValidateViewerToken(token);
            if (payload is null)
                return Unauthorized(new { error = "viewer_token_invalid", message = "Viewer token invalid or expired." });

            if (!await CanAccessStudyAsync(payload, ct))
                return Forbid();

            var normalizedPath = string.IsNullOrWhiteSpace(path) ? "studies" : path.Trim('/');
            if (!normalizedPath.StartsWith("studies", StringComparison.OrdinalIgnoreCase))
                return Forbid();

            // Restrict to the single study from token.
            // Allowed:
            //   /studies?StudyInstanceUID=<allowed>
            //   /studies/{allowed}/...
            var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > 1)
            {
                var pathStudyUid = Uri.UnescapeDataString(segments[1]);
                if (!string.Equals(pathStudyUid, payload.StudyInstanceUid, StringComparison.Ordinal))
                    return Forbid();
            }

            if (Request.Query.TryGetValue("StudyInstanceUID", out var queryStudyUids))
            {
                foreach (var q in queryStudyUids)
                {
                    if (!string.Equals(q, payload.StudyInstanceUid, StringComparison.Ordinal))
                        return Forbid();
                }
            }

            var query = BuildForwardQuery(normalizedPath, payload.StudyInstanceUid);
            var target = await BuildOrthancDicomWebUrlAsync(normalizedPath, query, payload, ct);
            if (target is null)
                return Problem(statusCode: 500, title: "Institution Orthanc URL not configured.");

            using var outgoing = new HttpRequestMessage(HttpMethod.Get, target);
            CopyRequestHeaders(outgoing);

            using var client = _httpClientFactory.CreateClient();
            using var upstream = await client.SendAsync(outgoing, HttpCompletionOption.ResponseHeadersRead, ct);

            Response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream);

            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(Response.Body, ct);

            return new EmptyResult();
        }

        private async Task<string?> BuildOrthancDicomWebUrlAsync(string path, QueryString query, ViewerTokenPayload payload, CancellationToken ct)
        {
            string? baseUrl;

            // For both SuperAdmin and regular users: if token has InstitutionId, use that institution's Orthanc
            if (payload.InstitutionId.HasValue)
            {
                baseUrl = await GetInstitutionOrthancUrlAsync(payload.InstitutionId.Value, ct);
            }
            else if (payload.IsSuperAdmin)
            {
                // SuperAdmin without InstitutionId: fallback to shared Orthanc (backward compatibility, shouldn't happen in multi-Orthanc)
                baseUrl = _orthancOptions.BaseUrl;
            }
            else
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var root = _orthancOptions.DicomWebRoot.Trim('/');
            return $"{baseUrl.TrimEnd('/')}/{root}/{path}{query.Value}";
        }

        private QueryString BuildForwardQuery(string normalizedPath, string studyInstanceUid)
        {
            // For /studies searches force StudyInstanceUID to the token value,
            // so OHIF cannot discover other studies/priors.
            if (!string.Equals(normalizedPath, "studies", StringComparison.OrdinalIgnoreCase))
                return Request.QueryString;

            var qb = new QueryBuilder();

            foreach (var (key, values) in Request.Query)
            {
                if (string.Equals(key, "StudyInstanceUID", StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var value in values)
                {
                    if (value is not null)
                        qb.Add(key, value);
                }
            }

            qb.Add("StudyInstanceUID", studyInstanceUid);
            return qb.ToQueryString();
        }

        private string? ExtractViewerToken()
        {
            var headerToken = Request.Headers["X-Viewer-Token"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(headerToken))
                return headerToken;

            var queryToken = Request.Query["viewerToken"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(queryToken))
                return queryToken;

            var genericToken = Request.Query["token"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(genericToken))
                return genericToken;

            // Fallback for browsers serving stale OHIF app-config without custom headers:
            // the viewer URL still includes ?viewerToken=... and arrives in Referer.
            var referer = Request.Headers.Referer.FirstOrDefault();
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                var refererQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(refererUri.Query);
                if (refererQuery.TryGetValue("viewerToken", out var refererViewerToken))
                {
                    var t = refererViewerToken.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(t))
                        return t;
                }
                if (refererQuery.TryGetValue("token", out var refererToken))
                {
                    var t = refererToken.FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(t))
                        return t;
                }
            }

            return null;
        }

        private async Task<bool> CanAccessStudyAsync(ViewerTokenPayload payload, CancellationToken ct)
        {
            if (payload.IsSuperAdmin)
            {
                return await _db.ImagingStudies
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(s => s.StudyInstanceUid == payload.StudyInstanceUid, ct);
            }

            if (!payload.InstitutionId.HasValue)
                return false;

            return await _db.ImagingStudies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(s =>
                    s.StudyInstanceUid == payload.StudyInstanceUid &&
                    s.InstitutionId == payload.InstitutionId.Value, ct);
        }

        private void CopyRequestHeaders(HttpRequestMessage outgoing)
        {
            foreach (var header in Request.Headers)
            {
                if (HopByHopHeaders.Contains(header.Key))
                    continue;

                if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(header.Key, "X-Viewer-Token", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                outgoing.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }

        private void CopyResponseHeaders(HttpResponseMessage upstream)
        {
            foreach (var header in upstream.Headers)
            {
                if (!HopByHopHeaders.Contains(header.Key))
                    Response.Headers[header.Key] = header.Value.ToArray();
            }

            foreach (var header in upstream.Content.Headers)
            {
                if (!HopByHopHeaders.Contains(header.Key))
                    Response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }
}

