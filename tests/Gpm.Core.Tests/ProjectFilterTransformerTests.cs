using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

public class ProjectFilterTransformerTests
{
    private static readonly IReadOnlyDictionary<string, string> Users =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["old-user"] = "old-user_shortcode",
            ["alice"] = "alice_shortcode",
        };

    private static readonly IReadOnlyDictionary<string, string> Repositories =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source-org/source-repo"] = "target-org/renamed-repo",
        };

    [Fact]
    public void Transform_maps_supported_identifiers_without_changing_filter_syntax()
    {
        const string Filter = "-assignee:old-user (author:\"alice\" OR repo:source-org/source-repo) org:source-org label:\"日本語 label\"";

        var result = ProjectFilterTransformer.Transform(Filter, Users, Repositories);

        Assert.Equal(
            "-assignee:old-user_shortcode (author:\"alice_shortcode\" OR repo:target-org/renamed-repo) org:target-org label:\"日本語 label\"",
            result.Transformed);
        Assert.Equal(4, result.Changes.Count);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void Transform_does_not_replace_substrings_or_unknown_qualifiers()
    {
        const string Filter = "assignee:old-user-2 label:old-user custom:source-org/source-repo";

        var result = ProjectFilterTransformer.Transform(Filter, Users, Repositories);

        Assert.Equal(Filter, result.Transformed);
        Assert.Equal([new FilterIdentifier("assignee", "old-user-2")], result.Unresolved);
        Assert.Equal([new FilterIdentifier("custom", "source-org/source-repo")], result.Unsupported);
    }

    [Fact]
    public void Transform_does_not_parse_qualifiers_inside_quoted_literals()
    {
        const string Filter = "\"assignee:old-user\" label:\"text author:alice\" assignee:old-user";

        var result = ProjectFilterTransformer.Transform(Filter, Users);

        Assert.Equal(
            "\"assignee:old-user\" label:\"text author:alice\" assignee:old-user_shortcode",
            result.Transformed);
        Assert.Empty(result.Unresolved);
        Assert.Empty(result.Unsupported);
    }

    [Fact]
    public void AnalyzeAutoAddRepositories_reports_mapped_unmapped_and_ambiguous_values()
    {
        var snapshot = Snapshot("is:open", "is:issue") with
        {
            Workflows =
            [
                new WorkflowSnapshot
                {
                    Number = 1,
                    Name = "Auto-add",
                    Enabled = true,
                    Ui = new WorkflowUiSnapshot { Repository = "source-repo", Filter = "is:issue" },
                },
            ],
        };
        var ambiguous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["one/source-repo"] = "target/one",
            ["two/source-repo"] = "target/two",
        };

        var result = Assert.Single(ProjectFilterTransformer.AnalyzeAutoAddRepositories(snapshot, ambiguous));

        Assert.Equal(RepositoryResolutionStatus.Ambiguous, result.Resolution.Status);
    }

    [Fact]
    public void Transform_is_case_insensitive_and_preserves_qualifier_casing()
    {
        var result = ProjectFilterTransformer.Transform("ASSIGNEE:OLD-USER Repo:SOURCE-ORG/SOURCE-REPO", Users, Repositories);

        Assert.Equal("ASSIGNEE:old-user_shortcode Repo:target-org/renamed-repo", result.Transformed);
    }

    [Fact]
    public void Transform_does_not_require_mapping_for_special_values()
    {
        var result = ProjectFilterTransformer.Transform("assignee:@me author:none");

        Assert.Equal("assignee:@me author:none", result.Transformed);
        Assert.Empty(result.Unresolved);
        Assert.Equal(
            [new FilterIdentifier("assignee", "@me"), new FilterIdentifier("author", "none")],
            result.Unchanged);
    }

    [Fact]
    public void Transform_maps_comma_separated_values_independently()
    {
        var result = ProjectFilterTransformer.Transform(
            "assignee:old-user,missing author:\"alice,old-user\"",
            Users);

        Assert.Equal(
            "assignee:old-user_shortcode,missing author:\"alice_shortcode,old-user_shortcode\"",
            result.Transformed);
        Assert.Equal([new FilterIdentifier("assignee", "missing")], result.Unresolved);
    }

    [Fact]
    public void Transform_preserves_whitespace_around_comma_separated_values()
    {
        var result = ProjectFilterTransformer.Transform(
            "assignee:\"alice, old-user \"",
            Users);

        Assert.Equal("assignee:\"alice_shortcode, old-user_shortcode \"", result.Transformed);
        Assert.Empty(result.Unresolved);
    }

    [Fact]
    public void BuildOrganizationMapping_omits_ambiguous_source_owners()
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source/a"] = "target-a/a",
            ["source/b"] = "target-b/b",
            ["stable/c"] = "target/c",
        };

        var organizations = ProjectFilterTransformer.BuildOrganizationMapping(mapping);

        Assert.False(organizations.ContainsKey("source"));
        Assert.Equal("target", organizations["stable"]);
    }

    [Fact]
    public void TransformSnapshot_maps_view_and_workflow_filters()
    {
        var snapshot = Snapshot(
            "assignee:old-user",
            "repo:source-org/source-repo");

        var transformed = ProjectFilterTransformer.TransformSnapshot(snapshot, Users, Repositories);

        Assert.Equal("assignee:old-user_shortcode", transformed.Views[0].Filter);
        Assert.Equal("repo:target-org/renamed-repo", transformed.Workflows[0].Ui!.Filter);
    }

    private static ProjectSnapshot Snapshot(string viewFilter, string workflowFilter) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "t", Public = false, Closed = false },
        Fields = [],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "View",
                Layout = "TABLE_LAYOUT",
                Filter = viewFilter,
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
                Ui = new WorkflowUiSnapshot { Repository = "source-repo", Filter = workflowFilter },
            },
        ],
        Items = [],
    };
}
