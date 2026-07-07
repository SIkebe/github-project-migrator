using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

/// <summary>
/// Integration tests for collaborator and linked-repository import (PLAN §4).
/// The GraphQL API has no read field for project collaborators, so the snapshot is
/// hand-authored here and application is verified through the mutation succeeding
/// (the API validates user/team ids) plus the absence of warnings; linked
/// repositories are read back via export. A temporary team is created through the
/// REST API and deleted in a finally block, as is the imported project.
/// Requires the GPM_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class CollaboratorImportTests
{
    private static string FixtureRepo => IntegrationTestSettings.FixtureRepositoryFullName;

    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    [Fact]
    public async Task Import_applies_collaborators_and_linked_repositories_and_warns_on_unresolvable_entries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var viewerData = await client.QueryAsync("query { viewer { login } }", null, cancellationToken);
        var viewerLogin = viewerData.GetProperty("viewer").GetProperty("login").GetString()!;

        var teamSlug = "gpm-collab-test-" + Guid.NewGuid().ToString("N")[..8];
        var missingUser = "gpm-missing-user-" + Guid.NewGuid().ToString("N")[..8];
        var missingRepo = SourceOrg + "/gpm-missing-repo-" + Guid.NewGuid().ToString("N")[..8];

        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot
            {
                Title = "gpm-collab-test-" + Guid.NewGuid().ToString("N"),
                Public = false,
                Closed = false,
            },
            Fields = [],
            Views = [],
            Workflows = [],
            Items = [],
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "USER", Login = "source-owner", Role = "WRITER" }, // resolved through the user mapping
                new CollaboratorSnapshot { Type = "TEAM", Login = teamSlug, Role = "READER" },
                new CollaboratorSnapshot { Type = "USER", Login = missingUser, Role = "READER" }, // warning + skip
            ],
            LinkedRepositories = [FixtureRepo, missingRepo], // the second one: warning + skip
        };

        using var rest = CreateRestClient();
        await CreateTeamAsync(rest, SourceOrg, teamSlug, cancellationToken);

        var importer = new ProjectImporter(client)
        {
            UserMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["source-owner"] = viewerLogin },
        };

        string? projectId = null;
        try
        {
            var result = await importer.ImportAsync(snapshot, SourceOrg, cancellationToken);
            projectId = result.ProjectId;

            // The mapped user and the temporary team were applied without warnings
            // (updateProjectV2Collaborators validates the ids); only the fabricated
            // user and repository were skipped.
            Assert.Contains(importer.Warnings, w => w.Contains(missingUser, StringComparison.Ordinal));
            Assert.Contains(importer.Warnings, w => w.Contains(missingRepo, StringComparison.Ordinal));
            Assert.Equal(2, importer.Warnings.Count);

            // Linked repositories can be read back through export
            // (collaborators cannot: the API has no read field for them).
            var exporter = new ProjectExporter(client);
            var readBack = await exporter.ExportAsync(SourceOrg, result.ProjectNumber, cancellationToken);
            Assert.NotNull(readBack.LinkedRepositories);
            Assert.Equal([FixtureRepo], readBack.LinkedRepositories);
            Assert.Null(readBack.Collaborators);
        }
        finally
        {
            if (projectId is not null)
            {
                await client.QueryAsync(
                    "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
                    new { projectId },
                    CancellationToken.None);
            }

            await DeleteTeamAsync(rest, SourceOrg, teamSlug);
        }
    }

    private static HttpClient CreateRestClient()
    {
        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("gpm-tests");
        return http;
    }

    /// <summary>Creates an org team (no GraphQL mutation exists for team creation).</summary>
    private static async Task CreateTeamAsync(HttpClient rest, string org, string slug, CancellationToken cancellationToken)
    {
        using var content = new StringContent(
            JsonSerializer.Serialize(new { name = slug, privacy = "closed" }),
            Encoding.UTF8,
            "application/json");
        using var response = await rest.PostAsync(new Uri($"orgs/{org}/teams", UriKind.Relative), content, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task DeleteTeamAsync(HttpClient rest, string org, string slug)
    {
        using var response = await rest.DeleteAsync(new Uri($"orgs/{org}/teams/{slug}", UriKind.Relative), CancellationToken.None);
        // Best-effort cleanup; the slug is unique per run so a 404 means it was never created.
    }
}
