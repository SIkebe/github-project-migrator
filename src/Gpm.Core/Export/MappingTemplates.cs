using System.Text;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Export;

/// <summary>
/// Generates repository, organization and user mapping CSV templates next to exported
/// snapshots. Candidates include item and linked repositories, Auto-add repositories,
/// View/Workflow filter identities, draft assignees and explicit user collaborators.
/// Repository and organization templates use <c>source,target</c>; user templates use
/// GitHub Enterprise Importer's <c>mannequin-user,mannequin-id,target-user</c> shape.
/// Existing files are preserved and newly discovered candidates are reported.
/// </summary>
public static class MappingTemplates
{
    public const string RepositoryMappingFileName = "repository-mappings.csv";
    public const string UserMappingFileName = "user-mappings.csv";
    public const string OrganizationMappingFileName = "organization-mappings.csv";

    /// <summary>
    /// Distinct repository mapping candidates from Issue/PR items, linked repositories,
    /// Auto-add repository short names and <c>repo:</c> filter values, in first-seen order.
    /// Candidates are not necessarily owner-qualified.
    /// </summary>
    public static IReadOnlyList<string> ExtractSourceRepositories(IEnumerable<ProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var snapshotList = snapshots.ToList();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<string>();
        foreach (var repository in snapshotList.SelectMany(s => s.Items)
                     .Select(item => item.Repository)
                     .Concat(snapshotList.SelectMany(s => s.LinkedRepositories ?? []))
                     .Concat(snapshotList.SelectMany(s => s.Workflows)
                         .Select(workflow => workflow.Ui?.Repository))
                     .Concat(FilterIdentifiers(snapshotList, "repo").Select(identifier => identifier.Value)))
        {
            if (repository is { Length: > 0 } && seen.Add(repository))
            {
                repositories.Add(repository);
            }
        }

        return repositories;
    }

    /// <summary>Distinct user logins from draft-issue assignees and explicit user collaborators, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractUserLogins(IEnumerable<ProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var logins = new List<string>();
        foreach (var assignee in snapshots.SelectMany(s => s.Items).Where(i => i.Draft is not null).SelectMany(i => i.Draft!.Assignees))
        {
            if (assignee.Length > 0 && seen.Add(assignee))
            {
                logins.Add(assignee);
            }
        }

        foreach (var collaborator in snapshots.SelectMany(s => s.Collaborators ?? []))
        {
            if (string.Equals(collaborator.Type, "USER", StringComparison.OrdinalIgnoreCase)
                && collaborator.Login.Length > 0
                && seen.Add(collaborator.Login))
            {
                logins.Add(collaborator.Login);
            }
        }

        foreach (var identifier in FilterIdentifiers(snapshots, "assignee", "author"))
        {
            if (identifier.Value.Length > 0 && seen.Add(identifier.Value))
            {
                logins.Add(identifier.Value);
            }
        }

        return logins;
    }

    public static IReadOnlyList<string> ExtractOrganizations(IEnumerable<ProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var snapshotList = snapshots.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var organizations = new List<string>();

        foreach (var repository in ExtractSourceRepositories(snapshotList))
        {
            var separator = repository.IndexOf('/');
            if (separator > 0 && seen.Add(repository[..separator]))
            {
                organizations.Add(repository[..separator]);
            }
        }

        foreach (var identifier in FilterIdentifiers(snapshotList, "org"))
        {
            if (identifier.Value.Length > 0 && seen.Add(identifier.Value))
            {
                organizations.Add(identifier.Value);
            }
        }

        return organizations;
    }

    /// <summary>
    /// Writes the mapping templates into <paramref name="directory"/>.
    /// <c>user-mappings.csv</c> is only written when at least one draft assignee or explicit user collaborator exists.
    /// Files that already exist are skipped (reported via <paramref name="onProgress"/>).
    /// </summary>
    public static async Task WriteAsync(
        IReadOnlyList<ProjectSnapshot> snapshots,
        string directory,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        await WriteTemplateAsync(
            Path.Combine(directory, RepositoryMappingFileName),
            ExtractSourceRepositories(snapshots),
            header: "source,target",
            rowFactory: source => string.Concat(source, ","),
            onProgress: onProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await WriteTemplateAsync(
            Path.Combine(directory, OrganizationMappingFileName),
            ExtractOrganizations(snapshots),
            header: "source,target",
            rowFactory: source => string.Concat(source, ","),
            onProgress: onProgress,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var userLogins = ExtractUserLogins(snapshots);
        if (userLogins.Count > 0)
        {
            await WriteTemplateAsync(
                Path.Combine(directory, UserMappingFileName),
                userLogins,
                header: "mannequin-user,mannequin-id,target-user",
                rowFactory: source => string.Concat(source, ",,"),
                onProgress: onProgress,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

    }

    private static IEnumerable<FilterIdentifier> FilterIdentifiers(
        IEnumerable<ProjectSnapshot> snapshots,
        params string[] qualifiers)
    {
        var allowed = new HashSet<string>(qualifiers, StringComparer.OrdinalIgnoreCase);
        return snapshots
            .SelectMany(snapshot => snapshot.Views.Select(view => view.Filter)
                .Concat(snapshot.Workflows.Select(workflow => workflow.Ui?.Filter)))
            .Where(filter => filter is not null)
            .SelectMany(filter => ProjectFilterTransformer.ExtractIdentifiers(filter!))
            .Where(identifier => allowed.Contains(identifier.Qualifier));
    }

    private static async Task WriteTemplateAsync(
        string path,
        IReadOnlyList<string> sources,
        string header,
        Func<string, string> rowFactory,
        Action<string>? onProgress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            var existingSources = File.ReadLines(path)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Skip(1)
                .Select(line => line.Split(',')[0].Trim())
                .Where(source => source.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = sources.Where(source => !existingSources.Contains(source)).ToList();
            var missingMessage = missing.Count == 0
                ? string.Empty
                : $" Missing candidates: {string.Join(", ", missing)}.";
            onProgress?.Invoke(
                $"Mapping template {path} already exists; not overwriting (your edits are preserved).{missingMessage}");
            return;
        }

        var builder = new StringBuilder(header).Append('\n');
        foreach (var source in sources)
        {
            builder.Append(rowFactory(source)).Append('\n');
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke($"Mapping template written to {path} (fill in the target column before import).");
    }
}
