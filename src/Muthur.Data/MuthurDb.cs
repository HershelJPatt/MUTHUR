using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Muthur.Core.Entities;

namespace Muthur.Data;

public sealed class MuthurDb(DbContextOptions<MuthurDb> options) : DbContext(options)
{
    public DbSet<MetaEntry> Meta => Set<MetaEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Timestamps are stored as UTC unix milliseconds: comparable and orderable on every
        // provider (SQLite cannot compare DateTimeOffset natively).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetaEntry>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Key);
        });
    }
}

public sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.ToUnixTimeMilliseconds(),
    v => DateTimeOffset.FromUnixTimeMilliseconds(v));
