using System.Globalization;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

/// <summary>
/// M4 integration tests: imports snapshot items into the target org (gpm-target) via the
/// real GraphQL API. Covers the full round-trip (drafts with titles, Status values and
/// order), resume through <c>import-log.json</c>, repository-mapped Issue items and the
/// unmapped-repository warning path. Every created project is deleted in a finally block.
/// Requires the GPM_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ItemImporterTests
{
    private const int FixtureProjectNumber = 3;
    private const string FixtureRepo = "gpm-source/fixture-repo";

    private static string SourceOrg => Environment.GetEnvironmentVariable("GPM_TEST_ORG") ?? "gpm-source";

    private static string TargetOrg => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_ORG") ?? "gpm-target";

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static string NewTestTitle() => "gpm-item-import-test-" + Guid.NewGuid().ToString("N");

    private static Dictionary<string, string> IdentityRepoMapping => new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureRepo] = FixtureRepo,
    };

    [Fact]
    public async Task Round_trip_imports_drafts_with_values_and_order_and_resume_skips_existing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);

        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var title = NewTestTitle();
        var snapshot = source with { Project = source.Project with { Title = title } };

        var result = await new ProjectImporter(client).ImportAsync(snapshot, TargetOrg, cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("gpm-m4-").FullName;
        try
        {
            var itemImporter = new ItemImporter(client) { RepositoryMapping = IdentityRepoMapping };
            var itemResult = await itemImporter.ImportAsync(snapshot, result, logDirectory, cancellationToken);

            // Every fixture item is mappable (drafts + fixture-repo issues), so everything is created.
            Assert.Equal(snapshot.Items.Count, itemResult.Created);
            Assert.Equal(0, itemResult.Skipped);

            var imported = await ExportUntilItemCountAsync(exporter, TargetOrg, result.ProjectNumber, snapshot.Items.Count, cancellationToken);
            Assert.Equal(snapshot.Items.Count, imported.Items.Count);

            // Overall order matches the snapshot (drafts and issues interleaved as in the source).
            Assert.Equal(
                snapshot.Items.OrderBy(i => i.Position).Select(ItemKey),
                imported.Items.OrderBy(i => i.Position).Select(ItemKey));

            // The three fixture drafts keep their titles, Status values and relative order.
            var sourceDrafts = snapshot.Items.OrderBy(i => i.Position).Where(i => i.Type == "DRAFT_ISSUE").ToList();
            var importedDrafts = imported.Items.OrderBy(i => i.Position).Where(i => i.Type == "DRAFT_ISSUE").ToList();
            Assert.Equal(3, sourceDrafts.Count);
            Assert.Equal(sourceDrafts.Select(i => i.Draft!.Title), importedDrafts.Select(i => i.Draft!.Title));
            Assert.Equal(sourceDrafts.Select(StatusOf), importedDrafts.Select(StatusOf));

            // The attribution note was prepended to every draft body.
            Assert.All(importedDrafts, draft =>
                Assert.StartsWith("> _Originally created by @", draft.Draft!.Body, StringComparison.Ordinal));

            // Resume: a second run against the same log directory creates nothing new.
            var resumeResult = await new ItemImporter(client) { RepositoryMapping = IdentityRepoMapping }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(0, resumeResult.Created);
            Assert.Equal(snapshot.Items.Count, resumeResult.Skipped);

            var afterResume = await exporter.ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Equal(imported.Items.Count, afterResume.Items.Count);
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
            TryDeleteDirectory(logDirectory);
        }
    }

    [Fact]
    public async Task Issue_items_are_relinked_through_repository_mapping_and_unmapped_repos_are_skipped()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);

        // The fixture project has no Issue item, so stage one in a temporary source project.
        var sourceOrgId = await GetOrganizationIdAsync(client, SourceOrg, cancellationToken);
        var (sourceProjectId, sourceProjectNumber) = await CreateProjectAsync(client, sourceOrgId, NewTestTitle(), cancellationToken);
        string? targetProjectId = null;
        var skipLogDirectory = Directory.CreateTempSubdirectory("gpm-m4-skip-").FullName;
        var logDirectory = Directory.CreateTempSubdirectory("gpm-m4-issue-").FullName;
        try
        {
            var issueId = await GetIssueIdAsync(client, "gpm-source", "fixture-repo", 1, cancellationToken);
            await client.QueryAsync(
                """
                mutation($projectId: ID!, $contentId: ID!) {
                  addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) { item { id } }
                }
                """,
                new { projectId = sourceProjectId, contentId = issueId },
                cancellationToken);

            var source = await ExportUntilItemCountAsync(exporter, SourceOrg, sourceProjectNumber, expectedCount: 1, cancellationToken);
            var sourceIssue = Assert.Single(source.Items, i => i.Type == "ISSUE");
            Assert.Equal(FixtureRepo, sourceIssue.Repository);

            var snapshot = source with { Project = source.Project with { Title = NewTestTitle() } };
            var result = await new ProjectImporter(client).ImportAsync(snapshot, TargetOrg, cancellationToken);
            targetProjectId = result.ProjectId;

            // Without a repository mapping the issue item is skipped with a warning.
            var skipResult = await new ItemImporter(client).ImportAsync(snapshot, result, skipLogDirectory, cancellationToken);
            Assert.Equal(0, skipResult.Created);
            Assert.Equal(1, skipResult.Skipped);
            Assert.Contains(skipResult.Warnings, w => w.Contains(FixtureRepo, StringComparison.Ordinal));

            // With the identity mapping the issue is resolved and linked cross-org.
            var itemResult = await new ItemImporter(client) { RepositoryMapping = IdentityRepoMapping }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(1, itemResult.Created);
            Assert.Equal(0, itemResult.Skipped);

            var imported = await ExportUntilItemCountAsync(exporter, TargetOrg, result.ProjectNumber, expectedCount: 1, cancellationToken);
            var importedIssue = Assert.Single(imported.Items, i => i.Type == "ISSUE");
            Assert.Equal(FixtureRepo, importedIssue.Repository);
            Assert.Equal(1, importedIssue.Number);
        }
        finally
        {
            await DeleteProjectAsync(client, sourceProjectId);
            if (targetProjectId is not null)
            {
                await DeleteProjectAsync(client, targetProjectId);
            }

            TryDeleteDirectory(skipLogDirectory);
            TryDeleteDirectory(logDirectory);
        }
    }

    private static string? StatusOf(ItemSnapshot item)
        => item.FieldValues.FirstOrDefault(v => v.FieldName == "Status")?.SingleSelectOptionName;

    private static string ItemKey(ItemSnapshot item) => item.Type == "DRAFT_ISSUE"
        ? "draft:" + item.Draft!.Title
        : string.Create(CultureInfo.InvariantCulture, $"{item.Type}:{item.Repository}#{item.Number}");

    /// <summary>Items are eventually consistent after writes; poll until the expected count is visible.</summary>
    private static async Task<ProjectSnapshot> ExportUntilItemCountAsync(
        ProjectExporter exporter, string org, int projectNumber, int expectedCount, CancellationToken cancellationToken)
    {
        ProjectSnapshot snapshot = null!;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            snapshot = await exporter.ExportAsync(org, projectNumber, cancellationToken);
            if (snapshot.Items.Count >= expectedCount)
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return snapshot;
    }

    private static async Task<string> GetOrganizationIdAsync(GitHubGraphQLClient client, string login, CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login },
            cancellationToken);
        return data.GetProperty("organization").GetProperty("id").GetString()!;
    }

    private static async Task<(string Id, int Number)> CreateProjectAsync(
        GitHubGraphQLClient client, string ownerId, string title, CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            """
            mutation($ownerId: ID!, $title: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title }) { projectV2 { id number } }
            }
            """,
            new { ownerId, title },
            cancellationToken);
        var project = data.GetProperty("createProjectV2").GetProperty("projectV2");
        return (project.GetProperty("id").GetString()!, project.GetProperty("number").GetInt32());
    }

    private static async Task<string> GetIssueIdAsync(
        GitHubGraphQLClient client, string owner, string name, int number, CancellationToken cancellationToken)
    {
        var data = await client.QueryAsync(
            "query($owner: String!, $name: String!, $number: Int!) { repository(owner: $owner, name: $name) { issue(number: $number) { id } } }",
            new { owner, name, number },
            cancellationToken);
        return data.GetProperty("repository").GetProperty("issue").GetProperty("id").GetString()!;
    }

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp log directory.
        }
    }
}
