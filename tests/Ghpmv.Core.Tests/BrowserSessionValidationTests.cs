using Ghpmv.Core.Browser;

namespace Ghpmv.Core.Tests;

public class BrowserSessionValidationTests
{
    [Fact]
    public void Authentication_result_accepts_matching_login_on_expected_host()
    {
        BrowserSession.EnsureAuthenticationResult(
            "https://tenant.ghe.com",
            "https://tenant.ghe.com/",
            "octocat",
            "OctoCat");
    }

    [Fact]
    public void Authentication_result_rejects_mismatched_login()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BrowserSession.EnsureAuthenticationResult(
                "https://tenant.ghe.com",
                "https://tenant.ghe.com/",
                "monalisa",
                "octocat"));

        Assert.Contains("does not match API token account", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_result_rejects_missing_login()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BrowserSession.EnsureAuthenticationResult(
                "https://tenant.ghe.com",
                "https://tenant.ghe.com/",
                null,
                "octocat"));

        Assert.Contains("not signed in", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Authentication_result_rejects_cross_host_redirect()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            BrowserSession.EnsureAuthenticationResult(
                "https://tenant.ghe.com",
                "https://github.com/login",
                "octocat",
                "octocat"));

        Assert.Contains("left the configured GitHub host", exception.Message, StringComparison.Ordinal);
    }
}
