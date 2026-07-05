using System.Globalization;
using Gpm.Core.Snapshot;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// UI import of views (B3/B4). Views are created in snapshot order via the "New view"
/// tab (D0: picking a layout menu item creates and activates the view immediately),
/// renamed by double-clicking the tab, configured through the "View" menu
/// (GraphQL-derived settings: filter/group-by/sort-by/visible fields; UI-only settings:
/// Dates/Zoom level/Slice by), then saved with "Save view" + the confirmation
/// alertdialog. The default "View 1" is reused when the first snapshot view is a table,
/// otherwise it is deleted at the end. Settings that cannot be applied are collected
/// as warnings and the import continues.
/// </summary>
public sealed class ViewUiImporter
{
    private const string DefaultViewName = "View 1";

    private static readonly AriaRole[] OptionRoles =
    [
        AriaRole.Menuitemradio,
        AriaRole.Option,
        AriaRole.Menuitem,
    ];

    private static readonly string[] RoadmapDateSuffixes = [" start", " end"];

    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public ViewUiImporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per view.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while importing (settings that could not be applied).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>True when the default "View 1" can be reused for the first snapshot view (a table).</summary>
    public static bool ShouldReuseDefaultView(IReadOnlyList<ViewSnapshot> views)
    {
        ArgumentNullException.ThrowIfNull(views);
        return views.Count > 0 && string.Equals(views[0].Layout, "TABLE_LAYOUT", StringComparison.Ordinal);
    }

    /// <summary>Maps a GraphQL layout enum value to the "New view" layout menu item name.</summary>
    public static string LayoutMenuName(string layout) => layout switch
    {
        "TABLE_LAYOUT" => "Table",
        "BOARD_LAYOUT" => "Board",
        "ROADMAP_LAYOUT" => "Roadmap",
        _ => throw new ArgumentException($"Unknown view layout '{layout}'.", nameof(layout)),
    };

    /// <summary>
    /// Pure pre-flight check: warns about view settings that reference fields missing from
    /// the snapshot and about sort keys beyond the first (only one key is applied in v1).
    /// </summary>
    public static IReadOnlyList<string> CollectPreflightWarnings(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var warnings = new List<string>();
        var fieldNames = new HashSet<string>(snapshot.Fields.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var view in snapshot.Views)
        {
            foreach (var field in view.GroupByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "group-by", field);
            }

            foreach (var field in view.VerticalGroupByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "column-by", field);
            }

