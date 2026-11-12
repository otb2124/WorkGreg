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

    /// <summary>What is entered into application forms for a profile: standard values and custom "question -> answer" rules.</summary>
    public DbSet<AutofillField> AutofillFields => Set<AutofillField>();

    /// <summary>Files (CV, cover-letter file) attached to application forms, one per profile and kind.</summary>
    public DbSet<AutofillDocument> AutofillDocuments => Set<AutofillDocument>();

    /// <summary>Application attempts and the applied/skipped tracker. History: survives job purges and profile deletion.</summary>
    public DbSet<Application> Applications => Set<Application>();

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

        // Autofill: SearchProfile -> AutofillField / AutofillDocument, cascade delete (the user's settings go with the profile).
        b.Entity<AutofillField>().HasOne(f => f.SearchProfile).WithMany()
            .HasForeignKey(f => f.SearchProfileId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AutofillField>().Property(f => f.Kind).HasConversion<string>();
        b.Entity<AutofillField>().Property(f => f.Key).HasConversion<string>();
        b.Entity<AutofillField>().Property(f => f.MatchBy).HasConversion<string>();
        b.Entity<AutofillField>().Property(f => f.Pattern).HasMaxLength(AutofillLimits.PatternMaxLength);
        b.Entity<AutofillField>().Property(f => f.Value).HasMaxLength(AutofillLimits.CoverLetterMaxLength);
        // One standard row per profile and key. Custom rows have Key = NULL and are not constrained (hence the filter).
        b.Entity<AutofillField>().HasIndex(f => new { f.SearchProfileId, f.Key })
            .IsUnique().HasFilter("\"Key\" IS NOT NULL");

        b.Entity<AutofillDocument>().HasOne(d => d.SearchProfile).WithMany()
            .HasForeignKey(d => d.SearchProfileId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<AutofillDocument>().Property(d => d.Kind).HasConversion<string>();
        // Uploading a CV replaces the previous one.
        b.Entity<AutofillDocument>().HasIndex(d => new { d.SearchProfileId, d.Kind }).IsUnique();

        // Applications are history: deleting a job (retention) or a profile must NOT delete them, only detach them.
        b.Entity<Application>().HasOne(a => a.SearchProfile).WithMany()
            .HasForeignKey(a => a.SearchProfileId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Application>().HasOne(a => a.Job).WithMany()
            .HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Application>().Property(a => a.Ats).HasConversion<string>();
        b.Entity<Application>().Property(a => a.Status).HasConversion<string>();
        // One application per user and vacancy. SQLite treats NULLs as distinct, so rows whose job was purged do not collide.
        b.Entity<Application>().HasIndex(a => new { a.OwnerChatId, a.JobId }).IsUnique();
        b.Entity<Application>().HasIndex(a => new { a.OwnerChatId, a.Status });
    }
}