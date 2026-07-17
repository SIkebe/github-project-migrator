using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Import;

/// <summary>
/// Structurally maps identity-bearing GitHub Projects filter qualifier values while
/// preserving all other text exactly.
/// </summary>
public static partial class ProjectFilterTransformer
{
    private static readonly HashSet<string> UserQualifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "assignee",
        "author",
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
        var builder = new StringBuilder(filter.Length);
        var lastIndex = 0;

        foreach (Match match in QualifierValueRegex().Matches(filter))
        {
            builder.Append(filter, lastIndex, match.Index - lastIndex);
            builder.Append(match.Groups["qualifier"].Value).Append(':');

            var rawValue = match.Groups["value"].Value;
            var quoted = rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[^1] == '"';
            var value = quoted ? rawValue[1..^1] : rawValue;
            var qualifier = match.Groups["qualifier"].Value;
            var mapping = MappingFor(qualifier, userMapping, repositoryMapping, organizationMapping);

            if (mapping is null)
            {
                builder.Append(rawValue);
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
                    if (mapping.TryGetValue(item, out var mapped))
                    {
                        builder.Append(mapped);
                        if (!string.Equals(item, mapped, StringComparison.Ordinal))
                        {
                            changes.Add(new FilterTokenChange(qualifier, item, mapped));
                        }
                    }
                    else
                    {
                        builder.Append(item);
                        if (IsMappingRequired(item))
                        {
                            unresolved.Add(new FilterIdentifier(qualifier, item));
                        }
                    }
                }

                if (quoted)
                {
                    builder.Append('"');
                }
            }

            lastIndex = match.Index + match.Length;
        }

        builder.Append(filter, lastIndex, filter.Length - lastIndex);
        return new FilterTransformResult(filter, builder.ToString(), changes, unresolved);
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

    /// <summary>Maps an Auto-add repository short name only when the target is unambiguous.</summary>
    public static string ResolveRepositoryName(
        string sourceRepository,
        IReadOnlyDictionary<string, string> repositoryMapping)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRepository);
        ArgumentNullException.ThrowIfNull(repositoryMapping);

        var targets = repositoryMapping
            .Where(pair => string.Equals(ShortName(pair.Key), sourceRepository, StringComparison.OrdinalIgnoreCase))
            .Select(pair => ShortName(pair.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return targets.Count == 1 ? targets[0] : sourceRepository;

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

    private static bool IsMappingRequired(string value)
        => value.Length > 0
            && value[0] != '@'
            && !string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"(?<qualifier>[A-Za-z][A-Za-z0-9_-]*):(?<value>""(?:\\.|[^""])*""|[^\s()]+)", RegexOptions.CultureInvariant)]
    private static partial Regex QualifierValueRegex();
}

public sealed record FilterIdentifier(string Qualifier, string Value);

public sealed record FilterTokenChange(string Qualifier, string Source, string Target);

public sealed record FilterTransformResult(
    string Original,
    string Transformed,
    IReadOnlyList<FilterTokenChange> Changes,
    IReadOnlyList<FilterIdentifier> Unresolved);

public sealed record SnapshotFilterTransform(string Location, FilterTransformResult Result);
