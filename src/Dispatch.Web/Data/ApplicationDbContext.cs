using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Dispatch.Web.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Client> Clients => Set<Client>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<DriverRoute> Routes => Set<DriverRoute>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Stop> Stops => Set<Stop>();
    public DbSet<Proof> Proofs => Set<Proof>();
    public DbSet<DriverLocation> DriverLocations => Set<DriverLocation>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<CustomerNotification> CustomerNotifications => Set<CustomerNotification>();
    public DbSet<ClientIntegration> ClientIntegrations => Set<ClientIntegration>();
    public DbSet<IntakeLog> IntakeLogs => Set<IntakeLog>();
    public DbSet<ClosedDay> ClosedDays => Set<ClosedDay>();

    // Store enums as readable text ("Unassigned") rather than numbers
    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.HasSequence<int>("job_ref_seq").StartsAt(1000);

        b.Entity<Job>(e =>
        {
            e.Property(j => j.Ref).HasDefaultValueSql("'J-' || nextval('job_ref_seq')");
            e.HasIndex(j => j.Ref).IsUnique();
            e.HasIndex(j => new { j.Status, j.ZoneId });
            e.HasIndex(j => j.DriverId);
            // Order numbers are unique per client, so re-imported orders can't create duplicates
            e.HasIndex(j => new { j.ClientId, j.Source, j.ExternalId }).IsUnique();
            e.HasIndex(j => j.TrackingToken).IsUnique();
        });

        b.Entity<Stop>(e =>
        {
            e.HasIndex(s => new { s.JobId, s.Kind }).IsUnique();
            e.HasIndex(s => new { s.RouteId, s.Sequence });
        });

        b.Entity<DriverRoute>().HasIndex(r => new { r.DriverId, r.RouteDate }).IsUnique();
        b.Entity<Driver>().HasIndex(d => d.UserId).IsUnique();
        b.Entity<DriverLocation>().HasIndex(l => new { l.DriverId, l.RecordedAt });
        b.Entity<JobEvent>().HasIndex(e => new { e.JobId, e.CreatedAt });
        b.Entity<CustomerNotification>().HasIndex(n => new { n.JobId, n.Kind }).IsUnique();   // never the same text twice

        b.Entity<ClientIntegration>(e =>
        {
            e.HasIndex(i => i.PublicId).IsUnique();
            e.HasIndex(i => i.ApiKeyHash).IsUnique();
        });
        b.Entity<IntakeLog>().HasIndex(l => new { l.IntegrationId, l.ReceivedAt });
        b.Entity<ClosedDay>().HasIndex(d => d.Date).IsUnique();

        b.Entity<Zone>().HasData(new Zone { Id = 1, Name = "North" }, new Zone { Id = 2, Name = "South" });
    }
}
