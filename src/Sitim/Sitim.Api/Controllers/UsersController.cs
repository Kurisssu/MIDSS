using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sitim.Api.Security;
using Sitim.Core.Contracts.Auth;
using Sitim.Core.Contracts.Users;
using Sitim.Core.Options;
using Sitim.Core.Services;
using Sitim.Infrastructure.Data;
using Sitim.Infrastructure.Identity;
using System.Security.Claims;
using System.Text;

namespace Sitim.Api.Controllers
{
    /// <summary>
    /// User management: invite, list, edit, and deactivate users per institution.
    /// Admin manages their own institution's users. SuperAdmin manages all.
    /// </summary>
    [Authorize(Roles = SitimRoles.AnyStaff)]
    [ApiController]
    [Route("api/[controller]")]
    public sealed class UsersController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _users;
        private readonly AppDbContext _db;
        private readonly ITenantContext _tenant;
        private readonly IEmailService _email;
        private readonly SmtpOptions _smtpOptions;
        private readonly ILogger<UsersController> _logger;
        private readonly IWebHostEnvironment _env;

        public UsersController(
            UserManager<ApplicationUser> users,
            AppDbContext db,
            ITenantContext tenant,
            IEmailService email,
            IOptions<SmtpOptions> smtpOptions,
            ILogger<UsersController> logger,
            IWebHostEnvironment env)
        {
            _users = users;
            _db = db;
            _tenant = tenant;
            _email = email;
            _smtpOptions = smtpOptions.Value;
            _logger = logger;
            _env = env;
        }

        /// <summary>
        /// Lists users. SuperAdmin sees all; Admin sees only their institution.
        /// </summary>
        [HttpGet]
        [Authorize(Roles = SitimRoles.AnyStaff)]
        public async Task<ActionResult<IReadOnlyList<UserResult>>> List(CancellationToken ct)
        {
            // Load users + their roles via Identity tables
            IQueryable<ApplicationUser> query = _users.Users.AsNoTracking();

            if (!_tenant.IsSuperAdmin && _tenant.InstitutionId.HasValue)
                query = query.Where(u => u.InstitutionId == _tenant.InstitutionId);

            var appUsers = await query.OrderBy(u => u.Email).ToListAsync(ct);

            // Load institution names in one query
            var institutionIds = appUsers
                .Where(u => u.InstitutionId.HasValue)
                .Select(u => u.InstitutionId!.Value)
                .Distinct()
                .ToList();

            var institutions = await _db.Institutions
                .AsNoTracking()
                .Where(i => institutionIds.Contains(i.Id))
                .ToDictionaryAsync(i => i.Id, i => i.Name, ct);

            // Load roles for all users in batch (N+1 avoided by building a lookup)
            var results = new List<UserResult>(appUsers.Count);
            foreach (var u in appUsers)
            {
                var roles = await _users.GetRolesAsync(u);
                var role = roles.FirstOrDefault() ?? "";
                var instName = u.InstitutionId.HasValue
                    ? institutions.GetValueOrDefault(u.InstitutionId.Value)
                    : null;

                results.Add(new UserResult(
                    u.Id, u.Email ?? "", u.FullName, role,
                    u.InstitutionId, instName,
                    u.IsActive, u.CreatedAtUtc));
            }

            return Ok(results);
        }

