using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using Sitim.Core.Options;
using Sitim.Core.Services;

namespace Sitim.Infrastructure.Email;

/// <summary>
/// MailKit-based SMTP gateway. Connects on each send so the SMTP session
/// tracking matches per-message audit logging. Falls back to STARTTLS or
/// SmtpsSslOnConnect based on the configured port (587 vs 465).
/// </summary>
public sealed class SmtpEmailService : IEmailService
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailService> _logger;

    public SmtpEmailService(IOptions<SmtpOptions> options, ILogger<SmtpEmailService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<bool> SendInvitationAsync(
        string recipientEmail,
        string? recipientName,
        string invitationLink,
        string invitedByDisplay,
        DateTime expiresAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host) || string.IsNullOrWhiteSpace(_options.FromAddress))
        {
            _logger.LogError("SMTP not configured (Smtp:Host / Smtp:FromAddress empty). Invitation email NOT sent to {Recipient}.", recipientEmail);
            return false;
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(new MailboxAddress(recipientName ?? string.Empty, recipientEmail));
        message.Subject = InvitationEmailContent.Subject(_options.FromName);

        var body = new BodyBuilder
        {
            HtmlBody = InvitationEmailContent.Html(recipientName, invitationLink, invitedByDisplay, expiresAtUtc),
            TextBody = InvitationEmailContent.PlainText(recipientName, invitationLink, invitedByDisplay, expiresAtUtc),
        };
        message.Body = body.ToMessageBody();

        // Choose secure socket based on the port:
        //   587  → STARTTLS (Gmail's recommended submission port)
        //   465  → implicit TLS
        //   25   → plain (only for dev/local relays — avoid in production)
        var socketOptions = _options.Port switch
        {
            465 => SecureSocketOptions.SslOnConnect,
            587 => SecureSocketOptions.StartTls,
            _   => _options.UseSsl ? SecureSocketOptions.StartTlsWhenAvailable : SecureSocketOptions.None,
        };

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_options.Host, _options.Port, socketOptions, cancellationToken);

            if (!string.IsNullOrEmpty(_options.Username))
                await client.AuthenticateAsync(_options.Username, _options.Password, cancellationToken);

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            // Audit log — recipient + sender visible, BUT no token or link content.
            _logger.LogInformation(
                "Invitation email accepted by SMTP server. Recipient={Recipient}, From={From}, ExpiresAtUtc={Expires}",
                recipientEmail, _options.FromAddress, expiresAtUtc);

            return true;
        }
        catch (Exception ex)
        {
            // Never log the token. The link is logged only because it carries no useful
            // secret on its own (the token is keyed to the user's SecurityStamp and is
            // single-use). Even so, demote to Warning so it doesn't get aggressively
            // shipped off-host by log forwarders. Tweak as needed.
            _logger.LogError(ex,
                "Failed to send invitation email to {Recipient} via {Host}:{Port}",
                recipientEmail, _options.Host, _options.Port);
            return false;
        }
    }
}
