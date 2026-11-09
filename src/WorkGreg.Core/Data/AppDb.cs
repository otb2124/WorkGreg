using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WorkGreg.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();

    /// <summary>Raw vacancies, stored once per (provider, external id). Nothing in here is per-user.</summary>
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Per-user state of a vacancy (filters evaluated? message sent?). One row per source that found the job.</summary>
    public DbSet<JobDelivery> JobDeliveries => Set<JobDelivery>();

    /// <summary>Per-user settings (paused, delivery mode).</summary>
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();

    /// <summary>Job hunts: keyword + filters + sources.</summary>
    public DbSet<SearchProfile> SearchProfiles => Set<SearchProfile>();

    // List<string> <-> "a, b, c" in a TEXT column: the same format the columns already hold, so no data migration.
    private static readonly ValueConverter<List<string>, string> ListConverter = new(
        list => FilterList.Join(list),
        text => FilterList.Parse(text));

    private static readonly ValueComparer<List<string>> ListComparer = new(
        (a, b) => ReferenceEquals(a, b) || (a != null && b != null && a.SequenceEqual(b)),
        list => list.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
        list => list.ToList());

    private static PropertyBuilder<List<string>> AsList(PropertyBuilder<List<string>> property) =>
        property.HasConversion(ListConverter, ListComparer);

    protected override void OnModelCreating(ModelBuilder b)
    {
        // Ownership chain: SearchProfile -> Source -> JobDelivery. Deleting a parent deletes everything below it in the database.
        // A Job is NOT owned by anybody: it is shared by every delivery that points at it. Deleting a delivery leaves the job
        // alone; RetentionService removes jobs nobody references any more. Deleting a job (retention only) takes its deliveries along.
        // (EF Core's SQLite provider turns PRAGMA foreign_keys on for every connection.)
        b.Entity<Source>().HasOne(s => s.SearchProfile).WithMany()
            .HasForeignKey(s => s.SearchProfileId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<JobDelivery>().HasOne(d => d.Job).WithMany()
            .HasForeignKey(d => d.JobId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<JobDelivery>().HasOne<Source>().WithMany()
            .HasForeignKey(d => d.SourceId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Source>().Property(s => s.Type).HasConversion<string>();
        // A provider can be attached to a profile only once (NULL providers = legacy raw-URL rows, not constrained).
        b.Entity<Source>().HasIndex(s => new { s.SearchProfileId, s.Provider }).IsUnique();

        // A vacancy is identified by who published it and the id they gave it.
        b.Entity<Job>().HasIndex(j => new { j.Provider, j.ExternalId }).IsUnique();
        // "Has this user already been sent this vacancy under another board?" joins deliveries to jobs by hash.
        b.Entity<Job>().HasIndex(j => j.Hash);

        // One delivery per source and job; JobId gets its own index (EF adds it for the foreign key) for the retention orphan check.
        b.Entity<JobDelivery>().HasIndex(d => new { d.SourceId, d.JobId }).IsUnique();
        b.Entity<JobDelivery>().HasIndex(d => new { d.OwnerChatId, d.Processed });

        b.Entity<SearchProfile>().HasIndex(p => p.OwnerChatId);
        AsList(b.Entity<SearchProfile>().Property(p => p.Include));
        AsList(b.Entity<SearchProfile>().Property(p => p.Exclude));
        AsList(b.Entity<SearchProfile>().Property(p => p.Companies));
        AsList(b.Entity<SearchProfile>().Property(p => p.Locations));

        b.Entity<UserSettings>().HasKey(p => p.ChatId);
        b.Entity<UserSettings>().Property(p => p.ChatId).ValueGeneratedNever();
        b.Entity<UserSettings>().Property(p => p.Delivery).HasConversion<string>();
    }
}