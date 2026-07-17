using Gpm.Core.Export;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

public class MappingTemplatesTests
{
    [Fact]
    public void ExtractSourceRepositories_dedupes_case_insensitively_preserving_order()
    {
        var snapshot = SnapshotWithItems(
            IssueItem("org-a/repo-1"),
            IssueItem("Org-A/Repo-1"),
            IssueItem("org-a/repo-2"),
            DraftItem("draft", assignees: ["alice"]));

        Assert.Equal(["org-a/repo-1", "org-a/repo-2"], MappingTemplates.ExtractSourceRepositories([snapshot]));
    }

    [Fact]
    public void ExtractSourceRepositories_merges_multiple_snapshots()
    {
        var first = SnapshotWithItems(IssueItem("org-a/repo-1"));
        var second = SnapshotWithItems(IssueItem("org-a/repo-1"), IssueItem("org-a/repo-3"));

        Assert.Equal(["org-a/repo-1", "org-a/repo-3"], MappingTemplates.ExtractSourceRepositories([first, second]));
    }

    [Fact]
    public void ExtractUserLogins_dedupes_draft_assignees_and_user_collaborators()
    {
        var snapshot = SnapshotWithItems(
            [
                new CollaboratorSnapshot { Type = "USER", Login = "Carol", Role = "WRITER" },
                new CollaboratorSnapshot { Type = "TEAM", Login = "team-a", Role = "READER" },
                new CollaboratorSnapshot { Type = "USER", Login = "dave", Role = "READER" },
            ],
            DraftItem("d1", assignees: ["alice", "bob"]),
            DraftItem("d2", assignees: ["Alice", "carol"]),
            IssueItem("org-a/repo-1"));

        Assert.Equal(["alice", "bob", "carol", "dave"], MappingTemplates.ExtractUserLogins([snapshot]));
    }

