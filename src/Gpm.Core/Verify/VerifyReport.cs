namespace Gpm.Core.Verify;

/// <summary>Severity of a single verification difference.</summary>
public enum VerifySeverity
{
    /// <summary>Informational note that never affects the verification result (e.g. an intentional title change).</summary>
    Info,

    /// <summary>A difference in data that gpm cannot migrate yet (views/workflows until M6/M7).</summary>
    Warning,

    /// <summary>A difference in data the migration is expected to reproduce exactly.</summary>
    Error,
}

/// <summary>One difference found by <see cref="ProjectVerifier"/>.</summary>
public sealed record VerifyDifference
{
    public required VerifySeverity Severity { get; init; }

    /// <summary>What the difference concerns: Project, Field, View, Workflow or Item.</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }
}

/// <summary>Result of comparing a source snapshot against a target project (M5).</summary>
public sealed record VerifyReport
{
    /// <summary>All detected differences, in comparison order (project → fields → views → workflows → items).</summary>
    public required IReadOnlyList<VerifyDifference> Differences { get; init; }

    /// <summary>
    /// True when no <see cref="VerifySeverity.Error"/> difference was found. Warnings
    /// (views/workflows pending M6/M7) and infos do not affect the match result.
    /// </summary>
    public bool IsMatch => Differences.All(d => d.Severity != VerifySeverity.Error);
}
