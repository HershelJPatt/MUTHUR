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
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(options.DataDir, MuthurEnvironment.LogFile)));

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
        builder.Services.AddSingleton<EventService>();
        builder.Services.AddSingleton<RoleService>();
        builder.Services.AddSingleton<LifecycleService>();
        builder.Services.AddSingleton<MessageService>();
        builder.Services.AddSingleton<RequestService>();
        builder.Services.AddSingleton<HarnessService>();
        builder.Services.AddSingleton<InboundService>();
        builder.Services.AddSingleton<IngestService>();
        builder.Services.AddSingleton<IInboundSource, GitHubIssuesSource>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IPullRequestOpener, GhPullRequestOpener>();
        builder.Services.AddSingleton<ITaskLander, GitLander>();
        if (options.BackgroundServices)
        {
            builder.Services.AddHostedService<LeaseSweeper>();
            builder.Services.AddHostedService<IngestWorker>();
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

    /// <summary>Migrates the database and establishes instance identity. Runs before the server accepts requests.</summary>
    public static async Task InitializeMuthurAsync(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<MuthurOptions>();
        var clock = app.Services.GetRequiredService<TimeProvider>();
        var instance = app.Services.GetRequiredService<InstanceInfo>();

        await using (var db = await app.Services.GetRequiredService<IDbContextFactory<MuthurDb>>().CreateDbContextAsync())
        {
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
