using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Midss.Core.Contracts.AI;
using Midss.Core.Contracts.Auth;
using Midss.Core.Contracts.FL;
using Midss.Core.Contracts.Institutions;
using Midss.Core.Contracts.Studies;
using Midss.Core.Contracts.Users;
using Midss.Core.Models;

namespace Midss.Web.Services;

/// <summary>
/// Typed HTTP client that wraps all MIDSS API calls.
/// Token is attached from circuit-scoped <see cref="AuthTokenStore"/>.
/// </summary>
public sealed class MidssApiClient
{
    private readonly HttpClient _http;
    private readonly AuthTokenStore _store;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public MidssApiClient(HttpClient http, AuthTokenStore store)
    {
        _http = http;
        _store = store;
    }

    /// <summary>
    /// Ensures the current JWT token (if any) is set on the HttpClient before making a request.
    /// </summary>
    private void AttachToken()
    {
        var token = _store.Token;
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        else
            _http.DefaultRequestHeaders.Authorization = null;
    }

    // ── Auth ──────────────────────────────────────────────

    public async Task<LoginResult?> LoginAsync(string email, string password)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/login", new { email, password });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<LoginResult>(JsonOpts);
    }

    public async Task<MeResult?> GetMeAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/auth/me");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<MeResult>(JsonOpts);
    }

    /// <summary>
    /// Trade a refresh token for a fresh access token + a rotated refresh token.
    /// The API rotates the refresh side: the supplied plaintext is single-use.
    /// Caller is responsible for persisting the new pair.
    /// </summary>
    public async Task<LoginResult?> RefreshAsync(string refreshToken)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/refresh", new RefreshRequest(refreshToken));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<LoginResult>(JsonOpts);
    }

    /// <summary>Best-effort server-side revocation of the supplied refresh token.</summary>
    public async Task LogoutAsync(string? refreshToken)
    {
        try
        {
            await _http.PostAsJsonAsync("api/auth/logout", new LogoutRequest(refreshToken));
        }
        catch
        {
            // Logout is idempotent client-side too — don't surface network errors.
        }
    }

    // ── Worklist (local DB) ──────────────────────────────

    public async Task<List<StudySummary>> GetLocalStudiesAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/local/studies");
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            return [];
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<StudySummary>>(JsonOpts) ?? [];
    }

    public async Task<StudyDetails?> GetLocalStudyAsync(string orthancStudyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/local/studies/{orthancStudyId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<StudyDetails>(JsonOpts);
    }

    public async Task<StudyDetails?> SyncStudyAsync(string orthancStudyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/local/studies/{orthancStudyId}/sync");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<StudyDetails>(JsonOpts);
    }

    public async Task<int> SyncAllStudiesAsync()
    {
        AttachToken();
        var resp = await _http.PostAsync("api/local/studies/sync-all", null);
        resp.EnsureSuccessStatusCode();
        var result = await resp.Content.ReadFromJsonAsync<SyncAllResult>(JsonOpts);
        return result?.Synced ?? 0;
    }

    public async Task<bool> DeleteStudyAsync(string orthancStudyId)
    {
        AttachToken();
        var resp = await _http.DeleteAsync($"api/local/studies/{orthancStudyId}");
        return resp.IsSuccessStatusCode;
    }

    // ── Orthanc studies (viewer link) ────────────────────

    public async Task<string?> GetViewerLinkAsync(string orthancStudyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/studies/{orthancStudyId}/viewer-link");
        if (!resp.IsSuccessStatusCode) return null;
        var result = await resp.Content.ReadFromJsonAsync<ViewerLinkResult>(JsonOpts);
        return result?.Url;
    }

    // ── Import ───────────────────────────────────────────

    public async Task<ImportResult?> ImportArchiveAsync(Stream fileStream, string fileName)
    {
        AttachToken();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "Archive", fileName);

        var resp = await _http.PostAsync("api/studies/import", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ImportResult>(JsonOpts);
    }

    public async Task<ImportResult?> ImportFilesAsync(IEnumerable<(byte[] data, string name)> files)
    {
        AttachToken();
        using var content = new MultipartFormDataContent();
        foreach (var (data, name) in files)
        {
            var sc = new ByteArrayContent(data);
            sc.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Add(sc, "Files", name);
        }

        var resp = await _http.PostAsync("api/studies/import", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ImportResult>(JsonOpts);
    }

    // ── Users ─────────────────────────────────────────────

    public async Task<List<UserResult>> GetUsersAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/users");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<UserResult>>(JsonOpts) ?? [];
    }

    /// <summary>
    /// Invite a new user. The API sends the activation email itself, so we no longer
    /// pass the Web app base URL — the server reads <c>Smtp:WebBaseUrl</c> from config.
    /// </summary>
    public async Task<InviteUserResponse?> InviteUserAsync(
        string email, string? fullName, string role, Guid? institutionId)
    {
        AttachToken();
        var resp = await _http.PostAsJsonAsync(
            "api/users/invite",
            new InviteUserRequest(email, fullName, role, institutionId));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<InviteUserResponse>(JsonOpts);
    }

    public async Task<UserResult?> UpdateUserAsync(Guid id, string? fullName, string? role, bool? isActive)
    {
        AttachToken();
        var resp = await _http.PutAsJsonAsync($"api/users/{id}",
            new UpdateUserRequest(fullName, role, isActive));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<UserResult>(JsonOpts);
    }

    public async Task<bool> DeactivateUserAsync(Guid id)
    {
        AttachToken();
        var resp = await _http.DeleteAsync($"api/users/{id}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> SetPasswordAsync(Guid userId, string token, string newPassword)
    {
        var resp = await _http.PostAsJsonAsync("api/auth/set-password",
            new SetPasswordRequest(userId, token, newPassword));
        return resp.IsSuccessStatusCode;
    }

    // ── Health ────────────────────────────────────────────

    public async Task<bool> HealthCheckAsync()
    {
        try
        {
            var resp = await _http.GetAsync("api/health");
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Institutions (SuperAdmin) ─────────────────────────

    public async Task<List<InstitutionResult>> GetInstitutionsAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/institutions");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<InstitutionResult>>(JsonOpts) ?? [];
    }

    public async Task<InstitutionResult?> CreateInstitutionAsync(
        string name, string slug, string orthancBaseUrl,
        string? orthancUsername = null, string? orthancPassword = null)
    {
        AttachToken();
        var resp = await _http.PostAsJsonAsync("api/institutions",
            new CreateInstitutionRequest(name, slug, orthancBaseUrl, orthancUsername, orthancPassword));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<InstitutionResult>(JsonOpts);
    }

    public async Task<InstitutionResult?> UpdateInstitutionAsync(
        Guid id, string name, string orthancBaseUrl, bool isActive,
        string? orthancUsername = null, string? orthancPassword = null)
    {
        AttachToken();
        var resp = await _http.PutAsJsonAsync($"api/institutions/{id}",
            new UpdateInstitutionRequest(name, orthancBaseUrl, isActive, orthancUsername, orthancPassword));
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<InstitutionResult>(JsonOpts);
    }

    // ── Federated Learning (SuperAdmin) ───────────────────

    public async Task<List<FLSessionDto>> GetFLSessionsAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/fl/sessions");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<FLSessionDto>>(JsonOpts) ?? [];
    }

    public async Task<FLSessionDetailsDto?> GetFLSessionAsync(Guid sessionId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/fl/sessions/{sessionId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FLSessionDetailsDto>(JsonOpts);
    }

    public async Task<List<FLConnectedClientDto>> GetFLConnectedClientsAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/fl/clients");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<FLConnectedClientDto>>(JsonOpts) ?? [];
    }

    public async Task<FLSessionDto?> StartFLSessionAsync(string modelKey, int totalRounds, List<Guid> institutionIds, string? architecture)
    {
        AttachToken();
        var request = new StartFLSessionRequest(modelKey, totalRounds, institutionIds, architecture);
        var resp = await _http.PostAsJsonAsync("api/fl/sessions", request);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FLSessionDto>(JsonOpts);
    }

    public async Task<bool> StopFLSessionAsync(Guid sessionId)
    {
        AttachToken();
        var resp = await _http.PostAsync($"api/fl/sessions/{sessionId}/stop", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<FLPublishedModelDto?> GetFLPublishedModelAsync(Guid sessionId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/fl/sessions/{sessionId}/published-model");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FLPublishedModelDto>(JsonOpts);
    }

    public async Task<FLPublishedModelDto?> ActivateFLPublishedModelAsync(Guid sessionId)
    {
        AttachToken();
        var resp = await _http.PostAsync($"api/fl/sessions/{sessionId}/activate-model", null);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<FLPublishedModelDto>(JsonOpts);
    }

    public async Task<List<ModelDefinitionDto>> GetModelRegistryAsync(bool activeOnly = false, string? task = null, string? modality = null)
    {
        AttachToken();

        var query = new List<string>();
        if (activeOnly) query.Add("activeOnly=true");
        if (!string.IsNullOrWhiteSpace(task)) query.Add($"task={Uri.EscapeDataString(task)}");
        if (!string.IsNullOrWhiteSpace(modality)) query.Add($"modality={Uri.EscapeDataString(modality)}");
        var queryString = query.Count > 0 ? "?" + string.Join("&", query) : string.Empty;

        var resp = await _http.GetAsync($"api/models{queryString}");
        if (!resp.IsSuccessStatusCode) return [];
        return await resp.Content.ReadFromJsonAsync<List<ModelDefinitionDto>>(JsonOpts) ?? [];
    }

    // All DTOs now live in Midss.Core.Contracts.* — see usings at top of file.

    // ── AI Models ─────────────────────────────────────────

    public async Task<List<AIModelDto>> GetAIModelsAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/ai/models");
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<AIModelDto>>(JsonOpts) ?? new();
    }

    public async Task<bool> ToggleModelStatusAsync(Guid modelId)
    {
        AttachToken();
        var resp = await _http.PatchAsync($"api/ai/models/{modelId}/toggle", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteModelAsync(Guid modelId)
    {
        AttachToken();
        var resp = await _http.DeleteAsync($"api/ai/models/{modelId}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Update an existing model's interpretation metadata (class names, severities,
    /// recommendations). Returns the API error message on failure, or null on success.
    /// </summary>
    public async Task<string?> UpdateModelMetadataAsync(Guid modelId, UpdateModelMetadataRequest request)
    {
        AttachToken();
        var resp = await _http.PutAsJsonAsync($"api/ai/models/{modelId}/metadata", request, JsonOpts);
        if (resp.IsSuccessStatusCode)
            return null;
        var body = await resp.Content.ReadAsStringAsync();
        return string.IsNullOrWhiteSpace(body) ? $"HTTP {(int)resp.StatusCode}" : body;
    }

    public async Task<List<AIModelDto>> GetActiveModelsAsync()
    {
        AttachToken();
        var resp = await _http.GetAsync("api/ai/models");
        resp.EnsureSuccessStatusCode();
        var allModels = await resp.Content.ReadFromJsonAsync<List<AIModelDto>>(JsonOpts) ?? new();
        return allModels.Where(m => m.IsActive).ToList();
    }

    public async Task<AIAnalysisResultDto?> RunAIAnalysisAsync(Guid studyId, Guid? modelId = null)
    {
        AttachToken();
        var request = new AnalyzeStudyRequest(studyId, modelId);
        var resp = await _http.PostAsJsonAsync("api/ai/analyze", request);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AIAnalysisResultDto>(JsonOpts);
    }

    /// <summary>
    /// Get AI models compatible with a study's modality.
    /// Returns filtered list sorted by accuracy.
    /// </summary>
    public async Task<List<AIModelSelectionDto>> GetCompatibleModelsAsync(Guid studyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/models-for-study/{studyId}");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<AIModelSelectionDto>>(JsonOpts) ?? new();
    }

    /// <summary>
    /// Start an AI analysis job (runs in background via Hangfire).
    /// Returns job ID for polling job status.
    /// </summary>
    public async Task<Guid> StartAnalysisAsync(Guid studyId, Guid modelId)
    {
        AttachToken();
        var request = new AnalyzeStudyRequest(studyId, modelId);
        var resp = await _http.PostAsJsonAsync("api/ai/analyze", request);
        if (!resp.IsSuccessStatusCode) 
            throw new InvalidOperationException("Failed to start analysis");
        var result = await resp.Content.ReadFromJsonAsync<StartAnalysisResponseDto>(JsonOpts);
        return result?.JobId ?? throw new InvalidOperationException("No job ID returned");
    }

    /// <summary>
    /// Get latest analysis jobs (running + completed) visible in current tenant.
    /// </summary>
    public async Task<List<AIAnalysisJobListItemDto>> GetAnalysisJobsAsync(int limit = 100)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/jobs?limit={limit}");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<AIAnalysisJobListItemDto>>(JsonOpts) ?? new();
    }

    /// <summary>
    /// Get all analysis jobs for one study (running first, then latest completed/failed).
    /// </summary>
    public async Task<List<AIAnalysisJobListItemDto>> GetStudyAnalysisJobsAsync(Guid studyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/studies/{studyId}/jobs");
        if (!resp.IsSuccessStatusCode) return new();
        return await resp.Content.ReadFromJsonAsync<List<AIAnalysisJobListItemDto>>(JsonOpts) ?? new();
    }

    /// <summary>
    /// Get AI analysis job status and results (for polling).
    /// </summary>
    public async Task<AIAnalysisJobStatusDto?> GetAnalysisJobAsync(Guid jobId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/jobs/{jobId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AIAnalysisJobStatusDto>(JsonOpts);
    }

    /// <summary>
    /// Get full analysis results with diagnosis, severity, and recommendations.
    /// </summary>
    public async Task<AIAnalysisResultDto?> GetAnalysisResultsAsync(Guid jobId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/jobs/{jobId}/results");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<AIAnalysisResultDto>(JsonOpts);
    }

    /// <summary>
    /// Cancel a running analysis job.
    /// </summary>
    public async Task<bool> CancelAnalysisAsync(Guid jobId)
    {
        AttachToken();
        var resp = await _http.PostAsync($"api/ai/jobs/{jobId}/cancel", null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Delete a completed analysis job record.
    /// </summary>
    public async Task<bool> DeleteAnalysisAsync(Guid jobId)
    {
        AttachToken();
        var resp = await _http.DeleteAsync($"api/ai/jobs/{jobId}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Get analysis history for a study.
    /// </summary>
    public async Task<List<AIAnalysisResultDto>?> GetAnalysisHistoryAsync(Guid studyId)
    {
        AttachToken();
        var resp = await _http.GetAsync($"api/ai/studies/{studyId}/history");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<AIAnalysisResultDto>>(JsonOpts);
    }

    // AnalyzeStudyRequest, AIAnalysisResultDto, ClassProbability, StartAnalysisResponseDto,
    // AIAnalysisJobStatusDto, AIAnalysisJobListItemDto, AIModelDto and AIModelSelectionDto
    // moved to Midss.Core.Contracts.AI — see usings at top of file.
}
