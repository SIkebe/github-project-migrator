using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;

namespace Gpm.Integration.Tests;

/// <summary>
/// User-owned project support: creates a temporary project owned by the viewer (the test
/// account) via the real API, exports it with <see cref="ProjectOwnerType.User"/>, deletes
/// the source, re-imports it as a user project and compares the read-back. Skipped when
/// the account cannot create user projects (e.g. a policy-restricted EMU account).
/// Every created project is deleted in a finally block.
/// </summary>
public class UserProjectTests
{
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
    public async Task User_project_export_import_round_trip()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var viewer = await client.QueryAsync("query { viewer { id login } }", cancellationToken: cancellationToken);
        var viewerId = viewer.GetProperty("viewer").GetProperty("id").GetString()!;
        var viewerLogin = viewer.GetProperty("viewer").GetProperty("login").GetString()!;

        var title = "gpm-user-test-" + Guid.NewGuid().ToString("N");
        string? sourceProjectId = null;
        string? importedProjectId = null;
        try
        {
            // Create a temporary user project with one custom field.
            int sourceNumber;
            try
            {
                var created = await client.QueryAsync(
                    """
                    mutation($ownerId: ID!, $title: String!) {
                      createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                        projectV2 { id number url }
                      }
                    }
                    """,
                    new { ownerId = viewerId, title },
                    cancellationToken);
                var project = created.GetProperty("createProjectV2").GetProperty("projectV2");
                sourceProjectId = project.GetProperty("id").GetString()!;
                sourceNumber = project.GetProperty("number").GetInt32();
                Assert.Contains($"/users/{viewerLogin}/projects/", project.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
            }
            catch (GitHubGraphQLException exception)
            {
                Assert.Skip($"Account '{viewerLogin}' cannot create user-owned projects ({exception.ErrorType ?? "error"}): {exception.Message}");
                return;
            }

            await client.QueryAsync(
                """
                mutation($projectId: ID!) {
                  createProjectV2Field(input: { projectId: $projectId, name: "User Text", dataType: TEXT }) {
                    projectV2Field { ... on ProjectV2FieldCommon { id } }
                  }
                }
                """,
                new { projectId = sourceProjectId },
                cancellationToken);

            // Export as a user project.
            var exporter = new ProjectExporter(client) { OwnerType = ProjectOwnerType.User };
            var snapshot = await exporter.ExportAsync(viewerLogin, sourceNumber, cancellationToken);
            Assert.Equal(title, snapshot.Project.Title);
            Assert.Contains(snapshot.Fields, f => f.Name == "User Text" && f.DataType == "TEXT");

            // The user's project list must include the temporary project.
            var listed = await exporter.ListProjectsAsync(viewerLogin, includeClosed: false, cancellationToken);
            Assert.Contains(listed, p => p.Number == sourceNumber && p.Title == title);

            // Delete the source so the re-import does not hit the title-conflict path.
            await DeleteProjectAsync(client, sourceProjectId);
            sourceProjectId = null;

            // Import the snapshot back as a user project and read it back.
            var importer = new ProjectImporter(client)
            {
                OwnerType = ProjectOwnerType.User,
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            };
            var result = await importer.ImportAsync(snapshot, viewerLogin, cancellationToken);
            importedProjectId = result.ProjectId;

            Assert.True(result.Created);
            Assert.Contains($"/users/{viewerLogin}/projects/", result.Url, StringComparison.OrdinalIgnoreCase);
            Assert.True(result.FieldIds.ContainsKey("User Text"));

            var readBack = await exporter.ExportAsync(viewerLogin, result.ProjectNumber, cancellationToken);
            Assert.Equal(title, readBack.Project.Title);
            Assert.Contains(readBack.Fields, f => f.Name == "User Text" && f.DataType == "TEXT");
        }
        finally
        {
            if (sourceProjectId is not null)
            {
                await DeleteProjectAsync(client, sourceProjectId);
            }

            if (importedProjectId is not null)
            {
                await DeleteProjectAsync(client, importedProjectId);
            }
        }
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }
}
