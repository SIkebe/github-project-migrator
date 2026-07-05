using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

/// <summary>
/// M2 integration tests: exports the fixture project (gpm-source #3) via the real
/// GraphQL API and verifies the snapshot contents against the known fixture state.
/// Requires the GPM_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// Skipped when the variable is not set (e.g. fork PRs without secrets).
/// </summary>
public class ProjectExporterTests
{
    private const int FixtureProjectNumber = 3;

    private static string Org => Environment.GetEnvironmentVariable("GPM_TEST_ORG") ?? "gpm-source";

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static async Task<ProjectSnapshot> ExportFixtureAsync()
    {
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);
        return await exporter.ExportAsync(Org, FixtureProjectNumber, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Bulk_export_writes_one_snapshot_directory_per_project()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);

        var entries = await exporter.ListProjectsAsync(Org, includeClosed: false, cancellationToken);
        Assert.Contains(entries, e => e.Number == FixtureProjectNumber && !e.Closed);

        var outDirectory = Path.Combine(Path.GetTempPath(), "gpm-bulk-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Same loop the CLI runs when --project is omitted.
            var snapshots = new List<ProjectSnapshot>();
            foreach (var entry in entries)
            {
                var snapshot = await exporter.ExportAsync(Org, entry.Number, cancellationToken);
                var directory = Path.Combine(outDirectory, entry.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
                await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
                snapshots.Add(snapshot);
            }

            await MappingTemplates.WriteAsync(snapshots, outDirectory, cancellationToken: cancellationToken);

            Assert.True(File.Exists(Path.Combine(outDirectory, "3", SnapshotFile.FileName)));
            Assert.True(File.Exists(Path.Combine(outDirectory, MappingTemplates.RepositoryMappingFileName)));

            var reloaded = await SnapshotFile.LoadAsync(Path.Combine(outDirectory, "3"), cancellationToken);
            Assert.Equal(ProjectSnapshot.CurrentSchemaVersion, reloaded.SchemaVersion);
        }
        finally
        {
            Directory.Delete(outDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Export_has_schema_version_and_project_metadata()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(ProjectSnapshot.CurrentSchemaVersion, snapshot.SchemaVersion);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.Project.Title));
        Assert.False(snapshot.Project.Closed);
    }

    [Fact]
    public async Task Export_contains_all_fixture_fields_with_options_and_iterations()
    {
        var snapshot = await ExportFixtureAsync();

        var fieldNames = snapshot.Fields.Select(f => f.Name).ToList();
        foreach (var name in (string[])["Fixture Text", "Fixture Number", "Fixture Date", "Fixture Select", "Fixture Sprint"])
        {
            Assert.Contains(name, fieldNames);
        }

        Assert.Equal("TEXT", snapshot.Fields.Single(f => f.Name == "Fixture Text").DataType);
        Assert.Equal("NUMBER", snapshot.Fields.Single(f => f.Name == "Fixture Number").DataType);
        Assert.Equal("DATE", snapshot.Fields.Single(f => f.Name == "Fixture Date").DataType);

        var select = snapshot.Fields.Single(f => f.Name == "Fixture Select");
        Assert.Equal("SINGLE_SELECT", select.DataType);
        Assert.NotNull(select.Options);
        Assert.Equal(["Alpha", "Beta", "Gamma"], select.Options.Select(o => o.Name));
        Assert.Equal(["RED", "BLUE", "GREEN"], select.Options.Select(o => o.Color));

        var sprint = snapshot.Fields.Single(f => f.Name == "Fixture Sprint");
        Assert.Equal("ITERATION", sprint.DataType);
        Assert.NotNull(sprint.IterationConfiguration);
        Assert.Equal(14, sprint.IterationConfiguration.Duration);

        // Iterations move to completedIterations as time passes, so check the union.
        var allIterations = sprint.IterationConfiguration.Iterations
            .Concat(sprint.IterationConfiguration.CompletedIterations)
            .ToList();
        foreach (var title in (string[])["Sprint 1", "Sprint 2", "Sprint 3"])
        {
            var iteration = Assert.Single(allIterations, i => i.Title == title);
            Assert.Equal(14, iteration.Duration);
            Assert.False(string.IsNullOrWhiteSpace(iteration.StartDate));
        }
    }

    [Fact]
    public async Task Export_contains_three_views_with_expected_layouts()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(3, snapshot.Views.Count);
        Assert.Equal("TABLE_LAYOUT", Assert.Single(snapshot.Views, v => v.Name == "View 1").Layout);
        Assert.Equal("BOARD_LAYOUT", Assert.Single(snapshot.Views, v => v.Name == "Fixture Board").Layout);
        Assert.Equal("ROADMAP_LAYOUT", Assert.Single(snapshot.Views, v => v.Name == "Fixture Roadmap").Layout);

        foreach (var view in snapshot.Views)
        {
            Assert.True(view.Number > 0);
            Assert.NotEmpty(view.VisibleFields);
            Assert.Null(view.Ui); // reserved for M6
        }
    }

    [Fact]
    public async Task Export_contains_seven_enabled_fixture_workflows()
    {
        var snapshot = await ExportFixtureAsync();

        string[] expected =
        [
            "Item closed",
            "Pull request merged",
            "Auto-close issue",
            "Auto-add sub-issues to project",
            "Pull request linked to issue",
            "Item added to project",
            "Auto-add to project",
        ];

        var enabled = snapshot.Workflows.Where(w => w.Enabled).Select(w => w.Name).ToList();
        Assert.Equal(7, enabled.Count);
        foreach (var name in expected)
        {
            Assert.Contains(name, enabled);
        }

        Assert.All(snapshot.Workflows, w => Assert.Null(w.Ui)); // reserved for M7
    }

    [Fact]
    public async Task Export_contains_three_fixture_drafts_with_positions()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(3, snapshot.Items.Count);
        Assert.All(snapshot.Items, item => Assert.Equal("DRAFT_ISSUE", item.Type));
        Assert.All(snapshot.Items, item => Assert.False(item.IsArchived));

        var draftTitles = snapshot.Items.Select(i => i.Draft?.Title).ToList();
        foreach (var title in (string[])["Fixture draft 1", "Fixture draft 2", "Fixture draft 3"])
        {
            Assert.Contains(title, draftTitles);
        }

        // Position records enumeration order.
        Assert.Equal([0, 1, 2], snapshot.Items.Select(i => i.Position));

        // Drafts carry their Title as a text field value.
        Assert.All(snapshot.Items, item =>
            Assert.Contains(item.FieldValues, v => v.FieldName == "Title" && !string.IsNullOrEmpty(v.Text)));
    }
}
