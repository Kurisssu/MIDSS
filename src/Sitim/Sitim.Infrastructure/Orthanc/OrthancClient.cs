using Sitim.Core.Models;
using Sitim.Core.Services;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sitim.Infrastructure.Orthanc
{
    /// <summary>
    /// Very small Orthanc REST client (MVP):
    /// - GET /studies
    /// - GET /studies/{id}
    /// </summary>
    public sealed class OrthancClient : IOrthancClient
    {
        private readonly HttpClient _http;

        public OrthancClient(HttpClient http)
        {
            _http = http;
        }

        public async Task<IReadOnlyList<string>> GetStudyIdsAsync(CancellationToken ct)
        {
            // Orthanc returns a simple JSON array of study IDs for GET /studies.
            var ids = await _http.GetFromJsonAsync<List<string>>("/studies", cancellationToken: ct);
            return ids ?? new List<string>();
        }

        public async Task<OrthancStudyDetails?> GetStudyAsync(string orthancStudyId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(orthancStudyId))
                throw new ArgumentException("orthancStudyId is required", nameof(orthancStudyId));

            using var resp = await _http.GetAsync($"studies/{orthancStudyId}?requestedTags=ModalitiesInStudy", ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? GetTag(JsonElement parent, string property, string tagName)
            {
                if (parent.TryGetProperty(property, out var tags) &&
                    tags.ValueKind == JsonValueKind.Object &&
                    tags.TryGetProperty(tagName, out var v))
                {
                    return v.GetString();
                }
                return null;
            }

            var studyInstanceUid = GetTag(root, "MainDicomTags", "StudyInstanceUID");
            var studyDate = GetTag(root, "MainDicomTags", "StudyDate");

            var patientId = GetTag(root, "PatientMainDicomTags", "PatientID");
            var patientName = GetTag(root, "PatientMainDicomTags", "PatientName");

            var modalities = new List<string>();

            // 1) Orthanc computes ModalitiesInStudy from series when requested via ?requestedTags=
            // and returns it in the "RequestedTags" object.
            var modalityStr = GetTag(root, "RequestedTags", "ModalitiesInStudy");

            // 2) Fallback: some datasets store it directly in MainDicomTags (0008,0061).
            if (string.IsNullOrWhiteSpace(modalityStr))
                modalityStr = GetTag(root, "MainDicomTags", "ModalitiesInStudy");

            if (!string.IsNullOrWhiteSpace(modalityStr))
            {
                // DICOM multi-value strings use backslash as separator: "CT\MR"
                modalities.AddRange(modalityStr.Split('\\', StringSplitOptions.RemoveEmptyEntries));
            }

            // 3) Last resort: top-level array (older Orthanc versions).
            if (modalities.Count == 0 &&
                root.TryGetProperty("ModalitiesInStudy", out var mods) &&
                mods.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in mods.EnumerateArray())
                {
                    var s = m.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        modalities.Add(s);
                }
            }

            var seriesIds = new List<string>();
            if (root.TryGetProperty("Series", out var series) && series.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in series.EnumerateArray())
                {
                    var id = s.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                        seriesIds.Add(id);
                }
            }

            return new OrthancStudyDetails(
                OrthancStudyId: orthancStudyId,
                StudyInstanceUid: studyInstanceUid,
                StudyDate: studyDate,
                PatientId: patientId,
                PatientName: patientName,
                ModalitiesInStudy: modalities,
                SeriesOrthancIds: seriesIds
            );
        }
        public async Task<OrthancUploadResult> UploadInstanceAsync(Stream dicomStream, CancellationToken ct)
        {
            if (dicomStream is null)
                throw new ArgumentNullException(nameof(dicomStream));

            using var content = new StreamContent(dicomStream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/dicom");

            using var req = new HttpRequestMessage(HttpMethod.Post, "instances")
            {
                Content = content
            };
            // Avoid "Expect: 100-continue" rountrip in some environmets
            req.Headers.ExpectContinue = false;

            using var resp = await _http.SendAsync(req, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                //Bubble up a readable error for Swagger / logs
                throw new HttpRequestException(
                    $"Orthanc upload failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). Body: {json}",
                    null,
                    resp.StatusCode);
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? GetString(string name)
                => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

            return new OrthancUploadResult(
                Id: GetString("ID") ?? GetString("Id"),
                ParentPatient: GetString("ParentPatient"),
                ParentStudy: GetString("ParentStudy"),
                ParentSeries: GetString("ParentSeries"),
                Path: GetString("Path"),
                Status: GetString("Status")
            );
        }

        public async Task DownloadStudyArchiveAsync(string orthancStudyId, Stream destination, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(orthancStudyId))
                throw new ArgumentException("orthancStudyId is required", nameof(orthancStudyId));
            if (destination is null)
                throw new ArgumentNullException(nameof(destination));

            using var resp = await _http.GetAsync($"studies/{orthancStudyId}/archive", HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Orthanc archive download failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). Body: {body}",
                    null,
                    resp.StatusCode);
            }

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await src.CopyToAsync(destination, ct);
        }

        /// <inheritdoc/>
        public async Task<bool> DeleteStudyAsync(string orthancStudyId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(orthancStudyId))
                throw new ArgumentException("orthancStudyId is required", nameof(orthancStudyId));

            using var resp = await _http.DeleteAsync($"studies/{orthancStudyId}", ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException(
                    $"Orthanc delete study failed ({(int)resp.StatusCode} {resp.ReasonPhrase}). Body: {body}",
                    null,
                    resp.StatusCode);
            }

            return true;
        }

    }
}
