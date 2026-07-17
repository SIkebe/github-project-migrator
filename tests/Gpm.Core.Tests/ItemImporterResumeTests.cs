using System.Net;
using System.Text;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

public class ItemImporterResumeTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Ambiguous_item_create_is_reconciled_without_resending(bool draft)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-resume-").FullName;
        try
        {
            using var handler = new ResumeHandler(draft, directory);
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var importer = CreateImporter(client);
            var snapshot = CreateSnapshot(draft, assignedDraft: false);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            var pending = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(pending);
            var operationId = draft
                ? Assert.Single(pending.PendingDrafts).Value.OperationId
                : Assert.Single(pending.PendingContents).Value.OperationId;
            Assert.True(handler.PendingWasPresentAtMutation);
            Assert.Equal(operationId, handler.ClientMutationId);

            handler.Resume = true;
            await importer.ImportAsync(snapshot, Target, directory, cancellationToken);

            var completed = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(completed);
            Assert.Equal("PVTI_new", completed.Items["0"]);
            Assert.Empty(completed.PendingDrafts);
            Assert.Empty(completed.PendingContents);
            Assert.Equal(1, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Definitive_or_presend_failure_clears_pending_operation(bool failBeforeMutation)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-resume-").FullName;
        try
        {
            using var handler = new ResumeHandler(draft: true, directory)
            {
                FailBeforeMutation = failBeforeMutation,
                FailDefinitively = !failBeforeMutation,
            };
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var importer = CreateImporter(client);

            await Assert.ThrowsAsync<GitHubGraphQLException>(
                () => importer.ImportAsync(
                    CreateSnapshot(draft: true, assignedDraft: failBeforeMutation),
                    Target,
                    directory,
                    cancellationToken));

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Empty(log.PendingDrafts);
            Assert.Empty(log.PendingContents);
            Assert.Equal(failBeforeMutation ? 0 : 1, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_rejects_pending_operation_for_different_item_kind()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-resume-").FullName;
        try
        {
            using var handler = new ResumeHandler(draft: true, directory);
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var importer = CreateImporter(client);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(CreateSnapshot(draft: true, assignedDraft: false), Target, directory, cancellationToken));
            var mutationCount = handler.CreateMutationCount;

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(CreateSnapshot(draft: false, assignedDraft: false), Target, directory, cancellationToken));

            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.NotNull(log);
            Assert.Single(log.PendingDrafts);
            Assert.Empty(log.PendingContents);
            Assert.Equal(mutationCount, handler.CreateMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ItemImporter CreateImporter(GitHubGraphQLClient client) => new(client)
    {
        RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["source/repo"] = "target/repo",
        },
        UserMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["missing"] = "missing",
        },
    };

    private static ProjectSnapshot CreateSnapshot(bool draft, bool assignedDraft)
        => new()
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "Project", Public = false, Closed = false },
            Fields = [],
            Views = [],
            Workflows = [],
            Items =
            [
                draft
                    ? new ItemSnapshot
                    {
                        Type = "DRAFT_ISSUE",
                        Position = 0,
                        IsArchived = false,
                        Draft = new DraftIssueSnapshot
                        {
                            Title = "Draft",
                            Body = "Body",
                            Assignees = assignedDraft ? ["missing"] : [],
                        },
                        FieldValues = [],
                    }
                    : new ItemSnapshot
                    {
                        Type = "ISSUE",
                        Position = 0,
                        IsArchived = false,
                        Repository = "source/repo",
                        Number = 1,
                        FieldValues = [],
                    },
            ],
        };

    private static readonly ImportResult Target = new()
    {
        ProjectId = "PVT_project",
        ProjectNumber = 1,
        Url = "https://github.com/orgs/target/projects/1",
        Outcome = ProjectImportOutcome.Updated,
        FieldIds = new Dictionary<string, string>(),
        OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
        IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
    };

    private sealed class ResumeHandler(bool draft, string logDirectory) : HttpMessageHandler
    {
        public bool Resume { get; set; }

        public bool FailBeforeMutation { get; init; }

        public bool FailDefinitively { get; init; }

        public bool PendingWasPresentAtMutation { get; private set; }

        public string? ClientMutationId { get; private set; }

        public int CreateMutationCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var query = document.RootElement.GetProperty("query").GetString() ?? string.Empty;

            if (query.Contains("issueOrPullRequest", StringComparison.Ordinal))
            {
                return Json("""{"data":{"repository":{"issueOrPullRequest":{"id":"I_content"}}}}""");
            }

            if (query.Contains("user(login:", StringComparison.Ordinal))
            {
                return FailBeforeMutation
                    ? Json("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"Lookup failed"}]}""")
                    : Json("""{"data":null,"errors":[{"type":"NOT_FOUND","message":"User not found"}]}""");
            }

            if (query.Contains("items(first: 100", StringComparison.Ordinal))
            {
                if (!Resume)
                {
                    return Json("""{"data":{"node":{"items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
                }

                return draft
                    ? Json("""{"data":{"node":{"items":{"nodes":[{"id":"PVTI_new","type":"DRAFT_ISSUE","content":{"title":"Draft","body":"Body"}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""")
                    : Json("""{"data":{"node":{"items":{"nodes":[{"id":"PVTI_new","content":{"id":"I_content"}}],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("addProjectV2DraftIssue", StringComparison.Ordinal)
                || query.Contains("addProjectV2ItemById", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                var pending = await ImportLog.LoadAsync(logDirectory, cancellationToken);
                PendingWasPresentAtMutation = draft
                    ? pending?.PendingDrafts.Count == 1
                    : pending?.PendingContents.Count == 1;
                ClientMutationId = document.RootElement.GetProperty("variables").GetProperty("clientMutationId").GetString();

                if (FailDefinitively)
                {
                    return Json("""{"data":null,"errors":[{"type":"UNPROCESSABLE","message":"Invalid input"}]}""");
                }

                throw new HttpRequestException("Response ended prematurely.");
            }

            if (query.Contains("updateProjectV2ItemPosition", StringComparison.Ordinal))
            {
                return Json("""{"data":{"updateProjectV2ItemPosition":{"clientMutationId":"position"}}}""");
            }

            throw new InvalidOperationException($"Unexpected GraphQL operation: {query}");
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
