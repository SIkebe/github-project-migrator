using System.Collections.ObjectModel;
using System.Globalization;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// UI import of workflows (B7/B8). Built-in workflows are matched to the target's
/// sidebar entries by name; when the current settings differ from the snapshot the
/// workflow is edited ("Edit" → apply values → "Save workflow" / "Save and turn on
/// workflow") and workflows that are enabled on the target but disabled in the
/// snapshot are switched off with the toggle (M7 discovery: the toggle applies
/// immediately). The first Auto-add workflow reuses the built-in "Auto-add to
/// project" entry; additional ones are created via the sidebar kebab → "Duplicate
/// workflow" (plan limit: GHEC = 20; the excess is skipped with a warning).
/// Auto-add repositories are resolved through the same repository mapping the item
/// importer uses. Settings that cannot be applied are collected as warnings and the
/// import continues.
/// </summary>
public sealed class WorkflowUiImporter
{
    /// <summary>Default Auto-add workflow entry name on a new project.</summary>
    public const string AutoAddDefaultName = "Auto-add to project";

    /// <summary>Auto-add instance limit for GHEC (Free = 1, Pro/Team = 5, GHEC incl. DR = 20).</summary>
    public const int DefaultMaxAutoAddWorkflows = 20;

    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public WorkflowUiImporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per workflow.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while importing (settings that could not be applied).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Number of workflows successfully applied.</summary>
    public int ImportedCount { get; private set; }

    /// <summary>Source → target repository mapping ("owner/name" form, shared with the item importer).</summary>
    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> UserMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> OrganizationMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Plan limit for Auto-add workflow instances on the target.</summary>
    public int MaxAutoAddWorkflows { get; init; } = DefaultMaxAutoAddWorkflows;

    /// <summary>True when the snapshot workflow is an Auto-add instance (it targets a repository).</summary>
    public static bool IsAutoAdd(WorkflowSnapshot workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        return workflow.Ui?.Repository is not null;
    }

    /// <summary>
    /// Resolves an Auto-add repository short name through an "owner/name" mapping:
    /// the first entry whose key's name part matches is applied; otherwise the
    /// source name is used unchanged (same-name repository expected on the target).
    /// </summary>
    public static string ResolveRepositoryName(string sourceRepository, IReadOnlyDictionary<string, string> mapping)
        => ProjectFilterTransformer.ResolveRepositoryName(sourceRepository, mapping);

