using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

public class IntegrationFixtureSnapshotTests
{
    [Fact]
    public void SelectCanonicalItems_excludes_unrelated_shared_fixture_items()
    {
        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "fixture", Public = false, Closed = false },
            Fields = [],
            Views = [],
            Workflows = [],
            Items =
            [
                Draft("Fixture draft 1", 0),
                Draft("Fixture draft 2", 1),
                Draft("Fixture draft 3", 2),
                Content("ISSUE", 1, 3),
                Content("PULL_REQUEST", 3, 4),
                Draft("Fixture archived draft", 5),
                Draft("Fixture assigned draft", 6),
                Content("ISSUE", 4, 7),
            ],
        };

        var result = IntegrationFixtureSnapshot.SelectCanonicalItems(snapshot);

        Assert.Equal(7, result.Items.Count);
        Assert.Equal(Enumerable.Range(0, 7), result.Items.Select(item => item.Position));
        Assert.DoesNotContain(result.Items, item => item.Type == "ISSUE" && item.Number == 4);
    }

    private static ItemSnapshot Draft(string title, int position) => new()
    {
        Type = "DRAFT_ISSUE",
        Position = position,
        IsArchived = title == "Fixture archived draft",
        Draft = new DraftIssueSnapshot { Title = title, Assignees = [] },
        FieldValues = [],
    };

    private static ItemSnapshot Content(string type, int number, int position) => new()
    {
        Type = type,
        Position = position,
        IsArchived = false,
        Repository = IntegrationTestSettings.FixtureRepositoryFullName,
        Number = number,
        FieldValues = [],
    };
}
