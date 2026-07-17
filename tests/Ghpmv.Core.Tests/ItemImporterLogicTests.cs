using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

/// <summary>Pure-logic tests for <see cref="ItemImporter"/> (draft attribution note) and <see cref="ImportLog"/> persistence.</summary>
public class ItemImporterLogicTests
{
    private static DraftIssueSnapshot Draft(string? body = null, string? creator = null, string? createdAt = null) => new()
    {
        Title = "Draft",
        Body = body,
        Creator = creator,
        CreatedAt = createdAt,
        Assignees = [],
    };

    [Fact]
    public void BuildDraftBody_prepends_creator_and_date()
    {
        var body = ItemImporter.BuildDraftBody(Draft(body: "Original body.", creator: "octocat", createdAt: "2026-07-01T12:34:56Z"));

        Assert.Equal("> _Originally created by @octocat on 2026-07-01T12:34:56Z._\n\nOriginal body.", body);
    }

    [Fact]
    public void BuildDraftBody_with_creator_only()
    {
        var body = ItemImporter.BuildDraftBody(Draft(body: "Body", creator: "octocat"));

        Assert.Equal("> _Originally created by @octocat._\n\nBody", body);
    }

    [Fact]
    public void BuildDraftBody_with_date_only()
    {
        var body = ItemImporter.BuildDraftBody(Draft(body: "Body", createdAt: "2026-07-01T00:00:00Z"));

        Assert.Equal("> _Originally created on 2026-07-01T00:00:00Z._\n\nBody", body);
    }

    [Fact]
    public void BuildDraftBody_returns_body_unchanged_without_attribution()
    {
        Assert.Equal("Body", ItemImporter.BuildDraftBody(Draft(body: "Body")));
        Assert.Null(ItemImporter.BuildDraftBody(Draft()));
    }

    [Fact]
    public void BuildDraftBody_returns_only_the_note_when_body_is_empty()
    {
        var body = ItemImporter.BuildDraftBody(Draft(creator: "octocat", createdAt: "2026-07-01T00:00:00Z"));

        Assert.Equal("> _Originally created by @octocat on 2026-07-01T00:00:00Z._", body);
    }

