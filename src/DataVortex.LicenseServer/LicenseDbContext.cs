using Microsoft.EntityFrameworkCore;

namespace DataVortex.LicenseServer;

public sealed class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<AuthLog> AuthLogs => Set<AuthLog>();
    public DbSet<SigningKeyRecord> SigningKeys => Set<SigningKeyRecord>();
    public DbSet<Admin> Admins => Set<Admin>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<License>().HasIndex(l => l.KeyHash).IsUnique();
        b.Entity<Device>().HasIndex(d => d.FingerprintHash);
        b.Entity<Activation>().HasIndex(a => a.LicenseId);
        b.Entity<Session>().HasIndex(s => s.LicenseId);
        b.Entity<Session>().HasIndex(s => s.ExpiresAt);
        b.Entity<AuthLog>().HasIndex(a => a.At);
        b.Entity<SigningKeyRecord>().HasKey(k => k.Kid);
        b.Entity<Admin>().HasIndex(a => a.Email).IsUnique();
    }
}
