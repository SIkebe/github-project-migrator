using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectImporterLogicTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public void Visibility_update_is_only_required_when_the_value_changes(
        bool currentPublic,
        bool desiredPublic,
        bool expected)
        => Assert.Equal(expected, ProjectImporter.ShouldUpdateVisibility(currentPublic, desiredPublic));

    [Fact]
    public async Task Conflict_skip_returns_skipped_without_sending_mutations()
    {
        const string response =
            """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """;
        using var handler = new StubHandler(response);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);
        var importer = new ProjectImporter(client)
        {
            OnConflict = ConflictAction.Skip,
            OperationLogDirectory = Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}"),
        };

        var result = await importer.ImportAsync(
            MinimalSnapshot("Roadmap"),
            "target",
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectImportOutcome.Skipped, result.Outcome);
        Assert.False(result.Created);
        Assert.Equal(42, result.ProjectNumber);
        Assert.Empty(result.FieldIds);
        var request = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(request);
        Assert.DoesNotContain(
            "mutation",
            document.RootElement.GetProperty("query").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conflict_update_runs_prewrite_hook_before_sending_mutations()
    {
        const string response =
            """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """;
        using var handler = new StubHandler(response);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);
        var importer = new ProjectImporter(client)
        {
            OnConflict = ConflictAction.Update,
            BeforeWriteAsync = _ => throw new InvalidOperationException("authentication failed"),
            OperationLogDirectory = Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}"),
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(
                MinimalSnapshot("Roadmap"),
                "target",
                TestContext.Current.CancellationToken));

        Assert.Equal("authentication failed", exception.Message);
        var request = Assert.Single(handler.RequestBodies);
        using var document = JsonDocument.Parse(request);
        Assert.DoesNotContain(
            "mutation",
            document.RootElement.GetProperty("query").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Import_creates_and_links_multi_select_issue_field()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler();
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Project = MinimalSnapshot("Roadmap").Project with
                {
                    ShortDescription = null,
                    Readme = null,
                    Public = false,
                    Closed = false,
                },
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.Equal("IFO_platform", result.IssueFieldOptionIds["Teams"]["Platform"]);
            Assert.Equal("IFO_sdk", result.IssueFieldOptionIds["Teams"]["SDK"]);
            Assert.Equal("PVTF_teams", result.FieldIds["Teams"]);
            Assert.Contains(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
            var linkMutation = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
            Assert.DoesNotContain("id name dataType", linkMutation, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_reuses_existing_multi_select_issue_field_link()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.Equal("PVTF_teams", result.FieldIds["Teams"]);
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
            var fieldsQuery = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("fields(first:", StringComparison.Ordinal));
            Assert.DoesNotContain("id name dataType", fieldsQuery, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_links_issue_field_when_same_named_project_field_has_another_type()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, normalSameName: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            Assert.Equal("IFM_teams", result.IssueFieldIds["Teams"]);
            Assert.Equal("PVTF_linked_teams", result.FieldIds["Teams"]);
            Assert.Contains(
                handler.RequestBodies,
                body => body.Contains("createProjectV2IssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_updates_existing_issue_field_and_registers_replaced_options()
    {
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-import-").FullName;
        try
        {
            using var handler = new IssueFieldStubHandler(existing: true, requiresUpdate: true);
            using var client = new GitHubGraphQLClient(
                "dummy-token",
                new Uri("https://example.test/graphql"),
                handler,
                delayAsync: null);
            var snapshot = MinimalSnapshot("Roadmap") with
            {
                Fields =
                [
                    new FieldSnapshot
                    {
                        Name = "Teams",
                        DataType = "MULTI_SELECT",
                        Options =
                        [
                            new SingleSelectOptionSnapshot { Id = "source-platform", Name = "Platform", Color = "PURPLE" },
                            new SingleSelectOptionSnapshot { Id = "source-sdk", Name = "SDK", Color = "GREEN" },
                        ],
                        IssueField = new IssueFieldConfigurationSnapshot
                        {
                            Description = "Teams involved",
                            Visibility = "ALL",
                        },
                    },
                ],
            };
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
            };

            var result = await importer.ImportIntoAsync(
                snapshot,
                "target",
                7,
                TestContext.Current.CancellationToken);

            var updateRequest = Assert.Single(
                handler.RequestBodies,
                body => body.Contains("updateIssueField", StringComparison.Ordinal));
            using var document = JsonDocument.Parse(updateRequest);
            var variables = document.RootElement.GetProperty("variables");
            Assert.Equal("IFM_teams", variables.GetProperty("id").GetString());
            Assert.Equal("Teams involved", variables.GetProperty("description").GetString());
            Assert.Equal("ALL", variables.GetProperty("visibility").GetString());
            Assert.Equal(
                ["Platform", "SDK"],
                variables.GetProperty("options").EnumerateArray().Select(option => option.GetProperty("name").GetString()));
            Assert.Equal("IFO_platform_updated", result.IssueFieldOptionIds["Teams"]["Platform"]);
            Assert.Equal("IFO_sdk_updated", result.IssueFieldOptionIds["Teams"]["SDK"]);
            Assert.DoesNotContain(handler.RequestBodies, body => body.Contains("createIssueField", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectSnapshot MinimalSnapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            ShortDescription = "must not be applied",
            Readme = "must not be applied",
            Public = true,
            Closed = true,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class IssueFieldStubHandler(
        bool existing = false,
        bool requiresUpdate = false,
        bool normalSameName = false) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            RequestBodies.Add(body);
            var response = body switch
            {
                _ when body.Contains("projectV2(number:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"projectV2":{"id":"PVT_target","number":7,"title":"Roadmap","url":"https://github.com/orgs/target/projects/7","public":false}}}}""",
                _ when body.Contains("updateProjectV2(", StringComparison.Ordinal) =>
                    """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_target"}}}}""",
                _ when body.Contains("fields(first:", StringComparison.Ordinal) =>
                    existing
                        ? """{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_title","name":"Title"},{"__typename":"ProjectV2Field","id":"PVTF_teams","name":"Teams"},{"__typename":"ProjectV2Field","id":"PVTF_areas","name":"Areas"}]}}}}"""
                        : """{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_title","name":"Title","dataType":"TITLE"}]}}}}""",
                _ when body.Contains("nodes(ids:", StringComparison.Ordinal) =>
                    body.Contains("PVTF_title", StringComparison.Ordinal)
                        ? """{"data":{"nodes":[{"id":"PVTF_title","dataType":"TITLE"}]}}"""
                        : normalSameName && body.Contains("PVTF_teams", StringComparison.Ordinal)
                            ? """{"data":{"nodes":[{"id":"PVTF_teams","dataType":"TEXT"}]}}"""
                        : """{"data":{"nodes":[null]},"errors":[{"message":"Something went wrong while executing your query on the preview API."}]}""",
                _ when body.Contains("issueFields(first:", StringComparison.Ordinal) =>
                    existing
                        ? requiresUpdate
                            ? """
                              {"data":{"organization":{"issueFields":{"nodes":[{
                                "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                                "dataType":"MULTI_SELECT","description":"Old description","visibility":"ALL",
                                "options":[
                                  {"id":"IFO_old","name":"Old","color":"GRAY","description":null}
                                ]
                              },{
                                "__typename":"IssueFieldMultiSelect","id":"IFM_areas","name":"Areas",
                                "dataType":"MULTI_SELECT","description":null,"visibility":"ALL","options":[]
                              }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                              """
                            : """
                          {"data":{"organization":{"issueFields":{"nodes":[{
                            "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                            "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                            "options":[
                              {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null},
                              {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                            ]
                          },{
                            "__typename":"IssueFieldMultiSelect","id":"IFM_areas","name":"Areas",
                            "dataType":"MULTI_SELECT","description":null,"visibility":"ALL","options":[]
                          }],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
                          """
                        : """{"data":{"organization":{"issueFields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""",
                _ when body.Contains("updateIssueField(", StringComparison.Ordinal) =>
                    """
                    {"data":{"updateIssueField":{"issueField":{
                      "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                      "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                      "options":[
                        {"id":"IFO_platform_updated","name":"Platform","color":"PURPLE","description":null},
                        {"id":"IFO_sdk_updated","name":"SDK","color":"GREEN","description":null}
                      ]
                    }}}}
                    """,
                _ when body.Contains("organization(login:", StringComparison.Ordinal) =>
                    """{"data":{"organization":{"id":"O_target"}}}""",
                _ when body.Contains("createIssueField(", StringComparison.Ordinal) =>
                    """
                    {"data":{"createIssueField":{"issueField":{
                      "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                      "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                      "options":[
                        {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null},
                        {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                      ]
                    }}}}
                    """,
                _ when body.Contains("createProjectV2IssueField(", StringComparison.Ordinal) =>
                    normalSameName
                        ? """{"data":{"createProjectV2IssueField":{"projectV2Field":{"id":"PVTF_linked_teams","name":"Teams","dataType":"SINGLE_SELECT"}}}}"""
                        : """{"data":{"createProjectV2IssueField":{"projectV2Field":{"id":"PVTF_teams","name":"Teams","dataType":"SINGLE_SELECT"}}}}""",
                _ => throw new InvalidOperationException($"Unexpected request: {body}"),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
