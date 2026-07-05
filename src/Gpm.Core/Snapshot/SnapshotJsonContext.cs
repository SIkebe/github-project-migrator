using System.Text.Json.Serialization;

namespace Gpm.Core.Snapshot;

/// <summary>System.Text.Json source-generation context for the snapshot schema.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ProjectSnapshot))]
public sealed partial class SnapshotJsonContext : JsonSerializerContext;
