using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;
using Muthur.Data;
using Muthur.Launch;
using Muthur.Server.Auth;
using Muthur.Server.Services;

namespace Muthur.Server.Infrastructure;

public static class Startup
{
    public static MuthurOptions AddMuthur(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection("Muthur").Get<MuthurOptions>() ?? new MuthurOptions();
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddSingleton(options);
        Directory.CreateDirectory(options.DataDir);

        builder.WebHost.UseUrls(options.Url);
        // CreateBuilder registers Console, Debug, EventSource and — on Windows — EventLog. The Event Log is the
        // problem: the hub never reads it, nothing in this repository does, and a session with ordinary user rights
        // or a sandbox that denies it fails at a layer that has nothing to do with what MUTHUR does. Start from
        // nothing and add back the two that have a reader.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        // A factory, not AddProvider(instance): nobody disposes an instance the container did not create, so the
        // handle on muthur.log survived the hub and was released only by finalization. The creator is the disposer.
        builder.Logging.Services.AddSingleton<ILoggerProvider>(
            _ => new FileLoggerProvider(Path.Combine(options.DataDir, MuthurEnvironment.LogFile)));

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<InstanceInfo>();
        builder.Services.AddDbContextFactory<MuthurDb>((sp, db) =>
        {
            var o = sp.GetRequiredService<MuthurOptions>();
            switch (o.DbProvider.ToLowerInvariant())
            {
                case "sqlite":
                    db.UseSqlite(o.ResolveConnectionString());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Muthur:DbProvider '{o.DbProvider}'.");
            }
        });

        builder.Services.AddSingleton(new LeasePolicy(
            TimeSpan.FromMinutes(options.ClaimLeaseMinutes),
            TimeSpan.FromMinutes(options.RoleLeaseMinutes),
            TimeSpan.FromSeconds(options.AgentStaleSeconds)));
        builder.Services.AddSingleton<EventFeed>();
        builder.Services.AddSingleton<Ledger>();
        builder.Services.AddSingleton<AgentService>();
        builder.Services.AddSingleton<ProjectService>();
        builder.Services.AddSingleton<TaskService>();
        builder.Services.AddSingleton<TaskUnitService>();
        builder.Services.AddSingleton<EventService>();
        builder.Services.AddSingleton<RoleService>();
        builder.Services.AddSingleton<BriefFileReader>();
        builder.Services.AddSingleton<LifecycleService>();
        builder.Services.AddSingleton<MessageService>();
        builder.Services.AddSingleton<RequestService>();
        builder.Services.AddSingleton<FounderAttention>();
        builder.Services.AddSingleton<HarnessService>();
        builder.Services.AddSingleton<InboundService>();
        builder.Services.AddSingleton<OutboundService>();
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton<IOutboundChannel, FileChannel>();
        builder.Services.AddSingleton<IOutboundChannel, DiscordWebhookChannel>();
        builder.Services.AddSingleton<IOutboundChannel, GitHubIssueChannel>();
        builder.Services.AddSingleton<IngestService>();
        builder.Services.AddSingleton<PushService>();
        builder.Services.AddSingleton<IInboundSource, GitHubIssuesSource>();
        builder.Services.AddSingleton<IInboundSource, DiscordChannelSource>();
        builder.Services.AddSingleton<IInboundSource, CensusSource>();
        builder.Services.AddSingleton<ICensusCommandRunner, CensusCommandRunner>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IValidatorSessionLauncher, ValidatorSessionLauncher>();
        builder.Services.AddSingleton<IOrchestratorSessionLauncher, OrchestratorSessionLauncher>();
        builder.Services.AddSingleton<ConductorService>();
        builder.Services.AddSingleton<OverseerService>();
        builder.Services.AddSingleton<CollisionService>();
        builder.Services.AddSingleton<IPullRequestOpener, GhPullRequestOpener>();
        builder.Services.AddSingleton<ITaskLander, GitLander>();
        builder.Services.AddSingleton<DoctorService>();
        builder.Services.AddSingleton<ReceiptsService>();
        // Registered in the order DoctorService reports them, so the list reads like the report.
        builder.Services.AddSingleton<IDoctorCheck, DoctorAgentCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorHarnessCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorCapabilityCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorIngestCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorLoggingCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorOutboundCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorProjectCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorRepoCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorPushCheck>();
        builder.Services.AddSingleton<IDoctorCheck, DoctorRoleCheck>();
        if (options.BackgroundServices)
        {
            builder.Services.AddHostedService<LeaseSweeper>();
            builder.Services.AddHostedService<PushWorker>();
            builder.Services.AddHostedService<IngestWorker>();
            builder.Services.AddHostedService<ConductorWorker>();
        }

        builder.Services.AddRazorComponents().AddInteractiveServerComponents();

        builder.Services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.TypeInfoResolverChain.Insert(0, MuthurJsonContext.Default);
            // Agents pay per token: no nulls, and no escape sequences for plain punctuation.
            json.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            json.SerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        });

        return options;
    }

    /// <summary>Copies the database aside, migrates it, and establishes instance identity. Runs before the server accepts requests.</summary>
    public static async Task InitializeMuthurAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MuthurOptions>();
        var clock = app.Services.GetRequiredService<TimeProvider>();
        var instance = app.Services.GetRequiredService<InstanceInfo>();
        var backupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseBackup));
        var stopping = app.Lifetime.ApplicationStopping;

        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
            await DatabaseBackup.BeforeMigratingAsync(db, options, clock, backupLogger, stopping);
            await db.Database.MigrateAsync();
            if (db.Database.IsSqlite())
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");

            var id = await db.Meta.FindAsync(MetaEntry.InstanceId);
            if (id is null)
            {
                id = new MetaEntry { Key = MetaEntry.InstanceId, Value = Guid.NewGuid().ToString("n") };
                db.Meta.Add(id);
                db.Meta.Add(new MetaEntry { Key = MetaEntry.CreatedAt, Value = clock.GetUtcNow().ToString("O") });
                await db.SaveChangesAsync();
            }
            instance.InstanceId = id.Value;
        }

        app.Services.GetRequiredService<HarnessService>().EnsureCatalogExists();
        await app.Services.GetRequiredService<OverseerService>().RecoverAfterRestartAsync(stopping);

        instance.StartedAt = clock.GetUtcNow();
        instance.FounderToken = LoadOrCreateFounderToken(options.DataDir);

        var pidFile = Path.Combine(options.DataDir, MuthurEnvironment.PidFile);
        await File.WriteAllTextAsync(pidFile, Environment.ProcessId.ToString());
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            try { File.Delete(pidFile); } catch (IOException) { }
        });
    }

    private static string LoadOrCreateFounderToken(string dataDir)
    {
        var path = Path.Combine(dataDir, MuthurEnvironment.FounderTokenFile);
        if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } existing)
            return existing;
        var token = Tokens.Generate();
        File.WriteAllText(path, token);
        return token;
    }
}
