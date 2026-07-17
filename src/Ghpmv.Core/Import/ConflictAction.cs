namespace Ghpmv.Core.Import;

/// <summary>How <see cref="ProjectImporter"/> reacts when the target organization already has a project with the same title.</summary>
public enum ConflictAction
{
    /// <summary>Throw an exception (default).</summary>
    Fail,

    /// <summary>Return the existing project without changing it.</summary>
    Skip,

    /// <summary>Apply the snapshot to the existing project (update metadata, create missing fields, overwrite options).</summary>
    Update,
}

/// <summary>Case-insensitive parsing helpers for <see cref="ConflictAction"/> (CLI <c>--on-conflict</c>).</summary>
public static class ConflictActions
{
    /// <summary>Parses "skip", "update" or "fail" (case-insensitive).</summary>
    public static bool TryParse(string? value, out ConflictAction action)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case "SKIP":
                action = ConflictAction.Skip;
                return true;
            case "UPDATE":
                action = ConflictAction.Update;
                return true;
            case "FAIL":
                action = ConflictAction.Fail;
                return true;
            default:
                action = default;
                return false;
        }
    }
}
