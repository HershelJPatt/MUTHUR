using Muthur.Contracts;
using Muthur.Data;

namespace Muthur.Server.Services;

/// <summary>Read helpers for per-task validation verdicts.</summary>
public static class Validations
{
    public static Task<Dictionary<int, IReadOnlyList<ValidationDto>>> ForTasksAsync(MuthurDb db, IReadOnlyList<int> taskIds, CancellationToken ct) =>
        Task.FromResult(new Dictionary<int, IReadOnlyList<ValidationDto>>());
}
