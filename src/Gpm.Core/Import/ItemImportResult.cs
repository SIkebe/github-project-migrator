namespace Gpm.Core.Import;

/// <summary>Result of <see cref="ItemImporter.ImportAsync"/>: item counts and collected warnings.</summary>
public sealed record ItemImportResult
{
    /// <summary>Number of items created in the target project by this run.</summary>
    public required int Created { get; init; }

    /// <summary>Number of existing items whose incomplete stages were processed by this run.</summary>
    public required int Resumed { get; init; }

    /// <summary>Number of items already complete before this run.</summary>
    public required int AlreadyComplete { get; init; }

    /// <summary>Number of items skipped because they could not be imported.</summary>
    public required int Skipped { get; init; }

    /// <summary>Human-readable warnings collected during the import (skipped items, dropped values/assignees).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
