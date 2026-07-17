using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

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
        var directory = Directory.CreateTempSubdirectory("gpm-importlog-").FullName;
        try
        {
            var log = new ImportLog { ProjectId = "PVT_abc123" };
            log.Items["0"] = "PVTI_item0";
            log.Items["2"] = "PVTI_item2";
            log.PendingDrafts["3"] = new PendingDraftOperation
            {
                OperationId = "operation-3",
                AttemptedAt = DateTimeOffset.Parse("2026-07-17T05:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
                Title = "Pending draft",
                Body = "Pending body",
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

            var loaded = await ImportLog.LoadAsync(directory, cancellationToken);

            Assert.NotNull(loaded);
            Assert.Equal("PVT_abc123", loaded.ProjectId);
            Assert.Equal(2, loaded.Items.Count);
            Assert.Equal("PVTI_item0", loaded.Items["0"]);
            Assert.Equal("PVTI_item2", loaded.Items["2"]);
            var pending = Assert.Single(loaded.PendingDrafts);
            Assert.Equal("3", pending.Key);
            Assert.Equal("operation-3", pending.Value.OperationId);
            Assert.Equal(["PVTI_existing"], pending.Value.ExistingItemIds);
            Assert.Equal("I_issue", Assert.Single(loaded.PendingContents).Value.ContentId);
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
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
    public async Task ImportLog_load_returns_null_when_missing_or_corrupt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Directory.CreateTempSubdirectory("gpm-importlog-").FullName;
        try
        {
            Assert.Null(await ImportLog.LoadAsync(directory, cancellationToken));

            await File.WriteAllTextAsync(Path.Combine(directory, ImportLog.FileName), "{ not json", cancellationToken);
            Assert.Null(await ImportLog.LoadAsync(directory, cancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
