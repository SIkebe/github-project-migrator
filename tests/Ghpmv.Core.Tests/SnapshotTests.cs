using System.Text.Json;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>M2 unit tests for the snapshot schema (serialization roundtrip, schema version).</summary>
public class SnapshotTests
{
    private static ProjectSnapshot CreateFullSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Fixture",
            ShortDescription = "A test project",
            Readme = "# Readme\n\nBody",
            Public = false,
            Closed = false,
        },
        Fields =
        [
            new FieldSnapshot { Name = "Title", DataType = "TITLE" },
            new FieldSnapshot { Name = "Fixture Text", DataType = "TEXT" },
            new FieldSnapshot
            {
                Name = "Fixture Select",
                DataType = "SINGLE_SELECT",
                Options =
                [
                    new SingleSelectOptionSnapshot { Id = "o1", Name = "Alpha", Color = "RED", Description = "First" },
                    new SingleSelectOptionSnapshot { Id = "o2", Name = "Beta", Color = "BLUE", Description = null },
                ],
            },
            new FieldSnapshot
            {
                Name = "Fixture Teams",
                DataType = "MULTI_SELECT",
                Options =
                [
                    new SingleSelectOptionSnapshot { Id = "m1", Name = "Platform", Color = "PURPLE", Description = "Platform work" },
                    new SingleSelectOptionSnapshot { Id = "m2", Name = "SDK", Color = "GREEN", Description = null },
                ],
                IssueField = new IssueFieldConfigurationSnapshot
                {
                    Description = "Teams involved",
                    Visibility = "ALL",
                },
            },
            new FieldSnapshot
            {
                Name = "Fixture Sprint",
                DataType = "ITERATION",
                IterationConfiguration = new IterationConfigurationSnapshot
                {
                    Duration = 14,
                    StartDay = 1,
                    Iterations =
                    [
                        new IterationSnapshot { Id = "i1", Title = "Sprint 1", StartDate = "2026-07-06", Duration = 14 },
                    ],
                    CompletedIterations =
                    [
                        new IterationSnapshot { Id = "i0", Title = "Sprint 0", StartDate = "2026-06-22", Duration = 14 },
                    ],
                },
            },
        ],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "View 1",
                Layout = "TABLE_LAYOUT",
                Filter = "is:issue -status:Done",
                GroupByFields = ["Status"],
                SortByFields = [new SortByFieldSnapshot { Field = "Fixture Number", Direction = "DESC" }],
                VerticalGroupByFields = [],
                VisibleFields = ["Title", "Status", "Fixture Text"],
                Ui = null,
            },
        ],
        Workflows =
        [
            new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = true, Ui = null },
        ],
        Items =
        [
            new ItemSnapshot
            {
                Type = "ISSUE",
                Position = 0,
                IsArchived = false,
                Repository = "gpm-source/fixture-repo",
                Number = 1,
                FieldValues =
                [
                    new FieldValueSnapshot { FieldName = "Fixture Text", Text = "hello" },
                    new FieldValueSnapshot { FieldName = "Fixture Number", Number = 42.5 },
                    new FieldValueSnapshot { FieldName = "Fixture Date", Date = "2026-07-05" },
                    new FieldValueSnapshot { FieldName = "Fixture Select", SingleSelectOptionName = "Alpha" },
                    new FieldValueSnapshot { FieldName = "Fixture Teams", MultiSelectOptionNames = ["Platform", "SDK"] },
                    new FieldValueSnapshot { FieldName = "Fixture Sprint", IterationTitle = "Sprint 1" },
                ],
            },
            new ItemSnapshot
            {
                Type = "DRAFT_ISSUE",
                Position = 1,
                IsArchived = true,
                Draft = new DraftIssueSnapshot { Title = "Fixture draft 1", Body = "body", Assignees = ["octocat"] },
                FieldValues = [],
            },
        ],
        Collaborators =
        [
            new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" },
            new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" },
        ],
        LinkedRepositories = ["gpm-source/fixture-repo"],
    };

    [Fact]
    public void Roundtrip_preserves_all_values()
    {
        var original = CreateFullSnapshot();

        var json = JsonSerializer.Serialize(original, SnapshotJsonContext.Default.ProjectSnapshot);
        var restored = JsonSerializer.Deserialize(json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(original.Project, restored.Project);

        Assert.Equal(original.Fields.Count, restored.Fields.Count);
        var select = restored.Fields.Single(f => f.Name == "Fixture Select");
        Assert.NotNull(select.Options);
        Assert.Equal(["Alpha", "Beta"], select.Options.Select(o => o.Name));
        Assert.Equal(["RED", "BLUE"], select.Options.Select(o => o.Color));

        var multiSelect = restored.Fields.Single(f => f.Name == "Fixture Teams");
        Assert.Equal("MULTI_SELECT", multiSelect.DataType);
        Assert.Equal(["Platform", "SDK"], multiSelect.Options!.Select(o => o.Name));
        Assert.Equal("Teams involved", multiSelect.IssueField!.Description);
        Assert.Equal("ALL", multiSelect.IssueField.Visibility);

        var sprint = restored.Fields.Single(f => f.Name == "Fixture Sprint");
        Assert.NotNull(sprint.IterationConfiguration);
        Assert.Equal(14, sprint.IterationConfiguration.Duration);
        Assert.Equal(1, sprint.IterationConfiguration.StartDay);
        Assert.Equal("Sprint 1", Assert.Single(sprint.IterationConfiguration.Iterations).Title);
        Assert.Equal("Sprint 0", Assert.Single(sprint.IterationConfiguration.CompletedIterations).Title);

        var view = Assert.Single(restored.Views);
        Assert.Equal("TABLE_LAYOUT", view.Layout);
        Assert.Equal("is:issue -status:Done", view.Filter);
        Assert.Equal(["Status"], view.GroupByFields);
        Assert.Equal(new SortByFieldSnapshot { Field = "Fixture Number", Direction = "DESC" }, Assert.Single(view.SortByFields));
        Assert.Equal(["Title", "Status", "Fixture Text"], view.VisibleFields);
        Assert.Null(view.Ui);

        var workflow = Assert.Single(restored.Workflows);
        Assert.Equal(new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = true }, workflow);

        Assert.Equal(2, restored.Items.Count);
        var issue = restored.Items[0];
        Assert.Equal("gpm-source/fixture-repo", issue.Repository);
        Assert.Equal(1, issue.Number);
        Assert.Equal(
            original.Items[0].FieldValues.Select(value => value.FieldName),
            issue.FieldValues.Select(value => value.FieldName));
        foreach (var (expected, actual) in original.Items[0].FieldValues.Zip(issue.FieldValues))
        {
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Number, actual.Number);
            Assert.Equal(expected.Date, actual.Date);
            Assert.Equal(expected.SingleSelectOptionName, actual.SingleSelectOptionName);
            Assert.Equal(expected.MultiSelectOptionNames, actual.MultiSelectOptionNames);
            Assert.Equal(expected.IterationTitle, actual.IterationTitle);
        }
        Assert.Equal(
            ["Platform", "SDK"],
            issue.FieldValues.Single(value => value.FieldName == "Fixture Teams").MultiSelectOptionNames);
        var draftItem = restored.Items[1];
        Assert.True(draftItem.IsArchived);
        Assert.NotNull(draftItem.Draft);
        Assert.Equal("Fixture draft 1", draftItem.Draft.Title);
        Assert.Equal(["octocat"], draftItem.Draft.Assignees);

        Assert.NotNull(restored.Collaborators);
        Assert.Equal(2, restored.Collaborators.Count);
        Assert.Equal(new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" }, restored.Collaborators[0]);
        Assert.Equal(new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" }, restored.Collaborators[1]);
        Assert.Equal(["gpm-source/fixture-repo"], restored.LinkedRepositories);
    }

    [Fact]
    public void Deserialize_snapshot_without_collaborators_and_linked_repositories_yields_null()
    {
        // Snapshots written before the collaborator/linked-repository fields stay loadable
        // within schema version 1; the new fields deserialize as null ("not captured").
        const string Json =
            """
            {
              "schemaVersion": 1,
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [], "views": [], "workflows": [], "items": []
            }
            """;

        var restored = JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot);

        Assert.NotNull(restored);
        Assert.Null(restored.Collaborators);
        Assert.Null(restored.LinkedRepositories);
    }

    [Fact]
    public void Serialized_json_contains_schema_version()
    {
        var json = JsonSerializer.Serialize(CreateFullSnapshot(), SnapshotJsonContext.Default.ProjectSnapshot);

        using var document = JsonDocument.Parse(json);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void Deserialize_without_schema_version_throws()
    {
        const string Json =
            """
            {
              "project": { "title": "T", "public": false, "closed": false },
              "fields": [], "views": [], "workflows": [], "items": []
            }
            """;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(Json, SnapshotJsonContext.Default.ProjectSnapshot));
    }

    [Fact]
    public async Task SnapshotFile_saves_and_loads_snapshot_json()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ghpmv-test-{Guid.NewGuid():N}");
        try
        {
            var original = CreateFullSnapshot();

            var path = await SnapshotFile.SaveAsync(original, directory, TestContext.Current.CancellationToken);

            Assert.Equal(Path.Combine(directory, "snapshot.json"), path);
            var text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.Contains("\n", text, StringComparison.Ordinal); // indented output has line breaks

            var restored = await SnapshotFile.LoadAsync(directory, TestContext.Current.CancellationToken);
            Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
            Assert.Equal(original.Project, restored.Project);
            Assert.Equal(original.Items.Count, restored.Items.Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
