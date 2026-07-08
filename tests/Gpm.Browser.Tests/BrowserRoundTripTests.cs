using Gpm.Core.Browser;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using System.Text.Json;

namespace Gpm.Browser.Tests;

/// <summary>
/// M6 E2E: exports the fixture project (gpm-source #3) including browser-scraped UI
/// settings, imports it into the target org (project + fields via GraphQL, views via
/// browser automation), re-exports the target and asserts the views round-trip
/// (name / layout / UI settings). Requires GPM_BROWSER_STATE (a storage-state file
/// saved by <c>gpm login</c>) and GPM_TEST_TOKEN; skipped otherwise.
/// The created project is deleted in a finally block.
/// </summary>
[Trait("Category", "E2E")]
public class BrowserRoundTripTests
{
    private const int FixtureProjectNumber = 3;
    private const string ExplicitCollaboratorLogin = "ravel-maurice-uo_sde";

    private static string SourceOrg => Environment.GetEnvironmentVariable("GPM_TEST_ORG") ?? "gpm-source";

    private static string TargetOrg => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_ORG") ?? "gpm-target";

    [Fact]
    public async Task Explicit_collaborators_are_exported_through_browser_automation()
    {
        var statePath = Environment.GetEnvironmentVariable("GPM_BROWSER_STATE");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath),
            "GPM_BROWSER_STATE is not set or the file does not exist; skipping browser E2E test.");
        var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(token!);
        var (projectId, userId) = await ResolveProjectAndUserIdsAsync(client, SourceOrg, ExplicitCollaboratorLogin, cancellationToken);

        await SetCollaboratorAsync(client, projectId, userId, "WRITER", cancellationToken);
        try
        {
            await using var session = new BrowserSession(new BrowserSessionOptions { StatePath = statePath });
            var exporter = new ProjectExporter(client);
            var snapshot = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
            var collaboratorExporter = new CollaboratorUiExporter(session);

            snapshot = await collaboratorExporter.EnrichAsync(snapshot, SourceOrg, ProjectOwnerType.Organization, FixtureProjectNumber, cancellationToken);

            Assert.Empty(collaboratorExporter.Warnings);
            var collaborator = Assert.Single(snapshot.Collaborators!, c =>
                string.Equals(c.Login, ExplicitCollaboratorLogin, StringComparison.OrdinalIgnoreCase));
            Assert.Equal("USER", collaborator.Type);
            Assert.Equal("WRITER", collaborator.Role);
        }
        finally
        {
            await SetCollaboratorAsync(client, projectId, userId, "NONE", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Views_round_trip_through_browser_automation()
    {
        var statePath = Environment.GetEnvironmentVariable("GPM_BROWSER_STATE");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath),
            "GPM_BROWSER_STATE is not set or the file does not exist; skipping browser E2E test.");
        var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(token!);
        await using var session = new BrowserSession(new BrowserSessionOptions { StatePath = statePath });

        // Export the fixture with UI settings and retarget it under a unique title.
        var exporter = new ProjectExporter(client);
        var uiExporter = new ViewUiExporter(session);
        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        source = await uiExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
        Assert.Empty(uiExporter.Warnings);
        Assert.All(source.Views, v => Assert.NotNull(v.Ui));

        // Explicit source expectations (fixture enrichment, 2026-07-06) — guards against
        // silently comparing null-to-null when the scrape misses a setting.
        var sourceTable = Assert.Single(source.Views, v => v.Name == "View 1");
        Assert.Equal("status:Todo", sourceTable.Filter);
        Assert.Equal("Fixture Number", Assert.Single(sourceTable.SortByFields).Field);
        Assert.NotNull(sourceTable.Ui!.SortBy);
        Assert.Equal("Fixture Select", sourceTable.Ui.SliceBy);

        var sourceBoard = Assert.Single(source.Views, v => v.Name == "Fixture Board");
        Assert.Equal("Fixture Select", Assert.Single(sourceBoard.VerticalGroupByFields));
        Assert.Equal("Status", sourceBoard.Ui!.Swimlanes);
        Assert.Equal(["Fixture Number"], sourceBoard.Ui.FieldSum);

        var sourceRoadmap = Assert.Single(source.Views, v => v.Name == "Fixture Roadmap");
        Assert.Equal("Quarter", sourceRoadmap.Ui!.Roadmap?.Zoom);
        Assert.Contains("Fixture Date", sourceRoadmap.Ui.Roadmap?.Markers ?? []);

        var title = "gpm-browser-test-" + Guid.NewGuid().ToString("N");
        var snapshot = source with { Project = source.Project with { Title = title } };

        var importer = new ProjectImporter(client);
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var viewImporter = new ViewUiImporter(session);
            await viewImporter.ImportAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(viewImporter.Warnings);

            // Re-export the target (GraphQL + UI scrape) and diff the views.
            var reExported = await exporter.ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);
            var reExportUi = new ViewUiExporter(session);
            reExported = await reExportUi.EnrichAsync(reExported, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(reExportUi.Warnings);

            Assert.Equal(snapshot.Views.Count, reExported.Views.Count);
            // Tab order re-creation is out of scope for v1 (PLAN §8.1) and target view
            // numbers are re-assigned, so views are matched by name instead of position.
            foreach (var expected in snapshot.Views)
            {
                var actual = Assert.Single(reExported.Views, v => string.Equals(v.Name, expected.Name, StringComparison.Ordinal));
                Assert.Equal(expected.Layout, actual.Layout);

                Assert.NotNull(expected.Ui);
                Assert.NotNull(actual.Ui);
                Assert.Equal(expected.Ui!.GroupBy, actual.Ui!.GroupBy);
                Assert.Equal(expected.Ui.SortBy, actual.Ui.SortBy);
                Assert.Equal(expected.Ui.SliceBy, actual.Ui.SliceBy);
                Assert.Equal(expected.Ui.Swimlanes, actual.Ui.Swimlanes);
                Assert.Equal(expected.Ui.FieldSum ?? [], actual.Ui.FieldSum ?? []);
                Assert.Equal(expected.Ui.Roadmap is null, actual.Ui.Roadmap is null);
                if (expected.Ui.Roadmap is { } roadmap)
                {
                    Assert.Equal(roadmap.StartField, actual.Ui.Roadmap!.StartField);
                    Assert.Equal(roadmap.TargetField, actual.Ui.Roadmap.TargetField);
                    Assert.Equal(roadmap.Zoom, actual.Ui.Roadmap.Zoom);
                    Assert.Equal(roadmap.Markers ?? [], actual.Ui.Roadmap.Markers ?? []);
                }
            }
        }
        finally
        {
            await client.QueryAsync(
                "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
                new { projectId = result.ProjectId },
                CancellationToken.None);
        }
    }

    /// <summary>
    /// M7 E2E: exports the fixture workflows (GraphQL + UI scrape), imports them into a
    /// fresh target project (workflows via browser automation, Auto-add repository
    /// resolved through the repo mapping), re-exports the target and asserts the
    /// enabled state / content types / status values / filter / repository round-trip.
    /// Kept independent of the views E2E so each run stays focused (and faster).
    /// </summary>
    [Fact]
    public async Task Workflows_round_trip_through_browser_automation()
    {
        var statePath = Environment.GetEnvironmentVariable("GPM_BROWSER_STATE");
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath),
            "GPM_BROWSER_STATE is not set or the file does not exist; skipping browser E2E test.");
        var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping browser E2E test.");

        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(token!);
        await using var session = new BrowserSession(new BrowserSessionOptions { StatePath = statePath });

        // Export the fixture with workflow UI settings and retarget it under a unique title.
        var exporter = new ProjectExporter(client);
        var workflowExporter = new WorkflowUiExporter(session);
        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        source = await workflowExporter.EnrichAsync(source, SourceOrg, FixtureProjectNumber, cancellationToken);
        Assert.Empty(workflowExporter.Warnings);
        Assert.All(source.Workflows, w => Assert.NotNull(w.Ui));

        // Explicit source expectations (fixture enrichment, 2026-07-06): two Auto-add
        // instances (exercising the Duplicate path) and a saved-but-disabled workflow
        // (exercising the disable mirroring incl. the save-once path on the target).
        Assert.Equal(2, source.Workflows.Count(w => w.Ui!.Repository is not null));
        var sourceSecondary = Assert.Single(source.Workflows, w => w.Name == "Auto-add secondary");
        Assert.True(sourceSecondary.Enabled);
        Assert.Equal("fixture-repo", sourceSecondary.Ui!.Repository);
        Assert.Equal("is:issue label:bug", sourceSecondary.Ui.Filter);
        var sourceDisabled = Assert.Single(source.Workflows, w => !w.Enabled);
        Assert.Equal("Code changes requested", sourceDisabled.Name);
        Assert.Equal("In Progress", sourceDisabled.Ui!.StatusValue);

        var title = "gpm-browser-wf-test-" + Guid.NewGuid().ToString("N");
        var snapshot = source with { Project = source.Project with { Title = title } };

        var importer = new ProjectImporter(client);
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var workflowImporter = new WorkflowUiImporter(session)
            {
                RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [$"{SourceOrg}/fixture-repo"] = $"{TargetOrg}/fixture-repo",
                },
            };
            await workflowImporter.ImportAsync(snapshot, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(workflowImporter.Warnings);
            Assert.Equal(snapshot.Workflows.Count, workflowImporter.ImportedCount);

            // Re-export the target (GraphQL + UI scrape) and diff the workflows by name.
            var reExported = await exporter.ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);
            var reExportUi = new WorkflowUiExporter(session);
            reExported = await reExportUi.EnrichAsync(reExported, TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Empty(reExportUi.Warnings);

            Assert.Equal(
                snapshot.Workflows.Select(w => w.Name).Order(StringComparer.Ordinal),
                reExported.Workflows.Select(w => w.Name).Order(StringComparer.Ordinal));
            foreach (var expected in snapshot.Workflows)
            {
                var actual = Assert.Single(reExported.Workflows, w => string.Equals(w.Name, expected.Name, StringComparison.Ordinal));
                Assert.Equal(expected.Enabled, actual.Enabled);

                Assert.NotNull(expected.Ui);
                Assert.NotNull(actual.Ui);
                Assert.Equal(expected.Ui!.ContentTypes ?? [], actual.Ui!.ContentTypes ?? []);
                Assert.Equal(expected.Ui.StatusValue, actual.Ui.StatusValue);
                Assert.Equal(expected.Ui.Filter, actual.Ui.Filter);
                Assert.Equal(expected.Ui.Repository, actual.Ui.Repository); // short names ("fixture-repo" on both sides)
            }
        }
        finally
        {
            await client.QueryAsync(
                "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
                new { projectId = result.ProjectId },
                CancellationToken.None);
        }
    }

        private static async Task<(string ProjectId, string UserId)> ResolveProjectAndUserIdsAsync(
                GitHubGraphQLClient client,
                string org,
                string login,
                CancellationToken cancellationToken)
        {
                var data = await client.QueryAsync(
                        """
                        query($org: String!, $number: Int!, $login: String!) {
                            organization(login: $org) { projectV2(number: $number) { id } }
                            user(login: $login) { id }
                        }
                        """,
                        new { org, number = FixtureProjectNumber, login },
                        cancellationToken);
                return (
                        data.GetProperty("organization").GetProperty("projectV2").GetProperty("id").GetString()!,
                        data.GetProperty("user").GetProperty("id").GetString()!);
        }

        private static Task<JsonElement> SetCollaboratorAsync(
                GitHubGraphQLClient client,
                string projectId,
                string userId,
                string role,
                CancellationToken cancellationToken)
                => client.QueryAsync(
                        """
                        mutation($projectId: ID!, $userId: ID!, $role: ProjectV2Roles!) {
                            updateProjectV2Collaborators(input: { projectId: $projectId, collaborators: [{ userId: $userId, role: $role }] }) {
                                clientMutationId
                            }
                        }
                        """,
                        new { projectId, userId, role },
                        cancellationToken);
}