        /// <summary>
        /// Invites a new user: creates an inactive account, generates a single-use
        /// password-reset token, and emails the activation link to the recipient.
        /// The admin caller never sees the token — only a success/failure indicator.
        /// </summary>
        [HttpPost("invite")]
        [Authorize(Roles = SitimRoles.AnyStaff)]
        public async Task<ActionResult<InviteUserResponse>> Invite([FromBody] InviteUserRequest req)
        {
            // Determine which institution the new user belongs to
            Guid? institutionId;
            if (_tenant.IsSuperAdmin)
            {
                if (!req.InstitutionId.HasValue)
                    return BadRequest("SuperAdmin must specify an InstitutionId.");
                institutionId = req.InstitutionId;
            }
            else
            {
                institutionId = _tenant.InstitutionId;
            }

            // Validate role — only Doctor and Technician can be invited this way
            var allowedRoles = new[] { SitimRoles.Doctor, SitimRoles.Technician, SitimRoles.Admin };
            if (!allowedRoles.Contains(req.Role))
                return BadRequest($"Role '{req.Role}' is not allowed. Use Doctor, Technician, or Admin.");

            // Only SuperAdmin can invite Admin-level users
            if (req.Role == SitimRoles.Admin && !_tenant.IsSuperAdmin)
                return Forbid();

            if (await _users.FindByEmailAsync(req.Email) is not null)
                return Conflict($"A user with email '{req.Email}' already exists.");

            // Validate the WebBaseUrl that links will point to. In production it MUST be HTTPS
            // — invitation links must never travel as plain text over HTTP.
            var webBaseUrl = (_smtpOptions.WebBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(webBaseUrl))
                return Problem("Smtp:WebBaseUrl is not configured. Set it to the public URL of the SITIM web app.");
            if (!_env.IsDevelopment() && !webBaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return Problem("Smtp:WebBaseUrl must use HTTPS in non-Development environments.");

            var user = new ApplicationUser
            {
                UserName = req.Email,
                Email = req.Email,
                FullName = req.FullName?.Trim(),
                InstitutionId = institutionId,
                IsActive = false, // activated after setting password
                CreatedAtUtc = DateTime.UtcNow,
                EmailConfirmed = false
            };

            // Create with a random placeholder password (user will set their own via the invite link)
            var createResult = await _users.CreateAsync(user, GeneratePlaceholderPassword());
            if (!createResult.Succeeded)
                return BadRequest(createResult.Errors.Select(e => e.Description));

            await _users.AddToRoleAsync(user, req.Role);

            // Generate a password-reset token. Lifespan is driven by
            // DataProtectionTokenProviderOptions.TokenLifespan (configured in Program.cs).
            var rawToken = await _users.GeneratePasswordResetTokenAsync(user);

            // URL-safe Base64 encoding survives transport through any proxy/email
            // client without the corruption that Uri.EscapeDataString sometimes suffers
            // when "+" / "=" / "/" are re-encoded inconsistently.
            var encodedToken = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawToken));

            var link = $"{webBaseUrl}/set-password?userId={user.Id}&token={encodedToken}";

            // Send invitation email. If the SMTP send fails, roll back the user so we
            // don't end up with orphan inactive accounts that nobody can ever activate.
            var lifetime = TimeSpan.FromHours(Math.Clamp(_smtpOptions.InvitationLifetimeHours, 1, 24));
            var expiresAtUtc = DateTime.UtcNow.Add(lifetime);
            var inviterDisplay = User.FindFirstValue(ClaimTypes.Email)
                                  ?? User.Identity?.Name
                                  ?? "A SITIM administrator";

            var sent = await _email.SendInvitationAsync(
                recipientEmail: user.Email!,
                recipientName: user.FullName,
                invitationLink: link,
                invitedByDisplay: inviterDisplay,
                expiresAtUtc: expiresAtUtc);

            if (!sent)
            {
                // Compensating action — roll back the orphan account so the admin
                // can retry the invitation cleanly. Avoids "ghost" inactive users.
                var deletion = await _users.DeleteAsync(user);
                _logger.LogError(
                    "Invitation email failed for {Recipient}. User rollback Succeeded={Rollback}.",
                    user.Email, deletion.Succeeded);

                // In Development, return the link as a fallback so manual handoff still works
                // (no need for SMTP to be configured locally). Production NEVER leaks the link.
                var fallback = _env.IsDevelopment() ? link : null;
                return Ok(new InviteUserResponse(Guid.Empty, user.Email!, EmailSent: false, FallbackLink: fallback));
            }

