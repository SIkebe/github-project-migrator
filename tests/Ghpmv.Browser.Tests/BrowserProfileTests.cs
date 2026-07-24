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

    [Fact]
    public void Fresh_login_context_does_not_load_existing_profile_state()
    {
        var path = Path.GetTempFileName();
        try
        {
            Assert.Null(BrowserSession.ResolveStorageStatePath(loadStoredState: false, path));
            Assert.Equal(path, BrowserSession.ResolveStorageStatePath(loadStoredState: true, path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Expected_login_rejects_a_different_account()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => BrowserSession.EnsureExpectedLogin("previous-user", "source-user"));

        Assert.Contains("browser state was not saved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Expected_login_is_case_insensitive()
        => BrowserSession.EnsureExpectedLogin("Source-User", "source-user");
}
