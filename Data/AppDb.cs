using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Per-user settings (paused, delivery mode). Table name kept as "Profiles" to avoid a rename.</summary>
    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    /// <summary>Job hunts: keyword + filters + sources.</summary>
    public DbSet<SearchProfile> SearchProfiles => Set<SearchProfile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Source>().Property(s => s.Type).HasConversion<string>();
        // A provider can be attached to a profile only once (NULL providers = legacy raw-URL rows, not constrained).
        b.Entity<Source>().HasIndex(s => new { s.SearchProfileId, s.Provider }).IsUnique();

        b.Entity<Job>().HasIndex(j => new { j.SourceId, j.ExternalId }).IsUnique();
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Hash });
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Processed });

        b.Entity<SearchProfile>().HasIndex(p => p.OwnerChatId);

        b.Entity<UserProfile>().HasKey(p => p.ChatId);
        b.Entity<UserProfile>().Property(p => p.ChatId).ValueGeneratedNever();

        b.Entity<UserProfile>().Property(p => p.Delivery).HasConversion<string>();
    }
}
