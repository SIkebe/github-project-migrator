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

    // Enterprise SSO interstitial heading ("Single sign-on to <enterprise>", M7 discovery).
    private static readonly Regex SsoHeadingName = new("^Single sign-on to ");

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

    /// <summary>Layout switch button (Table/Board/Roadmap) inside the View menu overlay.</summary>
    public static ILocator ViewLayoutButton(IPage page, string layoutName)
        => page.GetByRole(AriaRole.List, new() { Name = "Layout" })
            .GetByRole(AriaRole.Button, new() { Name = layoutName, Exact = true });

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

    /// <summary>Enterprise SSO interstitial heading ("Single sign-on to &lt;enterprise&gt;").</summary>
    public static ILocator SsoHeading(IPage page)
        => page.GetByRole(AriaRole.Heading, new() { NameRegex = SsoHeadingName });

    /// <summary>"Continue" button of the SSO interstitial (re-authenticates via the stored IdP session).</summary>
    public static ILocator SsoContinueButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Continue", Exact = true });

    /// <summary>"Delete view" item in the active tab's view options menu.</summary>
    public static ILocator DeleteViewMenuItem(ILocator menu)
        => menu.GetByRole(AriaRole.Menuitem, new() { NameRegex = DeleteViewName });

    /// <summary>Confirm button of the delete-view confirmation dialog.</summary>
    public static ILocator ConfirmDeleteButton(IPage page)
        => page.Locator("[role='alertdialog'], [role='dialog']")
            .GetByRole(AriaRole.Button, new() { NameRegex = DeleteButtonName }).First;

    // === Workflows (M7 discovery, 2026-07-05) ===

    // Saved Auto-add entries carry a kebab button whose label is appended to the link name.
    private const string WorkflowOptionsSuffix = "( Open workflow options)?$";

    // Edit-mode save button: "Save workflow" (saved workflow) / "Save and turn on workflow" (unsaved).
    private static readonly Regex SaveWorkflowName = new("^Save (workflow|and turn on workflow)$");

    // View/edit mode "When" value button, e.g. "When an item is closed : issue, pull request".
    // Prefix-only: the " : <value>" suffix disappears when the binding is cleared (e.g.
    // after the importer overwrote the Status options), and accessible names can contain
    // line breaks that defeat a "$"-anchored pattern.
    private static readonly Regex WhenButtonName = new("^When ");

    // Auto-add repository picker button: "When the filter matches a new or updated item : <repo>".
    private static readonly Regex RepositoryButtonName = new("^When the filter matches a new or updated item");

    // "Set value : <status>" button (text "Status: <status>"). With a cleared binding the
    // name becomes "Set valueundefined" (GitHub UI quirk) — match by prefix only.
    private static readonly Regex SetValueButtonName = new("^Set value");

    // Option-picker overlays: dialog "Select an item" / "Select items" / "Select a repository".
    private static readonly Regex SelectDialogName = new("^Select ");

    /// <summary>Sidebar "Default workflows" list on the workflows page.</summary>
    public static ILocator WorkflowsSidebar(IPage page)
        => page.GetByRole(AriaRole.List, new() { Name = "Default workflows" });

    /// <summary>Sidebar workflow link by name (saved Auto-add links append "Open workflow options").</summary>
    public static ILocator WorkflowLink(IPage page, string name)
        => WorkflowsSidebar(page).GetByRole(AriaRole.Link, new() { NameRegex = new Regex($"^{Regex.Escape(name)}{WorkflowOptionsSuffix}") });

    /// <summary>The h2 heading of the currently displayed workflow.</summary>
    public static ILocator WorkflowHeading(IPage page, string name)
        => page.GetByRole(AriaRole.Heading, new() { Name = name, Exact = true, Level = 2 });

    /// <summary>Enable/disable toggle button; its accessible name equals the workflow name, aria-pressed = enabled.</summary>
    public static ILocator WorkflowToggle(IPage page, string name)
        => page.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>"Edit" button (view mode). Exact match — "Edit workflow name" also starts with "Edit".</summary>
    public static ILocator EditWorkflowButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true });

    /// <summary>Edit-mode save button ("Save workflow" or "Save and turn on workflow").</summary>
    public static ILocator SaveWorkflowButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = SaveWorkflowName });

    /// <summary>"When ... : &lt;value&gt;" buttons (content types, Auto-close status, Auto-add repository).</summary>
    public static ILocator WorkflowWhenButtons(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = WhenButtonName });

    /// <summary>Auto-add repository picker button (disabled in view mode; text = repo short name).</summary>
    public static ILocator WorkflowRepositoryButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = RepositoryButtonName });

    /// <summary>Auto-add "When the filter matches..." section heading (h3) — marks Auto-add pages.</summary>
    public static ILocator WhenFilterMatchesHeading(IPage page)
        => page.GetByRole(AriaRole.Heading, new() { NameRegex = RepositoryButtonName });

    /// <summary>"Set value : &lt;status&gt;" button (disabled in view mode; text "Status: &lt;status&gt;").</summary>
    public static ILocator WorkflowSetValueButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = SetValueButtonName });

    /// <summary>Auto-add/Auto-archive filter: read-only textbox in view mode (value readable while disabled).</summary>
    public static ILocator WorkflowFiltersTextbox(IPage page)
        => page.GetByRole(AriaRole.Textbox, new() { Name = "Filters" });

    /// <summary>Auto-add/Auto-archive filter input in edit mode (combobox inside form "Filter").</summary>
    public static ILocator WorkflowFiltersCombobox(IPage page)
        => page.GetByRole(AriaRole.Combobox, new() { Name = "Filters" });

    /// <summary>Option-picker overlay ("Select an item" / "Select items" / "Select a repository").</summary>
    public static ILocator WorkflowSelectDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { NameRegex = SelectDialogName });

    /// <summary>An option inside a workflow option-picker dialog.</summary>
    public static ILocator WorkflowDialogOption(ILocator dialog, string name)
        => dialog.GetByRole(AriaRole.Option, new() { Name = name, Exact = true });

    /// <summary>Search input of the "Select a repository" picker dialog.</summary>
    public static ILocator RepositorySearchCombobox(ILocator dialog)
        => dialog.GetByRole(AriaRole.Combobox, new() { Name = "Search repositories" });

    /// <summary>Kebab button inside a saved Auto-add sidebar link (appears on hover).</summary>
    public static ILocator WorkflowOptionsKebab(ILocator workflowLink)
        => workflowLink.GetByRole(AriaRole.Button, new() { Name = "Open workflow options" });

    /// <summary>"Duplicate workflow" item of the kebab menu.</summary>
    public static ILocator DuplicateWorkflowMenuItem(IPage page)
        => page.GetByRole(AriaRole.Menuitem, new() { Name = "Duplicate workflow" });

    /// <summary>"Duplicate workflow" name-prompt dialog (textbox "Workflow name" + button "Duplicate").</summary>
    public static ILocator DuplicateWorkflowDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Duplicate workflow" });

    /// <summary>"Edit workflow name" button next to the workflow heading.</summary>
    public static ILocator EditWorkflowNameButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { Name = "Edit workflow name" });

    /// <summary>"Edit workflow name" dialog (textbox "Workflow name" + Save/Cancel).</summary>
    public static ILocator EditWorkflowNameDialog(IPage page)
        => page.GetByRole(AriaRole.Dialog, new() { Name = "Edit workflow name" });

    /// <summary>The "Workflow name" textbox inside a workflow name dialog.</summary>
    public static ILocator WorkflowNameTextbox(ILocator dialog)
        => dialog.GetByRole(AriaRole.Textbox, new() { Name = "Workflow name" });
}
