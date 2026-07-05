using System.Text.Json;

namespace Gpm.Core.Snapshot;

/// <summary>Reads and writes the single <c>snapshot.json</c> file inside a snapshot directory.</summary>
public static class SnapshotFile
{
    public const string FileName = "snapshot.json";

    /// <summary>Writes the snapshot as indented UTF-8 JSON and returns the file path.</summary>
    public static async Task<string> SaveAsync(ProjectSnapshot snapshot, string directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, snapshot, SnapshotJsonContext.Default.ProjectSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Loads a snapshot from the <c>snapshot.json</c> file inside <paramref name="directory"/>.</summary>
    public static async Task<ProjectSnapshot> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);
        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, SnapshotJsonContext.Default.ProjectSnapshot, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"'{path}' contained a null snapshot.");
        }
    }
}
