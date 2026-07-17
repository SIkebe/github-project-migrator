using System.Globalization;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M4 integration tests: imports snapshot items into the target org (ghpmv-target) via the
/// real GraphQL API. Covers the full round-trip (drafts with titles, Status values and
/// order), resume through <c>import-log.json</c>, repository-mapped Issue items and the
/// unmapped-repository warning path. Every created project is deleted in a finally block.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ItemImporterTests
{
    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;
    private static string FixtureRepo => IntegrationTestSettings.FixtureRepositoryFullName;

    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

    private static string TargetOrg => IntegrationTestSettings.TargetOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static string NewTestTitle() => "ghpmv-item-import-test-" + Guid.NewGuid().ToString("N");

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

        var exported = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var source = IntegrationFixtureSnapshot.SelectCanonicalItems(exported);
        var title = NewTestTitle();
        var snapshot = source with { Project = source.Project with { Title = title } };

        // Identity user mapping so the fixture's assigned draft keeps its assignee.
        var userMapping = snapshot.Items
            .SelectMany(i => i.Draft?.Assignees ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(login => login, login => login, StringComparer.OrdinalIgnoreCase);

        // The fixture links the source fixture repository; map it to the target org's clone.
        var linkMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [FixtureRepo] = IntegrationTestSettings.TargetFixtureRepositoryFullName,
        };

        var projectImporter = new ProjectImporter(client)
        {
            RepositoryMapping = linkMapping,
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        };
        var result = await projectImporter.ImportAsync(snapshot, TargetOrg, cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-m4-").FullName;
        try
        {
            Assert.Empty(projectImporter.Warnings); // the mapped linked repository must resolve

            var itemImporter = new ItemImporter(client) { RepositoryMapping = IdentityRepoMapping, UserMapping = userMapping };
            var itemResult = await itemImporter.ImportAsync(snapshot, result, logDirectory, cancellationToken);
            await IntegrationFixtureSnapshot.RemoveUnexpectedItemsAsync(
                client, TargetOrg, result.ProjectNumber, snapshot, cancellationToken);

            // Every fixture item is mappable (drafts + fixture-repo issue/PR), so everything is created.
            Assert.Equal(snapshot.Items.Count, itemResult.Created);
            Assert.Equal(0, itemResult.Skipped);

            var imported = await ExportUntilItemCountAsync(exporter, TargetOrg, result.ProjectNumber, snapshot.Items.Count, cancellationToken);
            Assert.Equal(snapshot.Items.Count, imported.Items.Count);

            // The linked repository was remapped to the target org.
            Assert.NotNull(imported.LinkedRepositories);
            Assert.Contains(IntegrationTestSettings.TargetFixtureRepositoryFullName, imported.LinkedRepositories, StringComparer.OrdinalIgnoreCase);

            // Non-archived items keep the snapshot order (archived items are excluded from
            // the position chain, so their enumeration position is not guaranteed).
            Assert.Equal(
                snapshot.Items.Where(i => !i.IsArchived).OrderBy(i => i.Position).Select(ItemKey),
                imported.Items.Where(i => !i.IsArchived).OrderBy(i => i.Position).Select(ItemKey));

            // The five fixture drafts keep their titles, Status values and relative order.
            var sourceDrafts = snapshot.Items.OrderBy(i => i.Position).Where(i => i.Type == "DRAFT_ISSUE" && !i.IsArchived).ToList();
            var importedDrafts = imported.Items.OrderBy(i => i.Position).Where(i => i.Type == "DRAFT_ISSUE" && !i.IsArchived).ToList();
            Assert.Equal(4, sourceDrafts.Count);
            Assert.Equal(sourceDrafts.Select(i => i.Draft!.Title), importedDrafts.Select(i => i.Draft!.Title));
            Assert.Equal(sourceDrafts.Select(StatusOf), importedDrafts.Select(StatusOf));

            // Every typed field value round-trips on draft 1 (text with non-ASCII, number, date, select, iteration).
            var sourceDraft1 = Assert.Single(sourceDrafts, i => i.Draft!.Title == "Fixture draft 1");
            var importedDraft1 = Assert.Single(importedDrafts, i => i.Draft!.Title == "Fixture draft 1");
            foreach (var fieldName in (string[])["Fixture Text", "Fixture Number", "Fixture Date", "Fixture Select", "Fixture Sprint"])
            {
                var expected = sourceDraft1.FieldValues.Single(v => v.FieldName == fieldName);
                var actual = importedDraft1.FieldValues.Single(v => v.FieldName == fieldName);
                Assert.Equal(expected, actual);
            }

            // The fixture's Issue and PR items were relinked (identity mapping, cross-org).
            var importedIssue = Assert.Single(imported.Items, i => i.Type == "ISSUE");
            Assert.Equal(FixtureRepo, importedIssue.Repository);
            Assert.Equal(1, importedIssue.Number);
            var importedPullRequest = Assert.Single(imported.Items, i => i.Type == "PULL_REQUEST");
            Assert.Equal(FixtureRepo, importedPullRequest.Repository);

            // The archived draft was re-archived in the target.
            var importedArchived = Assert.Single(imported.Items, i => i.IsArchived);
            Assert.Equal("Fixture archived draft", importedArchived.Draft?.Title);

            // The assigned draft kept its assignee through the identity user mapping.
            var sourceAssigned = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture assigned draft");
            var importedAssigned = Assert.Single(imported.Items, i => i.Draft?.Title == "Fixture assigned draft");
            Assert.NotEmpty(sourceAssigned.Draft!.Assignees);
            Assert.Equal(sourceAssigned.Draft.Assignees, importedAssigned.Draft!.Assignees);

            // The attribution note was prepended to every draft body.
            Assert.All(importedDrafts, draft =>
                Assert.StartsWith("> _Originally created by @", draft.Draft!.Body, StringComparison.Ordinal));

            // Resume: a second run against the same log directory creates nothing new.
            var resumeResult = await new ItemImporter(client) { RepositoryMapping = IdentityRepoMapping, UserMapping = userMapping }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(0, resumeResult.Created);
            Assert.Equal(snapshot.Items.Count, resumeResult.AlreadyComplete);
            Assert.Equal(0, resumeResult.Skipped);

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
        var skipLogDirectory = Directory.CreateTempSubdirectory("ghpmv-m4-skip-").FullName;
        var logDirectory = Directory.CreateTempSubdirectory("ghpmv-m4-issue-").FullName;
        try
        {
            var issueId = await GetIssueIdAsync(client, SourceOrg, IntegrationTestSettings.FixtureRepositoryName, 1, cancellationToken);
            await client.QueryAsync(
                """
                mutation($projectId: ID!, $contentId: ID!) {
                  addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) { item { id } }
                }
                """,
                new { projectId = sourceProjectId, contentId = issueId },
                cancellationToken);

            var exported = await ExportUntilAsync(
                exporter,
                SourceOrg,
                sourceProjectNumber,
                snapshot => snapshot.Items.Any(item => item.Type == "ISSUE" && item.Number == 1),
                cancellationToken);
            var sourceIssue = Assert.Single(exported.Items, i => i.Type == "ISSUE" && i.Number == 1);
            Assert.Equal(FixtureRepo, sourceIssue.Repository);

            var snapshot = exported with
            {
                Project = exported.Project with { Title = NewTestTitle() },
                Items = [sourceIssue with { Position = 0 }],
            };
            var result = await new ProjectImporter(client)
            {
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            }.ImportAsync(snapshot, TargetOrg, cancellationToken);
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
            await IntegrationFixtureSnapshot.RemoveUnexpectedItemsAsync(
                client, TargetOrg, result.ProjectNumber, snapshot, cancellationToken);

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
    private static Task<ProjectSnapshot> ExportUntilItemCountAsync(
        ProjectExporter exporter, string org, int projectNumber, int expectedCount, CancellationToken cancellationToken)
        => ExportUntilAsync(
            exporter,
            org,
            projectNumber,
            snapshot => snapshot.Items.Count >= expectedCount,
            cancellationToken);

    private static async Task<ProjectSnapshot> ExportUntilAsync(
        ProjectExporter exporter,
        string org,
        int projectNumber,
        Func<ProjectSnapshot, bool> predicate,
        CancellationToken cancellationToken)
    {
        ProjectSnapshot snapshot = null!;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            snapshot = await exporter.ExportAsync(org, projectNumber, cancellationToken);
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        throw new InvalidOperationException($"Project #{projectNumber} did not reach the expected item state.");
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
