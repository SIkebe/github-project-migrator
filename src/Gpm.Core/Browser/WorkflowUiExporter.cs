using System.Globalization;
using Gpm.Core.Snapshot;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// UI export of workflow settings that GraphQL does not expose (B6). Each snapshot
/// workflow is opened through the sidebar "Default workflows" link (link names match
/// the GraphQL <c>name</c>; unsaved workflow URLs are volatile GUIDs so links are the
/// only safe navigation). The viewing mode exposes every setting (M7 discovery):
/// the enable toggle (<c>aria-pressed</c>), "When ... : &lt;value&gt;" buttons
/// (content types / Auto-close status / Auto-add repository) and the disabled
/// "Filters" textbox. Results are stored in <see cref="WorkflowSnapshot.Ui"/>;
/// unreadable workflows keep <c>Ui = null</c> and add a warning.
/// </summary>
public sealed class WorkflowUiExporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public WorkflowUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>Invoked with a human-readable progress message per workflow.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Warnings collected while scraping (workflows whose settings could not be read).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Returns a copy of <paramref name="snapshot"/> with <see cref="WorkflowSnapshot.Ui"/> populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(ProjectSnapshot snapshot, string orgLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgLogin);

        if (snapshot.Workflows.Count == 0)
        {
            return snapshot;
        }

        var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
        var url = string.Create(CultureInfo.InvariantCulture,
            $"{_session.BaseUrl}/orgs/{orgLogin}/projects/{projectNumber}/workflows");
        await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
        await Sel.WorkflowsSidebar(page).WaitForAsync().ConfigureAwait(false);

        var workflows = new List<WorkflowSnapshot>(snapshot.Workflows.Count);
        foreach (var workflow in snapshot.Workflows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OnProgress?.Invoke($"Reading UI settings for workflow '{workflow.Name}'...");
            WorkflowUiSnapshot? ui = null;
            try
            {
                await OpenWorkflowAsync(page, workflow.Name, cancellationToken).ConfigureAwait(false);
                var state = await ReadCurrentWorkflowAsync(page, workflow.Name).ConfigureAwait(false);
                ui = new WorkflowUiSnapshot
                {
                    ContentTypes = state.ContentTypes,
                    StatusValue = state.StatusValue,
                    Filter = state.Filter,
                    Repository = state.Repository,
                    ScrapedAt = DateTimeOffset.UtcNow,
                };
            }
            catch (PlaywrightException exception)
            {
                _warnings.Add($"workflow '{workflow.Name}': UI settings could not be read — {exception.Message}");
            }

            workflows.Add(workflow with { Ui = ui });
        }

        return snapshot with { Workflows = workflows };
    }

    /// <summary>Navigates to a workflow via its sidebar link and waits for its heading.</summary>
    internal static async Task OpenWorkflowAsync(IPage page, string name, CancellationToken cancellationToken)
    {
        await Sel.WorkflowLink(page, name).First.ClickAsync().ConfigureAwait(false);
        await Sel.WorkflowHeading(page, name).First.WaitForAsync().ConfigureAwait(false);
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the settings of the currently displayed workflow (viewing mode).
    /// Shared by the exporter and the importer's diff step.
    /// </summary>
    internal static async Task<WorkflowUiState> ReadCurrentWorkflowAsync(IPage page, string name)
    {
        var toggle = Sel.WorkflowToggle(page, name).First;
        var enabled = string.Equals(
            await toggle.GetAttributeAsync("aria-pressed").ConfigureAwait(false), "true", StringComparison.Ordinal);

        string? repository = null;
        var repositoryButton = Sel.WorkflowRepositoryButton(page);
        if (await Sel.WhenFilterMatchesHeading(page).CountAsync().ConfigureAwait(false) > 0)
        {
            // Auto-add page: the disabled repository button hydrates after the heading —
            // wait briefly instead of racing a bare count.
            try
            {
                await repositoryButton.First.WaitForAsync(new() { Timeout = 5_000 }).ConfigureAwait(false);
            }
            catch (PlaywrightException)
            {
                // No repository bound (fresh Auto-add) — fall through with null.
            }
        }

        if (await repositoryButton.CountAsync().ConfigureAwait(false) > 0)
        {
            repository = ViewUiExporter.NormalizeUiText(await repositoryButton.First.InnerTextAsync().ConfigureAwait(false));
        }

        IReadOnlyList<string>? contentTypes = null;
        string? statusValue = null;

        // "When ... : <value>" buttons: content types ("issue, pull request") or a status
        // ("Status: Done" for Auto-close issue). The Auto-add repository button matches the
        // same pattern, but Auto-add has no other "When" button, so the loop is skipped
        // entirely when the repository button is present.
        if (repository is null)
        {
            var whenButtons = Sel.WorkflowWhenButtons(page);
            var whenCount = await whenButtons.CountAsync().ConfigureAwait(false);
            for (var i = 0; i < whenCount; i++)
            {
                var text = ViewUiExporter.NormalizeUiText(await whenButtons.Nth(i).InnerTextAsync().ConfigureAwait(false));
                if (ParseStatusText(text) is { } status)
                {
                    statusValue = status;
                }
                else
                {
                    contentTypes = ParseContentTypes(text);
                }
            }
        }

        // "Set value : <status>" button (text "Status: <status>").
        var setValueButton = Sel.WorkflowSetValueButton(page);
        if (await setValueButton.CountAsync().ConfigureAwait(false) > 0)
        {
            var text = ViewUiExporter.NormalizeUiText(await setValueButton.First.InnerTextAsync().ConfigureAwait(false));
            statusValue = ParseStatusText(text) ?? text;
        }

        // Auto-add / Auto-archive filter (readable while disabled in viewing mode).
        string? filter = null;
        var filters = Sel.WorkflowFiltersTextbox(page);
        if (await filters.CountAsync().ConfigureAwait(false) > 0)
        {
            filter = ViewUiExporter.NormalizeUiText(await filters.First.InputValueAsync().ConfigureAwait(false));
        }

        return new WorkflowUiState(enabled, contentTypes, statusValue, filter, repository);
    }

    /// <summary>Parses "Status: &lt;value&gt;" into the value; null when the text has no such prefix.</summary>
    public static string? ParseStatusText(string? text)
    {
        if (text is null || !text.StartsWith("Status:", StringComparison.Ordinal))
        {
            return null;
        }

        return ViewUiExporter.NormalizeUiText(text["Status:".Length..]);
    }

    /// <summary>
    /// Parses a content-type list ("issue, pull request") into GraphQL-style names
    /// (ISSUE, PULL_REQUEST). Unknown tokens are preserved uppercased with underscores.
    /// </summary>
    public static IReadOnlyList<string>? ParseContentTypes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        return parts
            .Select(p => p.Replace(' ', '_').ToUpperInvariant())
            .ToList();
    }

    /// <summary>Maps a GraphQL-style content type (ISSUE) back to its UI option name (issue).</summary>
    public static string ContentTypeOptionName(string contentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        return contentType.Replace('_', ' ').ToLowerInvariant();
    }
}

/// <summary>Settings of a workflow as currently displayed in the UI (M7).</summary>
internal sealed record WorkflowUiState(
    bool Enabled,
    IReadOnlyList<string>? ContentTypes,
    string? StatusValue,
    string? Filter,
    string? Repository);
