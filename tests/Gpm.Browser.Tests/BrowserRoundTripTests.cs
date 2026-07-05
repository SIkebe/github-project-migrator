using Gpm.Core.Browser;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;

namespace Gpm.Browser.Tests;

/// <summary>
/// M6 E2E: exports the fixture project (gpm-source #3) including browser-scraped UI
/// settings, imports it into the target org (project + fields via GraphQL, views via
/// browser automation), re-exports the target and asserts the views round-trip
/// (name / layout / UI settings). Requires GPM_BROWSER_STATE (a storage-state file
/// saved by <c>gpm login</c>) and GPM_TEST_TOKEN; skipped otherwise.
/// The created project is deleted in a finally block.
/// </summary>
public class BrowserRoundTripTests
{
    private const int FixtureProjectNumber = 3;

    private static string SourceOrg => Environment.GetEnvironmentVariable("GPM_TEST_ORG") ?? "gpm-source";

    private static string TargetOrg => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_ORG") ?? "gpm-target";

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
            foreach (var (expected, actual) in snapshot.Views.OrderBy(v => v.Number)
                .Zip(reExported.Views.OrderBy(v => v.Number)))
            {
                Assert.Equal(expected.Name, actual.Name);
                Assert.Equal(expected.Layout, actual.Layout);

                Assert.NotNull(expected.Ui);
                Assert.NotNull(actual.Ui);
                Assert.Equal(expected.Ui!.GroupBy, actual.Ui!.GroupBy);
                Assert.Equal(expected.Ui.SortBy, actual.Ui.SortBy);
                Assert.Equal(expected.Ui.SliceBy, actual.Ui.SliceBy);
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
        Assert.Contains(source.Workflows, w => w.Ui!.Repository is not null); // fixture Auto-add

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
}
