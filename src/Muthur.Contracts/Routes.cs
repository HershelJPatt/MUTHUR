namespace Muthur.Contracts;

public static class Routes
{
    public const string Api = "/api/v1";

    public const string Status = Api + "/status";
    public const string Doctor = Api + "/doctor";
    public const string Receipts = Api + "/receipts";
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

    public const string Roles = Api + "/roles";
    public static string Role(string key) => $"{Roles}/{Uri.EscapeDataString(key)}";
    public static string RoleAction(string key, string action) => $"{Role(key)}/{action}";

    public const string Validations = Api + "/validations";
    public const string ValidationQueue = Validations + "/queue";

    public const string Messages = Api + "/messages";
    public const string Inbox = Messages + "/inbox";

    public const string Requests = Api + "/requests";
    public static string RequestAction(int id, string action) => $"{Requests}/{id}/{action}";

    public const string Tiers = Api + "/harness/tiers";
    public const string Conductor = Api + "/conductor";
    public const string ConductorSessions = Api + "/conductor/sessions";
    public const string AccountLimits = Api + "/harness/limits";
    public const string WorkerRuns = Api + "/workers/runs";

    public const string Inbound = Api + "/inbound";
    public static string InboundAction(string id, string action) => $"{Inbound}/{Uri.EscapeDataString(id)}/{action}";
    public const string IngestSources = Api + "/ingest/sources";
    public const string IngestPoll = Api + "/ingest/poll";

    public const string Outbound = Api + "/outbound";
    public const string OutboundTargets = Api + "/outbound-targets";
    public static string OutboundAction(string id, string action) => $"{Outbound}/{Uri.EscapeDataString(id)}/{action}";

    public const string Events = Api + "/events";
}
