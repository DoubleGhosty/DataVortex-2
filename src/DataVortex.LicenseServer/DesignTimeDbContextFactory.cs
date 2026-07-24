using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DataVortex.LicenseServer;

/// <summary>Lets <c>dotnet ef migrations</c> build the context at design time WITHOUT running the app host — so
/// none of the startup code (Migrate, seeding, signing-key init) executes and no live database is needed.
/// The connection string here only selects the SQLite provider for the model builder.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LicenseDbContext>
{
    public LicenseDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseSqlite("Data Source=datavortex_licenses.db")
            .Options;
        return new LicenseDbContext(options);
    }
}
