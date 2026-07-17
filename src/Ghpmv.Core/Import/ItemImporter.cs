using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>
/// Imports a snapshot's items into a target project (M4): adds Issue/PR items through
/// the repository mapping, recreates draft issues (with an attribution note and mapped
/// assignees), applies field values via the option/iteration id maps of
/// <see cref="ImportResult"/>, restores item order with <c>updateProjectV2ItemPosition</c>
/// and re-archives archived items. Progress of created items is persisted to
/// <see cref="ImportLog"/> (<c>import-log.json</c>) after every item so a re-run resumes
/// without duplicating items.
/// </summary>
public sealed class ItemImporter
{
    /// <summary>The built-in Title field is set through item content, never via updateProjectV2ItemFieldValue.</summary>
    private const string TitleFieldName = "Title";

    private readonly GitHubGraphQLClient _client;
    private readonly Dictionary<string, string?> _userIdCache = new(StringComparer.OrdinalIgnoreCase);

    public ItemImporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Source "org/repo" → target "org/repo" mapping for Issue/PR items. Items whose repository is unmapped are skipped with a warning.</summary>
    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Source login → target login mapping for draft issue assignees. Unmapped assignees are dropped with a warning.</summary>
    public IReadOnlyDictionary<string, string> UserMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Invoked with a human-readable progress message for each item and stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>
    /// Imports all snapshot items into the project identified by <paramref name="target"/>.
    /// The resume log is read from and written to <paramref name="logDirectory"/>.
    /// </summary>
    public async Task<ItemImportResult> ImportAsync(ProjectSnapshot snapshot, ImportResult target, string logDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);

