using System.Net.Http.Json;
using Muthur.Contracts;

namespace Muthur.Server.Tests;

public static class HubTestExtensions
{
    public static async Task<HttpClient> RegisterAgentAsync(this HubFactory hub, string name, string harness = "claude", string model = "opus", string? tier = null)
    {
        var response = await hub.CreateClient().PostAsJsonAsync(Routes.AgentRegister, new RegisterAgentRequest(name, harness, model, tier));
        response.EnsureSuccessStatusCode();
        var registered = await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.RegisterAgentResponse);
        return hub.CreateClient(registered!.Token);
    }

    public static async Task<ProjectDto> AddProjectAsync(this HubFactory hub, string key = "demo", string? repoPath = null, LandMode landMode = LandMode.Merge,
        string[]? validators = null, string[]? ingest = null)
    {
        var response = await hub.Founder().PostAsJsonAsync(Routes.Projects,
            new AddProjectRequest(key, repoPath ?? hub.DataDir, LandMode: landMode, RequiredValidators: validators ?? [], IngestSources: ingest ?? []));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ProjectDto))!;
    }

    public static async Task<TaskDto> AddTaskAsync(this HttpClient client, string title, string? project = null, int priority = 0)
    {
        var response = await client.PostAsJsonAsync(Routes.Tasks, new AddTaskRequest(title, project, Priority: priority));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.TaskDto))!;
    }

    public static Task<HttpResponseMessage> PostActionAsync<T>(this HttpClient client, string taskId, string action, T body) =>
        client.PostAsJsonAsync(Routes.TaskAction(taskId, action), body);

    public static Task<HttpResponseMessage> ClaimAsync(this HttpClient client, string taskId, int? leaseMinutes = null) =>
        client.PostActionAsync(taskId, "claim", new ClaimTaskRequest(leaseMinutes));

    public static async Task<TaskDetailDto> GetTaskAsync(this HttpClient client, string taskId) =>
        (await client.GetFromJsonAsync(Routes.Task(taskId), MuthurJsonContext.Default.TaskDetailDto))!;

    public static async Task<TaskDto> ReadTaskAsync(this HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.TaskDto))!;
    }

    public static async Task<ErrorResponse> ReadErrorAsync(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync(MuthurJsonContext.Default.ErrorResponse))!;
}
