using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// Connectivity smoke tests against the real GitHub GraphQL API (M0).
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// Skipped when the variable is not set (e.g. fork PRs without secrets).
/// </summary>
public class ConnectivityTests
{
    internal static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    [Fact]
    public async Task Viewer_query_returns_authenticated_login()
    {
        using var client = new GitHubGraphQLClient(Token);

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(login));
    }

    [Fact]
    public async Task Source_org_is_reachable()
    {
        var org = IntegrationTestSettings.SourceOrg;
        using var client = new GitHubGraphQLClient(Token);

        var data = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { login } }",
            new { login = org },
            TestContext.Current.CancellationToken);

        Assert.Equal(org, data.GetProperty("organization").GetProperty("login").GetString());
    }
}
