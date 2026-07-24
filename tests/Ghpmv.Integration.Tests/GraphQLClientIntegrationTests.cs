using System.Net;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M1 integration tests against the real GitHub GraphQL API.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// Skipped when the variable is not set (e.g. fork PRs without secrets).
/// </summary>
public class GraphQLClientIntegrationTests
{
    private static string Org => IntegrationTestSettings.SourceOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    [Fact]
    public async Task Fixture_project_has_expected_title_and_custom_fields()
    {
        using var client = new GitHubGraphQLClient(Token);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            Org,
            IntegrationTestSettings.FixtureProjectNumber,
            TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Project.Title));
        var fieldNames = snapshot.Fields.Select(field => field.Name).ToList();

        string[] expected = ["Fixture Text", "Fixture Number", "Fixture Date", "Fixture Select", "Fixture Sprint", "Fixture Teams"];
        foreach (var name in expected)
        {
            Assert.Contains(name, fieldNames);
        }
    }

    [Fact]
    public async Task QueryPaginatedAsync_enumerates_120_items_across_real_pages()
    {
        using var client = new GitHubGraphQLClient(Token);
        var cancellationToken = TestContext.Current.CancellationToken;

        var orgData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = Org },
            cancellationToken);
        var ownerId = orgData.GetProperty("organization").GetProperty("id").GetString()!;

        var title = $"ghpmv-test-{Guid.NewGuid():N}";
        var createData = await client.QueryAsync(
            "mutation($ownerId: ID!, $title: String!) { createProjectV2(input: {ownerId: $ownerId, title: $title}) { projectV2 { id } } }",
            new { ownerId, title },
            cancellationToken);
        var projectId = createData.GetProperty("createProjectV2").GetProperty("projectV2").GetProperty("id").GetString()!;

        try
        {
            // Serial on purpose: parallel writes would trip the secondary rate limit.
            for (var i = 1; i <= 120; i++)
            {
                await client.QueryAsync(
                    "mutation($projectId: ID!, $title: String!) { addProjectV2DraftIssue(input: {projectId: $projectId, title: $title}) { projectItem { id } } }",
                    new { projectId, title = $"Draft {i:D3}" },
                    cancellationToken);
            }

            // The items connection is eventually consistent right after writes,
            // so poll until all 120 items are visible (up to ~75s).
            List<string?> itemIds = [];
            for (var attempt = 0; attempt < 16; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }

                itemIds = [];
                await foreach (var node in client.QueryPaginatedAsync(
                    """
                    query($projectId: ID!, $first: Int!, $after: String) {
                      node(id: $projectId) {
                        ... on ProjectV2 {
                          items(first: $first, after: $after) {
                            nodes { id }
                            pageInfo { hasNextPage endCursor }
                          }
                        }
                      }
                    }
                    """,
                    new { projectId, first = 50 },
                    "node.items",
                    cancellationToken: cancellationToken))
                {
                    itemIds.Add(node.GetProperty("id").GetString());
                }

                if (itemIds.Count >= 120)
                {
                    break;
                }
            }

            Assert.Equal(120, itemIds.Count);
            Assert.Equal(120, itemIds.Distinct().Count());
        }
        finally
        {
            await client.QueryAsync(
                "mutation($projectId: ID!) { deleteProjectV2(input: {projectId: $projectId}) { projectV2 { id } } }",
                new { projectId },
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task Invalid_token_fails_with_401_without_retrying()
    {
        _ = Token; // Skip when no real-API access is configured.

        using var client = new GitHubGraphQLClient("invalid-token");
        var retries = 0;
        client.OnRetry = _ => retries++;

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(0, retries);
    }
}
