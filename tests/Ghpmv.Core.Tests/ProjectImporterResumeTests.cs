using System.Net;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class ProjectImporterResumeTests
{
    [Fact]
    public async Task Ambiguous_project_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-resume-").FullName;
        try
        {
            using var handler = new ProjectResumeHandler(directory);
            using var client = CreateClient(handler);
            var prewriteCount = 0;
            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = directory,
                BeforeWriteAsync = _ =>
                {
                    prewriteCount++;
                    return Task.CompletedTask;
                },
            };

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
            Assert.Equal(2, prewriteCount);
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
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-resume-").FullName;
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

    [Fact]
    public async Task Import_into_rejects_pending_project_before_mutating_selected_project()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-project-target-").FullName;
        try
        {
            var log = new ProjectImportLog
            {
                PendingProject = new PendingProjectOperation
                {
                    OperationId = "pending-project",
                    OwnerLogin = "target",
                    Title = "Project",
                    ExistingProjectIds = [],
                },
            };
            await log.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Contains("pending project operation", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_rejects_pending_field_omitted_from_snapshot_before_mutating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-target-").FullName;
        try
        {
            var log = new ProjectImportLog();
            log.PendingFields["Custom"] = new PendingFieldOperation
            {
                OperationId = "pending-field",
                ProjectId = "PVT_existing",
                Name = "Custom",
                DataType = "TEXT",
                ExistingFieldIds = [],
            };
            await log.SaveAsync(directory, cancellationToken);
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(), "target", 7, cancellationToken));

            Assert.Contains("does not match", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Field_reconciliation_rejects_multiple_same_named_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-field-duplicates-").FullName;
        try
        {
            using var handler = new FieldResumeHandler(directory);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicateFields = true;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(Snapshot(withField: true), "target", 7, cancellationToken));

            Assert.Contains("multiple new fields", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_issue_field_create_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-resume-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: true);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken);

            Assert.Equal("IFM_created", result.IssueFieldIds["Teams"]);
            Assert.Equal(1, handler.IssueFieldCreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Issue_field_reconciliation_rejects_multiple_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-duplicates-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: true);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicates = true;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Contains("multiple new Issue Fields", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.IssueFieldCreateMutationCount);
            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFields);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Ambiguous_issue_field_link_is_adopted_without_resending()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-link-resume-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
            Assert.True(handler.PendingWasPresentAtMutation);

            handler.Resume = true;
            var result = await importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken);

            Assert.Equal("PVTF_created", result.FieldIds["Teams"]);
            Assert.Equal(1, handler.LinkCreateMutationCount);
            Assert.Empty((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Issue_field_link_reconciliation_rejects_multiple_candidates()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-issue-field-link-duplicates-").FullName;
        try
        {
            using var handler = new IssueFieldResumeHandler(directory, ambiguousFieldCreate: false);
            using var client = CreateClient(handler);
            var importer = new ProjectImporter(client) { OperationLogDirectory = directory };

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            handler.Resume = true;
            handler.ReturnDuplicates = true;
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportIntoAsync(IssueFieldSnapshot(), "target", 7, cancellationToken));

            Assert.Contains("multiple new project fields", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.LinkCreateMutationCount);
            Assert.Single((await ProjectImportLog.LoadAsync(directory, cancellationToken)).PendingIssueFieldLinks);
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

    private static ProjectSnapshot IssueFieldSnapshot() => Snapshot() with
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
                ],
                IssueField = new IssueFieldConfigurationSnapshot
                {
                    Description = "Teams involved",
                    Visibility = "ALL",
                },
            },
        ],
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
        public bool ReturnDuplicateFields { get; set; }

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
                if (!Resume)
                {
                    return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
                }

                return ReturnDuplicateFields
                    ? Json("""{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_created_1","name":"Custom","dataType":"TEXT"},{"id":"PVTF_created_2","name":"Custom","dataType":"TEXT"}]}}}}""")
                    : Json("""{"data":{"node":{"fields":{"nodes":[{"id":"PVTF_created","name":"Custom","dataType":"TEXT"}]}}}}""");
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

    private sealed class IssueFieldResumeHandler(string directory, bool ambiguousFieldCreate) : ResumeHandler(directory)
    {
        public bool ReturnDuplicates { get; set; }

        public int IssueFieldCreateMutationCount { get; private set; }

        public int LinkCreateMutationCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var (query, _) = await ReadAsync(request, cancellationToken);
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
                if (!Resume || ambiguousFieldCreate)
                {
                    return Json("""{"data":{"node":{"fields":{"nodes":[]}}}}""");
                }

                return ReturnDuplicates
                    ? Json("""{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_created_1","name":"Teams","dataType":"MULTI_SELECT"},{"__typename":"ProjectV2Field","id":"PVTF_created_2","name":"Teams","dataType":"MULTI_SELECT"}]}}}}""")
                    : Json("""{"data":{"node":{"fields":{"nodes":[{"__typename":"ProjectV2Field","id":"PVTF_created","name":"Teams","dataType":"MULTI_SELECT"}]}}}}""");
            }

            if (query.Contains("issueFields(first:", StringComparison.Ordinal))
            {
                if (ambiguousFieldCreate && !Resume)
                {
                    return Json("""{"data":{"organization":{"issueFields":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
                }

                var nodes = ReturnDuplicates && ambiguousFieldCreate
                    ? IssueFieldNode("IFM_created_1") + "," + IssueFieldNode("IFM_created_2")
                    : IssueFieldNode("IFM_created");
                return Json(string.Concat(
                    """{"data":{"organization":{"issueFields":{"nodes":[""",
                    nodes,
                    """],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}"""));
            }

            if (query.Contains("createIssueField(", StringComparison.Ordinal))
            {
                IssueFieldCreateMutationCount++;
                var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                PendingWasPresentAtMutation = log.PendingIssueFields.Count == 1;
                throw new HttpRequestException("Response ended prematurely.");
            }

            if (query.Contains("createProjectV2IssueField(", StringComparison.Ordinal))
            {
                LinkCreateMutationCount++;
                if (!ambiguousFieldCreate)
                {
                    var log = await ProjectImportLog.LoadAsync(Directory, cancellationToken);
                    PendingWasPresentAtMutation = log.PendingIssueFieldLinks.Count == 1;
                    throw new HttpRequestException("Response ended prematurely.");
                }

                return Json("""{"data":{"createProjectV2IssueField":{"projectV2Field":{"__typename":"ProjectV2Field","id":"PVTF_created","name":"Teams","dataType":"MULTI_SELECT"}}}}""");
            }

            if (query.Contains("organization(login:", StringComparison.Ordinal))
            {
                return Json("""{"data":{"organization":{"id":"O_target"}}}""");
            }

            throw new InvalidOperationException($"Unexpected operation: {query}");
        }

        private static string IssueFieldNode(string id)
            => $$"""{"__typename":"IssueFieldMultiSelect","id":"{{id}}","name":"Teams","dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL","options":[{"id":"IFO_platform","name":"Platform","color":"PURPLE","description":null}]}""";
    }
}
