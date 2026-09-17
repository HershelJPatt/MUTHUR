namespace Muthur.Contracts;

public static class Routes
{
    public const string Api = "/api/v1";

    public const string Status = Api + "/status";
    public const string Shutdown = Api + "/admin/shutdown";

    public const string Agents = Api + "/agents";
    public const string AgentRegister = Agents + "/register";
    public const string AgentHeartbeat = Agents + "/heartbeat";
    public const string AgentLimited = Agents + "/limited";
    public const string AgentMe = Agents + "/me";

    public const string Projects = Api + "/projects";
    public static string Project(string key) => $"{Projects}/{Uri.EscapeDataString(key)}";

    public const string Tasks = Api + "/tasks";
    public static string Task(string id) => $"{Tasks}/{Uri.EscapeDataString(id)}";
    public static string TaskAction(string id, string action) => $"{Task(id)}/{action}";

    public const string Events = Api + "/events";
}
