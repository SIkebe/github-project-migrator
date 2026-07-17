using System.Text.Json;
using System.Text.Json.Serialization;

namespace Gpm.Core.Import;

public sealed record ProjectImportLog
{
    public const string FileName = "project-import-log.json";

    public PendingProjectOperation? PendingProject { get; set; }

    public Dictionary<string, PendingFieldOperation> PendingFields { get; init; } = new(StringComparer.Ordinal);

    public static async Task<ProjectImportLog> LoadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return new ProjectImportLog();
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(
            stream,
            ProjectImportLogJsonContext.Default.ProjectImportLog,
            cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException($"{FileName} contained null.");
    }

    public async Task SaveAsync(string directory, CancellationToken cancellationToken)
    {
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
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    this,
                    ProjectImportLogJsonContext.Default.ProjectImportLog,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

public sealed record PendingProjectOperation
{
    public required string OperationId { get; init; }

    public required string OwnerLogin { get; init; }

    public required string Title { get; init; }

    public required string[] ExistingProjectIds { get; init; }
}

public sealed record PendingFieldOperation
{
    public required string OperationId { get; init; }

    public required string ProjectId { get; init; }

    public required string Name { get; init; }

    public required string DataType { get; init; }

    public required string[] ExistingFieldIds { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProjectImportLog))]
public sealed partial class ProjectImportLogJsonContext : JsonSerializerContext;
