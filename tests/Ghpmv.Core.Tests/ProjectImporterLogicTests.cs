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

    private sealed class StubHandler(string response) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            };
        }
    }
}
