using Muthur.Contracts;
using Muthur.Core;
using Muthur.Core.Entities;

namespace Muthur.Core.Tests;

public sealed class TaskStateMachineTests
{
    [Theory]
    [InlineData(TaskState.Backlog, TaskState.InProgress, true)]
    [InlineData(TaskState.InProgress, TaskState.Validating, true)]
    [InlineData(TaskState.Validating, TaskState.InProgress, true)]
    [InlineData(TaskState.Validated, TaskState.Done, true)]
    [InlineData(TaskState.Backlog, TaskState.Done, false)]
    [InlineData(TaskState.InProgress, TaskState.Done, false)]
    [InlineData(TaskState.Validating, TaskState.Done, false)]
    [InlineData(TaskState.Done, TaskState.Backlog, false)]
    [InlineData(TaskState.Cancelled, TaskState.Backlog, true)]
    public void Transitions(TaskState from, TaskState to, bool allowed) =>
        Assert.Equal(allowed, TaskStateMachine.CanTransition(from, to));

    [Fact]
    public void Done_is_only_reachable_through_validated()
    {
        foreach (var from in Wire.AllTaskStates.Where(s => s != TaskState.Validated))
            Assert.False(TaskStateMachine.CanTransition(from, TaskState.Done), $"{from} must not reach done directly");
    }

    [Fact]
    public void Illegal_transition_is_a_rule_violation()
    {
        var ex = Assert.Throws<MuthurException>(() => TaskStateMachine.EnsureCanTransition("T-1", TaskState.Backlog, TaskState.Done));
        Assert.Equal(ErrorKind.RuleViolation, ex.Kind);
        Assert.Equal("invalid_transition", ex.Code);
    }

    [Fact]
    public void Wire_names_round_trip()
    {
        foreach (var state in Wire.AllTaskStates)
        {
            Assert.True(Wire.TryParseTaskState(state.ToWire(), out var parsed));
            Assert.Equal(state, parsed);
        }
        Assert.True(Wire.TryParseTaskState("in-progress", out var dashed));
        Assert.Equal(TaskState.InProgress, dashed);
        Assert.True(Wire.TryParseTaskId("t-42", out var id));
        Assert.Equal(42, id);
        Assert.False(Wire.TryParseTaskId("T-0", out _));
    }

    [Fact]
    public void Claimability_depends_on_state_and_lease()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        WorkTask Task(TaskState state, DateTimeOffset? expires) => new() { Title = "t", State = state, ClaimExpires = expires };

        Assert.True(LeasePolicy.IsClaimable(Task(TaskState.Backlog, null), now));
        Assert.False(LeasePolicy.IsClaimable(Task(TaskState.InProgress, now.AddMinutes(1)), now));
        Assert.True(LeasePolicy.IsClaimable(Task(TaskState.InProgress, now), now));
        Assert.False(LeasePolicy.IsClaimable(Task(TaskState.Validating, null), now));
        Assert.False(LeasePolicy.IsClaimable(Task(TaskState.Blocked, now.AddHours(-5)), now));
    }

    [Fact]
    public void Requested_lease_is_capped()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), LeasePolicy.Default.ResolveClaimLease(null));
        Assert.Equal(LeasePolicy.MaxClaimLease, LeasePolicy.Default.ResolveClaimLease(100_000));
        Assert.Throws<MuthurException>(() => LeasePolicy.Default.ResolveClaimLease(0));
    }
}
