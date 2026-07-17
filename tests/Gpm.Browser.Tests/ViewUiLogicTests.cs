using Gpm.Core.Browser;
using Gpm.Core.Snapshot;
using Gpm.Core.Verify;

namespace Gpm.Browser.Tests;

/// <summary>
/// Pure-logic unit tests for the browser module (no Playwright required):
/// menu-value parsing, default-view reuse, layout mapping, pre-flight warning
/// collection and the verifier's UI-settings comparison.
/// </summary>
public class ViewUiLogicTests
{
    // ----- ViewUiExporter.ParseMenuValue / ParseListValue -----

    [Theory]
    [InlineData("Group by: Status", "Status")]
    [InlineData("Group by:\nStatus", "Status")]
    [InlineData("Zoom level: Month", "Month")]
    [InlineData("Group by: none", null)]
    [InlineData("Group by: None", null)]
    [InlineData("Group by:", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseMenuValue_extracts_the_value_and_normalizes_none(string? text, string? expected)
        => Assert.Equal(expected, ViewUiExporter.ParseMenuValue(text));

    [Fact]
    public void ParseMenuValue_returns_the_whole_text_when_there_is_no_label()
        => Assert.Equal("Status", ViewUiExporter.ParseMenuValue("Status"));

    [Fact]
    public void ParseListValue_splits_on_commas()
        => Assert.Equal(["Milestone", "Fixture Sprint"], ViewUiExporter.ParseListValue("Markers: Milestone, Fixture Sprint"));

    [Fact]
    public void ParseListValue_splits_prose_conjunctions()
    {
        // The UI renders lists in prose form (E2E discovery, 2026-07-06).
        Assert.Equal(["Count", "Fixture Number"], ViewUiExporter.ParseListValue("Field sum: Count and Fixture Number"));
        Assert.Equal(["A", "B", "C"], ViewUiExporter.ParseListValue("Fields: A, B, and C"));
    }

    [Fact]
    public void ParseListValue_returns_null_for_none()
        => Assert.Null(ViewUiExporter.ParseListValue("Markers: none"));

    [Theory]
    [InlineData("  Fixture   Sprint\nend ", "Fixture Sprint end")]
    [InlineData("   ", null)]
    public void NormalizeUiText_collapses_whitespace(string text, string? expected)
        => Assert.Equal(expected, ViewUiExporter.NormalizeUiText(text));

    // ----- ViewUiImporter static logic -----

    [Fact]
    public void ShouldReuseDefaultView_is_true_when_the_first_view_is_a_table()
        => Assert.True(ViewUiImporter.ShouldReuseDefaultView([View("View 1", "TABLE_LAYOUT")]));

    [Fact]
    public void ShouldReuseDefaultView_is_false_when_the_first_view_is_a_board()
        => Assert.False(ViewUiImporter.ShouldReuseDefaultView([View("Board", "BOARD_LAYOUT"), View("Table", "TABLE_LAYOUT")]));

    [Fact]
    public void ShouldReuseDefaultView_is_false_for_an_empty_list()
        => Assert.False(ViewUiImporter.ShouldReuseDefaultView([]));

    [Theory]
    [InlineData("TABLE_LAYOUT", "Table")]
    [InlineData("BOARD_LAYOUT", "Board")]
    [InlineData("ROADMAP_LAYOUT", "Roadmap")]
    public void LayoutMenuName_maps_graphql_layouts_to_menu_items(string layout, string expected)
        => Assert.Equal(expected, ViewUiImporter.LayoutMenuName(layout));

    [Fact]
    public void LayoutMenuName_throws_for_unknown_layouts()
        => Assert.Throws<ArgumentException>(() => ViewUiImporter.LayoutMenuName("LIST_LAYOUT"));

    // ----- pre-flight warning collection -----

    [Fact]
    public void CollectPreflightWarnings_is_empty_when_all_settings_are_applicable()
    {
        var snapshot = Snapshot(
            fields: ["Status", "Fixture Date", "Fixture Sprint"],
            View("Board", "BOARD_LAYOUT", groupBy: ["Status"], sortBy: [Sort("Status", "ASC")]),
            View("Roadmap", "ROADMAP_LAYOUT") with
            {
                Ui = new ViewUiSnapshot
                {
                    SliceBy = "Status",
                    Roadmap = new RoadmapSettingsSnapshot { StartField = "Fixture Date", TargetField = "Fixture Sprint end" },
                },
            });

        Assert.Empty(ViewUiImporter.CollectPreflightWarnings(snapshot));
    }

    [Fact]
    public void CollectPreflightWarnings_reports_missing_fields_and_extra_sort_keys()
    {
        var snapshot = Snapshot(
            fields: ["Status"],
            View("V", "TABLE_LAYOUT",
                groupBy: ["Missing group"],
                sortBy: [Sort("Status", "ASC"), Sort("Missing sort", "DESC")]) with
            {
                Ui = new ViewUiSnapshot
                {
                    SliceBy = "Missing slice",
                    Roadmap = new RoadmapSettingsSnapshot { StartField = "Missing start", TargetField = "Status end" },
                },
            });

        var warnings = ViewUiImporter.CollectPreflightWarnings(snapshot);

        Assert.Contains(warnings, w => w.Contains("group-by field 'Missing group'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("sort-by field 'Missing sort'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("only the first of 2 sort keys", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("slice-by field 'Missing slice'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("start date field 'Missing start'", StringComparison.Ordinal));
        // "Status end" resolves to the iteration-style suffix of the existing "Status" field.
        Assert.DoesNotContain(warnings, w => w.Contains("'Status end'", StringComparison.Ordinal));
        Assert.Equal(5, warnings.Count);
    }

    [Fact]
    public void CollectPreflightWarnings_reports_missing_swimlanes_and_field_sum_fields()
    {
        var snapshot = Snapshot(
            fields: ["Status"],
            View("Board", "BOARD_LAYOUT") with
            {
                Ui = new ViewUiSnapshot
                {
                    Swimlanes = "Missing swimlane",
                    FieldSum = ["Count", "Missing number"],
                },
            });

        var warnings = ViewUiImporter.CollectPreflightWarnings(snapshot);

        Assert.Contains(warnings, w => w.Contains("swimlanes field 'Missing swimlane'", StringComparison.Ordinal));
        // "Count" is a built-in Field sum entry, not a field.
        Assert.Contains(warnings, w => w.Contains("field-sum field 'Missing number'", StringComparison.Ordinal));
        Assert.Equal(2, warnings.Count);
    }

    [Fact]
    public void FixtureUiSnapshotFactory_creates_importable_standard_views_and_workflows()
    {
        var snapshot = FixtureUiSnapshotFactory.Create("fixture-repo");

        Assert.Equal(["View 1", "Fixture Board", "Fixture Roadmap"], snapshot.Views.Select(v => v.Name));
        Assert.Contains(snapshot.Workflows, w => w.Name == "Auto-add to project" && w.Ui?.Repository == "fixture-repo");
        Assert.Contains(snapshot.Workflows, w => w.Name == "Auto-add secondary" && w.Ui?.Filter == "is:issue label:bug");
        Assert.Empty(ViewUiImporter.CollectPreflightWarnings(snapshot));
        Assert.Empty(WorkflowUiImporter.CollectPreflightWarnings(snapshot, WorkflowUiImporter.DefaultMaxAutoAddWorkflows));
    }

    // ----- verifier: Ui comparison (M6) -----

    [Fact]
    public void Verifier_reports_no_view_differences_when_ui_settings_match()
    {
        var view = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var report = ProjectVerifier.Compare(Snapshot(["Status"], view), Snapshot(["Status"], view));

        Assert.DoesNotContain(report.Differences, d => d.Category == "View");
    }

    [Fact]
    public void Verifier_warns_when_ui_settings_differ()
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var target = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Status") };

        var report = ProjectVerifier.Compare(Snapshot(["Status"], source), Snapshot(["Status"], target));

        var difference = Assert.Single(report.Differences, d => d.Category == "View");
        Assert.Equal(VerifySeverity.Error, difference.Severity);
        Assert.Contains("slice by mismatch", difference.Message, StringComparison.Ordinal);
        Assert.Equal(VerifyStatus.Mismatch, report.Status);
    }

    [Fact]
    public void Verifier_marks_ui_not_verified_when_one_side_has_no_ui()
    {
        var source = View("Roadmap", "ROADMAP_LAYOUT") with { Ui = Ui("Assignees") };
        var target = View("Roadmap", "ROADMAP_LAYOUT");

        var report = ProjectVerifier.Compare(Snapshot(["Status"], source), Snapshot(["Status"], target));

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.Contains(report.Categories, category =>
            category.Category == "View" && category.Status == VerifyStatus.NotVerified);
    }

    // ----- helpers -----

    private static ViewUiSnapshot Ui(string sliceBy) => new()
    {
        GroupBy = null,
        SortBy = null,
        SliceBy = sliceBy,
        Roadmap = new RoadmapSettingsSnapshot
        {
            StartField = "Fixture Date",
            TargetField = "Fixture Sprint end",
            Zoom = "Month",
            Markers = ["Fixture Sprint"],
        },
        ScrapedAt = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero),
    };

    private static SortByFieldSnapshot Sort(string field, string direction) => new() { Field = field, Direction = direction };

    private static ViewSnapshot View(
        string name,
        string layout,
        IReadOnlyList<string>? groupBy = null,
        IReadOnlyList<SortByFieldSnapshot>? sortBy = null) => new()
        {
            Number = 1,
            Name = name,
            Layout = layout,
            GroupByFields = groupBy ?? [],
            SortByFields = sortBy ?? [],
            VerticalGroupByFields = [],
            VisibleFields = [],
        };

    private static ProjectSnapshot Snapshot(IReadOnlyList<string> fields, params ViewSnapshot[] views) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
        Fields = [.. fields.Select(f => new FieldSnapshot { Name = f, DataType = "TEXT" })],
        Views = views,
        Workflows = [],
        Items = [],
    };
}
