using System.Globalization;
using System.Text;

namespace Sitim.Infrastructure.Email;

/// <summary>
/// Builds the HTML + plain-text body of the SITIM user-invitation email.
/// Separated from the SMTP transport so the templating can be unit-tested
/// without sending real messages.
/// </summary>
internal static class InvitationEmailContent
{
    /// <summary>Subject line of the invitation email (kept short for inbox preview).</summary>
    public static string Subject(string fromName) =>
        $"Access invitation — {fromName}";

    /// <summary>
    /// Constructs the visible HTML body. Uses inline styles only — embedded styles
    /// and external resources are widely stripped by webmail clients (Gmail, Outlook).
    /// </summary>
    public static string Html(
        string? recipientName,
        string invitationLink,
        string invitedByDisplay,
        DateTime expiresAtUtc)
    {
        var displayName = string.IsNullOrWhiteSpace(recipientName) ? "Hello" : $"Hello, {Escape(recipientName)}";
        var expiryLocal = expiresAtUtc.ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var safeLink = Escape(invitationLink);
        var safeInviter = Escape(invitedByDisplay);

        // Email-client compatible HTML: tables + inline styles only.
        // rel="noreferrer noopener" prevents the token from leaking via the Referer header.
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>SITIM Invitation</title>
</head>
<body style="margin:0;padding:0;background:#0b0f16;font-family:Segoe UI,Helvetica,Arial,sans-serif;color:#e6e9ee;">
  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" border="0" style="background:#0b0f16;">
    <tr>
      <td align="center" style="padding:32px 16px;">
        <table role="presentation" width="560" cellspacing="0" cellpadding="0" border="0"
               style="max-width:560px;background:#111722;border:1px solid rgba(255,255,255,0.08);border-radius:14px;overflow:hidden;">
          <tr>
            <td style="padding:28px 32px 12px;border-bottom:1px solid rgba(255,255,255,0.06);">
              <div style="font-size:22px;font-weight:600;color:#00b8a9;letter-spacing:-0.02em;">SITIM</div>
              <div style="font-size:13px;color:rgba(255,255,255,0.55);margin-top:2px;">
                Medical imaging diagnostics support system
              </div>
            </td>
          </tr>
          <tr>
            <td style="padding:28px 32px 8px;">
              <p style="margin:0 0 14px;font-size:17px;line-height:1.4;color:rgba(255,255,255,0.95);">
                {{displayName}},
              </p>
              <p style="margin:0 0 14px;font-size:14px;line-height:1.6;color:rgba(255,255,255,0.78);">
                {{safeInviter}} has created an account for you on the SITIM platform. To activate it, set a password by pressing the button below.
              </p>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding:12px 32px 24px;">
              <a href="{{safeLink}}"
                 rel="noreferrer noopener"
                 style="display:inline-block;padding:12px 28px;background:linear-gradient(135deg,#00c8b8,#0096c7);
                        color:#ffffff;text-decoration:none;font-weight:600;font-size:14px;border-radius:10px;
                        box-shadow:0 4px 18px rgba(0,184,169,0.32);">
                Set password
              </a>
            </td>
          </tr>
          <tr>
            <td style="padding:0 32px 24px;">
              <p style="margin:0 0 10px;font-size:13px;color:rgba(255,255,255,0.55);line-height:1.55;">
                The link is valid until <strong style="color:rgba(255,255,255,0.85);">{{expiryLocal}}</strong> and can be used only once.
              </p>
              <p style="margin:0 0 6px;font-size:12px;color:rgba(255,255,255,0.42);line-height:1.55;">
                If the button does not work, copy the address into your browser manually:
              </p>
              <p style="margin:0;font-size:11px;color:rgba(255,255,255,0.58);
                        font-family:'Cascadia Code',Consolas,monospace;word-break:break-all;line-height:1.4;">
                {{safeLink}}
              </p>
            </td>
          </tr>
          <tr>
            <td style="padding:18px 32px 26px;border-top:1px solid rgba(255,255,255,0.06);">
              <p style="margin:0;font-size:12px;color:rgba(255,255,255,0.42);line-height:1.55;">
                If you were not expecting this invitation, ignore this email — the account stays inactive until the link is used.
              </p>
            </td>
          </tr>
        </table>
        <div style="margin-top:14px;font-size:11px;color:rgba(255,255,255,0.30);">
          © {{DateTime.UtcNow.Year}} SITIM · Automated email, do not reply to this address.
        </div>
      </td>
    </tr>
  </table>
</body>
</html>
""";
    }

    /// <summary>Plain-text version for clients that block HTML rendering.</summary>
    public static string PlainText(
        string? recipientName,
        string invitationLink,
        string invitedByDisplay,
        DateTime expiresAtUtc)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(recipientName))
            sb.AppendLine($"Hello, {recipientName},");
        else
            sb.AppendLine("Hello,");
        sb.AppendLine();
        sb.AppendLine($"{invitedByDisplay} has created an account for you on the SITIM platform.");
        sb.AppendLine("To activate it, open the following link and set a password:");
        sb.AppendLine();
        sb.AppendLine(invitationLink);
        sb.AppendLine();
        sb.AppendLine($"The link is valid until {expiresAtUtc:dd.MM.yyyy HH:mm} UTC and can be used only once.");
        sb.AppendLine();
        sb.AppendLine("If you were not expecting this invitation, ignore this email.");
        sb.AppendLine();
        sb.AppendLine($"— SITIM");
        return sb.ToString();
    }

    private static string Escape(string raw) =>
        raw.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
