using WorkGreg.Domain;
using Microsoft.EntityFrameworkCore;

namespace WorkGreg.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<Source> Sources => Set<Source>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<UserProfile> Profiles => Set<UserProfile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Source>().Property(s => s.Type).HasConversion<string>();

        b.Entity<Job>().HasIndex(j => new { j.SourceId, j.ExternalId }).IsUnique();
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Hash });
        b.Entity<Job>().HasIndex(j => new { j.OwnerChatId, j.Processed });

        b.Entity<UserProfile>().HasKey(p => p.ChatId);
        b.Entity<UserProfile>().Property(p => p.ChatId).ValueGeneratedNever();

        b.Entity<UserProfile>().Property(p => p.Delivery).HasConversion<string>();
    }
}
