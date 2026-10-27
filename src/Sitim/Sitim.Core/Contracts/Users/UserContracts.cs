namespace Sitim.Core.Contracts.Users;

/// <summary>User row returned by /api/users endpoints.</summary>
public sealed record UserResult(
    Guid Id,
    string Email,
    string? FullName,
    string Role,
    Guid? InstitutionId,
    string? InstitutionName,
    bool IsActive,
    DateTime CreatedAtUtc);

/// <summary>Body for POST /api/users/invite.</summary>
public sealed record InviteUserRequest(
    string Email,
    string? FullName,
    string Role,
    // Only used by SuperAdmin. Admin-created users inherit the Admin's institution.
    Guid? InstitutionId);

/// <summary>
/// Response from /api/users/invite.
/// <para>
/// <c>EmailSent</c> is the source of truth for the UI ("we sent the email" success state).
/// <c>FallbackLink</c> is populated ONLY when SMTP is disabled or unreachable AND the API
/// is running in Development — it lets the admin still complete the invitation manually.
/// In production this is always <c>null</c>.
/// </para>
/// </summary>
public sealed record InviteUserResponse(
    Guid UserId,
    string Email,
    bool EmailSent,
    string? FallbackLink);

/// <summary>Body for PATCH /api/users/{id}.</summary>
public sealed record UpdateUserRequest(
    string? FullName,
    string? Role,
    bool? IsActive);
