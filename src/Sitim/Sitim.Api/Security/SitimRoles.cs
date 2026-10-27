namespace Sitim.Api.Security
{
    public static class SitimRoles
    {
        public const string SuperAdmin = "SuperAdmin";
        public const string Admin = "Admin";
        public const string Doctor = "Doctor";
        public const string Technician = "Technician";

        // Common role bundles for [Authorize(Roles="...")]
        // SuperAdmin is included in all bundles – sees all data across tenants (no Query Filter applied).
        public const string AnyStaff = SuperAdmin + "," + Admin + "," + Doctor + "," + Technician;
        public const string CanImport = SuperAdmin + "," + Admin + "," + Doctor + "," + Technician;
        public const string CanAnalyze = SuperAdmin + "," + Admin + "," + Doctor + "," + Technician;

        // Platform-level operations (SuperAdmin only)
        public const string PlatformAdmin = SuperAdmin;
    }
}
