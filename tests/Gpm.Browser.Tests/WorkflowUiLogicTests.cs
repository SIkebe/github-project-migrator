using Gpm.Core.Browser;
using Gpm.Core.Snapshot;

namespace Gpm.Browser.Tests;

/// <summary>
/// Pure-logic unit tests for the M7 workflow browser module (no Playwright required):
/// "Status: &lt;value&gt;" / content-type parsing, Auto-add classification, repository
/// mapping resolution and the Auto-add plan-limit pre-flight check.
/// </summary>
public class WorkflowUiLogicTests
{
    // ----- WorkflowUiExporter parsing -----

    [Theory]
    [InlineData("Status: Todo", "Todo")]
    [InlineData("Status: In Progress", "In Progress")]
    [InlineData("Status:  Done ", "Done")]
    [InlineData("issue, pull request", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseStatusText_extracts_the_value_after_the_status_prefix(string? text, string? expected)
        => Assert.Equal(expected, WorkflowUiExporter.ParseStatusText(text));

    [Fact]
    public void ParseContentTypes_maps_ui_names_to_graphql_style()
        => Assert.Equal(["ISSUE", "PULL_REQUEST"], WorkflowUiExporter.ParseContentTypes("issue, pull request"));

    [Fact]
    public void ParseContentTypes_handles_a_single_type()
        => Assert.Equal(["ISSUE"], WorkflowUiExporter.ParseContentTypes("issue"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParseContentTypes_returns_null_for_empty_input(string? text)
        => Assert.Null(WorkflowUiExporter.ParseContentTypes(text));

    [Theory]
    [InlineData("ISSUE", "issue")]
    [InlineData("PULL_REQUEST", "pull request")]
    public void ContentTypeOptionName_round_trips_to_the_ui_option_name(string contentType, string expected)
        => Assert.Equal(expected, WorkflowUiExporter.ContentTypeOptionName(contentType));

    [Theory]
    [InlineData("https://github.com/orgs/o/projects/3/workflows/104097621", true)]
    [InlineData("https://github.com/orgs/o/projects/3/workflows/104097621?pane=x", true)]
    [InlineData("https://github.com/orgs/o/projects/3/workflows/a66897b0-6706-4c0b-be5f-c0bbf362fe26", false)]
    [InlineData("https://github.com/orgs/o/projects/3/workflows", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSavedWorkflowUrl_detects_numeric_workflow_ids(string? url, bool expected)
        => Assert.Equal(expected, WorkflowUiExporter.IsSavedWorkflowUrl(url));

    // ----- WorkflowUiImporter classification / mapping -----

    [Fact]
    public void IsAutoAdd_is_true_when_the_workflow_targets_a_repository()
        => Assert.True(WorkflowUiImporter.IsAutoAdd(Workflow("Auto-add to project", ui: new WorkflowUiSnapshot
        {
            Repository = "fixture-repo",
            Filter = "is:issue is:open",
        })));

    [Fact]
    public void IsAutoAdd_is_false_for_builtin_workflows_and_missing_ui()
    {
        Assert.False(WorkflowUiImporter.IsAutoAdd(Workflow("Item closed", ui: new WorkflowUiSnapshot { StatusValue = "Done" })));
        Assert.False(WorkflowUiImporter.IsAutoAdd(Workflow("Auto-add to project", ui: null)));
    }

    [Fact]
    public void ResolveRepositoryName_applies_the_owner_name_mapping_by_short_name()
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpm-source/fixture-repo"] = "gpm-target/renamed-repo",
        };

        Assert.Equal("renamed-repo", WorkflowUiImporter.ResolveRepositoryName("fixture-repo", mapping));
    }

    [Fact]
    public void ResolveRepositoryName_falls_back_to_the_source_name_without_a_mapping()
        => Assert.Equal("fixture-repo", WorkflowUiImporter.ResolveRepositoryName(
            "fixture-repo", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

    // ----- pre-flight: Auto-add plan limit -----

    [Fact]
    public void CollectPreflightWarnings_is_empty_when_within_the_auto_add_limit()
    {
        var snapshot = Snapshot(
            Workflow("Item closed", ui: new WorkflowUiSnapshot { StatusValue = "Done" }),
            AutoAdd("Auto-add to project", "repo-a"),
            AutoAdd("Second auto-add", "repo-b"));

        Assert.Empty(WorkflowUiImporter.CollectPreflightWarnings(snapshot, maxAutoAddWorkflows: 20));
    }

    [Fact]
    public void CollectPreflightWarnings_reports_auto_add_instances_beyond_the_plan_limit()
    {
        var snapshot = Snapshot(
            AutoAdd("Auto-add to project", "repo-a"),
            AutoAdd("Second auto-add", "repo-b"));

        var warnings = WorkflowUiImporter.CollectPreflightWarnings(snapshot, maxAutoAddWorkflows: 1);

        var warning = Assert.Single(warnings);
        Assert.Contains("2 Auto-add workflows", warning, StringComparison.Ordinal);
        Assert.Contains("allows 1", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void CollectPreflightWarnings_reports_an_enabled_auto_add_without_ui_settings()
    {
        var snapshot = Snapshot(Workflow("Auto-add to project", ui: null));

        var warnings = WorkflowUiImporter.CollectPreflightWarnings(snapshot, WorkflowUiImporter.DefaultMaxAutoAddWorkflows);

        var warning = Assert.Single(warnings);
        Assert.Contains("cannot be enabled without UI settings", warning, StringComparison.Ordinal);
    }

    // ----- helpers -----

    private static WorkflowSnapshot Workflow(string name, WorkflowUiSnapshot? ui, bool enabled = true, int number = 1)
        => new() { Number = number, Name = name, Enabled = enabled, Ui = ui };

    private static WorkflowSnapshot AutoAdd(string name, string repository)
        => Workflow(name, new WorkflowUiSnapshot { Repository = repository, Filter = "is:issue" });

    private static ProjectSnapshot Snapshot(params WorkflowSnapshot[] workflows) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
        Fields = [],
        Views = [],
        Workflows = workflows.Select((w, i) => w with { Number = i + 1 }).ToList(),
        Items = [],
    };
}
