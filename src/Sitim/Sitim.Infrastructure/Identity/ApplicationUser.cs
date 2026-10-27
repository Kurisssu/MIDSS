using Microsoft.AspNetCore.Identity;

namespace Sitim.Infrastructure.Identity
{
    /// <summary>
    /// Application user (Identity) using Guid as primary key.
    /// </summary>
    public sealed class ApplicationUser : IdentityUser<Guid>
    {
        /// <summary>
        /// The institution this user belongs to.
        /// Null only for SuperAdmin users (platform-level administrators).
        /// </summary>
        public Guid? InstitutionId { get; set; }

        /// <summary>Full display name of the user.</summary>
        public string? FullName { get; set; }

        /// <summary>Whether the user account is active. Inactive users cannot log in.</summary>
        public bool IsActive { get; set; } = true;

        /// <summary>UTC timestamp when the account was created.</summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