    [Fact]
    public void Mapping_candidates_include_linked_auto_add_and_filter_identifiers()
    {
        var snapshot = SnapshotWithItems(IssueItem("items-org/item-repo")) with
        {
            LinkedRepositories = ["linked-org/linked-repo"],
            Views =
            [
                new ViewSnapshot
                {
                    Number = 1,
                    Name = "Filtered",
                    Layout = "TABLE_LAYOUT",
                    Filter = "assignee:filter-user author:@me repo:filter-org/filter-repo org:explicit-org",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
            ],
            Workflows =
            [
                new WorkflowSnapshot
                {
                    Number = 1,
                    Name = "Auto-add",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot { Repository = "auto-add-repo", Filter = "author:workflow-user" },
                },
            ],
        };

        Assert.Equal(
            ["items-org/item-repo", "linked-org/linked-repo", "auto-add-repo", "filter-org/filter-repo"],
            MappingTemplates.ExtractSourceRepositories([snapshot]));
        Assert.Equal(["filter-user", "workflow-user"], MappingTemplates.ExtractUserLogins([snapshot]));
        Assert.Equal(
            ["items-org", "linked-org", "filter-org", "explicit-org"],
            MappingTemplates.ExtractOrganizations([snapshot]));
    }

    [Fact]
    public async Task WriteAsync_writes_repository_template_and_skips_user_template_without_assignees()
    {
        var directory = NewTempDirectory();
        try
        {
            var snapshot = SnapshotWithItems(IssueItem("org-a/repo-1"), DraftItem("d1", assignees: []));
            await MappingTemplates.WriteAsync([snapshot], directory, cancellationToken: TestContext.Current.CancellationToken);

            var repoPath = Path.Combine(directory, MappingTemplates.RepositoryMappingFileName);
            Assert.True(File.Exists(repoPath));
            Assert.Equal("source,target\norg-a/repo-1,\n", await File.ReadAllTextAsync(repoPath, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(directory, MappingTemplates.UserMappingFileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_writes_user_template_when_a_draft_has_assignees()
    {
        var directory = NewTempDirectory();
        try
        {
            var snapshot = SnapshotWithItems(DraftItem("d1", assignees: ["alice", "bob"]));
            await MappingTemplates.WriteAsync([snapshot], directory, cancellationToken: TestContext.Current.CancellationToken);

            var userPath = Path.Combine(directory, MappingTemplates.UserMappingFileName);
            Assert.Equal("mannequin-user,mannequin-id,target-user\nalice,,\nbob,,\n", await File.ReadAllTextAsync(userPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_writes_user_template_when_snapshot_has_user_collaborators()
    {
        var directory = NewTempDirectory();
        try
        {
            var snapshot = SnapshotWithItems(
                [
                    new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" },
                    new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" },
                ]);
            await MappingTemplates.WriteAsync([snapshot], directory, cancellationToken: TestContext.Current.CancellationToken);

            var userPath = Path.Combine(directory, MappingTemplates.UserMappingFileName);
            Assert.Equal("mannequin-user,mannequin-id,target-user\noctocat,,\n", await File.ReadAllTextAsync(userPath, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAsync_never_overwrites_existing_files()
    {
        var directory = NewTempDirectory();
        try
        {
            var repoPath = Path.Combine(directory, MappingTemplates.RepositoryMappingFileName);
            const string UserEdited = "source,target\norg-a/repo-1,org-b/repo-1\n";
            await File.WriteAllTextAsync(repoPath, UserEdited, TestContext.Current.CancellationToken);

            var messages = new List<string>();
            var snapshot = SnapshotWithItems(IssueItem("org-a/repo-9"));
            await MappingTemplates.WriteAsync([snapshot], directory, messages.Add, TestContext.Current.CancellationToken);

            Assert.Equal(UserEdited, await File.ReadAllTextAsync(repoPath, TestContext.Current.CancellationToken));
            Assert.Contains(messages, m => m.Contains("already exists", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Generated_template_loads_as_an_empty_mapping()
    {
        var directory = NewTempDirectory();
        try
        {
            var snapshot = SnapshotWithItems(IssueItem("org-a/repo-1"), IssueItem("org-a/repo-2"));
            await MappingTemplates.WriteAsync([snapshot], directory, cancellationToken: TestContext.Current.CancellationToken);

            // Blank targets are ignored, so the untouched template is a valid no-op mapping.
            var map = CsvMapping.Load(Path.Combine(directory, MappingTemplates.RepositoryMappingFileName));
            Assert.Empty(map);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Generated_user_template_loads_as_an_empty_mapping()
    {
        var directory = NewTempDirectory();
        try
        {
            var snapshot = SnapshotWithItems(DraftItem("d1", assignees: ["alice", "bob"]));
            await MappingTemplates.WriteAsync([snapshot], directory, cancellationToken: TestContext.Current.CancellationToken);

            // Blank target users are ignored, so the untouched template is a valid no-op mapping.
            var map = CsvMapping.LoadUserMapping(Path.Combine(directory, MappingTemplates.UserMappingFileName));
            Assert.Empty(map);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "gpm-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static ProjectSnapshot SnapshotWithItems(params ItemSnapshot[] items)
        => SnapshotWithItems(null, items);

    private static ProjectSnapshot SnapshotWithItems(IReadOnlyList<CollaboratorSnapshot>? collaborators, params ItemSnapshot[] items) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = items,
        Collaborators = collaborators,
    };

    private static ItemSnapshot IssueItem(string repository) => new()
    {
        Type = "ISSUE",
        Position = 0,
        IsArchived = false,
        Repository = repository,
        Number = 1,
        FieldValues = [],
    };

    private static ItemSnapshot DraftItem(string title, IReadOnlyList<string> assignees) => new()
    {
        Type = "DRAFT_ISSUE",
        Position = 0,
        IsArchived = false,
        Draft = new DraftIssueSnapshot { Title = title, Assignees = assignees },
        FieldValues = [],
    };
}
