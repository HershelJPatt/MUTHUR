using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Muthur.Core.Entities;

namespace Muthur.Data;

public sealed class MuthurDb(DbContextOptions<MuthurDb> options) : DbContext(options)
{
    public DbSet<MetaEntry> Meta => Set<MetaEntry>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<WorkTask> Tasks => Set<WorkTask>();
    public DbSet<LedgerEvent> Events => Set<LedgerEvent>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleHold> RoleHolds => Set<RoleHold>();
    public DbSet<TaskValidation> TaskValidations => Set<TaskValidation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<FounderRequest> FounderRequests => Set<FounderRequest>();
    public DbSet<AccountLimit> AccountLimits => Set<AccountLimit>();
    public DbSet<InboundItem> Inbound => Set<InboundItem>();
    public DbSet<IngestCursor> IngestCursors => Set<IngestCursor>();
    public DbSet<OutboundTarget> OutboundTargets => Set<OutboundTarget>();
    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Timestamps are stored as UTC unix milliseconds: comparable and orderable on every
        // provider (SQLite cannot compare DateTimeOffset natively).
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UnixMillisecondsConverter>();
        configurationBuilder.Properties<Enum>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MetaEntry>(e =>
        {
            e.ToTable("meta");
            e.HasKey(x => x.Key);
        });

        modelBuilder.Entity<Project>(e =>
        {
            e.ToTable("projects");
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.RequiredValidators).HasConversion(StringListConverter.Instance, StringListConverter.Comparer);
            e.Property(x => x.IngestSources).HasConversion(StringListConverter.Instance, StringListConverter.Comparer);
        });

        modelBuilder.Entity<Agent>(e =>
        {
            e.ToTable("agents");
            e.HasIndex(x => x.Name).IsUnique();
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.Ignore(x => x.ModelLabel);
        });

        modelBuilder.Entity<WorkTask>(e =>
        {
            e.ToTable("tasks");
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Owner).WithMany().HasForeignKey(x => x.OwnerAgentId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.State);
            e.HasIndex(x => x.OwnerAgentId);
        });

        modelBuilder.Entity<LedgerEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Seq);
            e.HasIndex(x => x.TaskId);
        });

        modelBuilder.Entity<Role>(e =>
        {
            e.ToTable("roles");
            e.HasKey(x => x.Key);
        });

        modelBuilder.Entity<RoleHold>(e =>
        {
            e.ToTable("role_holds");
            e.HasKey(x => x.RoleKey); // one holder per role
            e.HasOne<Role>().WithOne().HasForeignKey<RoleHold>(x => x.RoleKey).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskValidation>(e =>
        {
            e.ToTable("task_validations");
            e.HasIndex(x => new { x.TaskId, x.ValidatorKey }).IsUnique();
            e.HasOne<WorkTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Agent).WithMany().HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Message>(e =>
        {
            e.ToTable("messages");
            e.HasIndex(x => new { x.ToKind, x.ToKey, x.ReadAt });
        });

        modelBuilder.Entity<FounderRequest>(e =>
        {
            e.ToTable("founder_requests");
            e.HasIndex(x => x.Status);
            e.Property(x => x.Options).HasConversion(StringListConverter.Instance, StringListConverter.Comparer);
        });

        modelBuilder.Entity<AccountLimit>(e =>
        {
            e.ToTable("account_limits");
            e.HasKey(x => x.Account);
        });

        modelBuilder.Entity<InboundItem>(e =>
        {
            e.ToTable("inbound");
            e.HasIndex(x => new { x.Source, x.ExternalId }).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Project).WithMany().HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.ClaimedBy).WithMany().HasForeignKey(x => x.ClaimedByAgentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<IngestCursor>(e =>
        {
            e.ToTable("ingest_cursors");
            e.HasKey(x => x.Source);
        });

        modelBuilder.Entity<OutboundTarget>(e =>
        {
            e.ToTable("outbound_targets");
            e.HasIndex(x => x.Key).IsUnique();
        });

        modelBuilder.Entity<OutboundMessage>(e =>
        {
            e.ToTable("outbound");
            e.HasIndex(x => x.Status);
            e.HasOne(x => x.Target).WithMany().HasForeignKey(x => x.TargetId).OnDelete(DeleteBehavior.Restrict);
        });

        ApplySnakeCaseColumns(modelBuilder);
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}

public sealed class UnixMillisecondsConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.ToUnixTimeMilliseconds(),
    v => DateTimeOffset.FromUnixTimeMilliseconds(v));

public static class StringListConverter
{
    public static readonly ValueConverter<List<string>, string> Instance = new(
        v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
        v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

    public static readonly ValueComparer<List<string>> Comparer = new(
        (a, b) => a != null && b != null && a.SequenceEqual(b),
        v => v.Aggregate(0, (h, s) => HashCode.Combine(h, s.GetHashCode())),
        v => v.ToList());
}
