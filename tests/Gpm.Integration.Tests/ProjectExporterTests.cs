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

        // Enriched fixture metadata: short description and a multiline README with emoji.
        Assert.Equal("gpm fixture project", snapshot.Project.ShortDescription);
        Assert.NotNull(snapshot.Project.Readme);
        Assert.Contains("\n", snapshot.Project.Readme, StringComparison.Ordinal);
        Assert.Contains("\uD83D\uDCE6", snapshot.Project.Readme, StringComparison.Ordinal); // 📦
    }

    [Fact]
    public async Task Export_captures_linked_repositories_and_leaves_collaborators_null()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.NotNull(snapshot.LinkedRepositories);
        Assert.Contains("gpm-source/fixture-repo", snapshot.LinkedRepositories, StringComparer.OrdinalIgnoreCase);

        // The GraphQL API has no read field for project collaborators, so exports leave them null.
        Assert.Null(snapshot.Collaborators);
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

        // Sprint 0 is past-dated, so the API must classify it into completedIterations.
        var sprint0 = Assert.Single(sprint.IterationConfiguration.CompletedIterations, i => i.Title == "Sprint 0");
        Assert.Equal(14, sprint0.Duration);
        Assert.True(
            DateTime.Parse(sprint0.StartDate, System.Globalization.CultureInfo.InvariantCulture).AddDays(sprint0.Duration) < DateTime.UtcNow.Date.AddDays(1),
            $"Sprint 0 ({sprint0.StartDate} + {sprint0.Duration}d) should have ended in the past");

        // Iterations move to completedIterations as time passes, so check the union.
        var allIterations = sprint.IterationConfiguration.Iterations
            .Concat(sprint.IterationConfiguration.CompletedIterations)
            .ToList();
        Assert.Equal(4, allIterations.Count);
        foreach (var title in (string[])["Sprint 0", "Sprint 1", "Sprint 2", "Sprint 3"])
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
    public async Task Export_contains_all_seven_fixture_items_with_positions()
    {
        var snapshot = await ExportFixtureAsync();

        Assert.Equal(7, snapshot.Items.Count);
        Assert.Equal(Enumerable.Range(0, 7), snapshot.Items.Select(i => i.Position));
        Assert.Equal(5, snapshot.Items.Count(i => i.Type == "DRAFT_ISSUE"));

        // Issue and PR items carry their repository and number.
        var issue = Assert.Single(snapshot.Items, i => i.Type == "ISSUE");
        Assert.Equal("gpm-source/fixture-repo", issue.Repository);
        Assert.Equal(1, issue.Number);
        Assert.False(issue.IsArchived);

        var pullRequest = Assert.Single(snapshot.Items, i => i.Type == "PULL_REQUEST");
        Assert.Equal("gpm-source/fixture-repo", pullRequest.Repository);
        Assert.True(pullRequest.Number > 0);

        // The archived draft is exported with its archived state.
        var archived = Assert.Single(snapshot.Items, i => i.IsArchived);
        Assert.Equal("DRAFT_ISSUE", archived.Type);
        Assert.Equal("Fixture archived draft", archived.Draft?.Title);

        // The assigned draft carries its assignee login.
        var assigned = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture assigned draft");
        var assignee = Assert.Single(assigned.Draft!.Assignees);
        Assert.False(string.IsNullOrWhiteSpace(assignee));

        // Every draft carries its Title as a text field value.
        Assert.All(snapshot.Items.Where(i => i.Type == "DRAFT_ISSUE"), item =>
            Assert.Contains(item.FieldValues, v => v.FieldName == "Title" && !string.IsNullOrEmpty(v.Text)));
    }

    [Fact]
    public async Task Export_captures_all_field_value_types_on_the_fixture_drafts()
    {
        var snapshot = await ExportFixtureAsync();

        var draft1 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 1");
        var draft2 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 2");
        var draft3 = Assert.Single(snapshot.Items, i => i.Draft?.Title == "Fixture draft 3");

        // TEXT round-trips non-ASCII (Japanese, accents, emoji) and markup-like characters.
        Assert.Equal("日本語テキスト & <special> chars", ValueOf(draft1, "Fixture Text")?.Text);
        Assert.Equal("Café emoji 🚀 – em dash", ValueOf(draft2, "Fixture Text")?.Text);
        Assert.Equal("plain ascii text", ValueOf(draft3, "Fixture Text")?.Text);

        // NUMBER covers fractional, negative and zero values (zero must not export as null).
        Assert.Equal(3.14, ValueOf(draft1, "Fixture Number")?.Number);
        Assert.Equal(-42d, ValueOf(draft2, "Fixture Number")?.Number);
        Assert.Equal(0d, ValueOf(draft3, "Fixture Number")?.Number);

        // DATE values are exported as yyyy-MM-dd.
        foreach (var draft in (ItemSnapshot[])[draft1, draft2, draft3])
        {
            var date = ValueOf(draft, "Fixture Date")?.Date;
            Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", date);
        }

        // SINGLE_SELECT covers every option once.
        Assert.Equal("Alpha", ValueOf(draft1, "Fixture Select")?.SingleSelectOptionName);
        Assert.Equal("Beta", ValueOf(draft2, "Fixture Select")?.SingleSelectOptionName);
        Assert.Equal("Gamma", ValueOf(draft3, "Fixture Select")?.SingleSelectOptionName);

        // ITERATION includes a completed iteration (Sprint 0) as a value.
        Assert.Equal("Sprint 0", ValueOf(draft1, "Fixture Sprint")?.IterationTitle);
        Assert.Equal("Sprint 1", ValueOf(draft2, "Fixture Sprint")?.IterationTitle);
        Assert.Equal("Sprint 2", ValueOf(draft3, "Fixture Sprint")?.IterationTitle);
    }

    private static FieldValueSnapshot? ValueOf(ItemSnapshot item, string fieldName)
        => item.FieldValues.FirstOrDefault(v => v.FieldName == fieldName);
}
