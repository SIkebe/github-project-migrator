using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Creates the UI-only portion of the standard integration-test fixture.
/// The returned snapshot is intentionally minimal: it contains just enough field,
/// view and workflow metadata for <see cref="ViewUiImporter"/> and
/// <see cref="WorkflowUiImporter"/> to drive the GitHub Projects UI against a project
/// whose API-backed fields and repository were created by <c>ghpmv setup --fixture</c>.
/// </summary>
public static class FixtureUiSnapshotFactory
{
    public static ProjectSnapshot Create(string repositoryName = "fixture-repo")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        return new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot
            {
                Title = "ghpmv-fixture-ui",
                ShortDescription = "UI fixture settings for ghpmv integration tests",
                Readme = null,
                Public = false,
                Closed = false,
            },
            Fields = CreateFields(),
            Views = CreateViews(),
            Workflows = CreateWorkflows(repositoryName),
            Items = [],
        };
    }

    private static IReadOnlyList<FieldSnapshot> CreateFields() =>
    [
        new FieldSnapshot { Name = "Title", DataType = "TITLE" },
        new FieldSnapshot { Name = "Assignees", DataType = "ASSIGNEES" },
        new FieldSnapshot { Name = "Status", DataType = "SINGLE_SELECT" },
        new FieldSnapshot { Name = "Fixture Text", DataType = "TEXT" },
        new FieldSnapshot { Name = "Fixture Number", DataType = "NUMBER" },
        new FieldSnapshot { Name = "Fixture Date", DataType = "DATE" },
        new FieldSnapshot { Name = "Fixture Select", DataType = "SINGLE_SELECT" },
        new FieldSnapshot { Name = "Fixture Sprint", DataType = "ITERATION" },
    ];

    private static IReadOnlyList<ViewSnapshot> CreateViews() =>
    [
        new ViewSnapshot
        {
            Number = 1,
            Name = "View 1",
            Layout = "TABLE_LAYOUT",
            Filter = "status:Todo",
            GroupByFields = [],
            SortByFields = [new SortByFieldSnapshot { Field = "Fixture Number", Direction = "ASC" }],
            VerticalGroupByFields = [],
            VisibleFields = ["Title", "Assignees", "Status", "Fixture Text", "Fixture Date", "Fixture Select", "Fixture Sprint"],
            Ui = new ViewUiSnapshot
            {
                SortBy = "Fixture Number",
                SliceBy = "Fixture Select",
            },
        },
        new ViewSnapshot
        {
            Number = 2,
            Name = "Fixture Board",
            Layout = "BOARD_LAYOUT",
            Filter = null,
            GroupByFields = ["Status"],
            SortByFields = [],
            VerticalGroupByFields = ["Fixture Select"],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                Swimlanes = "Status",
                FieldSum = ["Fixture Number"],
            },
        },
        new ViewSnapshot
        {
            Number = 3,
            Name = "Fixture Roadmap",
            Layout = "ROADMAP_LAYOUT",
            Filter = null,
            GroupByFields = [],
            SortByFields = [],
            VerticalGroupByFields = [],
            VisibleFields = [],
            Ui = new ViewUiSnapshot
            {
                Roadmap = new RoadmapSettingsSnapshot
                {
                    StartField = "Fixture Date",
                    TargetField = "Fixture Sprint end",
                    Zoom = "Quarter",
                    Markers = ["Fixture Date"],
                },
            },
        },
    ];

    private static IReadOnlyList<WorkflowSnapshot> CreateWorkflows(string repositoryName) =>
    [
        new WorkflowSnapshot
        {
            Number = 1,
            Name = "Item added to project",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Todo",
            },
        },
        new WorkflowSnapshot
        {
            Number = 2,
            Name = "Item reopened",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Todo",
            },
        },
        new WorkflowSnapshot
        {
            Number = 3,
            Name = "Item closed",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                ContentTypes = ["ISSUE", "PULL_REQUEST"],
                StatusValue = "Done",
            },
        },
        new WorkflowSnapshot
        {
            Number = 4,
            Name = "Code changes requested",
            Enabled = false,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "In Progress",
            },
        },
        new WorkflowSnapshot
        {
            Number = 5,
            Name = "Code review approved",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "In Progress",
            },
        },
        new WorkflowSnapshot
        {
            Number = 6,
            Name = "Pull request merged",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                StatusValue = "Done",
            },
        },
        new WorkflowSnapshot
        {
            Number = 7,
            Name = "Auto-add to project",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                Repository = repositoryName,
                Filter = "is:issue is:open",
            },
        },
        new WorkflowSnapshot
        {
            Number = 8,
            Name = "Auto-add secondary",
            Enabled = true,
            Ui = new WorkflowUiSnapshot
            {
                Repository = repositoryName,
                Filter = "is:issue label:bug",
            },
        },
    ];
}
