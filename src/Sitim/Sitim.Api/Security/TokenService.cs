using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Sitim.Api.Options;
using Sitim.Infrastructure.Identity;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Sitim.Api.Security
{
    public interface ITokenService
    {
        (string token, int expiresInSeconds) CreateAccessToken(
            ApplicationUser user,
            IReadOnlyList<string> roles,
            Guid? institutionId = null,
            string? institutionSlug = null);
    }

    public sealed class TokenService : ITokenService
    {
        private readonly JwtOptions _opt;

        public TokenService(IOptions<JwtOptions> opt)
        {
            _opt = opt.Value;
        }

        public (string token, int expiresInSeconds) CreateAccessToken(
            ApplicationUser user,
            IReadOnlyList<string> roles,
            Guid? institutionId = null,
            string? institutionSlug = null)
        {
            var now = DateTime.UtcNow;
            var expires = now.AddMinutes(_opt.AccessTokenMinutes);

            var claims = new List<Claim>
            {
                new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
                new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            };

            foreach (var r in roles)
                claims.Add(new Claim(ClaimTypes.Role, r));

            // Add institution claims when user belongs to an institution
            if (institutionId.HasValue)
                claims.Add(new Claim("institution_id", institutionId.Value.ToString()));

            if (!string.IsNullOrWhiteSpace(institutionSlug))
                claims.Add(new Claim("institution_slug", institutionSlug));

            var keyBytes = Encoding.UTF8.GetBytes(_opt.SigningKey);
            var key = new SymmetricSecurityKey(keyBytes);
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _opt.Issuer,
                audience: _opt.Audience,
                claims: claims,
                notBefore: now,
                expires: expires,
                signingCredentials: creds);

            var encoded = new JwtSecurityTokenHandler().WriteToken(token);
            var expiresInSeconds = (int)(expires - now).TotalSeconds;

            return (encoded, expiresInSeconds);
        }
    }

}
