using Ghpmv.Core.Browser;

namespace Ghpmv.Browser.Tests;

/// <summary>Storage-state path resolution for cross-account browser profiles.</summary>
public class BrowserProfileTests
{
    [Fact]
    public void Profile_maps_to_a_dedicated_state_file()
    {
        var path = BrowserSession.DefaultStatePath("source");

        Assert.EndsWith(Path.Combine("ghpmv", "browser-state.source.json"), path, StringComparison.Ordinal);
    }

    [Fact]
    public void Profiles_do_not_collide()
    {
        Assert.NotEqual(BrowserSession.DefaultStatePath("source"), BrowserSession.DefaultStatePath("target"));
    }

    [Fact]
    public void Explicit_state_path_wins_over_profile()
    {
        var session = new BrowserSession(new BrowserSessionOptions
        {
            StatePath = "C:/tmp/custom.json",
            Profile = "source",
        });

        Assert.Equal("C:/tmp/custom.json", session.StatePath);
    }
}