    /// <summary>
    /// Pure pre-flight check: warns about Auto-add instances beyond the plan limit
    /// (they are skipped) and about workflows that cannot be applied without UI settings.
    /// </summary>
    public static IReadOnlyList<string> CollectPreflightWarnings(ProjectSnapshot snapshot, int maxAutoAddWorkflows)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAutoAddWorkflows);

        var warnings = new List<string>();
        var autoAddCount = snapshot.Workflows.Count(IsAutoAdd);
        if (autoAddCount > maxAutoAddWorkflows)
        {
            warnings.Add(string.Create(CultureInfo.InvariantCulture,
                $"snapshot has {autoAddCount} Auto-add workflows but the target plan allows {maxAutoAddWorkflows}; the excess is skipped"));
        }

        foreach (var workflow in snapshot.Workflows)
        {
            if (workflow.Enabled && workflow.Ui is null
                && workflow.Name.StartsWith(AutoAddDefaultName, StringComparison.Ordinal))
            {
                warnings.Add($"workflow '{workflow.Name}': Auto-add cannot be enabled without UI settings (repository/filter); skipped");
            }
        }

        return warnings;
    }

    /// <summary>Applies all snapshot workflows to the target project.</summary>
    public async Task ImportAsync(ProjectSnapshot snapshot, string orgLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgLogin);

        var workflows = snapshot.Workflows.OrderBy(w => w.Number).ToList();
        if (workflows.Count == 0)
        {
            return;
        }

        _warnings.AddRange(CollectPreflightWarnings(snapshot, MaxAutoAddWorkflows));

        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{_session.BaseUrl}/orgs/{orgLogin}/projects/{projectNumber}/workflows");
        await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        await Sel.WorkflowsSidebar(page).WaitForAsync().ConfigureAwait(false);

        var autoAddApplied = 0;
        string? firstAutoAddName = null;
        foreach (var workflow in workflows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnProgress?.Invoke($"Applying workflow '{workflow.Name}'...");
            try
            {
                if (IsAutoAdd(workflow))
                {
                    if (autoAddApplied >= MaxAutoAddWorkflows)
                    {
                        _warnings.Add($"workflow '{workflow.Name}': skipped (Auto-add plan limit reached)");
                        continue;
                    }

                    await ApplyAutoAddAsync(page, workflow, autoAddApplied == 0, firstAutoAddName, cancellationToken).ConfigureAwait(false);
                    autoAddApplied++;
                    firstAutoAddName ??= workflow.Name;
                }
                else
                {
                    if (workflow.Enabled && workflow.Ui is null
                        && workflow.Name.StartsWith(AutoAddDefaultName, StringComparison.Ordinal))
                    {
                        continue; // Already reported by the pre-flight check.
                    }

                    await ApplyBuiltInAsync(page, workflow, cancellationToken).ConfigureAwait(false);
                }

                ImportedCount++;
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                _warnings.Add($"workflow '{workflow.Name}': import failed — {exception.Message}");
            }
        }
    }

    internal async Task UpdateExistingFilterAsync(
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        WorkflowSnapshot workflow,
        string filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(filter);

        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        var url = BrowserProjectUrl.Build(
            _session.BaseUrl,
            ownerLogin,
            ownerType,
            projectNumber,
            "workflows");
        await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        await Sel.WorkflowsSidebar(page).WaitForAsync().ConfigureAwait(false);
        await WorkflowUiExporter.OpenWorkflowAsync(
            page,
            workflow.Name,
            workflow.Number,
            cancellationToken).ConfigureAwait(false);

        if (await Sel.SaveWorkflowButton(page).CountAsync().ConfigureAwait(false) == 0)
        {
            await Sel.EditWorkflowButton(page).First.ClickAsync().ConfigureAwait(false);
        }

        await Sel.SaveWorkflowButton(page).First.WaitForAsync().ConfigureAwait(false);
        await Sel.WorkflowFiltersCombobox(page).First.FillAsync(filter).ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        await SaveWorkflowAsync(page, cancellationToken).ConfigureAwait(false);
    }

    // ----- built-in workflows (matched by name) -----

    private async Task ApplyBuiltInAsync(IPage page, WorkflowSnapshot workflow, CancellationToken cancellationToken)
    {
        var link = Sel.WorkflowLink(page, workflow.Name);
        if (await link.CountAsync().ConfigureAwait(false) == 0)
        {
            _warnings.Add($"workflow '{workflow.Name}': no matching workflow exists on the target; skipped");
            return;
        }

        await WorkflowUiExporter.OpenWorkflowAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
        var current = await WorkflowUiExporter.ReadCurrentWorkflowAsync(page, workflow.Name).ConfigureAwait(false);

        if (!workflow.Enabled)
        {
            await ApplyDisabledAsync(page, workflow, current, cancellationToken).ConfigureAwait(false);
            return;
        }

        var needsEdit = workflow.Ui is { } ui
            && (!ContentTypesEqual(ui.ContentTypes, current.ContentTypes)
                || !ValueEquals(ui.StatusValue, current.StatusValue)
                || !ValueEquals(ui.Filter is null ? null : TransformFilter(ui.Filter), current.Filter));

        if (needsEdit)
        {
            await EditAndSaveAsync(page, workflow, current, cancellationToken).ConfigureAwait(false);
        }
        else if (!current.Enabled)
        {
            await ToggleAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors a disabled snapshot workflow. A target workflow that was never saved is
    /// invisible to GraphQL (M7 discovery), so it is saved once ("Save and turn on
    /// workflow" is clickable even without setting changes) and then toggled off —
    /// producing a saved-but-disabled workflow like the source. Saved workflows only
    /// mirror the toggle state (v1 applies no setting edits to disabled workflows).
    /// </summary>
    private async Task ApplyDisabledAsync(IPage page, WorkflowSnapshot workflow, WorkflowUiState current, CancellationToken cancellationToken)
    {
        if (!current.IsSaved && workflow.Ui is not null)
        {
            await EditAndSaveAsync(page, workflow, current, cancellationToken).ConfigureAwait(false);

            // The enable toggle only exists on saved workflows, so its appearance is
            // the "save landed" signal (the SPA may keep the GUID URL after saving).
            var toggle = Sel.WorkflowToggle(page, workflow.Name).First;
            try
            {
                await toggle.WaitForAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                _warnings.Add($"workflow '{workflow.Name}': could not be saved as a disabled workflow on the target");
                return;
            }

            if (string.Equals(await toggle.GetAttributeAsync("aria-pressed").ConfigureAwait(false), "true", StringComparison.Ordinal))
            {
                await ToggleAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (current.Enabled)
        {
            await ToggleAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EditAndSaveAsync(IPage page, WorkflowSnapshot workflow, WorkflowUiState current, CancellationToken cancellationToken)
    {
        var ui = workflow.Ui!;
        await Sel.EditWorkflowButton(page).First.ClickAsync().ConfigureAwait(false);
        await Sel.SaveWorkflowButton(page).First.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        if (ui.ContentTypes is { Count: > 0 } contentTypes && !ContentTypesEqual(contentTypes, current.ContentTypes))
        {
            await SetContentTypesAsync(page, contentTypes, cancellationToken).ConfigureAwait(false);
        }

        if (ui.StatusValue is { } statusValue && !ValueEquals(statusValue, current.StatusValue))
        {
            await SetStatusValueAsync(page, workflow.Name, statusValue, cancellationToken).ConfigureAwait(false);
        }

        if (ui.Filter is { } filter && !ValueEquals(TransformFilter(filter), current.Filter))
        {
            var transformed = TransformFilter(filter);
            await Sel.WorkflowFiltersCombobox(page).First.FillAsync(transformed).ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }

        await SaveWorkflowAsync(page, cancellationToken).ConfigureAwait(false);
    }

    // ----- Auto-add workflows -----

    private async Task ApplyAutoAddAsync(IPage page, WorkflowSnapshot workflow, bool isFirst, string? firstAutoAddName, CancellationToken cancellationToken)
    {
        if (isFirst)
        {
            // Reuse the built-in (unsaved) "Auto-add to project" entry.
            await WorkflowUiExporter.OpenWorkflowAsync(page, AutoAddDefaultName, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await DuplicateAutoAddAsync(page, firstAutoAddName!, workflow.Name, cancellationToken).ConfigureAwait(false);
        }

        var ui = workflow.Ui!;
        // A freshly duplicated workflow opens directly in edit mode (no "Edit" button,
        // E2E discovery 2026-07-06); only click "Edit" when still in viewing mode.
        if (await Sel.SaveWorkflowButton(page).CountAsync().ConfigureAwait(false) == 0)
        {
            await Sel.EditWorkflowButton(page).First.ClickAsync().ConfigureAwait(false);
        }

        await Sel.SaveWorkflowButton(page).First.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        // Repository picker: search by the mapped short name and pick the exact option.
        var targetRepository = ResolveRepositoryName(ui.Repository!, RepositoryMapping);
        await Sel.WorkflowRepositoryButton(page).First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.WorkflowSelectDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await Sel.RepositorySearchCombobox(dialog).FillAsync(targetRepository).ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        var option = Sel.WorkflowDialogOption(dialog, targetRepository);
        try
        {
            // The picker refilters asynchronously after typing (debounce + fetch),
            // so the option must be awaited rather than counted immediately.
            await option.First.WaitForAsync(new() { Timeout = 10_000 }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"workflow '{workflow.Name}': repository '{targetRepository}' was not found on the target; skipped");
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await DiscardEditAsync(page, cancellationToken).ConfigureAwait(false);
            return;
        }

        await option.First.ClickAsync().ConfigureAwait(false);
        await CloseSelectDialogAsync(page, cancellationToken).ConfigureAwait(false);

        if (ui.Filter is { } filter)
        {
            await Sel.WorkflowFiltersCombobox(page).First.FillAsync(TransformFilter(filter)).ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }

        await SaveWorkflowAsync(page, cancellationToken).ConfigureAwait(false);

        if (isFirst && !string.Equals(workflow.Name, AutoAddDefaultName, StringComparison.Ordinal))
        {
            await RenameCurrentWorkflowAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
        }

        if (!workflow.Enabled)
        {
            await ToggleAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private string TransformFilter(string filter)
        => ProjectFilterTransformer.Transform(
            filter,
            UserMapping,
            RepositoryMapping,
            OrganizationMapping).Transformed;

    /// <summary>
    /// Creates an additional Auto-add instance: hover the saved Auto-add sidebar link,
    /// open its kebab menu, "Duplicate workflow", name it and open the new entry.
    /// </summary>
    private static async Task DuplicateAutoAddAsync(IPage page, string sourceName, string newName, CancellationToken cancellationToken)
    {
        var link = Sel.WorkflowLink(page, sourceName).First;
        await link.HoverAsync().ConfigureAwait(false);
        await Sel.WorkflowOptionsKebab(link).First.ClickAsync().ConfigureAwait(false);
        await Sel.DuplicateWorkflowMenuItem(page).First.ClickAsync().ConfigureAwait(false);

        var dialog = Sel.DuplicateWorkflowDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await Sel.WorkflowNameTextbox(dialog).FillAsync(newName).ConfigureAwait(false);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Duplicate", Exact = true }).ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        await WorkflowUiExporter.OpenWorkflowAsync(page, newName, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RenameCurrentWorkflowAsync(IPage page, string newName, CancellationToken cancellationToken)
    {
        await Sel.EditWorkflowNameButton(page).First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.EditWorkflowNameDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await Sel.WorkflowNameTextbox(dialog).FillAsync(newName).ConfigureAwait(false);
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync().ConfigureAwait(false);
        await Sel.WorkflowHeading(page, newName).First.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- shared edit-mode operations -----

    private static async Task SetContentTypesAsync(IPage page, IReadOnlyList<string> contentTypes, CancellationToken cancellationToken)
    {
        // The multi-select "Select items" overlay: options toggle on click and apply live.
        await Sel.WorkflowWhenButtons(page).First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.WorkflowSelectDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        var desired = contentTypes.Select(WorkflowUiExporter.ContentTypeOptionName).ToHashSet(StringComparer.Ordinal);
        var options = dialog.GetByRole(AriaRole.Option);
        var count = await options.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var option = options.Nth(i);
            var name = ViewUiExporter.NormalizeUiText(await option.InnerTextAsync().ConfigureAwait(false));
            if (name is null)
            {
                continue;
            }

            var isSelected = string.Equals(
                await option.GetAttributeAsync("aria-selected").ConfigureAwait(false), "true", StringComparison.Ordinal);
            if (desired.Contains(name) != isSelected)
            {
                await option.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await CloseSelectDialogAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private async Task SetStatusValueAsync(IPage page, string workflowName, string statusValue, CancellationToken cancellationToken)
    {
        // "Set value : ..." button, or the status-carrying "When ..." button (Auto-close issue).
        var button = Sel.WorkflowSetValueButton(page);
        if (await button.CountAsync().ConfigureAwait(false) == 0)
        {
            button = Sel.WorkflowWhenButtons(page);
        }

        await button.First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.WorkflowSelectDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        var option = Sel.WorkflowDialogOption(dialog, statusValue);
        if (await option.CountAsync().ConfigureAwait(false) == 0)
        {
            _warnings.Add($"workflow '{workflowName}': status value '{statusValue}' is not available on the target");
        }
        else
        {
            await option.First.ClickAsync().ConfigureAwait(false);
        }

        await CloseSelectDialogAsync(page, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveWorkflowAsync(IPage page, CancellationToken cancellationToken)
    {
        // M7 discovery: when the applied values end up identical to the current state
        // (e.g. the "When"/"Set value" defaults already match the snapshot) the save
        // button stays disabled. Nothing to persist — leave edit mode via Discard.
        var save = Sel.SaveWorkflowButton(page).First;
        if (await save.IsDisabledAsync().ConfigureAwait(false))
        {
            await DiscardEditAsync(page, cancellationToken).ConfigureAwait(false);
            return;
        }

        await save.ClickAsync().ConfigureAwait(false);
        // Saving returns to viewing mode ("Edit" reappears).
        await Sel.EditWorkflowButton(page).First.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DiscardEditAsync(IPage page, CancellationToken cancellationToken)
    {
        var discard = page.GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true });
        if (await discard.CountAsync().ConfigureAwait(false) > 0)
        {
            await discard.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ToggleAsync(IPage page, string name, CancellationToken cancellationToken)
    {
        // The toggle applies immediately (no confirmation, M7 discovery).
        var toggle = Sel.WorkflowToggle(page, name).First;
        await toggle.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CloseSelectDialogAsync(IPage page, CancellationToken cancellationToken)
    {
        // Single-select overlays close on selection; multi-select ones stay open.
        var dialog = Sel.WorkflowSelectDialog(page);
        if (await dialog.CountAsync().ConfigureAwait(false) > 0 && await dialog.First.IsVisibleAsync().ConfigureAwait(false))
        {
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        }

        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- pure helpers -----

    private static bool ValueEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static bool ContentTypesEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => (left ?? []).Order(StringComparer.Ordinal).SequenceEqual((right ?? []).Order(StringComparer.Ordinal), StringComparer.Ordinal);

    // 300ms between consecutive UI operations (BROWSER_AUTOMATION_PLAN §1.4).
    private static Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(300, cancellationToken);
}
