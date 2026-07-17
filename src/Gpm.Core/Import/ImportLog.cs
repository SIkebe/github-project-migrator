using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gpm.Core.Snapshot;

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
    public const string BackupFileName = "import-log.json.bak";
    public const int CurrentSchemaVersion = 2;

    /// <summary>Version of the durable import state schema.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Node ID of the target project this log belongs to.</summary>
    public required string ProjectId { get; init; }

    /// <summary>SHA-256 fingerprint of the complete source snapshot.</summary>
    public string? SourceSnapshotFingerprint { get; init; }

    /// <summary>Source item position (as an invariant string) → target item node id.</summary>
    public Dictionary<string, string> Items { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Stable source item key → durable progress through each import stage.</summary>
    public Dictionary<string, ImportItemState> ItemStates { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Draft creations persisted before sending so an ambiguous result can be reconciled safely.</summary>
    public Dictionary<string, PendingDraftOperation> PendingDrafts { get; init; } = new(StringComparer.Ordinal);

    /// <summary>Issue/PR additions persisted before sending so an ambiguous result can be reconciled safely.</summary>
    public Dictionary<string, PendingContentOperation> PendingContents { get; init; } = new(StringComparer.Ordinal);

    [JsonIgnore]
    public bool HasIncompleteItems => ItemStates.Values.Any(state =>
        !state.FieldValuesApplied || !state.PositionApplied || !state.ArchiveApplied);

    /// <summary>Loads the log from <paramref name="directory"/>, or returns null when missing.</summary>
    public static async Task<ImportLog?> LoadAsync(string directory, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        var stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            var log = await JsonSerializer.DeserializeAsync(stream, ImportLogJsonContext.Default.ImportLog, cancellationToken).ConfigureAwait(false)
                ?? throw new JsonException($"{FileName} contained null.");
            if (log.SchemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"{FileName} uses unsupported schema version {log.SchemaVersion}; expected {CurrentSchemaVersion}. The log cannot be resumed safely.");
            }
            if (string.IsNullOrWhiteSpace(log.ProjectId))
            {
                throw new InvalidDataException($"{FileName} does not contain a target project ID and cannot be resumed safely.");
            }
            if (string.IsNullOrWhiteSpace(log.SourceSnapshotFingerprint))
            {
                throw new InvalidDataException($"{FileName} does not contain a source snapshot fingerprint and cannot be resumed safely.");
            }
            if (log.Items is null
                || log.ItemStates is null
                || log.PendingDrafts is null
                || log.PendingContents is null
                || log.Items.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value))
                || log.ItemStates.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Value is null
                    || string.IsNullOrWhiteSpace(pair.Value.TargetItemId)
                    || string.IsNullOrWhiteSpace(pair.Value.TargetContentIdentity))
                || log.PendingDrafts.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Value is null
                    || string.IsNullOrWhiteSpace(pair.Value.OperationId)
                    || pair.Value.AssigneeIds is null
                    || pair.Value.ExistingItemIds is null
                    || pair.Value.AssigneeIds.Any(string.IsNullOrWhiteSpace)
                    || pair.Value.ExistingItemIds.Any(string.IsNullOrWhiteSpace))
                || log.PendingContents.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Value is null
                    || string.IsNullOrWhiteSpace(pair.Value.OperationId)
                    || string.IsNullOrWhiteSpace(pair.Value.ContentId)
                    || pair.Value.ExistingItemIds is null
                    || pair.Value.ExistingItemIds.Any(string.IsNullOrWhiteSpace)))
            {
                throw new InvalidDataException($"{FileName} contains malformed item state and cannot be resumed safely.");
            }
            var mappedIds = log.Items.Values.ToHashSet(StringComparer.Ordinal);
            var stateIds = log.ItemStates.Values.Select(state => state.TargetItemId).ToHashSet(StringComparer.Ordinal);
            if (mappedIds.Count != log.Items.Count
                || stateIds.Count != log.ItemStates.Count
                || !mappedIds.SetEquals(stateIds))
            {
                throw new InvalidDataException(
                    $"{FileName} contains inconsistent item mappings and cannot be resumed safely.");
            }
            if (log.PendingDrafts.Keys.Any(key =>
                    log.PendingContents.ContainsKey(key) || log.Items.ContainsKey(key))
                || log.PendingContents.Keys.Any(log.Items.ContainsKey))
            {
                throw new InvalidDataException(
                    $"{FileName} contains overlapping pending item operations and cannot be resumed safely.");
            }

            return log;
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

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, Path.Combine(directory, BackupFileName));
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }

        return path;
    }

    public static string ComputeSnapshotFingerprint(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var json = JsonSerializer.SerializeToUtf8Bytes(snapshot, SnapshotJsonContext.Default.ProjectSnapshot);
        return Convert.ToHexString(SHA256.HashData(json));
    }
}

public sealed record ImportItemState
{
    public required string TargetItemId { get; init; }

    public string? TargetContentIdentity { get; init; }

    public bool FieldValuesApplied { get; set; }

    public bool PositionApplied { get; set; }

    public bool ArchiveApplied { get; set; }

    public string? FieldValuesError { get; set; }

    public string? PositionError { get; set; }

    public string? ArchiveError { get; set; }

    [JsonIgnore]
    public string? LastError => FieldValuesError ?? PositionError ?? ArchiveError;
}

public sealed record PendingDraftOperation
{
    public required string OperationId { get; init; }

    public required DateTimeOffset AttemptedAt { get; init; }

    public required string Title { get; init; }

    public string? Body { get; init; }

    public string[]? AssigneeIds { get; init; }

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
