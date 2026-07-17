using System.Net;
using System.Text;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;
using Gpm.Core.Verify;

namespace Gpm.Core.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ProjectVerifier.Compare"/> (M5): snapshot-to-snapshot
/// comparison without any API access. Covers the match case, field/option/iteration and
/// item value drifts, view/workflow warnings and the draft attribution note handling.
/// </summary>
public class ProjectVerifierTests
{
    // ----- snapshot builders -----

    private static ProjectSnapshot BuildSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Fixture",
            ShortDescription = "desc",
            Readme = "# Readme",
            Public = false,
            Closed = false,
        },
        Fields =
        [
            new FieldSnapshot { Name = "Title", DataType = "TITLE" },
            new FieldSnapshot
            {
                Name = "Status",
                DataType = "SINGLE_SELECT",
                Options =
                [
                    Option("1", "Todo", "GRAY"),
                    Option("2", "In Progress", "YELLOW"),
                    Option("3", "Done", "GREEN"),
                ],
            },
            new FieldSnapshot { Name = "Estimate", DataType = "NUMBER" },
            new FieldSnapshot
            {
                Name = "Sprint",
                DataType = "ITERATION",
                IterationConfiguration = new IterationConfigurationSnapshot
                {
                    Duration = 14,
                    StartDay = 1,
                    Iterations = [Iteration("i1", "Sprint 1", "2026-07-06"), Iteration("i2", "Sprint 2", "2026-07-20")],
                    CompletedIterations = [Iteration("i0", "Sprint 0", "2026-06-22")],
                },
            },
        ],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "Table",
                Layout = "TABLE_LAYOUT",
                GroupByFields = [],
                SortByFields = [],
                VerticalGroupByFields = [],
                VisibleFields = ["Title", "Status"],
                Ui = new ViewUiSnapshot(),
            },
        ],
        Workflows = [new WorkflowSnapshot { Number = 1, Name = "Item closed", Enabled = true, Ui = new WorkflowUiSnapshot() }],
        Items =
        [
            DraftItem(position: 0, title: "Draft A", body: "Body A", status: "Todo"),
            DraftItem(position: 1, title: "Draft B", body: null, status: "Done"),
            new ItemSnapshot
            {
                Type = "ISSUE",
                Position = 2,
                IsArchived = false,
                Repository = "org/repo",
                Number = 1,
                FieldValues =
                [
                    new FieldValueSnapshot { FieldName = "Status", SingleSelectOptionName = "In Progress" },
                    new FieldValueSnapshot { FieldName = "Estimate", Number = 5 },
                    new FieldValueSnapshot { FieldName = "Sprint", IterationTitle = "Sprint 1" },
                ],
            },
        ],
        Collaborators = [],
        LinkedRepositories = [],
    };

    private static SingleSelectOptionSnapshot Option(string id, string name, string color, string? description = null)
        => new() { Id = id, Name = name, Color = color, Description = description };

    private static IterationSnapshot Iteration(string id, string title, string startDate, int duration = 14)
        => new() { Id = id, Title = title, StartDate = startDate, Duration = duration };

    private static ItemSnapshot DraftItem(int position, string title, string? body, string? status, bool archived = false) => new()
    {
        Type = "DRAFT_ISSUE",
        Position = position,
        IsArchived = archived,
        Draft = new DraftIssueSnapshot { Title = title, Body = body, Creator = "octocat", CreatedAt = "2026-07-01T00:00:00Z", Assignees = [] },
        FieldValues = status is null ? [] : [new FieldValueSnapshot { FieldName = "Status", SingleSelectOptionName = status }],
    };

    private static ProjectSnapshot WithFields(ProjectSnapshot snapshot, Func<FieldSnapshot, FieldSnapshot?> transform)
        => snapshot with { Fields = snapshot.Fields.Select(transform).Where(f => f is not null).Cast<FieldSnapshot>().ToList() };

    private static ProjectSnapshot WithItems(ProjectSnapshot snapshot, Func<ItemSnapshot, ItemSnapshot> transform)
        => snapshot with { Items = snapshot.Items.Select(transform).ToList() };

    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    // ----- match -----

    [Fact]
    public void Identical_snapshots_produce_no_differences()
    {
        var report = ProjectVerifier.Compare(BuildSnapshot(), BuildSnapshot());

        Assert.Empty(report.Differences);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public async Task VerifyAsync_applies_post_export_hook_before_comparison()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{"title":"Raw target","shortDescription":null,"readme":null,"public":false,"closed":false,"fields":{"nodes":[]},"views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}}}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}
            """);
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, delayAsync: null);
        var source = BuildSnapshot();
        var hookCalled = false;
        var verifier = new ProjectVerifier(client)
        {
            PostExportAsync = (target, _) =>
            {
                hookCalled = true;
                Assert.Equal("Raw target", target.Project.Title);
                return Task.FromResult(source with
                {
                    Project = source.Project with { ShortDescription = "changed by hook" },
                });
            },
        };

        var report = await verifier.VerifyAsync(
            source,
            "target-org",
            42,
            TestContext.Current.CancellationToken);

        Assert.True(hookCalled);
        var difference = Assert.Single(report.Differences);
        Assert.Equal(VerifySeverity.Error, difference.Severity);
        Assert.Equal("Project", difference.Category);
        Assert.Contains("short description mismatch", difference.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Title_difference_is_informational_only()
    {
        var source = BuildSnapshot();
        var target = source with { Project = source.Project with { Title = "Renamed on import" } };

        var report = ProjectVerifier.Compare(source, target);

        var difference = Assert.Single(report.Differences);
        Assert.Equal(VerifySeverity.Info, difference.Severity);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public void Project_visibility_difference_is_an_error()
    {
        var source = BuildSnapshot();
        var target = source with { Project = source.Project with { Public = true } };

        var report = ProjectVerifier.Compare(source, target);

        Assert.False(report.IsMatch);
        Assert.Contains(report.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "Project" && d.Message.Contains("visibility", StringComparison.Ordinal));
    }

    // ----- fields -----

    [Fact]
    public void Missing_field_in_target_is_an_error()
    {
        var target = WithFields(BuildSnapshot(), f => f.Name == "Estimate" ? null : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.False(report.IsMatch);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains("'Estimate'", StringComparison.Ordinal) && d.Message.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Extra_field_in_target_is_a_warning()
    {
        var source = WithFields(BuildSnapshot(), f => f.Name == "Estimate" ? null : f);

        var report = ProjectVerifier.Compare(source, BuildSnapshot());

        Assert.Equal(VerifyStatus.PartialMatch, report.Status);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Warning && d.Category == "Field" && d.Message.Contains("only in the target", StringComparison.Ordinal));
    }

    [Fact]
    public void Field_data_type_mismatch_is_an_error()
    {
        var target = WithFields(BuildSnapshot(), f => f.Name == "Estimate" ? f with { DataType = "TEXT" } : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains("data type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Option_color_difference_is_an_error()
    {
        var target = WithFields(BuildSnapshot(), f => f.Name == "Status"
            ? f with { Options = [Option("1", "Todo", "RED"), Option("2", "In Progress", "YELLOW"), Option("3", "Done", "GREEN")] }
            : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.False(report.IsMatch);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains("color mismatch", StringComparison.Ordinal) && d.Message.Contains("'Todo'", StringComparison.Ordinal));
    }

    [Fact]
    public void Option_order_difference_is_an_error()
    {
        var target = WithFields(BuildSnapshot(), f => f.Name == "Status"
            ? f with { Options = [Option("2", "In Progress", "YELLOW"), Option("1", "Todo", "GRAY"), Option("3", "Done", "GREEN")] }
            : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains("name mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Iterations_are_matched_by_title_ignoring_completed_classification()
    {
        // The target reports "Sprint 1" as completed (time passed since import); still a match.
        var target = WithFields(BuildSnapshot(), f => f.Name == "Sprint"
            ? f with
            {
                IterationConfiguration = f.IterationConfiguration! with
                {
                    Iterations = [Iteration("x2", "Sprint 2", "2026-07-20")],
                    CompletedIterations = [Iteration("x1", "Sprint 1", "2026-07-06"), Iteration("x0", "Sprint 0", "2026-06-22")],
                },
            }
            : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Empty(report.Differences);
    }

    [Fact]
    public void Iteration_start_date_difference_is_an_error()
    {
        var target = WithFields(BuildSnapshot(), f => f.Name == "Sprint"
            ? f with
            {
                IterationConfiguration = f.IterationConfiguration! with
                {
                    Iterations = [Iteration("i1", "Sprint 1", "2026-07-07"), Iteration("i2", "Sprint 2", "2026-07-20")],
                },
            }
            : f);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains("start date mismatch", StringComparison.Ordinal));
    }

    // ----- views / workflows -----

    [Fact]
    public void View_and_workflow_differences_are_errors_since_the_browser_module_migrates_them()
    {
        var source = BuildSnapshot();
        var target = source with
        {
            Views = [source.Views[0] with { Name = "View 1" }],
            Workflows = [source.Workflows[0] with { Enabled = false }],
        };

        var report = ProjectVerifier.Compare(source, target);

        Assert.False(report.IsMatch);
        Assert.Contains(report.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "View" && d.Message.Contains("'Table'", StringComparison.Ordinal) && d.Message.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "View" && d.Message.Contains("'View 1'", StringComparison.Ordinal) && d.Message.Contains("only in the target", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "Workflow" && d.Message.Contains("enabled state mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void View_layout_mismatch_is_an_error()
    {
        var source = BuildSnapshot();
        var target = source with { Views = [source.Views[0] with { Layout = "BOARD_LAYOUT" }] };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "View" && d.Message.Contains("layout mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Graphql_view_settings_are_compared_without_browser_automation()
    {
        var source = BuildSnapshot();
        var target = source with
        {
            Views =
            [
                source.Views[0] with
                {
                    Filter = "is:open",
                    VisibleFields = ["Status", "Title"],
                    GroupByFields = ["Status"],
                    VerticalGroupByFields = ["Assignees"],
                    SortByFields = [new SortByFieldSnapshot { Field = "Status", Direction = "DESC" }],
                },
            ],
        };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(5, report.Differences.Count(difference =>
            difference.Severity == VerifySeverity.Error && difference.Category == "View"));
        Assert.Equal(VerifyStatus.Mismatch, report.Status);
    }

    [Fact]
    public void View_ui_is_not_verified_when_target_ui_was_not_read()
    {
        var source = BuildSnapshot();
        var target = source with { Views = [source.Views[0] with { Ui = null }] };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.Contains(report.Categories, category =>
            category.Category == "View" && category.Status == VerifyStatus.NotVerified);
        Assert.False(report.IsMatch);
    }

    [Fact]
    public void Duplicate_view_names_are_compared_as_setting_multisets()
    {
        var baseline = BuildSnapshot();
        var first = baseline.Views[0] with
        {
            Number = 1,
            Filter = "status:Todo",
            Ui = new ViewUiSnapshot { SliceBy = "Status" },
        };
        var second = baseline.Views[0] with
        {
            Number = 2,
            Filter = "status:Done",
            Ui = new ViewUiSnapshot { SliceBy = "Assignees" },
        };
        var source = baseline with { Views = [first, second] };
        var reordered = baseline with { Views = [second with { Number = 8 }, first with { Number = 9 }] };

        Assert.DoesNotContain(ProjectVerifier.Compare(source, reordered).Differences, difference =>
            difference.Category == "View");

        var drifted = baseline with
        {
            Views = [second with { Number = 8 }, first with { Number = 9, Filter = "status:In Progress" }],
        };
        var report = ProjectVerifier.Compare(source, drifted);

        Assert.Contains(report.Differences, difference =>
            difference.Severity == VerifySeverity.Error
            && difference.Category == "View"
            && difference.Message.Contains("API-visible settings", StringComparison.Ordinal));

        var swappedUi = baseline with
        {
            Views =
            [
                first with { Number = 8, Ui = second.Ui },
                second with { Number = 9, Ui = first.Ui },
            ],
        };
        var swappedReport = ProjectVerifier.Compare(source, swappedUi);
        Assert.Contains(swappedReport.Differences, difference =>
            difference.Severity == VerifySeverity.Error
            && difference.Category == "View"
            && difference.Message.Contains("combined API and UI settings", StringComparison.Ordinal));
    }

    [Fact]
    public void Workflow_ui_differences_are_errors_when_both_sides_carry_ui()
    {
        var ui = new WorkflowUiSnapshot
        {
            ContentTypes = ["ISSUE", "PULL_REQUEST"],
            StatusValue = "Done",
            Filter = "is:issue is:open",
            Repository = "fixture-repo",
        };
        var source = BuildSnapshot();
        source = source with { Workflows = [source.Workflows[0] with { Ui = ui }] };
        var target = source with
        {
            Workflows =
            [
                source.Workflows[0] with
                {
                    Ui = ui with { StatusValue = "Todo", Repository = "other-repo" },
                },
            ],
        };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.Mismatch, report.Status);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Workflow" && d.Message.Contains("status value mismatch", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Workflow" && d.Message.Contains("repository mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Workflow_ui_is_not_verified_when_one_side_has_no_ui()
    {
        var source = BuildSnapshot();
        source = source with
        {
            Workflows = [source.Workflows[0] with { Ui = new WorkflowUiSnapshot { StatusValue = "Done" } }],
        };

        var target = BuildSnapshot() with
        {
            Workflows = [BuildSnapshot().Workflows[0] with { Ui = null }],
        };
        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.Contains(report.Categories, category =>
            category.Category == "Workflow" && category.Status == VerifyStatus.NotVerified);
    }

    [Fact]
    public void Duplicate_workflow_names_are_compared_as_setting_multisets()
    {
        var baseline = BuildSnapshot();
        var first = baseline.Workflows[0] with
        {
            Number = 1,
            Ui = new WorkflowUiSnapshot { Filter = "is:issue", StatusValue = "Todo" },
        };
        var second = baseline.Workflows[0] with
        {
            Number = 2,
            Ui = new WorkflowUiSnapshot { Filter = "is:pr", StatusValue = "Done" },
        };
        var source = baseline with { Workflows = [first, second] };
        var reordered = baseline with { Workflows = [second with { Number = 8 }, first with { Number = 9 }] };

        Assert.DoesNotContain(ProjectVerifier.Compare(source, reordered).Differences, difference =>
            difference.Category == "Workflow");

        var drifted = baseline with
        {
            Workflows =
            [
                second with { Number = 8 },
                first with { Number = 9, Ui = first.Ui! with { Filter = "is:open" } },
            ],
        };
        var report = ProjectVerifier.Compare(source, drifted);

        Assert.Contains(report.Differences, difference =>
            difference.Severity == VerifySeverity.Error
            && difference.Category == "Workflow"
            && difference.Message.Contains("UI settings", StringComparison.Ordinal));
    }

    // ----- items -----

    [Fact]
    public void Item_count_mismatch_is_an_error()
    {
        var source = BuildSnapshot();
        var target = source with { Items = source.Items.Take(2).ToList() };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains("item count mismatch", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains("missing in the target", StringComparison.Ordinal));
    }

    [Fact]
    public void Item_field_value_difference_is_an_error()
    {
        var target = WithItems(BuildSnapshot(), i => i.Draft?.Title == "Draft A"
            ? i with { FieldValues = [new FieldValueSnapshot { FieldName = "Status", SingleSelectOptionName = "Done" }] }
            : i);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.False(report.IsMatch);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error
            && d.Category == "Item"
            && d.Message.Contains("draft 'Draft A'", StringComparison.Ordinal)
            && d.Message.Contains("'Status' value mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Item_order_difference_is_an_error()
    {
        var source = BuildSnapshot();
        var target = WithItems(source, i => i.Position switch
        {
            0 => i with { Position = 1 },
            1 => i with { Position = 0 },
            _ => i,
        });

        var report = ProjectVerifier.Compare(source, target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains("order mismatch at position 0", StringComparison.Ordinal));
    }

    [Fact]
    public void Archived_item_position_differences_are_ignored()
    {
        // Archived items cannot be repositioned via the API, so only the relative order
        // of non-archived items is compared.
        var source = BuildSnapshot() with
        {
            Items =
            [
                DraftItem(position: 0, title: "Archived", body: null, status: null, archived: true),
                DraftItem(position: 1, title: "Draft A", body: null, status: null),
                DraftItem(position: 2, title: "Draft B", body: null, status: null),
            ],
        };
        var target = source with
        {
            Items =
            [
                DraftItem(position: 0, title: "Draft A", body: null, status: null),
                DraftItem(position: 1, title: "Draft B", body: null, status: null),
                DraftItem(position: 2, title: "Archived", body: null, status: null, archived: true),
            ],
        };

        var report = ProjectVerifier.Compare(source, target);

        Assert.DoesNotContain(report.Differences, d => d.Message.Contains("order mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Archived_state_difference_is_an_error()
    {
        var target = WithItems(BuildSnapshot(), i => i.Draft?.Title == "Draft B" ? i with { IsArchived = true } : i);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains("archived state mismatch", StringComparison.Ordinal));
    }

    // ----- draft attribution note -----

    [Fact]
    public void Draft_body_with_imported_attribution_note_matches_the_original()
    {
        // The target carries exactly what ItemImporter writes on import.
        var target = WithItems(BuildSnapshot(), i => i.Draft is null
            ? i
            : i with { Draft = i.Draft with { Body = ItemImporter.BuildDraftBody(i.Draft) } });

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Empty(report.Differences);
    }

    [Fact]
    public void Draft_body_difference_beyond_the_note_is_an_error()
    {
        var target = WithItems(BuildSnapshot(), i => i.Draft?.Title == "Draft A"
            ? i with { Draft = i.Draft with { Body = ItemImporter.BuildDraftBody(i.Draft with { Body = "Tampered body" }) } }
            : i);

        var report = ProjectVerifier.Compare(BuildSnapshot(), target);

        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains("draft body mismatch", StringComparison.Ordinal));
    }

    // ----- collaborators / linked repositories -----

    [Fact]
    public void Collaborators_are_not_verified_when_either_side_is_null()
    {
        // Exports always leave collaborators null (no read API), so a null side must not
        // produce differences even when the other side carries collaborators.
        var withCollaborators = BuildSnapshot() with
        {
            Collaborators = [new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" }],
        };

        var withoutCollaborators = BuildSnapshot() with { Collaborators = null };
        Assert.Equal(VerifyStatus.NotVerified, ProjectVerifier.Compare(withCollaborators, withoutCollaborators).Status);
        Assert.Equal(VerifyStatus.NotVerified, ProjectVerifier.Compare(withoutCollaborators, withCollaborators).Status);
    }

    [Fact]
    public void Missing_collaborators_and_role_differences_are_errors()
    {
        var source = BuildSnapshot() with
        {
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "USER", Login = "octocat", Role = "WRITER" },
                new CollaboratorSnapshot { Type = "TEAM", Login = "fixture-team", Role = "READER" },
            ],
        };
        var target = BuildSnapshot() with
        {
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "USER", Login = "Octocat", Role = "ADMIN" }, // role drift (login is case-insensitive)
                new CollaboratorSnapshot { Type = "USER", Login = "extra-user", Role = "READER" }, // extra
            ],
        };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.Mismatch, report.Status);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Message.Contains("USER 'octocat': role mismatch", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Message.Contains("TEAM 'fixture-team' is missing in the target", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Warning && d.Message.Contains("USER 'extra-user' exists only in the target", StringComparison.Ordinal));
    }

    [Fact]
    public void User_mapping_normalizes_user_collaborators_without_changing_teams()
    {
        var source = BuildSnapshot() with
        {
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "USER", Login = "source-user", Role = "WRITER" },
                new CollaboratorSnapshot { Type = "TEAM", Login = "source-team", Role = "READER" },
            ],
        };
        var target = BuildSnapshot() with
        {
            Collaborators =
            [
                new CollaboratorSnapshot { Type = "USER", Login = "target-user", Role = "WRITER" },
                new CollaboratorSnapshot { Type = "TEAM", Login = "source-team", Role = "READER" },
            ],
        };

        var report = ProjectVerifier.Compare(
            source,
            target,
            System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["source-user"] = "target-user",
                ["source-team"] = "target-team",
            });

        Assert.Empty(report.Differences);
    }

    [Fact]
    public void Missing_linked_repositories_are_errors_and_extra_repositories_are_warnings()
    {
        var source = BuildSnapshot() with { LinkedRepositories = ["org/repo-a", "org/repo-b"] };
        var target = BuildSnapshot() with { LinkedRepositories = ["ORG/REPO-A", "org/repo-c"] }; // names are case-insensitive

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.Mismatch, report.Status);
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Error && d.Message.Contains("'org/repo-b' is missing in the target", StringComparison.Ordinal));
        Assert.Contains(report.Differences, d =>
            d.Severity == VerifySeverity.Warning && d.Message.Contains("'org/repo-c' exists only in the target", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Differences, d => d.Message.Contains("repo-a", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Repository_mapping_normalizes_items_linked_repositories_and_workflow_ui_repositories()
    {
        var workflowUi = new WorkflowUiSnapshot { Repository = "repo", Filter = "is:open" };
        var source = BuildSnapshot() with
        {
            LinkedRepositories = ["org/repo"],
            Workflows = [BuildSnapshot().Workflows[0] with { Ui = workflowUi }],
        };
        var target = source with
        {
            Items = source.Items.Select(item => item.Repository == "org/repo" ? item with { Repository = "target-org/renamed-repo" } : item).ToList(),
            LinkedRepositories = ["target-org/renamed-repo"],
            Workflows = [source.Workflows[0] with { Ui = workflowUi with { Repository = "renamed-repo" } }],
        };

        var report = ProjectVerifier.Compare(source, target, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["org/repo"] = "target-org/renamed-repo",
        });

        Assert.Empty(report.Differences);
        Assert.True(report.IsMatch);
    }

    [Fact]
    public void Linked_repositories_are_not_verified_when_the_source_predates_capture()
    {
        var target = BuildSnapshot() with { LinkedRepositories = ["org/repo-a"] };

        var source = BuildSnapshot() with { LinkedRepositories = null };
        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.Contains(report.Categories, category =>
            category.Category == "LinkedRepository" && category.Status == VerifyStatus.NotVerified);
    }

    [Fact]
    public void Linked_repositories_are_not_verified_when_target_capture_is_missing()
    {
        var source = BuildSnapshot() with { LinkedRepositories = ["org/repo-a"] };
        var target = BuildSnapshot() with { LinkedRepositories = null };

        var report = ProjectVerifier.Compare(source, target);

        Assert.Equal(VerifyStatus.NotVerified, report.Status);
        Assert.DoesNotContain(report.Differences, difference => difference.Severity == VerifySeverity.Error);
        Assert.Contains(report.Differences, difference =>
            difference.Severity == VerifySeverity.Warning
            && difference.Category == "LinkedRepository"
            && difference.Message.Contains("could not be read", StringComparison.Ordinal));
    }

    [Fact]
    public void Exit_policy_fails_errors_unconditionally_and_optional_incomplete_results()
    {
        var partial = ProjectVerifier.Compare(
            BuildSnapshot() with { Fields = BuildSnapshot().Fields.Take(1).ToList() },
            BuildSnapshot());
        Assert.Equal(VerifyStatus.PartialMatch, partial.Status);
        Assert.False(partial.ShouldFail(strict: false, failOnWarning: false));
        Assert.True(partial.ShouldFail(strict: false, failOnWarning: true));

        var notVerified = ProjectVerifier.Compare(
            BuildSnapshot() with { Collaborators = null },
            BuildSnapshot());
        Assert.Equal(VerifyStatus.NotVerified, notVerified.Status);
        Assert.False(notVerified.ShouldFail(strict: false, failOnWarning: false));
        Assert.True(notVerified.ShouldFail(strict: true, failOnWarning: false));

        var mismatch = ProjectVerifier.Compare(
            BuildSnapshot(),
            BuildSnapshot() with { Project = BuildSnapshot().Project with { Public = true } });
        Assert.True(mismatch.ShouldFail(strict: false, failOnWarning: false));
    }

    [Fact]
    public void Added_browser_warnings_affect_counts_status_and_exit_policy()
    {
        var report = ProjectVerifier.Compare(BuildSnapshot(), BuildSnapshot())
            .WithWarnings("View", ["view scrape timed out"]);

        Assert.Equal(VerifyStatus.PartialMatch, report.Status);
        Assert.Equal(1, report.WarningCount);
        Assert.Contains(report.Differences, difference =>
            difference.Category == "View" && difference.Message == "view scrape timed out");
        Assert.True(report.ShouldFail(strict: false, failOnWarning: true));
    }

    [Fact]
    public async Task Json_report_contains_the_same_status_and_counts_as_the_report()
    {
        var report = ProjectVerifier.Compare(
            BuildSnapshot(),
            BuildSnapshot() with { Views = [BuildSnapshot().Views[0] with { Ui = null }] });
        var directory = Path.Combine(Path.GetTempPath(), "gpm-verify-report-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "report.json");
        try
        {
            await VerifyReportFile.SaveAsync(report, path, TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

            Assert.Equal(report.Status.ToString(), document.RootElement.GetProperty("status").GetString());
            Assert.Equal(report.ErrorCount, document.RootElement.GetProperty("errorCount").GetInt32());
            Assert.Equal(report.WarningCount, document.RootElement.GetProperty("warningCount").GetInt32());
            Assert.Equal(report.NotVerifiedCount, document.RootElement.GetProperty("notVerifiedCount").GetInt32());
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
