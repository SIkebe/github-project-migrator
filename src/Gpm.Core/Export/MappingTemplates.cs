using System.Text;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Export;

/// <summary>
/// Generates the mapping CSV templates written next to exported snapshots:
/// <c>repository-mappings.csv</c> (distinct source repositories of Issue/PR items) and
/// <c>user-mappings.csv</c> (distinct draft-issue assignee logins; only written when at
/// least one draft has assignees). The target column is left blank for the user to fill
/// in; rows with a blank target are ignored by <see cref="Import.CsvMapping"/>.
/// Existing files are never overwritten so user edits survive re-exports.
/// </summary>
public static class MappingTemplates
{
    public const string RepositoryMappingFileName = "repository-mappings.csv";
    public const string UserMappingFileName = "user-mappings.csv";

    /// <summary>Distinct source repositories ("org/repo") of all Issue/PR items, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractSourceRepositories(IEnumerable<ProjectSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<string>();
        foreach (var item in snapshots.SelectMany(s => s.Items))
        {
            if (item.Repository is { Length: > 0 } repository && seen.Add(repository))
            {
                repositories.Add(repository);
            }
        }

        return repositories;
    }

    /// <summary>Distinct assignee logins across all draft-issue items, in first-seen order.</summary>
    public static IReadOnlyList<string> ExtractDraftAssignees(IEnumerable<ProjectSnapshot> snapshots)
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

        return logins;
    }

    /// <summary>
    /// Writes the mapping templates into <paramref name="directory"/>.
    /// <c>user-mappings.csv</c> is only written when at least one draft item has assignees.
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
            onProgress,
            cancellationToken).ConfigureAwait(false);

        var assignees = ExtractDraftAssignees(snapshots);
        if (assignees.Count > 0)
        {
            await WriteTemplateAsync(
                Path.Combine(directory, UserMappingFileName),
                assignees,
                onProgress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteTemplateAsync(string path, IReadOnlyList<string> sources, Action<string>? onProgress, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            onProgress?.Invoke($"Mapping template {path} already exists; not overwriting (your edits are preserved).");
            return;
        }

        var builder = new StringBuilder("source,target\n");
        foreach (var source in sources)
        {
            builder.Append(source).Append(",\n");
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
        onProgress?.Invoke($"Mapping template written to {path} (fill in the target column before import).");
    }
}
