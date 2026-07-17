using System.Globalization;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// UI export of view settings that GraphQL does not expose (B2). For each view the
/// "View" configuration menu is opened and the current values of Group by / Markers /
/// Sort by / Dates / Zoom level / Slice by are read (D0: the menu item accessible name
/// is "Group by: &lt;value&gt;"). Results are stored in <see cref="ViewSnapshot.Ui"/>;
/// views whose UI settings cannot be read keep <c>Ui = null</c> and add a warning.
/// </summary>
public sealed class ViewUiExporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public ViewUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per view.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while scraping (views whose UI settings could not be read).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Returns a copy of <paramref name="snapshot"/> with <see cref="ViewSnapshot.Ui"/> populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(ProjectSnapshot snapshot, string orgLogin, int projectNumber, CancellationToken cancellationToken = default)
        => await EnrichAsync(snapshot, orgLogin, ProjectOwnerType.Organization, projectNumber, cancellationToken).ConfigureAwait(false);

    /// <summary>Returns a copy of <paramref name="snapshot"/> with <see cref="ViewSnapshot.Ui"/> populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        IPage page;
        try
        {
            page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
        {
            _warnings.Add($"view settings page could not be opened — {exception.Message}");
            return snapshot with
            {
                Views = snapshot.Views.Select(view => view with { Ui = null }).ToList(),
            };
        }

        var views = new List<ViewSnapshot>(snapshot.Views.Count);
        foreach (var view in snapshot.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"Reading UI settings for view '{view.Name}' (#{view.Number})..."));
            ViewUiSnapshot? ui = null;
            try
            {
                ui = await ReadViewUiAsync(page, ownerLogin, ownerType, projectNumber, view, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                _warnings.Add($"view '{view.Name}': UI settings could not be read — {exception.Message}");
            }

            views.Add(view with { Ui = ui });
        }

        return snapshot with { Views = views };
    }

    private async Task<ViewUiSnapshot> ReadViewUiAsync(
        IPage page,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        ViewSnapshot view,
        CancellationToken cancellationToken)
    {
        var ownerPath = ownerType == ProjectOwnerType.User ? "users" : "orgs";
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{_session.BaseUrl}/{ownerPath}/{ownerLogin}/projects/{projectNumber}/views/{view.Number}");
        await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);

        await Sel.ViewMenuButton(page).ClickAsync().ConfigureAwait(false);
        var menu = Sel.OpenMenu(page);
        await menu.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        var groupBy = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Group by").ConfigureAwait(false));
        var sortBy = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Sort by").ConfigureAwait(false));
        var sliceBy = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Slice by").ConfigureAwait(false));
        // Boards use "Swimlanes" (not "Group by") and expose a "Field sum" checkbox overlay;
        // both menu items combine label and value, so plain reads suffice (E2E discovery, 2026-07-06).
        var swimlanes = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Swimlanes").ConfigureAwait(false));
        var fieldSum = ParseListValue(await ReadMenuItemTextAsync(menu, "Field sum").ConfigureAwait(false));

        RoadmapSettingsSnapshot? roadmap = null;
        if (string.Equals(view.Layout, "ROADMAP_LAYOUT", StringComparison.Ordinal))
        {
            var zoom = ParseMenuValue(await ReadMenuItemTextAsync(menu, "Zoom level").ConfigureAwait(false));
            var markers = ParseListValue(await ReadMenuItemTextAsync(menu, "Markers").ConfigureAwait(false));
            var (startField, targetField) = await ReadDateFieldsAsync(page, menu, cancellationToken).ConfigureAwait(false);
            roadmap = new RoadmapSettingsSnapshot
            {
                StartField = startField,
                TargetField = targetField,
                Zoom = zoom,
                Markers = markers,
            };
        }

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);

        return new ViewUiSnapshot
        {
            GroupBy = groupBy,
            SortBy = sortBy,
            SliceBy = sliceBy,
            Swimlanes = swimlanes,
            FieldSum = fieldSum,
            Roadmap = roadmap,
            ScrapedAt = DateTimeOffset.UtcNow,
        };
    }

    private static async Task<string?> ReadMenuItemTextAsync(ILocator menu, string label)
    {
        var item = Sel.ConfigurationMenuItem(menu, label);
        if (await item.CountAsync().ConfigureAwait(false) == 0)
        {
            return null;
        }

        return await item.First.InnerTextAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Opens the "Dates" configuration item and reads the checked radios of the
    /// "Select date fields" dialog (D0: iteration fields expand to
    /// "&lt;name&gt; start" / "&lt;name&gt; end" radios).
    /// </summary>
    private static async Task<(string? StartField, string? TargetField)> ReadDateFieldsAsync(IPage page, ILocator menu, CancellationToken cancellationToken)
    {
        var item = Sel.ConfigurationMenuItem(menu, "Dates");
        if (await item.CountAsync().ConfigureAwait(false) == 0)
        {
            return (null, null);
        }

        await item.First.ClickAsync().ConfigureAwait(false);
        var dialog = Sel.DateFieldsDialog(page);
        await dialog.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);

        var startField = await ReadCheckedRadioAsync(dialog, "Start date").ConfigureAwait(false);
        var targetField = await ReadCheckedRadioAsync(dialog, "Target date").ConfigureAwait(false);

        await page.Keyboard.PressAsync("Escape").ConfigureAwait(false);
        return (startField, targetField);
    }

    private static async Task<string?> ReadCheckedRadioAsync(ILocator dialog, string groupName)
    {
        var radios = Sel.DateFieldGroup(dialog, groupName).GetByRole(AriaRole.Menuitemradio);
        var count = await radios.CountAsync().ConfigureAwait(false);
        for (var i = 0; i < count; i++)
        {
            var radio = radios.Nth(i);
            var isChecked = await radio.GetAttributeAsync("aria-checked").ConfigureAwait(false);
            if (string.Equals(isChecked, "true", StringComparison.Ordinal))
            {
                return NormalizeUiText(await radio.InnerTextAsync().ConfigureAwait(false));
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts the value from a configuration menu item text of the form
    /// "Group by: &lt;value&gt;". Whitespace (including newlines) is collapsed and
    /// "none" (case-insensitive) or an empty value is normalized to null.
    /// </summary>
    public static string? ParseMenuValue(string? menuItemText)
    {
        if (string.IsNullOrWhiteSpace(menuItemText))
        {
            return null;
        }

        var separatorIndex = menuItemText.IndexOf(':');
        var value = separatorIndex < 0 ? menuItemText : menuItemText[(separatorIndex + 1)..];
        var normalized = NormalizeUiText(value);
        if (normalized is null || normalized.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }

    /// <summary>
    /// Parses a list menu value into its entries, or null when none. The UI renders
    /// lists in prose form — "A and B" / "A, B, and C" (E2E discovery, 2026-07-06) —
    /// so both the comma and the " and " conjunction are treated as separators.
    /// </summary>
    public static IReadOnlyList<string>? ParseListValue(string? menuItemText)
    {
        var value = ParseMenuValue(menuItemText);
        if (value is null)
        {
            return null;
        }

        var parts = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(part => part.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(part => part.StartsWith("and ", StringComparison.Ordinal) ? part["and ".Length..] : part)
            .Where(part => part.Length > 0)
            .ToList();
        return parts.Count == 0 ? null : parts;
    }

    /// <summary>Collapses all whitespace runs (including newlines) to single spaces; null when empty.</summary>
    public static string? NormalizeUiText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
