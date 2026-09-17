using System.Text.Json.Serialization;

namespace Muthur.Contracts;

/// <summary>Source-generated JSON metadata so the AOT-compiled CLI never needs reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(RegisterAgentRequest))]
[JsonSerializable(typeof(RegisterAgentResponse))]
[JsonSerializable(typeof(HeartbeatRequest))]
[JsonSerializable(typeof(LimitedRequest))]
[JsonSerializable(typeof(AgentDto))]
[JsonSerializable(typeof(IReadOnlyList<AgentDto>))]
[JsonSerializable(typeof(AddProjectRequest))]
[JsonSerializable(typeof(UpdateProjectRequest))]
[JsonSerializable(typeof(ProjectDto))]
[JsonSerializable(typeof(IReadOnlyList<ProjectDto>))]
[JsonSerializable(typeof(AddTaskRequest))]
[JsonSerializable(typeof(ClaimTaskRequest))]
[JsonSerializable(typeof(ReleaseTaskRequest))]
[JsonSerializable(typeof(SetSpecRequest))]
[JsonSerializable(typeof(SetPriorityRequest))]
[JsonSerializable(typeof(CancelTaskRequest))]
[JsonSerializable(typeof(TaskDto))]
[JsonSerializable(typeof(IReadOnlyList<TaskDto>))]
[JsonSerializable(typeof(TaskDetailDto))]
[JsonSerializable(typeof(EventDto))]
[JsonSerializable(typeof(IReadOnlyList<EventDto>))]
[JsonSerializable(typeof(DefineRoleRequest))]
[JsonSerializable(typeof(RoleDto))]
[JsonSerializable(typeof(IReadOnlyList<RoleDto>))]
[JsonSerializable(typeof(RoleBriefDto))]
[JsonSerializable(typeof(ImplementedRequest))]
[JsonSerializable(typeof(VerdictRequest))]
public sealed partial class MuthurJsonContext : JsonSerializerContext;
