using Muthur.Server.Api;
using Muthur.Server.Auth;
using Muthur.Server.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.AddMuthur();

var app = builder.Build();
await app.InitializeMuthurAsync();

app.UseAntiforgery();
app.MapStaticAssets();
app.UseMiddleware<ErrorMiddleware>();
app.UseMiddleware<CallerMiddleware>();
app.MapSystemEndpoints();
app.MapAgentEndpoints();
app.MapProjectEndpoints();
app.MapTaskEndpoints();
app.MapRoleEndpoints();
app.MapLifecycleEndpoints();

app.MapRazorComponents<Muthur.Server.Components.App>().AddInteractiveServerRenderMode();

await app.RunAsync();

public partial class Program;
