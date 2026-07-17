namespace Ghpmv.Core.GitHub;

/// <summary>
/// Thrown when a mutation may have reached GitHub but no definitive result was received.
/// Retrying the same operation could duplicate a resource.
/// </summary>
public sealed class AmbiguousMutationResultException : GitHubGraphQLException
{
    public AmbiguousMutationResultException(
        string operationName,
        string clientMutationId,
        DateTimeOffset attemptedAt,
        string? target,
        string detail,
        Exception? innerException = null)
        : base(
            $"Mutation result is ambiguous. Automatic retry was stopped to avoid duplicates. "
            + $"Operation: {operationName}. Client mutation ID: {clientMutationId}. "
            + $"Attempted at: {attemptedAt:O}. "
            + $"Target: {target ?? "(not specified)"}. "
            + $"Recovery: inspect the target state, then rerun with the same snapshot and import log. Detail: {detail}",
            innerException!)
    {
        OperationName = operationName;
        ClientMutationId = clientMutationId;
        AttemptedAt = attemptedAt;
        Target = target;
    }

    public string OperationName { get; }

    public string ClientMutationId { get; }

    public DateTimeOffset AttemptedAt { get; }

    public string? Target { get; }
}
