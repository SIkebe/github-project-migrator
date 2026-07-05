using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// Selector registry for the GitHub Projects web UI — the single source of truth
/// (no selectors inline in logic). All entries were confirmed against the real UI
/// during D0 (docs/ui-maps/projects-ui-discovery.md, 2026-07-05) unless noted.
/// </summary>
internal static class Sel
{
    // Logged-in header avatar button (github.com, D0 login detection).
    private static readonly Regex AvatarButtonName = new("Open user navigation menu");

    // Active-tab menu item "Delete view" (D0: View options menu).
    private static readonly Regex DeleteViewName = new("^Delete view");

    // Confirmation button of the delete dialog.
    private static readonly Regex DeleteButtonName = new("^Delete");

    // Filter-bar "View" button. D0: once a setting is changed the accessible name
    // becomes "Unsaved changes View", so an exact "View" match only works before edits.
    private static readonly Regex ViewMenuButtonName = new("^(Unsaved changes )?View$");

    /// <summary>Filter-bar "View" button that opens the view configuration menu.</summary>
    public static ILocator ViewMenuButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = ViewMenuButtonName }).First;

    /// <summary>The most recently opened menu.</summary>
    public static ILocator OpenMenu(IPage page) => page.GetByRole(AriaRole.Menu).Last;

    /// <summary>
    /// Configuration menu item. D0: label and current value are combined in the accessible
    /// name ("Group by: &lt;value&gt;"), so the item is located by label prefix.
    /// </summary>
    public static ILocator ConfigurationMenuItem(ILocator menu, string label)
        => menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = new Regex($"^{Regex.Escape(label)}:") });

    /// <summary>"New view" tab; clicking it opens the layout menu (Table/Board/Roadmap).</summary>
    public static ILocator NewViewTab(IPage page)
        => page.GetByRole(AriaRole.Tab, new() { Name = "New view" });

    /// <summary>View tab by name (prefix match — an unsaved-changes dot can alter the suffix).</summary>
    public static ILocator ViewTab(IPage page, string name)
        => page.GetByRole(AriaRole.Tab, new() { NameRegex = new Regex($"^{Regex.Escape(name)}") });

    /// <summary>The currently selected view tab.</summary>
    public static ILocator SelectedViewTab(IPage page)
        => page.GetByRole(AriaRole.Tab, new() { Selected = true });

    /// <summary>Rename textbox shown after double-clicking a view tab.</summary>
    public static ILocator ViewNameTextbox(IPage page)
        => page.GetByRole(AriaRole.Textbox, new() { Name = "Change view name" });

    /// <summary>Filter-bar input.</summary>
    public static ILocator FilterCombobox(IPage page)
        => page.GetByRole(AriaRole.Combobox, new() { Name = "Filter" }).First;

    /// <summary>"Save view" button (settings changes require an explicit save, D0).</summary>
    public static ILocator SaveViewButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Save view", Exact = true });

    /// <summary>Confirmation alertdialog ("Save display options for &lt;view&gt;?", D0).</summary>
    public static ILocator SaveConfirmDialog(IPage page) => page.GetByRole(AriaRole.Alertdialog);

    /// <summary>"Select date fields" dialog opened from the "Dates" configuration item (Roadmap).</summary>
    public static ILocator DateFieldsDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Select date fields" });

    /// <summary>"Start date" / "Target date" group inside the date-fields dialog.</summary>
    public static ILocator DateFieldGroup(ILocator dialog, string groupName)
        => dialog.GetByRole(AriaRole.Group, new() { Name = groupName });

    /// <summary>Logged-in avatar button in the page header.</summary>
    public static ILocator AvatarButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = AvatarButtonName });

    /// <summary>"Delete view" item in the active tab's view options menu.</summary>
    public static ILocator DeleteViewMenuItem(ILocator menu)
        => menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = DeleteViewName });

    /// <summary>Confirm button of the delete-view confirmation dialog.</summary>
    public static ILocator ConfirmDeleteButton(IPage page)
        => page.Locator("[role='alertdialog'], [role='dialog']")
            .GetByRole(AriaRole.Button, new() { NameRegex = DeleteButtonName }).First;
}