    [Fact]
    public async Task ImportLog_round_trips_through_the_file()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-").FullName;
        try
        {
            var log = new ImportLog
            {
                ProjectId = "PVT_abc123",
                SourceSnapshotFingerprint = "snapshot-fingerprint",
            };
            log.Items["0"] = "PVTI_item0";
            log.Items["2"] = "PVTI_item2";
            log.ItemStates["issue:0"] = new ImportItemState
            {
                TargetItemId = "PVTI_item0",
                TargetContentIdentity = "ISSUE:target/repo:1",
            };
            log.ItemStates["issue:2"] = new ImportItemState
            {
                TargetItemId = "PVTI_item2",
                TargetContentIdentity = "ISSUE:target/repo:2",
            };
            log.PendingDrafts["3"] = new PendingDraftOperation
            {
                OperationId = "operation-3",
                AttemptedAt = DateTimeOffset.Parse("2026-07-17T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                Title = "Pending draft",
                Body = "Pending body",
                AssigneeIds = [],
                ExistingItemIds = ["PVTI_existing"],
            };
            log.PendingContents["4"] = new PendingContentOperation
            {
                OperationId = "operation-4",
                AttemptedAt = DateTimeOffset.Parse("2026-07-17T05:01:00Z", System.Globalization.CultureInfo.InvariantCulture),
                ContentId = "I_issue",
                ExistingItemIds = [],
            };
            await log.SaveAsync(directory, cancellationToken);
            await log.SaveAsync(directory, cancellationToken);

            var loaded = await ImportLog.LoadAsync(directory, cancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal(ImportLog.CurrentSchemaVersion, loaded.SchemaVersion);
            Assert.Equal("PVT_abc123", loaded.ProjectId);
            Assert.Equal("snapshot-fingerprint", loaded.SourceSnapshotFingerprint);
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal("PVTI_item0", loaded.Items["0"]);
            Assert.Equal("PVTI_item2", loaded.Items["2"]);
            var pending = Assert.Single(loaded.PendingDrafts);
            Assert.Equal("3", pending.Key);
            Assert.Equal("operation-3", pending.Value.OperationId);
            Assert.Equal(["PVTI_existing"], pending.Value.ExistingItemIds);
            Assert.Equal("I_issue", Assert.Single(loaded.PendingContents).Value.ContentId);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
            Assert.True(File.Exists(Path.Combine(directory, ImportLog.BackupFileName)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_target_switch_when_log_has_pending_operations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-switch-").FullName;
        try
        {
            var snapshot = new ProjectSnapshot
            {
                SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
                Project = new ProjectInfoSnapshot
                {
                    Title = "Snapshot",
                    Public = false,
                    Closed = false,
                },
                Fields = [],
                Views = [],
                Workflows = [],
                Items = [],
            };
            var log = new ImportLog
            {
                ProjectId = "PVT_original",
                SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            };
            log.PendingDrafts["0"] = new PendingDraftOperation
            {
                OperationId = "operation-0",
                AttemptedAt = DateTimeOffset.UtcNow,
                Title = "Pending draft",
                AssigneeIds = [],
                ExistingItemIds = [],
            };
            await log.SaveAsync(directory, cancellationToken);
            using var client = new GitHubGraphQLClient("dummy-token");
            var importer = new ItemImporter(client);
            var target = new ImportResult
            {
                ProjectId = "PVT_different",
                ProjectNumber = 1,
                Url = "https://example.test/project/1",
                Outcome = ProjectImportOutcome.Created,
                FieldIds = new Dictionary<string, string>(),
                OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, target, directory, cancellationToken));

            Assert.Contains("different source snapshot or target project", exception.Message, StringComparison.Ordinal);
            var preserved = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("PVT_original", preserved!.ProjectId);
            Assert.Single(preserved.PendingDrafts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_rejects_changed_snapshot_without_mutating_log_or_target()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-snapshot-").FullName;
        try
        {
            var snapshot = new ProjectSnapshot
            {
                SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
                Project = new ProjectInfoSnapshot { Title = "Snapshot", Public = false, Closed = false },
                Fields = [],
                Views = [],
                Workflows = [],
                Items = [],
            };
            var log = new ImportLog
            {
                ProjectId = "PVT_target",
                SourceSnapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot),
            };
            await log.SaveAsync(directory, cancellationToken);
            var changed = snapshot with
            {
                Project = snapshot.Project with { ShortDescription = "changed" },
            };
            var target = new ImportResult
            {
                ProjectId = "PVT_target",
                ProjectNumber = 1,
                Url = "https://example.test/project/1",
                Outcome = ProjectImportOutcome.Updated,
                FieldIds = new Dictionary<string, string>(),
                OptionIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
                IterationIds = new Dictionary<string, IReadOnlyDictionary<string, string>>(),
            };
            using var client = new GitHubGraphQLClient("dummy-token");

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => new ItemImporter(client).ImportAsync(changed, target, directory, cancellationToken));

            var preserved = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal(log.SourceSnapshotFingerprint, preserved!.SourceSnapshotFingerprint);
            Assert.Empty(preserved.ItemStates);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SelectReconciledDraftItemId_returns_only_new_unimported_match()
    {
        var result = ItemImporter.SelectReconciledDraftItemId(
            "operation",
            ["before", "already-imported", "created"],
            ["before"],
            ["already-imported"]);

        Assert.Equal("created", result);
    }

    [Fact]
    public void SelectReconciledDraftItemId_returns_null_when_create_did_not_happen()
    {
        var result = ItemImporter.SelectReconciledDraftItemId(
            "operation",
            ["before"],
            ["before"],
            []);

        Assert.Null(result);
    }

    [Fact]
    public void SelectReconciledDraftItemId_rejects_multiple_new_matches()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => ItemImporter.SelectReconciledDraftItemId(
                "operation",
                ["created-1", "created-2"],
                [],
                []));

        Assert.Contains("multiple new target items", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportLog_load_returns_null_when_missing_and_rejects_corrupt_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-").FullName;
        try
        {
            Assert.Null(await ImportLog.LoadAsync(directory, cancellationToken));

            await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName), "{ not json", cancellationToken);
            await Assert.ThrowsAsync<System.Text.Json.JsonException>(
                () => ImportLog.LoadAsync(directory, cancellationToken));

            await File.WriteAllTextAsync(
                Path.Combine(directory, ImportLog.FileName),
                """{"schemaVersion":2,"projectId":"PVT_target","sourceSnapshotFingerprint":"fingerprint","itemStates":{"item":{"targetItemId":null}}}""",
                cancellationToken);
            var malformed = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImportLog.LoadAsync(directory, cancellationToken));
            Assert.Contains("malformed item state", malformed.Message, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                Path.Combine(directory, ImportLog.FileName),
                """{"schemaVersion":2,"projectId":"PVT_target","sourceSnapshotFingerprint":"fingerprint","items":{"0":"PVTI_orphan"},"itemStates":{},"pendingDrafts":{},"pendingContents":{}}""",
                cancellationToken);
            var inconsistent = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImportLog.LoadAsync(directory, cancellationToken));
            Assert.Contains("inconsistent item mappings", inconsistent.Message, StringComparison.Ordinal);

            await File.WriteAllTextAsync(
                Path.Combine(directory, ImportLog.FileName),
                """{"schemaVersion":2,"projectId":"PVT_target","sourceSnapshotFingerprint":"fingerprint","items":{"0":"PVTI_item"},"itemStates":{"item":{"targetItemId":"PVTI_item","targetContentIdentity":"DRAFT_ISSUE:assignees:"}},"pendingDrafts":{"0":{"operationId":"op","attemptedAt":"2026-07-17T00:00:00Z","title":"Draft","assigneeIds":[],"existingItemIds":[]}},"pendingContents":{}}""",
                cancellationToken);
            var overlapping = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImportLog.LoadAsync(directory, cancellationToken));
            Assert.Contains("overlapping pending item operations", overlapping.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportLog_rejects_legacy_schema_instead_of_ignoring_it()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-legacy-").FullName;
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, ImportLog.FileName),
                """{"projectId":"PVT_old","items":{"0":"PVTI_old"}}""",
                cancellationToken);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(
                () => ImportLog.LoadAsync(directory, cancellationToken));

            Assert.Contains("unsupported schema version 0", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ImportLog_replace_failure_preserves_previous_primary()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("ghpmv-importlog-atomic-").FullName;
        try
        {
            var original = new ImportLog
            {
                ProjectId = "PVT_original",
                SourceSnapshotFingerprint = "original",
            };
            await original.SaveAsync(directory, cancellationToken);
            Directory.CreateDirectory(Path.Combine(directory, ImportLog.BackupFileName));

            var replacement = original with { SourceSnapshotFingerprint = "replacement" };
            var exception = await Record.ExceptionAsync(() => replacement.SaveAsync(directory, cancellationToken));
            Assert.True(exception is IOException or UnauthorizedAccessException);

            var preserved = await ImportLog.LoadAsync(directory, cancellationToken);
            Assert.Equal("original", preserved!.SourceSnapshotFingerprint);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
