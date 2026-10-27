using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sitim.Api.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Sitim.Api.Security
{
    public sealed record ViewerTokenPayload(
        Guid UserId,
        Guid? InstitutionId,
        string StudyInstanceUid,
        bool IsSuperAdmin);

    public interface IViewerTokenService
    {
        (string token, int expiresInSeconds) CreateViewerToken(
            Guid userId,
            Guid? institutionId,
            string studyInstanceUid,
            bool isSuperAdmin);

        ViewerTokenPayload? ValidateViewerToken(string token);
    }

    /// <summary>
    /// Short-lived token used only by OHIF to call the secure DICOMweb proxy.
    /// </summary>
    public sealed class ViewerTokenService : IViewerTokenService
    {
        private const string ViewerAudience = "SITIM.Viewer";
        private const int ViewerTokenMinutes = 15;

        private readonly JwtOptions _opt;
        private readonly TokenValidationParameters _validation;
        private readonly JwtSecurityTokenHandler _handler = new();

        public ViewerTokenService(IOptions<JwtOptions> opt)
        {
            _opt = opt.Value;
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey));

            _validation = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateIssuerSigningKey = true,
                ValidateLifetime = true,
                ValidIssuer = _opt.Issuer,
                ValidAudience = ViewerAudience,
                IssuerSigningKey = key,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        }

        public (string token, int expiresInSeconds) CreateViewerToken(
            Guid userId,
            Guid? institutionId,
            string studyInstanceUid,
            bool isSuperAdmin)
        {
            if (string.IsNullOrWhiteSpace(studyInstanceUid))
                throw new ArgumentException("studyInstanceUid is required", nameof(studyInstanceUid));

            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(ViewerTokenMinutes);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new("study_uid", studyInstanceUid),
                new("is_superadmin", isSuperAdmin ? "true" : "false")
            };

            if (institutionId.HasValue)
                claims.Add(new Claim("institution_id", institutionId.Value.ToString()));

            var creds = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opt.SigningKey)),
                SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _opt.Issuer,
                audience: ViewerAudience,
                claims: claims,
                notBefore: now,
                expires: expires,
                signingCredentials: creds);

            var encoded = _handler.WriteToken(token);
            return (encoded, (int)(expires - now).TotalSeconds);
        }

        public ViewerTokenPayload? ValidateViewerToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return null;

            try
            {
                var principal = _handler.ValidateToken(token, _validation, out _);

                var sub = principal.FindFirstValue(JwtRegisteredClaimNames.Sub)
                    ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
                var studyUid = principal.FindFirstValue("study_uid");
                var institutionClaim = principal.FindFirstValue("institution_id");
                var isSuperAdmin = string.Equals(
                    principal.FindFirstValue("is_superadmin"),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                if (!Guid.TryParse(sub, out var userId) || string.IsNullOrWhiteSpace(studyUid))
                    return null;

                Guid? institutionId = null;
                if (Guid.TryParse(institutionClaim, out var parsedInstitutionId))
                    institutionId = parsedInstitutionId;

                return new ViewerTokenPayload(
                    UserId: userId,
                    InstitutionId: institutionId,
                    StudyInstanceUid: studyUid,
                    IsSuperAdmin: isSuperAdmin);
            }
            catch
            {
                return null;
            }
        }
    }
}

