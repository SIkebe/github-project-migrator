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

    /// <summary>Draft creations persisted before sending so an ambiguous result can be reconciled safely.</summary>
    public Dictionary<string, PendingDraftOperation> PendingDrafts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Issue/PR additions persisted before sending so an ambiguous result can be reconciled safely.</summary>
    public Dictionary<string, PendingContentOperation> PendingContents { get; init; } = new(StringComparer.Ordinal);

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

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, this, ImportLogJsonContext.Default.ImportLog, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return path;
    }
}

public sealed record PendingDraftOperation
{
    public required string OperationId { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public required string[] ExistingItemIds { get; init; }
}

public sealed record PendingContentOperation
{
    public required string OperationId { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }

    public required string ContentId { get; init; }

    public required string[] ExistingItemIds { get; init; }
}

/// <summary>System.Text.Json source-generation context for <see cref="ImportLog"/>.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ImportLog))]
public sealed partial class ImportLogJsonContext : JsonSerializerContext;
