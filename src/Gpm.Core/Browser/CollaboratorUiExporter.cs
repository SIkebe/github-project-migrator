using System.Globalization;
using System.Text.RegularExpressions;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// UI export of explicit project collaborators. GitHub's public GraphQL schema has
/// a write mutation (<c>updateProjectV2Collaborators</c>) but no read field for
/// current collaborators. The Projects web UI does expose explicitly added
/// collaborators under Settings → Manage access; inherited/base-role access is
/// intentionally not captured.
/// </summary>
public sealed partial class CollaboratorUiExporter
{
    private readonly BrowserSession _session;
    private readonly List<string> _warnings = [];

    public CollaboratorUiExporter(BrowserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public Action<string>? OnProgress { get; set; }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Returns a copy of <paramref name="snapshot"/> with explicit collaborators populated.</summary>
    public async Task<ProjectSnapshot> EnrichAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        OnProgress?.Invoke("Reading explicit project collaborators from the access settings page...");

        try
        {
            var page = await _session.GetPageAsync(cancellationToken).ConfigureAwait(false);
            var ownerPath = ownerType == ProjectOwnerType.User ? "users" : "orgs";
            var url = string.Create(CultureInfo.InvariantCulture,
                $"{_session.BaseUrl}/{ownerPath}/{ownerLogin}/projects/{projectNumber}/settings/access");
            await _session.GotoAsync(url, cancellationToken).ConfigureAwait(false);
            await page.GetByRole(AriaRole.Heading, new() { Name = "Manage access", Level = 3 })
                .WaitForAsync().ConfigureAwait(false);
            await Task.Delay(500, cancellationToken).ConfigureAwait(false);

            var snapshotText = await page.Locator("body").AriaSnapshotAsync().ConfigureAwait(false);
            var collaborators = ParseAccessSnapshot(snapshotText, ownerLogin);
            return snapshot with { Collaborators = collaborators };
        }
        catch (Exception exception) when (exception is PlaywrightException or TimeoutException or InvalidOperationException)
        {
            _warnings.Add($"explicit collaborators could not be exported from the access settings UI — {exception.Message}");
            return snapshot with { Collaborators = null };
        }
    }

    /// <summary>
    /// Parses GitHub's accessibility snapshot for the Manage access table.
    /// The table exposes rows as a sequence like:
    /// checkbox "Select ravel-maurice-uo_sde" → link/profile text → button "Role: Write".
    /// </summary>
    public static IReadOnlyList<CollaboratorSnapshot> ParseAccessSnapshot(string snapshotText, string ownerLogin)
    {
        ArgumentNullException.ThrowIfNull(snapshotText);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        var lines = snapshotText.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var collaborators = new List<CollaboratorSnapshot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Length; i++)
        {
            var match = SelectCollaboratorRegex().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            var loginOrName = match.Groups["name"].Value.Trim();
            if (loginOrName.Length == 0 || loginOrName.StartsWith("all collaborators", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var role = "WRITER";
            var type = "USER";
            var login = loginOrName;

            for (var j = i + 1; j < Math.Min(lines.Length, i + 12); j++)
            {
                var roleMatch = RoleRegex().Match(lines[j]);
                if (roleMatch.Success)
                {
                    role = ToGraphQlRole(roleMatch.Groups["role"].Value);
                }

                var teamMatch = TeamUrlRegex(ownerLogin).Match(lines[j]);
                if (teamMatch.Success)
                {
                    type = "TEAM";
                    login = teamMatch.Groups["slug"].Value;
                }
            }

            var key = $"{type}:{login}";
            if (seen.Add(key))
            {
                collaborators.Add(new CollaboratorSnapshot
                {
                    Type = type,
                    Login = login,
                    Role = role,
                });
            }
        }

        return collaborators;
    }

    private static string ToGraphQlRole(string role) => role.ToUpperInvariant() switch
    {
        "READ" or "READER" => "READER",
        "WRITE" or "WRITER" => "WRITER",
        "ADMIN" => "ADMIN",
        _ => "WRITER",
    };

    [GeneratedRegex("checkbox \\\"Select (?<name>[^\\\"]+)\\\"")]
    private static partial Regex SelectCollaboratorRegex();

    [GeneratedRegex("button \\\"Role: (?<role>Read|Write|Admin|Reader|Writer)\\\"", RegexOptions.IgnoreCase)]
    private static partial Regex RoleRegex();

    private static Regex TeamUrlRegex(string ownerLogin)
        => new($"/orgs/{Regex.Escape(ownerLogin)}/teams/(?<slug>[A-Za-z0-9_.-]+)", RegexOptions.IgnoreCase);
}
