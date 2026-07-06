using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gpm.Core.Import;

/// <summary>
/// Persistent mapping of imported items (source item position → target item node id),
/// written to <c>import-log.json</c> after every created item so an interrupted or
/// re-run import can skip items that were already created (resume).
/// The log is bound to a target project id; a log for a different project is ignored.
/// </summary>
public sealed record ImportLog
{
    public const string FileName = "import-log.json";

    /// <summary>Node ID of the target project this log belongs to.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Source item position (as an invariant string) → target item node id.</summary>
    public Dictionary<string, string> Items { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Loads the log from <paramref name="directory"/>, or returns null when missing or unreadable.</summary>
    public static async Task<ImportLog?> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, ImportLogJsonContext.Default.ImportLog, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return null; // Corrupt log: start fresh rather than fail the import.
        }
    }

    /// <summary>Writes the log as indented UTF-8 JSON into <paramref name="directory"/> and returns the file path.</summary>
    public async Task<string> SaveAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileName);

        var stream = File.Create(path);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, this, ImportLogJsonContext.Default.ImportLog, cancellationToken).ConfigureAwait(false);
        }

        return path;
    }
}

/// <summary>System.Text.Json source-generation context for <see cref="ImportLog"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportLog))]
public sealed partial class ImportLogJsonContext : JsonSerializerContext;
