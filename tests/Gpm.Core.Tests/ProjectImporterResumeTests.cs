using System.Net;
using System.Text;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

public class ProjectImporterResumeTests
{
    [Fact]
    public async Task Ambiguous_project_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-project-resume-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(Snapshot(), "target", cancellationToken));

            var pending = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pending.PendingProject);
            Assert.Equal(pending.PendingProject.OperationId, handler.ClientMutationId);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportAsync(Snapshot(), "target", cancellationToken);

            Assert.Equal("PVT_created", result.ProjectId);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Null((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingProject);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_field_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-field-resume-").FullName;
        try
        {
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            var pending = await ProjectImportLog.LoadAsync(directory, cancellationToken);
            var operation = Assert.Single(pending.PendingFields).Value;
            Assert.Equal(operation.OperationId, handler.ClientMutationId);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken);

            Assert.Equal("PVTF_created", result.FieldIds["Custom"]);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static GitHubGraphQLClient CreateClient(HttpMessageHandler handler)
        => new("token", new Uri("https://example.test/graphql"), handler, (_, _) => Task.CompletedTask);

    private static ProjectSnapshot Snapshot(bool withField = false) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot { Title = "Project", Public = false, Closed = false },
        Fields = withField ? [new FieldSnapshot { Name = "Custom", DataType = "TEXT" }] : [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private abstract class ResumeHandler(string directory) : HttpMessageHandler
    {
        public bool Resume { get; set; }

        public bool PendingWasPresentAtMutation { get; protected set; }

        public string? ClientMutationId { get; protected set; }

        public int CreateMutationCount { get; protected set; }

        protected string Directory { get; } = directory;

        protected static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        protected static async Task<(string Query, JsonElement Variables)> ReadAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            return (
                document.RootElement.GetProperty("query").GetString() ?? string.Empty,
                document.RootElement.GetProperty("variables").Clone());
        }
    }

    private sealed class ProjectResumeHandler(string directory) : ResumeHandler(directory)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, variables) = await ReadAsync(request, cancellationToken);
            if (query.Contains("projectsV2(first:", StringComparison.Ordinal))
            {
                return Resume
                    ? Json("""{"data":{"organization":{"projectsV2":{"nodes":[{"id":"PVT_created","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""")
                    : Json("""{"data":{"organization":{"projectsV2":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("organization(login:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"id":"O_target"}}}""");
            }

            if (query.Contains("createProjectV2", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingProject is not null;
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();
                throw new HttpRequestException("Response ended prematurely.");
            }

            if (query.Contains("updateProjectV2", StringComparison.Ordinal))
            {
                return Json("""{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_created"}}}}""");
            }

            if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }
    }

    private sealed class FieldResumeHandler(string directory) : ResumeHandler(directory)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, variables) = await ReadAsync(request, cancellationToken);
            if (query.Contains("projectV2(number:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"projectV2":{"id":"PVT_existing","number":7,"title":"Project","url":"https://github.com/orgs/target/projects/7"}}}}""");
            }

            if (query.Contains("updateProjectV2", StringComparison.Ordinal))
            {
                return Json("""{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""");
            }

            if (query.Contains("fields(first:", StringComparison.Ordinal))
            {
                return Resume
                    ? Json("""{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_created","name":"Custom","dataType":"TEXT"}]}}}}""")
                    : Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
            }

            if (query.Contains("createProjectV2Field", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingFields.Count == 1;
                ClientMutationId = variables.GetProperty("clientMutationId").GetString();
                throw new HttpRequestException("Response ended prematurely.");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }
    }
}