        var snapshotFingerprint = ImportLog.ComputeSnapshotFingerprint(snapshot);
        var log = await ImportLog.LoadAsync(logDirectory, cancellationToken).ConfigureAwait(false);
        if (log is not null
            && (!string.Equals(log.ProjectId, target.ProjectId, StringComparison.Ordinal)
                || !string.Equals(log.SourceSnapshotFingerprint, snapshotFingerprint, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{ImportLog.FileName} in '{logDirectory}' belongs to a different source snapshot or target project. Use a separate log directory or restore the matching snapshot and target before resuming.");
        }

        log ??= new ImportLog
        {
            ProjectId = target.ProjectId,
            SourceSnapshotFingerprint = snapshotFingerprint,
        };

        var warnings = new List<string>();
        var items = snapshot.Items.OrderBy(i => i.Position).ToList();
        var total = items.Count;
        var created = 0;
        var resumed = 0;
        var alreadyComplete = 0;
        var skipped = 0;

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var key = item.Position.ToString(CultureInfo.InvariantCulture);
            var stateKey = BuildItemStateKey(item);
            var label = DescribeItem(item);
            var prefix = string.Create(CultureInfo.InvariantCulture, $"[{index + 1}/{total}]");
            IReadOnlyList<string>? draftAssigneeIds = null;
            if (item is { Type: "DRAFT_ISSUE", Draft: not null })
            {
                draftAssigneeIds = await ResolveAssigneeIdsAsync(item.Draft, warnings, cancellationToken).ConfigureAwait(false);
            }
            var targetContentIdentity = GetTargetContentIdentity(item, draftAssigneeIds);

            if (log.ItemStates.TryGetValue(stateKey, out var existingState)
                && !string.Equals(existingState.TargetContentIdentity, targetContentIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{label}: the target content mapping no longer matches the identity recorded in {ImportLog.FileName}. Restore the original repository or user mapping, or use a separate log directory.");
            }

            if (log.ItemStates.TryGetValue(stateKey, out var completedState)
                && completedState.FieldValuesApplied)
            {
                if (completedState.PositionApplied && completedState.ArchiveApplied)
                {
                    OnProgress?.Invoke($"{prefix} {label}: already complete.");
                    alreadyComplete++;
                }
                else
                {
                    OnProgress?.Invoke($"{prefix} {label}: content and field values already complete; resuming later stages.");
                    resumed++;
                }
                continue;
            }

            OnProgress?.Invoke($"{prefix} Importing or resuming {label}...");
            var hasPendingDraft = log.PendingDrafts.ContainsKey(key);
            var hasPendingContent = log.PendingContents.ContainsKey(key);
            var expectsDraft = item is { Type: "DRAFT_ISSUE", Draft: not null };
            var expectsContent = item.Type is "ISSUE" or "PULL_REQUEST";
            if ((hasPendingDraft && !expectsDraft)
                || (hasPendingContent && !expectsContent)
                || (hasPendingDraft && hasPendingContent))
            {
                throw new InvalidOperationException(
                    $"Pending operation at item position {key} does not match the current snapshot item type '{item.Type}'. Restore the original snapshot or reconcile the target manually.");
            }

            var resumedPendingOperation = hasPendingDraft || hasPendingContent;
            try
            {
                string? itemId = completedState?.TargetItemId;
                PendingDraftOperation? pendingDraft = null;
                if (itemId is null && item is { Type: "DRAFT_ISSUE", Draft: not null })
                {
                    var body = BuildDraftBody(item.Draft);
                    var assigneeIds = draftAssigneeIds ?? [];
                    if (log.PendingDrafts.TryGetValue(key, out pendingDraft))
                    {
                        if (!string.Equals(pendingDraft.Title, item.Draft.Title, StringComparison.Ordinal)
                            || !string.Equals(NormalizeDraftBody(pendingDraft.Body), NormalizeDraftBody(body), StringComparison.Ordinal)
                            || pendingDraft.AssigneeIds is null
                            || !pendingDraft.AssigneeIds.SequenceEqual(assigneeIds, StringComparer.Ordinal))
                        {
                            throw new InvalidOperationException(
                                $"Pending draft operation '{pendingDraft.OperationId}' no longer matches {label}. Restore the original snapshot or reconcile the target manually.");
                        }

                        itemId = await ReconcilePendingDraftAsync(
                            target.ProjectId,
                            pendingDraft,
                            log.Items.Values,
                            cancellationToken).ConfigureAwait(false);
                        OnProgress?.Invoke($"{prefix} {label}: reconciled the pending create operation to target item '{itemId}'.");
                    }
                    else
                    {
                        var existingIds = await FindMatchingDraftItemIdsAsync(
                            target.ProjectId,
                            item.Draft.Title,
                            body,
                            cancellationToken).ConfigureAwait(false);
                        pendingDraft = new PendingDraftOperation
                        {
                            OperationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
                            AttemptedAt = DateTimeOffset.UtcNow,
                            Title = item.Draft.Title,
                            Body = body,
                            AssigneeIds = [.. assigneeIds],
                            ExistingItemIds = [.. existingIds],
                        };
                        log.PendingDrafts[key] = pendingDraft;
                        await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                    }
                }

                itemId ??= await CreateItemAsync(
                    item,
                    target.ProjectId,
                    label,
                    warnings,
                    pendingDraft?.OperationId,
                    pendingDraft?.AssigneeIds,
                    log,
                    key,
                    logDirectory,
                    cancellationToken).ConfigureAwait(false);
                if (itemId is null)
                {
                    skipped++;
                    continue;
                }

                // Persist the mapping immediately so an interrupted run never duplicates this item.
                log.Items[key] = itemId;
                if (!log.ItemStates.TryGetValue(stateKey, out var itemState))
                {
                    itemState = new ImportItemState
                    {
                        TargetItemId = itemId,
                        TargetContentIdentity = targetContentIdentity,
                    };
                    log.ItemStates[stateKey] = itemState;
                }
                log.PendingDrafts.Remove(key);
                log.PendingContents.Remove(key);
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);

                itemState.FieldValuesApplied = await ApplyFieldValuesAsync(
                    item,
                    itemId,
                    target,
                    label,
                    warnings,
                    cancellationToken).ConfigureAwait(false);
                itemState.FieldValuesError = itemState.FieldValuesApplied
                    ? null
                    : "One or more field values could not be applied; resume will retry this stage.";
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                if (completedState is null && !resumedPendingOperation)
                {
                    created++;
                }
                else
                {
                    resumed++;
                }
            }
            catch (AmbiguousMutationResultException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (!resumedPendingOperation
                    && (log.PendingDrafts.Remove(key) | log.PendingContents.Remove(key)))
                {
                    await log.SaveAsync(logDirectory, CancellationToken.None).ConfigureAwait(false);
                }
                else if (log.ItemStates.TryGetValue(stateKey, out var itemState))
                {
                    itemState.FieldValuesError = exception.Message;
                    await log.SaveAsync(logDirectory, CancellationToken.None).ConfigureAwait(false);
                }

                throw;
            }
        }

        await ApplyPositionsAsync(items, target.ProjectId, log, logDirectory, cancellationToken).ConfigureAwait(false);
        await ArchiveItemsAsync(items, target.ProjectId, log, logDirectory, warnings, cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Item import finished: {created} created, {resumed} resumed, {alreadyComplete} already complete, {skipped} skipped, {warnings.Count} warnings."));

        return new ItemImportResult
        {
            Created = created,
            Resumed = resumed,
            AlreadyComplete = alreadyComplete,
            Skipped = skipped,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Builds the draft issue body with an attribution note (original creator and creation time)
    /// prepended, when the snapshot carries them. Returns the body unchanged otherwise.
    /// </summary>
    public static string? BuildDraftBody(DraftIssueSnapshot draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.Creator is null && draft.CreatedAt is null)
        {
            return draft.Body;
        }

        var note = (draft.Creator, draft.CreatedAt) switch
        {
            (not null, not null) => $"> _Originally created by @{draft.Creator} on {draft.CreatedAt}._",
            (not null, null) => $"> _Originally created by @{draft.Creator}._",
            _ => $"> _Originally created on {draft.CreatedAt}._",
        };

        return string.IsNullOrEmpty(draft.Body) ? note : note + "\n\n" + draft.Body;
    }

    private async Task<string?> CreateItemAsync(
        ItemSnapshot item,
        string projectId,
        string label,
        List<string> warnings,
        string? clientMutationId,
        IReadOnlyList<string>? draftAssigneeIds,
        ImportLog log,
        string key,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        switch (item.Type)
        {
            case "ISSUE" or "PULL_REQUEST":
                return await CreateContentItemAsync(
                    item,
                    projectId,
                    label,
                    warnings,
                    log,
                    key,
                    logDirectory,
                    cancellationToken).ConfigureAwait(false);

            case "DRAFT_ISSUE" when item.Draft is not null:
                return await CreateDraftItemAsync(item.Draft, projectId, draftAssigneeIds ?? [], clientMutationId, cancellationToken).ConfigureAwait(false);

            default:
                Warn(warnings, $"{label}: unsupported or incomplete item (type '{item.Type}'); skipping.");
                return null;
        }
    }

    private async Task<string?> CreateContentItemAsync(
        ItemSnapshot item,
        string projectId,
        string label,
        List<string> warnings,
        ImportLog log,
        string key,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        var hasPendingOperation = log.PendingContents.TryGetValue(key, out var existingPending);
        if (item.Repository is null || item.Number is null)
        {
            if (hasPendingOperation)
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{existingPending!.OperationId}' no longer has a repository and item number in the snapshot. Restore the original snapshot or reconcile the target manually.");
            }

            Warn(warnings, $"{label}: snapshot is missing the repository or number; skipping.");
            return null;
        }

        if (!RepositoryMapping.TryGetValue(item.Repository, out var targetRepository))
        {
            if (hasPendingOperation)
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{existingPending!.OperationId}' can no longer resolve repository mapping for '{item.Repository}'. Restore the original mapping or reconcile the target manually.");
            }

            Warn(warnings, $"{label}: no repository mapping for '{item.Repository}'; skipping.");
            return null;
        }

        var separator = targetRepository.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == targetRepository.Length - 1)
        {
            if (hasPendingOperation)
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{existingPending!.OperationId}' has invalid repository mapping '{targetRepository}'. Restore the original mapping or reconcile the target manually.");
            }