            foreach (var sort in view.SortByFields)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "sort-by", sort.Field);
            }

            if (view.SortByFields.Count > 1)
            {
                warnings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"view '{view.Name}': only the first of {view.SortByFields.Count} sort keys is applied"));
            }

            if (view.Ui?.SliceBy is { } sliceBy)
            {
                WarnIfMissing(warnings, fieldNames, view.Name, "slice-by", sliceBy);
            }

            if (view.Ui?.Roadmap is { } roadmap)
            {
                if (roadmap.StartField is { } startField && !RoadmapFieldExists(fieldNames, startField))
                {
                    warnings.Add($"view '{view.Name}': roadmap start date field '{startField}' does not exist in the snapshot");
                }

                if (roadmap.TargetField is { } targetField && !RoadmapFieldExists(fieldNames, targetField))
                {
                    warnings.Add($"view '{view.Name}': roadmap target date field '{targetField}' does not exist in the snapshot");
                }
            }
        }

        return warnings;
    }

    /// <summary>Creates and configures all snapshot views on the target project.</summary>
    public async Task ImportAsync(ProjectSnapshot snapshot, string orgLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgLogin);

        var views = snapshot.Views.OrderBy(v => v.Number).ToList();
        if (views.Count == 0)
        {
            return;
        }

        _warnings.AddRange(CollectPreflightWarnings(snapshot));

        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{_session.BaseUrl}/orgs/{orgLogin}/projects/{projectNumber}");
        await page.GotoAsync(url).ConfigureAwait(false);
        BrowserSession.EnsureSignedIn(page);
        await Sel.ViewTab(page, DefaultViewName).First.WaitForAsync().ConfigureAwait(false);

        var reuseDefault = ShouldReuseDefaultView(views);
        for (var i = 0; i < views.Count; i++)
        {
            var view = views[i];
            OnProgress?.Invoke($"Creating view '{view.Name}' ({view.Layout})...");
            try
            {
                if (i == 0 && reuseDefault)
                {
                    await Sel.ViewTab(page, DefaultViewName).First.ClickAsync().ConfigureAwait(false);
                    await PauseAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(view.Name, DefaultViewName, StringComparison.Ordinal))
                    {
                        await RenameSelectedTabAsync(page, view.Name, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await CreateViewAsync(page, view, cancellationToken).ConfigureAwait(false);
                }

                await ApplySettingsAsync(page, view, cancellationToken).ConfigureAwait(false);
                await SaveViewAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (PlaywrightException exception)
            {
                _warnings.Add($"view '{view.Name}': import failed — {exception.Message}");
            }
        }

        if (!reuseDefault)
        {
            try
            {
                await DeleteDefaultViewAsync(page, cancellationToken).ConfigureAwait(false);
            }
            catch (PlaywrightException exception)
            {
                _warnings.Add($"default view '{DefaultViewName}' could not be deleted — {exception.Message}");
            }
        }
    }

    // ----- view creation -----

    private static async Task CreateViewAsync(IPage page, ViewSnapshot view, CancellationToken cancellationToken)
    {
        await Sel.NewViewTab(page).ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await menu.GetByRole(AriaRole.Menuitem, new() { Name = LayoutMenuName(view.Layout), Exact = true })
            .First.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        await RenameSelectedTabAsync(page, view.Name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RenameSelectedTabAsync(IPage page, string name, CancellationToken cancellationToken)
    {
        await Sel.SelectedViewTab(page).First.DblClickAsync().ConfigureAwait(false);
        var textbox = Sel.ViewNameTextbox(page);
        await textbox.WaitForAsync().ConfigureAwait(false);
        await textbox.FillAsync(name).ConfigureAwait(false);
        await textbox.PressAsync("Enter").ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- settings -----

    private async Task ApplySettingsAsync(IPage page, ViewSnapshot view, CancellationToken cancellationToken)
    {
        // GraphQL-derived settings.
        if (view.GroupByFields.Count > 0)
        {
            await TrySetSingleAsync(page, "Group by", view.GroupByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }

        // A new board defaults to "Column by: Status"; only deviations need a click.
        if (view.VerticalGroupByFields.Count > 0
            && !(view.VerticalGroupByFields.Count == 1 && string.Equals(view.VerticalGroupByFields[0], "Status", StringComparison.Ordinal)))
        {
            await TrySetSingleAsync(page, "Column by", view.VerticalGroupByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }

        if (view.SortByFields.Count > 0)
        {
            await TrySetSortAsync(page, view.SortByFields[0], view.Name, cancellationToken).ConfigureAwait(false);
        }

        await TrySetVisibleFieldsAsync(page, view, cancellationToken).ConfigureAwait(false);

        // UI-only settings.
        if (view.Ui?.SliceBy is { } sliceBy)
        {
            await TrySetSingleAsync(page, "Slice by", sliceBy, view.Name, cancellationToken).ConfigureAwait(false);
        }

        if (view.Ui?.Roadmap is { } roadmap)
        {
            if (roadmap.StartField is not null || roadmap.TargetField is not null)
            {
                await TrySetDateFieldsAsync(page, roadmap, view.Name, cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.Zoom is { } zoom)
            {
                await TrySetSingleAsync(page, "Zoom level", zoom, view.Name, cancellationToken).ConfigureAwait(false);
            }

            if (roadmap.Markers is { Count: > 0 } markers)
            {
                await TrySetCheckboxesAsync(page, "Markers", markers, view.Name, cancellationToken).ConfigureAwait(false);
            }
        }

        // Filter last: it is typed into the filter bar, not the View menu.
        if (!string.IsNullOrWhiteSpace(view.Filter))
        {
            await TrySetFilterAsync(page, view.Filter, view.Name, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetSingleAsync(IPage page, string label, string value, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, label);
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': '{label}' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            var option = await FindOptionAsync(page, value).ConfigureAwait(false);
            if (option is null)
            {
                _warnings.Add($"view '{viewName}': {label} value '{value}' is not available on the target");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await option.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{viewName}': {label} could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetSortAsync(IPage page, SortByFieldSnapshot sort, string viewName, CancellationToken cancellationToken)
    {
        await TrySetSingleAsync(page, "Sort by", sort.Field, viewName, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(sort.Direction, "DESC", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Sort by");
            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            var descending = await FindOptionAsync(page, "Descending").ConfigureAwait(false);
            if (descending is null)
            {
                _warnings.Add($"view '{viewName}': descending sort direction for '{sort.Field}' could not be applied");
            }
            else
            {
                await descending.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{viewName}': sort direction could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetVisibleFieldsAsync(IPage page, ViewSnapshot view, CancellationToken cancellationToken)
    {
        if (view.VisibleFields.Count == 0)
        {
            return;
        }

        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Fields");
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                // Boards/roadmaps expose visible fields differently; not an error.
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            await ToggleCheckboxesAsync(page, new HashSet<string>(view.VisibleFields, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{view.Name}': visible fields could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TrySetCheckboxesAsync(IPage page, string label, IReadOnlyList<string> values, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, label);
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': '{label}' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
            await ToggleCheckboxesAsync(page, new HashSet<string>(values, StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{viewName}': {label} could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Toggles every enabled menu checkbox so the checked set matches <paramref name="desired"/>.</summary>
    private static async Task ToggleCheckboxesAsync(IPage page, HashSet<string> desired, CancellationToken cancellationToken)
    {
        var checkboxes = page.GetByRole(AriaRole.Menuitemcheckbox);
        var count = await checkboxes.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var checkbox = checkboxes.Nth(i);
            if (string.Equals(await checkbox.GetAttributeAsync("aria-disabled").ConfigureAwait(false), "true", StringComparison.Ordinal))
            {
                continue;
            }

            var name = ViewUiExporter.NormalizeUiText(await checkbox.InnerTextAsync().ConfigureAwait(false));
            if (name is null)
            {
                continue;
            }

            var isChecked = string.Equals(await checkbox.GetAttributeAsync("aria-checked").ConfigureAwait(false), "true", StringComparison.Ordinal);
            if (desired.Contains(name) != isChecked)
            {
                await checkbox.ClickAsync().ConfigureAwait(false);
                await PauseAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TrySetDateFieldsAsync(IPage page, RoadmapSettingsSnapshot roadmap, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var menu = await OpenViewMenuAsync(page, cancellationToken).ConfigureAwait(false);
            var item = Sel.ConfigurationMenuItem(menu, "Dates");
            if (await item.CountAsync().ConfigureAwait(false) == 0)
            {
                _warnings.Add($"view '{viewName}': 'Dates' is not available in this layout");
                await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
                return;
            }

            await item.First.ClickAsync().ConfigureAwait(false);
            var dialog = Sel.DateFieldsDialog(page);
            await dialog.WaitForAsync().ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);

            await SelectDateRadioAsync(dialog, "Start date", roadmap.StartField, viewName, cancellationToken).ConfigureAwait(false);
            await SelectDateRadioAsync(dialog, "Target date", roadmap.TargetField, viewName, cancellationToken).ConfigureAwait(false);

            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{viewName}': roadmap date fields could not be applied — {exception.Message}");
            await CloseMenusAsync(page, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SelectDateRadioAsync(ILocator dialog, string groupName, string? value, string viewName, CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return;
        }

        var radio = Sel.DateFieldGroup(dialog, groupName).GetByRole(AriaRole.Menuitemradio, new() { Name = value, Exact = true });
        if (await radio.CountAsync().ConfigureAwait(false) == 0)
        {
            _warnings.Add($"view '{viewName}': {groupName} field '{value}' is not available on the target");
            return;
        }

        await radio.First.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TrySetFilterAsync(IPage page, string filter, string viewName, CancellationToken cancellationToken)
    {
        try
        {
            var filterBox = Sel.FilterCombobox(page);
            await filterBox.ClickAsync().ConfigureAwait(false);
            await filterBox.FillAsync(filter).ConfigureAwait(false);
            await filterBox.PressAsync("Enter").ConfigureAwait(false);
            await PauseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PlaywrightException exception)
        {
            _warnings.Add($"view '{viewName}': filter could not be applied — {exception.Message}");
        }
    }

    // ----- save / delete -----

    private static async Task SaveViewAsync(IPage page, CancellationToken cancellationToken)
    {
        var save = Sel.SaveViewButton(page);
        if (await save.CountAsync().ConfigureAwait(false) == 0 || !await save.First.IsVisibleAsync().ConfigureAwait(false))
        {
            return; // No unsaved changes.
        }

        await save.First.ClickAsync().ConfigureAwait(false);

        // D0: saving raises a confirmation alertdialog "Save display options for <view>?".
        var confirm = Sel.SaveConfirmDialog(page);
        try
        {
            await confirm.WaitForAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
            await confirm.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).First.ClickAsync().ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // No confirmation dialog appeared; the save applied directly.
        }

        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteDefaultViewAsync(IPage page, CancellationToken cancellationToken)
    {
        var tabs = Sel.ViewTab(page, DefaultViewName);
        if (await tabs.CountAsync().ConfigureAwait(false) == 0)
        {
            return;
        }

        await tabs.First.ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);

        // Clicking the already-active tab opens its view options menu.
        await tabs.First.ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await Sel.DeleteViewMenuItem(menu).First.ClickAsync().ConfigureAwait(false);

        // D0: deletion asks for confirmation.
        await Sel.ConfirmDeleteButton(page).ClickAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
    }

    // ----- helpers -----

    private static async Task<ILocator> OpenViewMenuAsync(IPage page, CancellationToken cancellationToken)
    {
        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await PauseAsync(cancellationToken).ConfigureAwait(false);
        return menu;
    }

    private static async Task<ILocator?> FindOptionAsync(IPage page, string value)
    {
        foreach (var role in OptionRoles)
        {
            var option = page.GetByRole(role, new() { Name = value, Exact = true });
            if (await option.CountAsync().ConfigureAwait(false) > 0)
            {
                return option.First;
            }
        }

        return null;
    }

    private static async Task CloseMenusAsync(IPage page, CancellationToken cancellationToken)
    {
        try
        {
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        }
        catch (PlaywrightException)
        {
            // Best effort; the next navigation resets the UI state anyway.
        }
    }

    // 300ms between consecutive UI operations (BROWSER_AUTOMATION_PLAN §1.4).
    private static Task PauseAsync(CancellationToken cancellationToken) => Task.Delay(300, cancellationToken);

    private static void WarnIfMissing(List<string> warnings, HashSet<string> fieldNames, string viewName, string setting, string field)
    {
        if (!fieldNames.Contains(field))
        {
            warnings.Add($"view '{viewName}': {setting} field '{field}' does not exist in the snapshot");
        }
    }

    /// <summary>
    /// Roadmap date values may be a field name or "&lt;iteration field&gt; start" / "… end" (D0).
    /// </summary>
    private static bool RoadmapFieldExists(HashSet<string> fieldNames, string value)
    {
        if (fieldNames.Contains(value))
        {
            return true;
        }

        foreach (var suffix in RoadmapDateSuffixes)
        {
            if (value.EndsWith(suffix, StringComparison.Ordinal) && fieldNames.Contains(value[..^suffix.Length]))
            {
                return true;
            }
        }

        return false;
    }
}
