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
            if (failBeforeMutation)
            {
                Assert.Null(log);
            }
            else
            {
                Assert.NotNull(log);
                Assert.Empty(log.PendingDrafts);
                Assert.Empty(log.PendingContents);
            }
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

    [Fact]
    public async Task Resume_rejects_changed_draft_assignee_identity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-resume-").FullName;
        try
        {
            using var handler = new ResumeHandler(draft: true, directory);
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var importer = CreateImporter(client);
            var snapshot = CreateSnapshot(draft: true, assignedDraft: false);

            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));
            var log = await ImportLog.LoadAsync(directory, cancellationToken);
            var pending = Assert.Single(log!.PendingDrafts);
            log.PendingDrafts[pending.Key] = pending.Value with { AssigneeIds = ["changed-id"] };
            await log.SaveAsync(directory, cancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Single((await ImportLog.LoadAsync(directory, cancellationToken))!.PendingDrafts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_rejects_removed_repository_mapping_instead_of_skipping()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-resume-").FullName;
        try
        {
            using var handler = new ResumeHandler(draft: false, directory);
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            await Assert.ThrowsAsync<AmbiguousMutationResultException>(
                () => CreateImporter(client).ImportAsync(
                    CreateSnapshot(draft: false, assignedDraft: false),
                    Target,
                    directory,
                    cancellationToken));

            var importerWithoutMapping = new ItemImporter(client);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importerWithoutMapping.ImportAsync(
                    CreateSnapshot(draft: false, assignedDraft: false),
                    Target,
                    directory,
                    cancellationToken));

            Assert.Contains("repository mapping", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Single((await ImportLog.LoadAsync(directory, cancellationToken))!.PendingContents);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("field")]
    [InlineData("position")]
    [InlineData("archive")]
    public async Task Failed_stage_resumes_without_recreating_item(string failedStage)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-stage-resume-").FullName;
        try
        {
            using var handler = new StageResumeHandler(failedStage);
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var importer = CreateImporter(client);
            var snapshot = CreateStageSnapshot(archived: failedStage == "archive", withField: failedStage == "field");
            var target = Target with
            {
                FieldIds = failedStage == "field"
                    ? new Dictionary<string, string> { ["Text"] = "PVTF_text" }
                    : new Dictionary<string, string>(),
            };

            if (failedStage == "archive")
            {
                var first = await importer.ImportAsync(snapshot, target, directory, cancellationToken);
                Assert.Single(first.Warnings);
            }
            else
            {
                await Assert.ThrowsAsync<GitHubGraphQLException>(
                    () => importer.ImportAsync(snapshot, target, directory, cancellationToken));
            }

            var interrupted = await ImportLog.LoadAsync(directory, cancellationToken);
            var interruptedState = Assert.Single(interrupted!.ItemStates).Value;
            Assert.Equal("PVTI_new", interruptedState.TargetItemId);
            Assert.NotNull(interruptedState.LastError);

            await importer.ImportAsync(snapshot, target, directory, cancellationToken);

            var completed = await ImportLog.LoadAsync(directory, cancellationToken);
            var completedState = Assert.Single(completed!.ItemStates).Value;
            Assert.True(completedState.FieldValuesApplied);
            Assert.True(completedState.PositionApplied);
            Assert.True(completedState.ArchiveApplied);
            Assert.Null(completedState.LastError);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(2, failedStage switch
            {
                "field" => handler.FieldMutationCount,
                "position" => handler.PositionMutationCount,
                _ => handler.ArchiveMutationCount,
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_field_mapping_remains_resumable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-field-resume-").FullName;
        try
        {
            using var handler = new StageResumeHandler(failedStage: "");
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var snapshot = CreateStageSnapshot(archived: false, withField: true);

            var first = await CreateImporter(client).ImportAsync(snapshot, Target, directory, cancellationToken);
            Assert.Single(first.Warnings);
            Assert.False(Assert.Single((await ImportLog.LoadAsync(directory, cancellationToken))!.ItemStates).Value.FieldValuesApplied);

            var targetWithField = Target with
            {
                FieldIds = new Dictionary<string, string> { ["Text"] = "PVTF_text" },
            };
            await CreateImporter(client).ImportAsync(snapshot, targetWithField, directory, cancellationToken);

            Assert.True(Assert.Single((await ImportLog.LoadAsync(directory, cancellationToken))!.ItemStates).Value.FieldValuesApplied);
            Assert.Equal(1, handler.CreateMutationCount);
            Assert.Equal(1, handler.FieldMutationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Resume_rejects_changed_repository_mapping_after_creation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-mapping-resume-").FullName;
        try
        {
            using var handler = new StageResumeHandler(failedStage: "");
            using var client = new GitHubGraphQLClient("token", baseUrl: null, handler, (_, _) => Task.CompletedTask);
            var snapshot = CreateStageSnapshot(archived: false, withField: false);
            await CreateImporter(client).ImportAsync(snapshot, Target, directory, cancellationToken);

            var changedImporter = new ItemImporter(client)
            {
                RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["source/repo"] = "other/repo",
                },
            };
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => changedImporter.ImportAsync(snapshot, Target, directory, cancellationToken));

            Assert.Contains("repository mapping no longer matches", exception.Message, StringComparison.Ordinal);
            Assert.Equal(1, handler.CreateMutationCount);
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

    private static ProjectSnapshot CreateStageSnapshot(bool archived, bool withField)
        => new()
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot { Title = "Project", Public = false, Closed = false },
            Fields = [],
            Views = [],
            Workflows = [],
            Items =
            [
                new ItemSnapshot
                {
                    Type = "ISSUE",
                    Position = 0,
                    IsArchived = archived,
                    Repository = "source/repo",
                    Number = 1,
                    FieldValues = withField
                        ? [new FieldValueSnapshot { FieldName = "Text", Text = "value" }]
                        : [],
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
                    return Json("""{"data":null,"errors":[{"type":"BAD_USER_INPUT","message":"Invalid input"}]}""");
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

    private sealed class StageResumeHandler(string failedStage) : HttpMessageHandler
    {
        public int CreateMutationCount { get; private set; }

        public int FieldMutationCount { get; private set; }

        public int PositionMutationCount { get; private set; }

        public int ArchiveMutationCount { get; private set; }

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

            if (query.Contains("items(first: 100", StringComparison.Ordinal))
            {
                return Json("""{"data":{"node":{"items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}""");
            }

            if (query.Contains("addProjectV2ItemById", StringComparison.Ordinal))
            {
                CreateMutationCount++;
                return Json("""{"data":{"addProjectV2ItemById":{"item":{"id":"PVTI_new"}}}}""");
            }

            if (query.Contains("updateProjectV2ItemFieldValue", StringComparison.Ordinal))
            {
                FieldMutationCount++;
                return ShouldFail("field", FieldMutationCount)
                    ? Error()
                    : Json("""{"data":{"updateProjectV2ItemFieldValue":{"projectV2Item":{"id":"PVTI_new"}}}}""");
            }

            if (query.Contains("updateProjectV2ItemPosition", StringComparison.Ordinal))
            {
                PositionMutationCount++;
                return ShouldFail("position", PositionMutationCount)
                    ? Error()
                    : Json("""{"data":{"updateProjectV2ItemPosition":{"clientMutationId":"position"}}}""");
            }

            if (query.Contains("archiveProjectV2Item", StringComparison.Ordinal))
            {
                ArchiveMutationCount++;
                return ShouldFail("archive", ArchiveMutationCount)
                    ? Error()
                    : Json("""{"data":{"archiveProjectV2Item":{"item":{"id":"PVTI_new"}}}}""");
            }

            throw new InvalidOperationException($"Unexpected GraphQL operation: {query}");
        }

        private bool ShouldFail(string stage, int count)
            => string.Equals(failedStage, stage, StringComparison.Ordinal) && count == 1;

        private static HttpResponseMessage Error()
            => Json("""{"data":null,"errors":[{"type":"FORBIDDEN","message":"Injected stage failure"}]}""");

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
