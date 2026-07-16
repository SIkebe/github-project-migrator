using Gpm.Core.Browser;

namespace Gpm.Core.Tests;

public class BrowserBaseUrlTests
{
    [Theory]
    [InlineData(null, "https://github.com")]
    [InlineData("https://api.github.com/graphql", "https://github.com")]
    [InlineData("https://api.tenant.ghe.com/graphql", "https://tenant.ghe.com")]
    public void Resolve_derives_web_url_from_api_url(string? apiUrl, string expected)
    {
        var apiUri = apiUrl is null ? null : new Uri(apiUrl);

        Assert.Equal(expected, BrowserBaseUrl.Resolve(apiUri));
    }

    [Fact]
    public void Resolve_accepts_matching_explicit_url_and_removes_trailing_slash()
    {
        var result = BrowserBaseUrl.Resolve(
            new Uri("https://api.tenant.ghe.com/graphql"),
            " https://TENANT.ghe.com/ ");

        Assert.Equal("https://tenant.ghe.com", result);
    }

    [Theory]
    [InlineData("https://github.com/path")]
    [InlineData("ftp://github.com")]
    [InlineData("not-a-url")]
    public void NormalizeStandalone_rejects_invalid_web_origin(string baseUrl)
    {
        Assert.Throws<FormatException>(() => BrowserBaseUrl.NormalizeStandalone(baseUrl));
    }

    [Theory]
    [InlineData("https://api.tenant.ghe.com/graphql", "https://other.ghe.com")]
    [InlineData("https://api.github.com/graphql", "https://tenant.ghe.com")]
    [InlineData("https://api.tenant.ghe.com/graphql", "http://tenant.ghe.com")]
    public void Resolve_rejects_mismatched_api_and_web_urls(string apiUrl, string webUrl)
    {
        Assert.Throws<ArgumentException>(() => BrowserBaseUrl.Resolve(new Uri(apiUrl), webUrl));
    }

    [Fact]
    public void Resolve_rejects_unsupported_api_host()
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserBaseUrl.Resolve(new Uri("https://github.example.com/api/graphql")));
    }
}
