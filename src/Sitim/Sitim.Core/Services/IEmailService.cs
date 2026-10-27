namespace Sitim.Core.Services;

/// <summary>
/// Outbound email gateway. Implementations send transactional messages
/// (invitations, password resets, notifications) through an external SMTP relay.
/// </summary>
public interface IEmailService
{
    /// <summary>
    /// Sends the "you have been invited to SITIM, set your password" email to a new user.
    /// </summary>
    /// <param name="recipientEmail">Inbox to deliver to.</param>
    /// <param name="recipientName">Friendly name for the salutation (falls back to email local part if null).</param>
    /// <param name="invitationLink">Absolute https URL to the set-password page (already token-encoded).</param>
    /// <param name="invitedByDisplay">Admin name shown in the email body for context.</param>
    /// <param name="expiresAtUtc">When the link stops working (shown to recipient).</param>
    /// <returns><c>true</c> if the SMTP server accepted the message, <c>false</c> on any failure (logged internally).</returns>
    Task<bool> SendInvitationAsync(
        string recipientEmail,
        string? recipientName,
        string invitationLink,
        string invitedByDisplay,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default);
}
