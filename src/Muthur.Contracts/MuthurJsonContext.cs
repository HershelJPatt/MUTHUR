using System.Text.Json.Serialization;

namespace Muthur.Contracts;

/// <summary>Source-generated JSON metadata so the AOT-compiled CLI never needs reflection.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(StatusResponse))]
public sealed partial class MuthurJsonContext : JsonSerializerContext;