            Warn(warnings, $"{label}: mapped repository '{targetRepository}' is not in 'owner/name' form; skipping.");
            return null;
        }

        var owner = targetRepository[..separator];
        var name = targetRepository[(separator + 1)..];
        var contentId = await ResolveIssueOrPullRequestIdAsync(owner, name, item.Number.Value, cancellationToken).ConfigureAwait(false);
        if (contentId is null)
        {
            if (hasPendingOperation)
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{existingPending!.OperationId}' can no longer resolve '{targetRepository}#{item.Number.Value}'. Restore the original target content or reconcile the target manually.");
            }

            Warn(warnings, string.Create(CultureInfo.InvariantCulture,
                $"{label}: '{targetRepository}#{item.Number.Value}' was not found in the target; skipping."));
            return null;
        }

        PendingContentOperation pending;
        if (hasPendingOperation)
        {
            pending = existingPending!;
            if (!string.Equals(pending.ContentId, contentId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{pending.OperationId}' no longer matches {label}. Restore the original snapshot or reconcile the target manually.");
            }

            return await ReconcilePendingContentAsync(
                projectId,
                pending,
                log.Items.Values,
                cancellationToken).ConfigureAwait(false);
        }

        var existingIds = await FindContentItemIdsAsync(projectId, contentId, cancellationToken).ConfigureAwait(false);
        pending = new PendingContentOperation
        {
            OperationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture),
            AttemptedAt = DateTimeOffset.UtcNow,
            ContentId = contentId,
            ExistingItemIds = [.. existingIds],
        };
        log.PendingContents[key] = pending;
        await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);

        var data = await _client.MutationAsync(
            "addProjectV2ItemById",
            """
            mutation($projectId: ID!, $contentId: ID!, $clientMutationId: String!) {
              addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId, clientMutationId: $clientMutationId }) {
                item { id }
              }
            }
            """,
            new { projectId, contentId },
            target: $"{projectId}/{contentId}",
            clientMutationId: pending.OperationId,
            requiredResultPath: "item.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return data.GetProperty("addProjectV2ItemById").GetProperty("item").GetProperty("id").GetString();
    }

    private async Task<string?> CreateDraftItemAsync(
        DraftIssueSnapshot draft,
        string projectId,
        IReadOnlyList<string> assigneeIds,
        string? clientMutationId,
        CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "addProjectV2DraftIssue",
            """
            mutation($projectId: ID!, $title: String!, $body: String, $assigneeIds: [ID!], $clientMutationId: String!) {
              addProjectV2DraftIssue(input: { projectId: $projectId, title: $title, body: $body, assigneeIds: $assigneeIds, clientMutationId: $clientMutationId }) {
                projectItem { id }
              }
            }
            """,
            new { projectId, title = draft.Title, body = BuildDraftBody(draft), assigneeIds },
            target: projectId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectItem.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return data.GetProperty("addProjectV2DraftIssue").GetProperty("projectItem").GetProperty("id").GetString();
    }

    private async Task<IReadOnlyList<string>> FindMatchingDraftItemIdsAsync(
        string projectId,
        string title,
        string? body,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var node in _client.QueryPaginatedAsync(
            """
            query($projectId: ID!, $after: String) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  items(first: 100, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                    nodes {
                      id
                      type
                      content { ... on DraftIssue { title body } }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """,
            new { projectId },
            "node.items",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var content = node.GetProperty("content");
            if (!string.Equals(node.GetProperty("type").GetString(), "DRAFT_ISSUE", StringComparison.Ordinal)
                || content.ValueKind != JsonValueKind.Object
                || !string.Equals(content.GetProperty("title").GetString(), title, StringComparison.Ordinal)
                || !string.Equals(
                    NormalizeDraftBody(content.GetProperty("body").GetString()),
                    NormalizeDraftBody(body),
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (node.GetProperty("id").GetString() is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<string> ReconcilePendingDraftAsync(
        string projectId,
        PendingDraftOperation pending,
        IEnumerable<string> importedIds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var matchingIds = await FindMatchingDraftItemIdsAsync(
                projectId,
                pending.Title,
                pending.Body,
                cancellationToken).ConfigureAwait(false);
            var itemId = SelectReconciledDraftItemId(
                pending.OperationId,
                matchingIds,
                pending.ExistingItemIds,
                importedIds);
            if (itemId is not null)
            {
                return itemId;
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Pending draft operation '{pending.OperationId}' is not visible in the target after reconciliation polling. Do not resend it until the target state has been reconciled manually.");
    }

    private async Task<IReadOnlyList<string>> FindContentItemIdsAsync(
        string projectId,
        string contentId,
        CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        await foreach (var node in _client.QueryPaginatedAsync(
            """
            query($projectId: ID!, $after: String) {
              node(id: $projectId) {
                ... on ProjectV2 {
                  items(first: 100, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                    nodes {
                      id
                      content {
                        ... on Issue { id }
                        ... on PullRequest { id }
                      }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """,
            new { projectId },
            "node.items",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var content = node.GetProperty("content");
            if (content.ValueKind == JsonValueKind.Object
                && content.TryGetProperty("id", out var targetContentId)
                && string.Equals(targetContentId.GetString(), contentId, StringComparison.Ordinal)
                && node.GetProperty("id").GetString() is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private async Task<string> ReconcilePendingContentAsync(
        string projectId,
        PendingContentOperation pending,
        IEnumerable<string> importedIds,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var matchingIds = await FindContentItemIdsAsync(projectId, pending.ContentId, cancellationToken).ConfigureAwait(false);
            var excluded = new HashSet<string>(pending.ExistingItemIds, StringComparer.Ordinal);
            excluded.UnionWith(importedIds);
            var candidates = matchingIds.Where(id => !excluded.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending content operation '{pending.OperationId}' matches multiple new target items. Reconcile the target manually before resuming.");
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Pending content operation '{pending.OperationId}' is not visible in the target after reconciliation polling. Do not resend it until the target state has been reconciled manually.");
    }

    internal static string? SelectReconciledDraftItemId(
        string operationId,
        IEnumerable<string> matchingIds,
        IEnumerable<string> existingIds,
        IEnumerable<string> importedIds)
    {
        var excluded = new HashSet<string>(existingIds, StringComparer.Ordinal);
        excluded.UnionWith(importedIds);
        var candidates = matchingIds.Where(id => !excluded.Contains(id)).Distinct(StringComparer.Ordinal).ToArray();
        return candidates.Length switch
        {
            0 => null,
            1 => candidates[0],
            _ => throw new InvalidOperationException(
                $"Pending draft operation '{operationId}' matches multiple new target items. Reconcile the target manually before resuming."),
        };
    }

    private static string NormalizeDraftBody(string? body) => body ?? string.Empty;

    private async Task<string[]> ResolveAssigneeIdsAsync(DraftIssueSnapshot draft, List<string> warnings, CancellationToken cancellationToken)
    {
        var ids = new List<string>();
        foreach (var login in draft.Assignees)
        {
            if (!UserMapping.TryGetValue(login, out var targetLogin))
            {
                Warn(warnings, $"draft '{draft.Title}': no user mapping for assignee '{login}'; dropping.");
                continue;
            }

            var userId = await GetUserIdAsync(targetLogin, cancellationToken).ConfigureAwait(false);
            if (userId is null)
            {
                Warn(warnings, $"draft '{draft.Title}': mapped assignee '{targetLogin}' was not found; dropping.");
                continue;
            }

            ids.Add(userId);
        }

        return [.. ids];
    }

    private async Task<bool> ApplyFieldValuesAsync(ItemSnapshot item, string itemId, ImportResult target, string label, List<string> warnings, CancellationToken cancellationToken)
    {
        var allApplied = true;
        foreach (var value in item.FieldValues)
        {
            if (string.Equals(value.FieldName, TitleFieldName, StringComparison.Ordinal))
            {
                continue; // Set through item content.
            }

            if (!target.FieldIds.TryGetValue(value.FieldName, out var fieldId))
            {
                Warn(warnings, $"{label}: field '{value.FieldName}' does not exist in the target project; skipping the value.");
                allApplied = false;
                continue;
            }

            object? valueInput;
            if (value.Text is not null)
            {
                valueInput = new { text = value.Text };
            }
            else if (value.Number is { } number)
            {
                valueInput = new { number };
            }
            else if (value.Date is not null)
            {
                valueInput = new { date = value.Date };
            }
            else if (value.SingleSelectOptionName is not null)
            {
                if (!target.OptionIds.TryGetValue(value.FieldName, out var options)
                    || !options.TryGetValue(value.SingleSelectOptionName, out var optionId))
                {
                    Warn(warnings, $"{label}: option '{value.SingleSelectOptionName}' of field '{value.FieldName}' has no target id; skipping the value.");
                    allApplied = false;
                    continue;
                }

                valueInput = new { singleSelectOptionId = optionId };
            }
            else if (value.IterationTitle is not null)
            {
                if (!target.IterationIds.TryGetValue(value.FieldName, out var iterations)
                    || !iterations.TryGetValue(value.IterationTitle, out var iterationId))
                {
                    Warn(warnings, $"{label}: iteration '{value.IterationTitle}' of field '{value.FieldName}' has no target id; skipping the value.");
                    allApplied = false;
                    continue;
                }

                valueInput = new { iterationId };
            }
            else
            {
                continue; // Empty value: nothing to set.
            }

            await _client.MutationAsync(
                "updateProjectV2ItemFieldValue",
                """
                mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $value: ProjectV2FieldValue!, $clientMutationId: String!) {
                  updateProjectV2ItemFieldValue(input: { projectId: $projectId, itemId: $itemId, fieldId: $fieldId, value: $value, clientMutationId: $clientMutationId }) {
                    projectV2Item { id }
                  }
                }
                """,
                new { projectId = target.ProjectId, itemId, fieldId, value = valueInput },
                MutationRetryPolicy.Idempotent,
                target: itemId,
                requiredResultPath: "projectV2Item.id",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        return allApplied;
    }

    /// <summary>Restores snapshot order by chaining updateProjectV2ItemPosition (afterId = previous item's new id). Archived items are excluded.</summary>
    private async Task ApplyPositionsAsync(
        List<ItemSnapshot> items,
        string projectId,
        ImportLog log,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        OnProgress?.Invoke("Applying item positions...");
        string? afterId = null;
        foreach (var item in items)
        {
            var stateKey = BuildItemStateKey(item);
            if (!log.ItemStates.TryGetValue(stateKey, out var state))
            {
                continue;
            }

            var itemId = state.TargetItemId;
            if (!item.IsArchived && !state.PositionApplied)
            {
                try
                {
                    await _client.MutationAsync(
                        "updateProjectV2ItemPosition",
                        """
                        mutation($projectId: ID!, $itemId: ID!, $afterId: ID, $clientMutationId: String!) {
                          updateProjectV2ItemPosition(input: { projectId: $projectId, itemId: $itemId, afterId: $afterId, clientMutationId: $clientMutationId }) {
                            clientMutationId
                          }
                        }
                        """,
                        new { projectId, itemId, afterId },
                        MutationRetryPolicy.Idempotent,
                        target: itemId,
                        requiredResultPath: "clientMutationId",
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    state.PositionApplied = true;
                    state.PositionError = null;
                    await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    state.PositionError = exception.Message;
                    await log.SaveAsync(logDirectory, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
            }
            else if (item.IsArchived && !state.PositionApplied)
            {
                state.PositionApplied = true;
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
            }

            if (!item.IsArchived)
            {
                afterId = itemId;
            }
        }
    }

    private async Task ArchiveItemsAsync(
        List<ItemSnapshot> items,
        string projectId,
        ImportLog log,
        string logDirectory,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        foreach (var item in items)
        {
            var stateKey = BuildItemStateKey(item);
            if (!log.ItemStates.TryGetValue(stateKey, out var state)
                || state.ArchiveApplied
                || !state.FieldValuesApplied
                || !state.PositionApplied)
            {
                continue;
            }

            if (!item.IsArchived)
            {
                state.ArchiveApplied = true;
                state.ArchiveError = null;
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                await _client.MutationAsync(
                    "archiveProjectV2Item",
                    """
                    mutation($projectId: ID!, $itemId: ID!, $clientMutationId: String!) {
                      archiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId, clientMutationId: $clientMutationId }) {
                        item { id }
                      }
                    }
                    """,
                    new { projectId, itemId = state.TargetItemId },
                    MutationRetryPolicy.Idempotent,
                    target: state.TargetItemId,
                    requiredResultPath: "item.id",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                state.ArchiveApplied = true;
                state.ArchiveError = null;
                await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
            }
            catch (GitHubGraphQLException exception)
            {
                if (await IsItemArchivedAsync(state.TargetItemId, cancellationToken).ConfigureAwait(false))
                {
                    state.ArchiveApplied = true;
                    state.ArchiveError = null;
                    await log.SaveAsync(logDirectory, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                state.ArchiveError = exception.Message;
                await log.SaveAsync(logDirectory, CancellationToken.None).ConfigureAwait(false);
                Warn(warnings, $"{DescribeItem(item)}: could not archive: {exception.Message}");
            }
        }
    }

    private async Task<bool> IsItemArchivedAsync(string itemId, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            """
            query($itemId: ID!) {
              node(id: $itemId) {
                ... on ProjectV2Item { isArchived }
              }
            }
            """,
            new { itemId },
            cancellationToken).ConfigureAwait(false);
        var node = data.GetProperty("node");
        return node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("isArchived", out var archived)
            && archived.GetBoolean();
    }

    private static string BuildItemStateKey(ItemSnapshot item)
    {
        var identity = item.Type switch
        {
            "DRAFT_ISSUE" when item.Draft is not null
                => $"{item.Type}:{item.Draft.Title}:{item.Draft.Body}:{item.Draft.Creator}:{item.Draft.CreatedAt}",
            _ when item.Repository is not null && item.Number is not null
                => string.Create(CultureInfo.InvariantCulture, $"{item.Type}:{item.Repository}:{item.Number.Value}"),
            _ => item.Type,
        };
        return string.Create(CultureInfo.InvariantCulture, $"{identity}:position:{item.Position}");
    }

    private string? GetTargetContentIdentity(ItemSnapshot item, IReadOnlyList<string>? draftAssigneeIds)
    {
        if (item.Type == "DRAFT_ISSUE")
        {
            return "DRAFT_ISSUE:assignees:" + string.Join(",", draftAssigneeIds ?? []);
        }

        if (item.Type is not ("ISSUE" or "PULL_REQUEST")
            || item.Repository is null
            || item.Number is null)
        {
            return null;
        }

        var repository = RepositoryMapping.TryGetValue(item.Repository, out var mappedRepository)
            ? mappedRepository
            : null;
        return repository is null
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{item.Type}:{repository.ToLowerInvariant()}:{item.Number.Value}");
    }

    private async Task<string?> ResolveIssueOrPullRequestIdAsync(string owner, string name, int number, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                """
                query($owner: String!, $name: String!, $number: Int!) {
                  repository(owner: $owner, name: $name) {
                    issueOrPullRequest(number: $number) {
                      ... on Issue { id }
                      ... on PullRequest { id }
                    }
                  }
                }
                """,
                new { owner, name, number },
                cancellationToken).ConfigureAwait(false);

            var repository = data.GetProperty("repository");
            if (repository.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var content = repository.GetProperty("issueOrPullRequest");
            return content.ValueKind == JsonValueKind.Object ? content.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<string?> GetUserIdAsync(string login, CancellationToken cancellationToken)
    {
        if (_userIdCache.TryGetValue(login, out var cached))
        {
            return cached;
        }

        string? id;
        try
        {
            var data = await _client.QueryAsync(
                "query($login: String!) { user(login: $login) { id } }",
                new { login },
                cancellationToken).ConfigureAwait(false);

            var user = data.GetProperty("user");
            id = user.ValueKind == JsonValueKind.Object ? user.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            id = null;
        }

        _userIdCache[login] = id;
        return id;
    }

    private void Warn(List<string> warnings, string message)
    {
        warnings.Add(message);
        OnProgress?.Invoke("warning: " + message);
    }

    private static string DescribeItem(ItemSnapshot item) => item.Type switch
    {
        "DRAFT_ISSUE" => $"draft '{item.Draft?.Title}'",
        _ when item.Repository is not null && item.Number is not null
            => string.Create(CultureInfo.InvariantCulture, $"{item.Type} {item.Repository}#{item.Number.Value}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"item at position {item.Position}"),
    };
}
