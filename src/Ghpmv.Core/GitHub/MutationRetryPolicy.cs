namespace Ghpmv.Core.GitHub;

/// <summary>Controls whether a mutation can be repeated after an ambiguous HTTP result.</summary>
public enum MutationRetryPolicy
{
    /// <summary>Do not retry transport failures or 5xx responses because the mutation may have succeeded.</summary>
    Create,

    /// <summary>The caller guarantees that applying the same desired state more than once is safe.</summary>
    Idempotent,
}
