namespace Sitim.Core.Options;

/// <summary>
/// Outbound email configuration. Bound to the "Smtp" section in appsettings.
/// </summary>
/// <remarks>
/// For Gmail: host=smtp.gmail.com, port=587, useSsl=true (STARTTLS).
/// Use an App Password (not the account password) and store it in user-secrets:
///   <c>dotnet user-secrets set "Smtp:Password" "xxxx xxxx xxxx xxxx"</c>
/// </remarks>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    /// <summary>SMTP server hostname (e.g. smtp.gmail.com).</summary>
    public string Host { get; set; } = "";

    /// <summary>SMTP server port (587 for STARTTLS, 465 for SMTPS).</summary>
    public int Port { get; set; } = 587;

    /// <summary>Use TLS/STARTTLS for the connection.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>SMTP auth username (typically the From address for Gmail).</summary>
    public string Username { get; set; } = "";

    /// <summary>SMTP auth password (Gmail App Password — kept in user-secrets).</summary>
    public string Password { get; set; } = "";

    /// <summary>Address that appears as the email's From: header.</summary>
    public string FromAddress { get; set; } = "";

    /// <summary>Display name paired with FromAddress.</summary>
    public string FromName { get; set; } = "SITIM";

    /// <summary>
    /// Public base URL of the Sitim.Web app (e.g. https://localhost:5001).
    /// Used to build absolute links inside the emails. Must be HTTPS in production.
    /// </summary>
    public string WebBaseUrl { get; set; } = "";

    /// <summary>
    /// How long invitation tokens stay valid. Default 2 hours.
    /// Mirrors what is configured on the Identity TokenProvider.
    /// </summary>
    public int InvitationLifetimeHours { get; set; } = 2;
}
