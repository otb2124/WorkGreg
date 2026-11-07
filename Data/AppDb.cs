using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace WorkGreg.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Job> Jobs => Set<Job>();

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
        // Ownership chain: SearchProfile -> Source -> Job. Deleting a parent deletes everything below it in the database.
        // (EF Core's SQLite provider turns PRAGMA foreign_keys on for every connection.)
        b.Entity<Source>().HasOne(s => s.SearchProfile).WithMany()
            .HasForeignKey(s => s.SearchProfileId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Job>().HasOne<Source>().WithMany()
            .HasForeignKey(j => j.SourceId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<Source>().Property(s => s.Type).HasConversion<string>();
        // A provider can be attached to a profile only once (NULL providers = legacy raw-URL rows, not constrained).
        b.Entity<Source>().HasIndex(s => new { s.SearchProfileId, s.Provider }).IsUnique();

        b.Entity<Job>().HasIndex(j => new { j.SourceId, j.ExternalId }).IsUnique();
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Hash });
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Processed });

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
