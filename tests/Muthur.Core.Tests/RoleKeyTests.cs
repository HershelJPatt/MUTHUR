using Muthur.Core;

namespace Muthur.Core.Tests;

/// <summary>
/// The one rule for what may name a role. Two callers depend on it — a project's required validators and the
/// conductor's agent identity — so its boundaries are pinned here rather than at either caller.
/// </summary>
public sealed class RoleKeyTests
{
    [Theory]
    [InlineData("win-validator")]
    [InlineData("a")]
    [InlineData("9")]
    [InlineData("comms-oncall")]
    [InlineData("a-b-c-1-2-3")]
    public void A_key_of_lowercase_digits_and_hyphens_is_legal(string key) => Assert.True(RoleKey.IsValid(key));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-leading-hyphen")]
    [InlineData("win.validator")]
    [InlineData("win_validator")]
    [InlineData("win validator")]
    [InlineData("Win-Validator")]
    [InlineData("win/validator")]
    [InlineData("win-validatör")]
    public void Anything_else_is_not(string? key) => Assert.False(RoleKey.IsValid(key));

    [Fact]
    public void The_length_boundary_is_MaxLength_and_the_character_after_it_is_one_too_many()
    {
        Assert.True(RoleKey.IsValid(new string('a', RoleKey.MaxLength)));
        Assert.False(RoleKey.IsValid(new string('a', RoleKey.MaxLength + 1)));
    }

    /// <summary>
    /// The message the founder reads has to name the limit the code enforces; two literals for one number is
    /// exactly the drift this type exists to prevent.
    /// </summary>
    [Fact]
    public void The_rule_the_founder_reads_names_the_length_the_code_enforces() =>
        Assert.Contains($"1-{RoleKey.MaxLength} ", RoleKey.Rule, StringComparison.Ordinal);
}
