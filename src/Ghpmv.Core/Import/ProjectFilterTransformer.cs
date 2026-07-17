using System.Collections.ObjectModel;
using System.Text;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Import;

/// <summary>
/// Structurally maps identity-bearing GitHub Projects filter qualifier values while
/// preserving all other text exactly.
/// </summary>
public static class ProjectFilterTransformer
{
    private static readonly HashSet<string> UserQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "assignee",
        "author",
    };

    private static readonly HashSet<string> PassthroughQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "is",
        "iteration",
        "label",
        "milestone",
        "no",
        "reason",
        "status",
        "type",
        "updated",
    };

    /// <summary>Transforms supported qualifier values using source-to-target mappings.</summary>
    public static FilterTransformResult Transform(
        string filter,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null)
    {
        ArgumentNullException.ThrowIfNull(filter);
        userMapping ??= ReadOnlyDictionary<string, string>.Empty;
        repositoryMapping ??= ReadOnlyDictionary<string, string>.Empty;
        organizationMapping = MergeOrganizationMappings(repositoryMapping, organizationMapping);

        var changes = new List<FilterTokenChange>();
        var unresolved = new List<FilterIdentifier>();
        var unchanged = new List<FilterIdentifier>();
        var unsupported = new List<FilterIdentifier>();
        var builder = new StringBuilder(filter.Length);
        var position = 0;
        while (position < filter.Length)
        {
            if (filter[position] == '"')
            {
                var literalEnd = FindQuotedValueEnd(filter, position);
                builder.Append(filter, position, literalEnd - position);
                position = literalEnd;
                continue;
            }

            if (!IsQualifierBoundary(filter, position)
                || !IsQualifierStart(filter[position])
                || !TryReadQualifierValue(filter, position, out var qualifier, out var rawValue, out var tokenEnd))
            {
                builder.Append(filter[position]);
                position++;
                continue;
            }

            builder.Append(qualifier).Append(':');
            var quoted = rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"';
            var value = quoted ? rawValue[1..^1] : rawValue;
            var mapping = MappingFor(qualifier, userMapping, repositoryMapping, organizationMapping);

            if (mapping is null)
            {
                builder.Append(rawValue);
                if (!PassthroughQualifiers.Contains(qualifier))
                {
                    unsupported.Add(new FilterIdentifier(qualifier, value));
                }
                else
                {
                    unchanged.Add(new FilterIdentifier(qualifier, value));
                }
            }
            else
            {
                if (quoted)
                {
                    builder.Append('"');
                }

                var values = value.Split(',');
                for (var index = 0; index < values.Length; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    var item = values[index];
                    var identity = item.Trim();
                    var leadingWhitespace = item[..(item.Length - item.TrimStart().Length)];
                    var trailingWhitespace = item[item.TrimEnd().Length..];
                    var mappingIdentity = MappingIdentity(qualifier, identity, out var mappedPrefix);
                    if (mapping.TryGetValue(mappingIdentity, out var mapped))
                    {
                        var mappedValue = string.Concat(mappedPrefix, mapped);
                        builder.Append(leadingWhitespace).Append(mappedValue).Append(trailingWhitespace);
                        if (!string.Equals(identity, mappedValue, StringComparison.Ordinal))
                        {
                            changes.Add(new FilterTokenChange(qualifier, identity, mappedValue));
                        }
                        else
                        {
                            unchanged.Add(new FilterIdentifier(qualifier, identity));
                        }
                    }
                    else
                    {
                        builder.Append(item);
                        if (IsMappingRequired(qualifier, identity))
                        {
                            unresolved.Add(new FilterIdentifier(qualifier, mappingIdentity));
                        }
                        else if (identity.Length > 0)
                        {
                            unchanged.Add(new FilterIdentifier(qualifier, identity));
                        }
                    }
                }

                if (quoted)
                {
                    builder.Append('"');
                }
            }

            position = tokenEnd;
        }

        return new FilterTransformResult(filter, builder.ToString(), changes, unresolved, unchanged, unsupported);
    }

    /// <summary>Applies filter mappings to all Views and Workflows in a snapshot.</summary>
    public static ProjectSnapshot TransformSnapshot(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot with
        {
            Views = snapshot.Views.Select(view => view.Filter is null
                ? view
                : view with
                {
                    Filter = Transform(view.Filter, userMapping, repositoryMapping, organizationMapping).Transformed,
                }).ToList(),
            Workflows = snapshot.Workflows.Select(workflow => workflow.Ui?.Filter is null
                ? workflow
                : workflow with
                {
                    Ui = workflow.Ui with
                    {
                        Filter = Transform(workflow.Ui.Filter, userMapping, repositoryMapping, organizationMapping).Transformed,
                    },
                }).ToList(),
        };
    }

    /// <summary>Returns all View and Workflow filter transformation results with their locations.</summary>
    public static IReadOnlyList<SnapshotFilterTransform> AnalyzeSnapshot(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string>? userMapping = null,
        IReadOnlyDictionary<string, string>? repositoryMapping = null,
        IReadOnlyDictionary<string, string>? organizationMapping = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var results = new List<SnapshotFilterTransform>();
        results.AddRange(snapshot.Views.Where(view => view.Filter is not null).Select(view =>
            new SnapshotFilterTransform(
                $"view '{view.Name}'",
                Transform(view.Filter!, userMapping, repositoryMapping, organizationMapping))));
        results.AddRange(snapshot.Workflows.Where(workflow => workflow.Ui?.Filter is not null).Select(workflow =>
            new SnapshotFilterTransform(
                $"workflow '{workflow.Name}'",
                Transform(workflow.Ui!.Filter!, userMapping, repositoryMapping, organizationMapping))));
        return results;
    }

    /// <summary>Returns Auto-add repository mapping results with their workflow locations.</summary>
    public static IReadOnlyList<SnapshotRepositoryResolution> AnalyzeAutoAddRepositories(
        ProjectSnapshot snapshot,
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        return snapshot.Workflows
            .Where(workflow => workflow.Ui?.Repository is { Length: > 0 })
            .Select(workflow => new SnapshotRepositoryResolution(
                $"workflow '{workflow.Name}'",
                ResolveRepository(workflow.Ui!.Repository!, repositoryMapping)))
            .ToList();
    }

    /// <summary>
    /// Infers source-to-target organization mappings only when every repository mapping
    /// for a source owner points to the same target owner.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildOrganizationMapping(
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in repositoryMapping)
        {
            if (!TrySplitRepository(source, out var sourceOwner, out _)
                || !TrySplitRepository(target, out var targetOwner, out _))
            {
                continue;
            }

            if (!candidates.TryGetValue(sourceOwner, out var targetOwners))
            {
                targetOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                candidates[sourceOwner] = targetOwners;
            }

            targetOwners.Add(targetOwner);
        }

        return candidates
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Single(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Resolves an Auto-add repository short name without guessing ambiguous targets.</summary>
    public static RepositoryResolution ResolveRepository(
        string sourceRepository,
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRepository);
        ArgumentNullException.ThrowIfNull(repositoryMapping);

        var targets = repositoryMapping
            .Where(pair => string.Equals(ShortName(pair.Key), sourceRepository, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return targets.Count switch
        {
            0 => new RepositoryResolution(sourceRepository, null, RepositoryResolutionStatus.Unmapped),
            1 => new RepositoryResolution(sourceRepository, ShortName(targets[0]), RepositoryResolutionStatus.Mapped),
            _ => new RepositoryResolution(sourceRepository, null, RepositoryResolutionStatus.Ambiguous),
        };

        static string ShortName(string repository)
        {
            var separator = repository.LastIndexOf('/');
            return separator < 0 ? repository : repository[(separator + 1)..];
        }
    }

    private static Dictionary<string, string> MergeOrganizationMappings(
        IReadOnlyDictionary<string, string> repositoryMapping,
        IReadOnlyDictionary<string, string>? explicitMapping)
    {
        var result = new Dictionary<string, string>(
            BuildOrganizationMapping(repositoryMapping),
            StringComparer.OrdinalIgnoreCase);
        foreach (var (source, target) in explicitMapping ?? ReadOnlyDictionary<string, string>.Empty)
        {
            result[source] = target;
        }

        return result;
    }

    /// <summary>Enumerates supported identity-bearing tokens without changing the filter.</summary>
    public static IReadOnlyList<FilterIdentifier> ExtractIdentifiers(string filter)
        => Transform(filter).Unresolved;

    private static bool TryReadQualifierValue(
        string filter,
        int start,
        out string qualifier,
        out string rawValue,
        out int tokenEnd)
    {
        var separator = start + 1;
        while (separator < filter.Length && IsQualifierPart(filter[separator]))
        {
            separator++;
        }

        if (separator >= filter.Length || filter[separator] != ':' || separator + 1 >= filter.Length)
        {
            qualifier = string.Empty;
            rawValue = string.Empty;
            tokenEnd = start;
            return false;
        }

        var valueStart = separator + 1;
        tokenEnd = filter[valueStart] == '"'
            ? FindQuotedValueEnd(filter, valueStart)
            : FindUnquotedValueEnd(filter, valueStart);
        qualifier = filter[start..separator];
        rawValue = filter[valueStart..tokenEnd];
        return rawValue.Length > 0;
    }

    private static int FindQuotedValueEnd(string filter, int quoteStart)
    {
        var escaped = false;
        for (var index = quoteStart + 1; index < filter.Length; index++)
        {
            if (!escaped && filter[index] == '"')
            {
                return index + 1;
            }

            escaped = !escaped && filter[index] == '\\';
            if (filter[index] != '\\')
            {
                escaped = false;
            }
        }

        return filter.Length;
    }

    private static int FindUnquotedValueEnd(string filter, int valueStart)
    {
        var index = valueStart;
        while (index < filter.Length
            && !char.IsWhiteSpace(filter[index])
            && filter[index] is not '(' and not ')')
        {
            index++;
        }

        return index;
    }

    private static bool IsQualifierBoundary(string filter, int index)
    {
        if (index == 0 || char.IsWhiteSpace(filter[index - 1]) || filter[index - 1] == '(')
        {
            return true;
        }

        return filter[index - 1] == '-'
            && (index == 1 || char.IsWhiteSpace(filter[index - 2]) || filter[index - 2] == '(');
    }

    private static bool IsQualifierStart(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsQualifierPart(char value)
        => IsQualifierStart(value) || char.IsDigit(value) || value is '_' or '-';

    private static IReadOnlyDictionary<string, string>? MappingFor(
        string qualifier,
        IReadOnlyDictionary<string, string> userMapping,
        IReadOnlyDictionary<string, string> repositoryMapping,
        IReadOnlyDictionary<string, string> organizationMapping)
    {
        if (UserQualifiers.Contains(qualifier))
        {
            return userMapping;
        }

        if (string.Equals(qualifier, "repo", StringComparison.OrdinalIgnoreCase))
        {
            return repositoryMapping;
        }

        return string.Equals(qualifier, "org", StringComparison.OrdinalIgnoreCase)
            ? organizationMapping
            : null;
    }

    private static bool TrySplitRepository(string repository, out string owner, out string name)
    {
        var separator = repository.IndexOf('/');
        if (separator <= 0 || separator == repository.Length - 1)
        {
            owner = string.Empty;
            name = string.Empty;
            return false;
        }

        owner = repository[..separator];
        name = repository[(separator + 1)..];
        return true;
    }

    private static bool IsMappingRequired(string qualifier, string value)
        => value.Length > 0
            && (!UserQualifiers.Contains(qualifier)
                || !string.Equals(value, "@me", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase));

    private static string MappingIdentity(string qualifier, string value, out string prefix)
    {
        if (UserQualifiers.Contains(qualifier)
            && value.Length > 1
            && value[0] == '@'
            && !string.Equals(value, "@me", StringComparison.OrdinalIgnoreCase))
        {
            prefix = "@";
            return value[1..];
        }

        prefix = string.Empty;
        return value;
    }

}

public sealed record FilterIdentifier(string Qualifier, string Value);

public sealed record FilterTokenChange(string Qualifier, string Source, string Target);

public sealed record FilterTransformResult(
    string Original,
    string Transformed,
    IReadOnlyList<FilterTokenChange> Changes,
    IReadOnlyList<FilterIdentifier> Unresolved,
    IReadOnlyList<FilterIdentifier> Unchanged,
    IReadOnlyList<FilterIdentifier> Unsupported);

public sealed record SnapshotFilterTransform(string Location, FilterTransformResult Result);

public enum RepositoryResolutionStatus
{
    Mapped,
    Unmapped,
    Ambiguous,
}

public sealed record RepositoryResolution(
    string Source,
    string? Target,
    RepositoryResolutionStatus Status);

public sealed record SnapshotRepositoryResolution(string Location, RepositoryResolution Resolution);