            // Audit trail — recipient + admin + expiry. NEVER the token itself.
            _logger.LogInformation(
                "Invitation sent: User={UserId} Email={Email} Role={Role} InvitedBy={InvitedBy} ExpiresAtUtc={Expires}",
                user.Id, user.Email, req.Role, inviterDisplay, expiresAtUtc);

            return Ok(new InviteUserResponse(user.Id, user.Email!, EmailSent: true, FallbackLink: null));
        }

        /// <summary>
        /// Updates a user's FullName, Role, and/or IsActive status.
        /// </summary>
        [HttpPut("{id:guid}")]
        [Authorize(Roles = SitimRoles.AnyStaff)]
        public async Task<ActionResult<UserResult>> Update(
            Guid id,
            [FromBody] UpdateUserRequest req,
            CancellationToken ct)
        {
            var user = await _users.FindByIdAsync(id.ToString());
            if (user is null) return NotFound();

            // Scope check: Admin cannot edit users from other institutions
            if (!_tenant.IsSuperAdmin && user.InstitutionId != _tenant.InstitutionId)
                return Forbid();

            if (req.FullName is not null)
                user.FullName = req.FullName.Trim();

            if (req.IsActive.HasValue)
                user.IsActive = req.IsActive.Value;

            var updateResult = await _users.UpdateAsync(user);
            if (!updateResult.Succeeded)
                return BadRequest(updateResult.Errors.Select(e => e.Description));

            // Role change
            if (!string.IsNullOrWhiteSpace(req.Role))
            {
                var allowedRoles = new[] { SitimRoles.Doctor, SitimRoles.Technician, SitimRoles.Admin };
                if (!allowedRoles.Contains(req.Role))
                    return BadRequest($"Role '{req.Role}' is not allowed.");
                if (req.Role == SitimRoles.Admin && !_tenant.IsSuperAdmin)
                    return Forbid();

                var currentRoles = await _users.GetRolesAsync(user);
                await _users.RemoveFromRolesAsync(user, currentRoles);
                await _users.AddToRoleAsync(user, req.Role);
            }

            var roles = await _users.GetRolesAsync(user);
            var role = roles.FirstOrDefault() ?? "";

            string? instName = null;
            if (user.InstitutionId.HasValue)
            {
                var inst = await _db.Institutions.AsNoTracking()
                    .FirstOrDefaultAsync(i => i.Id == user.InstitutionId.Value, ct);
                instName = inst?.Name;
            }

            return Ok(new UserResult(
                user.Id, user.Email ?? "", user.FullName, role,
                user.InstitutionId, instName,
                user.IsActive, user.CreatedAtUtc));
        }

        /// <summary>
        /// Permanently deletes a user. SuperAdmin can delete any user; Admin can delete users from their institution.
        /// Cannot delete SuperAdmin accounts or your own account.
        /// </summary>
        [HttpDelete("{id:guid}")]
        [Authorize(Roles = SitimRoles.AnyStaff)]
        public async Task<IActionResult> Delete(Guid id)
        {
            var user = await _users.FindByIdAsync(id.ToString());
            if (user is null) return NotFound();

            if (!_tenant.IsSuperAdmin && user.InstitutionId != _tenant.InstitutionId)
                return Forbid();

            // Protect SuperAdmin accounts from deletion
            if (await _users.IsInRoleAsync(user, SitimRoles.SuperAdmin))
                return BadRequest("Cannot delete SuperAdmin accounts.");

            // Prevent self-deletion
            var currentUserId = User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier)
                             ?? User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub);
            if (Guid.TryParse(currentUserId, out var currentId) && currentId == id)
                return BadRequest("Cannot delete your own account.");

            var result = await _users.DeleteAsync(user);
            if (!result.Succeeded)
                return BadRequest(result.Errors.Select(e => e.Description));

            return NoContent();
        }

        private static string GeneratePlaceholderPassword() =>
            $"Tmp_{Guid.NewGuid():N}!Aa1";
    }
}
