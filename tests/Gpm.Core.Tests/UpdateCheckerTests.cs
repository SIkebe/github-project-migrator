using Gpm.Core;

namespace Gpm.Core.Tests;

public class UpdateCheckerTests
{
    [Theory]
    // Newer versions.
    [InlineData("0.1.0", "0.1.1", true)]
    [InlineData("0.1.0", "0.2.0", true)]
    [InlineData("0.1.0", "1.0.0", true)]
    [InlineData("0.9.9", "0.10.0", true)]
    [InlineData("1.9.0", "1.10.0", true)]
    [InlineData("0.1.9", "0.1.10", true)]
    // Same version.
    [InlineData("0.1.0", "0.1.0", false)]
    // Older versions.
    [InlineData("0.1.1", "0.1.0", false)]
    [InlineData("1.0.0", "0.9.9", false)]
    [InlineData("0.10.0", "0.9.9", false)]
    // "v" prefix and pre-release/build suffixes are ignored.
    [InlineData("v0.1.0", "v0.2.0", true)]
    [InlineData("0.1.0", "v0.1.1", true)]
    [InlineData("0.1.0-rc.1", "0.1.0", false)]
    [InlineData("0.1.0+abc123", "0.1.1", true)]
    [InlineData("0.1.0", "0.2.0-beta+sha", true)]
    public void IsNewer_ComparesMajorMinorPatch(string current, string latest, bool expected)
    {
        Assert.Equal(expected, UpdateChecker.IsNewer(current, latest));
    }

    [Theory]
    [InlineData("", "0.1.0")]
    [InlineData("0.1.0", "")]
    [InlineData("not-a-version", "0.1.0")]
    [InlineData("0.1.0", "not-a-version")]
    [InlineData("0.1", "0.2.0")]
    [InlineData("0.1.0", "0.2")]
    [InlineData("0.1.0.0", "0.2.0.0")]
    [InlineData("0.-1.0", "0.1.0")]
    public void IsNewer_ReturnsFalseForUnparseableInput(string current, string latest)
    {
        Assert.False(UpdateChecker.IsNewer(current, latest));
    }
}
