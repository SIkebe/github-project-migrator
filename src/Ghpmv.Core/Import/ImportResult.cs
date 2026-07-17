namespace Ghpmv.Core.Import;

/// <summary>
/// Result of <see cref="ProjectImporter.ImportAsync"/>: the target project identity
/// plus name-to-id mappings needed by the item import phase (M4).
/// </summary>
public sealed record ImportResult
{
    /// <summary>Node ID of the target project.</summary>
    public required string ProjectId { get; init; }

    /// <summary>Project number in the target organization.</summary>
    public required int ProjectNumber { get; init; }

    /// <summary>Web URL of the target project.</summary>
    public required string Url { get; init; }

    /// <summary>Whether this run created, updated, or skipped the target project.</summary>
    public required ProjectImportOutcome Outcome { get; init; }

    /// <summary>True when the project was created by this run.</summary>
    public bool Created => Outcome == ProjectImportOutcome.Created;

    /// <summary>Field name → field node ID.</summary>
    public required IReadOnlyDictionary<string, string> FieldIds { get; init; }

    /// <summary>Field name → (single-select option name → option ID).</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> OptionIds { get; init; }

    /// <summary>Field name → (iteration title → iteration ID). Includes completed iterations.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> IterationIds { get; init; }
}
