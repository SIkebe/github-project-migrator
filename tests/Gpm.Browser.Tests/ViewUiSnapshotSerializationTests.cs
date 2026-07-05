using Gpm.Core.Snapshot;

namespace Gpm.Browser.Tests;

/// <summary>
/// Serialization round-trip for the UI-only view settings added in M6
/// (<see cref="ViewUiSnapshot"/> incl. GroupBy/SortBy and roadmap settings).
/// No Playwright required.
/// </summary>
public class ViewUiSnapshotSerializationTests
{
    [Fact]
    public async Task Ui_settings_round_trip_through_snapshot_file()
    {
        var scrapedAt = new DateTimeOffset(2026, 7, 5, 1, 2, 3, TimeSpan.Zero);
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
            Fields = [],
            Views =
            [
                new ViewSnapshot
                {
                    Number = 3,
                    Name = "Fixture Roadmap",
                    Layout = "ROADMAP_LAYOUT",
                    Filter = "",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = ["Title"],
                    Ui = new ViewUiSnapshot
                    {
                        GroupBy = "Status",
                        SortBy = "Title",
                        SliceBy = "Assignees",
                        FieldSum = ["Fixture Number"],
                        Roadmap = new RoadmapSettingsSnapshot
                        {
                            StartField = "Fixture Date",
                            TargetField = "Fixture Sprint end",
                            Zoom = "Month",
                            Markers = ["Fixture Sprint"],
                        },
                        ScrapedAt = scrapedAt,
                    },
                },
                new ViewSnapshot
                {
                    Number = 4,
                    Name = "No UI",
                    Layout = "TABLE_LAYOUT",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
            ],
            Workflows = [],
            Items = [],
        };

        var directory = Path.Combine(Path.GetTempPath(), "gpm-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
            var loaded = await SnapshotFile.LoadAsync(directory, cancellationToken);

            Assert.Equal(2, loaded.Views.Count);

            var roadmap = loaded.Views[0];
            Assert.NotNull(roadmap.Ui);
            Assert.Equal("Status", roadmap.Ui!.GroupBy);
            Assert.Equal("Title", roadmap.Ui.SortBy);
            Assert.Equal("Assignees", roadmap.Ui.SliceBy);
            Assert.Equal(["Fixture Number"], roadmap.Ui.FieldSum);
            Assert.Equal(scrapedAt, roadmap.Ui.ScrapedAt);
            Assert.NotNull(roadmap.Ui.Roadmap);
            Assert.Equal("Fixture Date", roadmap.Ui.Roadmap!.StartField);
            Assert.Equal("Fixture Sprint end", roadmap.Ui.Roadmap.TargetField);
            Assert.Equal("Month", roadmap.Ui.Roadmap.Zoom);
            Assert.Equal(["Fixture Sprint"], roadmap.Ui.Roadmap.Markers);

            Assert.Null(loaded.Views[1].Ui);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Workflow_ui_settings_round_trip_through_snapshot_file()
    {
        var scrapedAt = new DateTimeOffset(2026, 7, 5, 4, 5, 6, TimeSpan.Zero);
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
            Fields = [],
            Views = [],
            Workflows =
            [
                new WorkflowSnapshot
                {
                    Number = 6,
                    Name = "Item added to project",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot
                    {
                        ContentTypes = ["ISSUE", "PULL_REQUEST"],
                        StatusValue = "Todo",
                        ScrapedAt = scrapedAt,
                    },
                },
                new WorkflowSnapshot
                {
                    Number = 7,
                    Name = "Auto-add to project",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot
                    {
                        Filter = "is:issue is:open",
                        Repository = "fixture-repo",
                        ScrapedAt = scrapedAt,
                    },
                },
                new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = false },
            ],
            Items = [],
        };

        var directory = Path.Combine(Path.GetTempPath(), "gpm-browser-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cancellationToken = TestContext.Current.CancellationToken;
            await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
            var loaded = await SnapshotFile.LoadAsync(directory, cancellationToken);

            Assert.Equal(3, loaded.Workflows.Count);

            var itemAdded = loaded.Workflows[0];
            Assert.NotNull(itemAdded.Ui);
            Assert.Equal(["ISSUE", "PULL_REQUEST"], itemAdded.Ui!.ContentTypes);
            Assert.Equal("Todo", itemAdded.Ui.StatusValue);
            Assert.Null(itemAdded.Ui.Filter);
            Assert.Null(itemAdded.Ui.Repository);
            Assert.Equal(scrapedAt, itemAdded.Ui.ScrapedAt);

            var autoAdd = loaded.Workflows[1];
            Assert.NotNull(autoAdd.Ui);
            Assert.Equal("is:issue is:open", autoAdd.Ui!.Filter);
            Assert.Equal("fixture-repo", autoAdd.Ui.Repository);
            Assert.Null(autoAdd.Ui.ContentTypes);
            Assert.Null(autoAdd.Ui.StatusValue);

            Assert.Null(loaded.Workflows[2].Ui);
            Assert.False(loaded.Workflows[2].Enabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
