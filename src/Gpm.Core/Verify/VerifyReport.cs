namespace Gpm.Core.Verify;

/// <summary>Severity of a single verification difference.</summary>
public enum VerifySeverity
{
    /// <summary>Informational note that never affects the verification result (e.g. an intentional title change).</summary>
    Info,

    /// <summary>A non-fatal difference or verification limitation.</summary>
    Warning,

    /// <summary>A difference in data the migration is expected to reproduce exactly.</summary>
    Error,
}

/// <summary>Verification outcome for the whole project or one category.</summary>
public enum VerifyStatus
{
    Match,
    Mismatch,
    PartialMatch,
    NotVerified,
}

/// <summary>One difference found by <see cref="ProjectVerifier"/>.</summary>
public sealed record VerifyDifference
{
    public required VerifySeverity Severity { get; init; }

    /// <summary>What the difference concerns: Project, Field, View, Workflow or Item.</summary>
    public required string Category { get; init; }

    public required string Message { get; init; }
}

/// <summary>Verification outcome for one independently verifiable category.</summary>
public sealed record VerifyCategoryResult
{
    public required string Category { get; init; }

    public required VerifyStatus Status { get; init; }
}

/// <summary>Result of comparing a source snapshot against a target project.</summary>
public sealed record VerifyReport
{
    /// <summary>All detected differences, in comparison order (project → fields → views → workflows → items).</summary>
    public required IReadOnlyList<VerifyDifference> Differences { get; init; }

    public required IReadOnlyList<VerifyCategoryResult> Categories { get; init; }

    public VerifyStatus Status
    {
        get
        {
            if (Categories.Any(category => category.Status == VerifyStatus.Mismatch))
            {
                return VerifyStatus.Mismatch;
            }

            if (Categories.Any(category => category.Status == VerifyStatus.NotVerified))
            {
                return VerifyStatus.NotVerified;
            }

            return Categories.Any(category => category.Status == VerifyStatus.PartialMatch)
                ? VerifyStatus.PartialMatch
                : VerifyStatus.Match;
        }
    }

    public bool IsMatch => Status == VerifyStatus.Match;

    public int ErrorCount => Differences.Count(difference => difference.Severity == VerifySeverity.Error);

    public int WarningCount => Differences.Count(difference => difference.Severity == VerifySeverity.Warning);

    public int InfoCount => Differences.Count(difference => difference.Severity == VerifySeverity.Info);

    public int NotVerifiedCount => Categories.Count(category => category.Status == VerifyStatus.NotVerified);

    public bool ShouldFail(bool strict, bool failOnWarning)
        => ErrorCount > 0
            || (strict && NotVerifiedCount > 0)
            || (failOnWarning && WarningCount > 0);
}
