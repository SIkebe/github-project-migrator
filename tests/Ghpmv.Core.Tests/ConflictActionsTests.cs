using Ghpmv.Core.Import;

namespace Ghpmv.Core.Tests;

public class ConflictActionsTests
{
    [Theory]
    [InlineData("skip", ConflictAction.Skip)]
    [InlineData("SKIP", ConflictAction.Skip)]
    [InlineData("update", ConflictAction.Update)]
    [InlineData("Update", ConflictAction.Update)]
    [InlineData("fail", ConflictAction.Fail)]
    [InlineData("FAIL", ConflictAction.Fail)]
    [InlineData(" skip ", ConflictAction.Skip)]
    public void TryParse_accepts_valid_values_case_insensitively(string value, ConflictAction expected)
    {
        Assert.True(ConflictActions.TryParse(value, out var action));
        Assert.Equal(expected, action);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("overwrite")]
    [InlineData("skip,update")]
    public void TryParse_rejects_invalid_values(string? value)
    {
        Assert.False(ConflictActions.TryParse(value, out _));
    }
}
