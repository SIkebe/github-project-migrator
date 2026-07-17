using Ghpmv.Core.Browser;

namespace Ghpmv.Core.Tests;

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
    [InlineData("http://github.com")]
    [InlineData("http://tenant.ghe.com")]
    [InlineData("https://github.com.attacker.example")]
    [InlineData("https://api.tenant.ghe.com")]
    [InlineData("https://nested.tenant.ghe.com")]
    [InlineData("https://example.com")]
    [InlineData("not-a-url")]
    public void NormalizeStandalone_rejects_invalid_web_origin(string baseUrl)
    {
        Assert.Throws<FormatException>(() => BrowserBaseUrl.NormalizeStandalone(baseUrl));
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    public void NormalizeStandalone_allows_http_only_for_loopback_tests(string input, string expected)
    {
        Assert.Equal(expected, BrowserBaseUrl.NormalizeStandalone(input));
    }

    [Fact]
    public void Resolve_rejects_cleartext_cloud_api_url()
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserBaseUrl.Resolve(new Uri("http://api.tenant.ghe.com/graphql")));
    }

    [Fact]
    public void Resolve_rejects_unsupported_scheme_on_loopback()
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserBaseUrl.Resolve(new Uri("ftp://localhost/graphql")));
    }

    [Theory]
    [InlineData("http://localhost:8080/graphql", null, "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080/graphql", "http://127.0.0.1:8080/", "http://127.0.0.1:8080")]
    public void Resolve_allows_matching_loopback_api_and_browser_origins(
        string apiUrl,
        string? browserUrl,
        string expected)
    {
        Assert.Equal(expected, BrowserBaseUrl.Resolve(new Uri(apiUrl), browserUrl));
    }

    [Fact]
    public void Resolve_rejects_mismatched_loopback_ports()
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserBaseUrl.Resolve(
                new Uri("http://localhost:8080/graphql"),
                "http://localhost:3000"));
    }

    [Theory]
    [InlineData("https://api.tenant.ghe.com/graphql", "https://other.ghe.com")]
    [InlineData("https://api.github.com/graphql", "https://tenant.ghe.com")]
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

    [Fact]
    public void Resolve_rejects_nested_ghe_api_host()
    {
        Assert.Throws<ArgumentException>(() =>
            BrowserBaseUrl.Resolve(new Uri("https://api.nested.tenant.ghe.com/graphql")));
    }
}
