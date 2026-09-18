using Muthur.Cli.Commands;

namespace Muthur.Cli.Tests;

/// <summary>
/// `validate list` has two optional filters, so either can be the first one in the query. Get the leading
/// '?' wrong and the hub reads no filter at all: the validator sees a list that is quietly the wrong one.
/// </summary>
public sealed class ValidationListQueryTests
{
    [Theory]
    [InlineData(null, false, "")]
    [InlineData(null, true, "?all=true")]
    [InlineData("win-validator", false, "?role=win-validator")]
    [InlineData("win-validator", true, "?role=win-validator&all=true")]
    public void Each_combination_of_filters_is_one_well_formed_query(string? role, bool all, string expected) =>
        Assert.Equal(expected, RoleCommands.ValidationListQuery(role, all));

    [Fact]
    public void A_role_key_is_escaped() =>
        Assert.Equal("?role=win%20validator%26all%3Dfalse&all=true", RoleCommands.ValidationListQuery("win validator&all=false", all: true));
}
