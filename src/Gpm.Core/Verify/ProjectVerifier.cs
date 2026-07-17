using System.Collections.ObjectModel;
using System.Globalization;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Verify;

/// <summary>
/// Verifies a migrated project against its source snapshot (M5). The target project is
/// read back through <see cref="ProjectExporter"/> and compared with the snapshot:
/// project metadata (title excluded — it may be changed on import), fields (options,
/// iterations), views/workflows (name-based; name/layout/enabled differences are errors
/// since M6/M7 migrate them, browser-scraped <c>Ui</c> details are compared as warnings
/// when both sides carry them) and items (counts, per-type counts, field values, order,
/// archived state). Draft bodies are compared with the import attribution note stripped.
/// </summary>
public sealed class ProjectVerifier
{
    private const string ProjectCategory = "Project";
    private const string FieldCategory = "Field";
    private const string ViewCategory = "View";
    private const string WorkflowCategory = "Workflow";
    private const string ItemCategory = "Item";

    private readonly GitHubGraphQLClient _client;

    public ProjectVerifier(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Invoked with a human-readable progress message while reading the target project.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Owner type of the target project: organization (default) or user.</summary>
    public ProjectOwnerType OwnerType { get; init; } = ProjectOwnerType.Organization;

    /// <summary>Source → target repository mapping ("owner/name" form), used to normalize the source snapshot before comparison.</summary>
    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Source → target user mapping, used to normalize explicit user collaborators before comparison.</summary>
    public IReadOnlyDictionary<string, string> UserMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// Optional post-processing hook for the target snapshot. Browser-assisted verification
    /// uses this to re-read UI-only settings before comparison.
    /// </summary>
    public Func<ProjectSnapshot, CancellationToken, Task<ProjectSnapshot>>? PostExportAsync { get; set; }

    /// <summary>Exports the target project and compares it against <paramref name="source"/>.</summary>
    public async Task<VerifyReport> VerifyAsync(ProjectSnapshot source, string targetOrgLogin, int targetProjectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetOrgLogin);

        var exporter = new ProjectExporter(_client)
        {
            OnProgress = OnProgress,
            OwnerType = OwnerType,
            PostExportAsync = PostExportAsync,
        };
        var target = await exporter.ExportAsync(targetOrgLogin, targetProjectNumber, cancellationToken).ConfigureAwait(false);
        return Compare(source, target, RepositoryMapping, UserMapping);
    }

    /// <summary>Pure snapshot-to-snapshot comparison (no API access).</summary>
    public static VerifyReport Compare(ProjectSnapshot source, ProjectSnapshot target)
        => Compare(source, target, ReadOnlyDictionary<string, string>.Empty);

    /// <summary>Pure snapshot-to-snapshot comparison (no API access), with source repository names normalized through a mapping.</summary>
    public static VerifyReport Compare(ProjectSnapshot source, ProjectSnapshot target, IReadOnlyDictionary<string, string> repositoryMapping)
        => Compare(source, target, repositoryMapping, ReadOnlyDictionary<string, string>.Empty);

    /// <summary>Pure snapshot comparison with source repository and user collaborator names normalized through mappings.</summary>
    public static VerifyReport Compare(
        ProjectSnapshot source,
        ProjectSnapshot target,
        IReadOnlyDictionary<string, string> repositoryMapping,
        IReadOnlyDictionary<string, string> userMapping)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(repositoryMapping);
        ArgumentNullException.ThrowIfNull(userMapping);

        if (repositoryMapping.Count > 0)
        {
            source = ApplyRepositoryMapping(source, repositoryMapping);
        }

        if (userMapping.Count > 0)
        {
            source = ApplyUserMapping(source, userMapping);
        }

        var differences = new List<VerifyDifference>();
        CompareProject(source.Project, target.Project, differences);
        CompareFields(source.Fields, target.Fields, differences);
        CompareViews(source.Views, target.Views, differences);
        CompareWorkflows(source.Workflows, target.Workflows, differences);
        CompareItems(source.Items, target.Items, differences);
        CompareCollaborators(source.Collaborators, target.Collaborators, differences);
        CompareLinkedRepositories(source.LinkedRepositories, target.LinkedRepositories, differences);
        return new VerifyReport { Differences = differences };
    }

    private static ProjectSnapshot ApplyRepositoryMapping(ProjectSnapshot source, IReadOnlyDictionary<string, string> repositoryMapping)
    {
        return source with
        {
            Items = source.Items.Select(item => item.Repository is { Length: > 0 } repository
                    && repositoryMapping.TryGetValue(repository, out var mappedRepository)
                ? item with { Repository = mappedRepository }
                : item).ToList(),
            LinkedRepositories = source.LinkedRepositories?.Select(repository => repositoryMapping.TryGetValue(repository, out var mappedRepository)
                ? mappedRepository
                : repository).ToList(),
            Workflows = source.Workflows.Select(workflow => workflow.Ui?.Repository is { Length: > 0 } repository
                ? workflow with { Ui = workflow.Ui with { Repository = ResolveRepositoryName(repository, repositoryMapping) } }
                : workflow).ToList(),
        };
    }

    private static ProjectSnapshot ApplyUserMapping(ProjectSnapshot source, IReadOnlyDictionary<string, string> userMapping)
    {
        return source with
        {
            Collaborators = source.Collaborators?.Select(collaborator =>
                string.Equals(collaborator.Type, "USER", StringComparison.OrdinalIgnoreCase)
                && userMapping.TryGetValue(collaborator.Login, out var mappedLogin)
                    ? collaborator with { Login = mappedLogin }
                    : collaborator).ToList(),
        };
    }

    private static string ResolveRepositoryName(string sourceRepository, IReadOnlyDictionary<string, string> mapping)
    {
        foreach (var (source, target) in mapping)
        {
            if (string.Equals(ShortName(source), sourceRepository, StringComparison.OrdinalIgnoreCase))
            {
                return ShortName(target);
            }
        }

        return sourceRepository;

        static string ShortName(string repository)
        {
            var separator = repository.LastIndexOf('/');
            return separator < 0 ? repository : repository[(separator + 1)..];
        }
    }

    // ----- project metadata -----

    private static void CompareProject(ProjectInfoSnapshot source, ProjectInfoSnapshot target, List<VerifyDifference> differences)
    {
        // The title may legitimately be changed on import, so it is informational only.
        if (!TextEquals(source.Title, target.Title))
        {
            Add(differences, VerifySeverity.Info, ProjectCategory,
                $"title differs (source '{source.Title}', target '{target.Title}') — titles may be changed on import");
        }

        if (!TextEquals(source.ShortDescription, target.ShortDescription))
        {
            AddError(differences, ProjectCategory, "short description mismatch");
        }

        if (!TextEquals(NormalizeBody(source.Readme), NormalizeBody(target.Readme)))
        {
            AddError(differences, ProjectCategory, "README mismatch");
        }

        if (source.Public != target.Public)
        {
            AddError(differences, ProjectCategory, string.Create(CultureInfo.InvariantCulture,
                $"visibility mismatch (source public={source.Public}, target public={target.Public})"));
        }

        if (source.Closed != target.Closed)
        {
            AddError(differences, ProjectCategory, string.Create(CultureInfo.InvariantCulture,
                $"closed state mismatch (source closed={source.Closed}, target closed={target.Closed})"));
        }
    }

    // ----- fields -----

    private static void CompareFields(IReadOnlyList<FieldSnapshot> source, IReadOnlyList<FieldSnapshot> target, List<VerifyDifference> differences)
    {
        var targetByName = new Dictionary<string, FieldSnapshot>(StringComparer.Ordinal);
        foreach (var field in target)
        {
            targetByName.TryAdd(field.Name, field);
        }

        foreach (var field in source)
        {
            if (!targetByName.TryGetValue(field.Name, out var other))
            {
                AddError(differences, FieldCategory, $"field '{field.Name}' ({field.DataType}) is missing in the target");
                continue;
            }

            if (!string.Equals(field.DataType, other.DataType, StringComparison.Ordinal))
            {
                AddError(differences, FieldCategory,
                    $"field '{field.Name}': data type mismatch (source {field.DataType}, target {other.DataType})");
                continue;
            }

            CompareOptions(field, other, differences);
            CompareIterations(field, other, differences);
        }

        var sourceNames = source.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var extra in target.Where(f => !sourceNames.Contains(f.Name)))
        {
            Add(differences, VerifySeverity.Warning, FieldCategory,
                $"field '{extra.Name}' ({extra.DataType}) exists only in the target");
        }
    }

    private static void CompareOptions(FieldSnapshot source, FieldSnapshot target, List<VerifyDifference> differences)
    {
        var sourceOptions = source.Options ?? [];
        var targetOptions = target.Options ?? [];

        if (sourceOptions.Count != targetOptions.Count)
        {
            AddError(differences, FieldCategory, string.Create(CultureInfo.InvariantCulture,
                $"field '{source.Name}': option count mismatch (source {sourceOptions.Count}, target {targetOptions.Count})"));
        }

        for (var i = 0; i < Math.Min(sourceOptions.Count, targetOptions.Count); i++)
        {
            var s = sourceOptions[i];
            var t = targetOptions[i];
            var position = string.Create(CultureInfo.InvariantCulture, $"field '{source.Name}' option #{i + 1}");

            if (!string.Equals(s.Name, t.Name, StringComparison.Ordinal))
            {
                AddError(differences, FieldCategory, $"{position}: name mismatch (source '{s.Name}', target '{t.Name}') — option order and names must match");
                continue;
            }

            if (!string.Equals(s.Color, t.Color, StringComparison.Ordinal))
            {
                AddError(differences, FieldCategory, $"{position} ('{s.Name}'): color mismatch (source {s.Color}, target {t.Color})");
            }

            if (!TextEquals(s.Description, t.Description))
            {
                AddError(differences, FieldCategory, $"{position} ('{s.Name}'): description mismatch");
            }
        }
    }

    private static void CompareIterations(FieldSnapshot source, FieldSnapshot target, List<VerifyDifference> differences)
    {
        if (source.IterationConfiguration is null && target.IterationConfiguration is null)
        {
            return;
        }

        // Completed/active classification depends on the current date, so iterations are
        // matched purely by title across both lists.
        var sourceIterations = MergeIterations(source.IterationConfiguration);
        var targetIterations = MergeIterations(target.IterationConfiguration);

        foreach (var (title, s) in sourceIterations)
        {
            if (!targetIterations.TryGetValue(title, out var t))
            {
                AddError(differences, FieldCategory, $"field '{source.Name}': iteration '{title}' is missing in the target");
                continue;
            }

            if (!string.Equals(s.StartDate, t.StartDate, StringComparison.Ordinal))
            {
                AddError(differences, FieldCategory,
                    $"field '{source.Name}' iteration '{title}': start date mismatch (source {s.StartDate}, target {t.StartDate})");
            }

            if (s.Duration != t.Duration)
            {
                AddError(differences, FieldCategory, string.Create(CultureInfo.InvariantCulture,
                    $"field '{source.Name}' iteration '{title}': duration mismatch (source {s.Duration}, target {t.Duration})"));
            }
        }

        foreach (var title in targetIterations.Keys.Where(k => !sourceIterations.ContainsKey(k)))
        {
            AddError(differences, FieldCategory, $"field '{source.Name}': iteration '{title}' exists only in the target");
        }
    }

    private static Dictionary<string, IterationSnapshot> MergeIterations(IterationConfigurationSnapshot? configuration)
    {
        var merged = new Dictionary<string, IterationSnapshot>(StringComparer.Ordinal);
        if (configuration is null)
        {
            return merged;
        }

        foreach (var iteration in configuration.Iterations.Concat(configuration.CompletedIterations))
        {
            merged.TryAdd(iteration.Title, iteration);
        }

        return merged;
    }

    // ----- collaborators / linked repositories (warnings: the API has no collaborator
    // read field, and linked repositories may be intentionally remapped on import) -----

    private static void CompareCollaborators(IReadOnlyList<CollaboratorSnapshot>? source, IReadOnlyList<CollaboratorSnapshot>? target, List<VerifyDifference> differences)
    {
        // Null means "not captured" (exports always leave collaborators null because the
        // GraphQL API has no read field for them); only compare when both sides carry data.
        if (source is null || target is null)
        {
            return;
        }

        var targetByKey = target.ToDictionary(CollaboratorKey, c => c, StringComparer.OrdinalIgnoreCase);
        foreach (var collaborator in source)
        {
            if (!targetByKey.TryGetValue(CollaboratorKey(collaborator), out var other))
            {
                Add(differences, VerifySeverity.Warning, ProjectCategory,
                    $"collaborator {Describe(collaborator)} is missing in the target");
            }
            else if (!string.Equals(collaborator.Role, other.Role, StringComparison.OrdinalIgnoreCase))
            {
                Add(differences, VerifySeverity.Warning, ProjectCategory,
                    $"collaborator {Describe(collaborator)}: role mismatch (source {collaborator.Role}, target {other.Role})");
            }
        }

        var sourceKeys = source.Select(CollaboratorKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in target.Where(c => !sourceKeys.Contains(CollaboratorKey(c))))
        {
            Add(differences, VerifySeverity.Warning, ProjectCategory,
                $"collaborator {Describe(extra)} exists only in the target");
        }
    }

    private static string CollaboratorKey(CollaboratorSnapshot collaborator)
        => collaborator.Type.ToUpperInvariant() + ":" + collaborator.Login;

    private static string Describe(CollaboratorSnapshot collaborator)
        => $"{collaborator.Type.ToUpperInvariant()} '{collaborator.Login}'";

    private static void CompareLinkedRepositories(IReadOnlyList<string>? source, IReadOnlyList<string>? target, List<VerifyDifference> differences)
    {
        // Null means the snapshot predates linked-repository capture; nothing to compare.
        if (source is null)
        {
            return;
        }

        var targetSet = (target ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var repository in source.Where(r => !targetSet.Contains(r)))
        {
            Add(differences, VerifySeverity.Warning, ProjectCategory,
                $"linked repository '{repository}' is missing in the target (it may be intentionally remapped)");
        }

        var sourceSet = source.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in (target ?? []).Where(r => !sourceSet.Contains(r)))
        {
            Add(differences, VerifySeverity.Warning, ProjectCategory,
                $"linked repository '{extra}' exists only in the target");
        }
    }

    // ----- views / workflows (errors since M6/M7 migrate them; UI details stay warnings) -----

    private static void CompareViews(IReadOnlyList<ViewSnapshot> source, IReadOnlyList<ViewSnapshot> target, List<VerifyDifference> differences)
    {
        foreach (var name in Names(source.Select(v => v.Name), target.Select(v => v.Name)))
        {
            var s = source.Where(v => string.Equals(v.Name, name, StringComparison.Ordinal)).ToList();
            var t = target.Where(v => string.Equals(v.Name, name, StringComparison.Ordinal)).ToList();

            if (s.Count == 0)
            {
                AddError(differences, ViewCategory, $"view '{name}' exists only in the target");
            }
            else if (t.Count == 0)
            {
                AddError(differences, ViewCategory, $"view '{name}' is missing in the target");
            }
            else if (s.Count != t.Count)
            {
                AddError(differences, ViewCategory, string.Create(CultureInfo.InvariantCulture,
                    $"view '{name}': count mismatch (source {s.Count}, target {t.Count})"));
            }
            else if (!s.Select(v => v.Layout).Order(StringComparer.Ordinal)
                .SequenceEqual(t.Select(v => v.Layout).Order(StringComparer.Ordinal), StringComparer.Ordinal))
            {
                AddError(differences, ViewCategory,
                    $"view '{name}': layout mismatch (source {string.Join(", ", s.Select(v => v.Layout))}, target {string.Join(", ", t.Select(v => v.Layout))})");
            }
            else if (s.Count == 1 && t.Count == 1 && s[0].Ui is { } sourceUi && t[0].Ui is { } targetUi)
            {
                // Both sides carry browser-scraped UI settings (M6): compare them too.
                // UI details remain warnings (scrape granularity can differ between runs).
                CompareViewUi(name, sourceUi, targetUi, differences);
            }
        }
    }

    private static void CompareViewUi(string name, ViewUiSnapshot source, ViewUiSnapshot target, List<VerifyDifference> differences)
    {
        CompareUiValue(differences, name, "group by", source.GroupBy, target.GroupBy);
        CompareUiValue(differences, name, "sort by", source.SortBy, target.SortBy);
        CompareUiValue(differences, name, "slice by", source.SliceBy, target.SliceBy);
        CompareUiValue(differences, name, "swimlanes", source.Swimlanes, target.Swimlanes);
        if (!UiListEquals(source.FieldSum, target.FieldSum))
        {
            Add(differences, VerifySeverity.Warning, ViewCategory,
                $"view '{name}': field sum mismatch (source [{JoinUi(source.FieldSum)}], target [{JoinUi(target.FieldSum)}])");
        }

        if ((source.Roadmap is null) != (target.Roadmap is null))
        {
            Add(differences, VerifySeverity.Warning, ViewCategory,
                $"view '{name}': roadmap settings are present on only one side");
        }
        else if (source.Roadmap is { } sourceRoadmap && target.Roadmap is { } targetRoadmap)
        {
            CompareUiValue(differences, name, "roadmap start date", sourceRoadmap.StartField, targetRoadmap.StartField);
            CompareUiValue(differences, name, "roadmap target date", sourceRoadmap.TargetField, targetRoadmap.TargetField);
            CompareUiValue(differences, name, "zoom level", sourceRoadmap.Zoom, targetRoadmap.Zoom);
            if (!UiListEquals(sourceRoadmap.Markers, targetRoadmap.Markers))
            {
                Add(differences, VerifySeverity.Warning, ViewCategory,
                    $"view '{name}': markers mismatch (source [{JoinUi(sourceRoadmap.Markers)}], target [{JoinUi(targetRoadmap.Markers)}])");
            }
        }
    }

    private static void CompareUiValue(List<VerifyDifference> differences, string viewName, string setting, string? source, string? target)
    {
        if (!string.Equals(source, target, StringComparison.Ordinal))
        {
            Add(differences, VerifySeverity.Warning, ViewCategory,
                $"view '{viewName}': {setting} mismatch (source '{source ?? "none"}', target '{target ?? "none"}')");
        }
    }

    private static bool UiListEquals(IReadOnlyList<string>? source, IReadOnlyList<string>? target)
        => (source ?? []).SequenceEqual(target ?? [], StringComparer.Ordinal);

    private static string JoinUi(IReadOnlyList<string>? values) => string.Join(", ", values ?? []);

    private static void CompareWorkflows(IReadOnlyList<WorkflowSnapshot> source, IReadOnlyList<WorkflowSnapshot> target, List<VerifyDifference> differences)
    {
        foreach (var name in Names(source.Select(w => w.Name), target.Select(w => w.Name)))
        {
            var s = source.Where(w => string.Equals(w.Name, name, StringComparison.Ordinal)).ToList();
            var t = target.Where(w => string.Equals(w.Name, name, StringComparison.Ordinal)).ToList();

            if (s.Count == 0)
            {
                AddError(differences, WorkflowCategory, $"workflow '{name}' exists only in the target");
            }
            else if (t.Count == 0)
            {
                AddError(differences, WorkflowCategory, $"workflow '{name}' is missing in the target");
            }
            else if (s.Count != t.Count)
            {
                AddError(differences, WorkflowCategory, string.Create(CultureInfo.InvariantCulture,
                    $"workflow '{name}': count mismatch (source {s.Count}, target {t.Count})"));
            }
            else if (!s.Select(w => w.Enabled).Order().SequenceEqual(t.Select(w => w.Enabled).Order()))
            {
                AddError(differences, WorkflowCategory,
                    $"workflow '{name}': enabled state mismatch (source {string.Join(", ", s.Select(w => w.Enabled))}, target {string.Join(", ", t.Select(w => w.Enabled))})");
            }
            else if (s.Count == 1 && t.Count == 1 && s[0].Ui is { } sourceUi && t[0].Ui is { } targetUi)
            {
                // Both sides carry browser-scraped UI settings (M7): compare them too.
                CompareWorkflowUi(name, sourceUi, targetUi, differences);
            }
        }
    }

    private static void CompareWorkflowUi(string name, WorkflowUiSnapshot source, WorkflowUiSnapshot target, List<VerifyDifference> differences)
    {
        if (!UiListEquals(source.ContentTypes, target.ContentTypes))
        {
            Add(differences, VerifySeverity.Warning, WorkflowCategory,
                $"workflow '{name}': content types mismatch (source [{JoinUi(source.ContentTypes)}], target [{JoinUi(target.ContentTypes)}])");
        }

        CompareWorkflowUiValue(differences, name, "status value", source.StatusValue, target.StatusValue);
        CompareWorkflowUiValue(differences, name, "filter", source.Filter, target.Filter);
        CompareWorkflowUiValue(differences, name, "repository", source.Repository, target.Repository);
    }

    private static void CompareWorkflowUiValue(List<VerifyDifference> differences, string workflowName, string setting, string? source, string? target)
    {
        if (!string.Equals(source, target, StringComparison.Ordinal))
        {
            Add(differences, VerifySeverity.Warning, WorkflowCategory,
                $"workflow '{workflowName}': {setting} mismatch (source '{source ?? "none"}', target '{target ?? "none"}')");
        }
    }

    private static IEnumerable<string> Names(IEnumerable<string> source, IEnumerable<string> target)
        => source.Concat(target).Distinct(StringComparer.Ordinal);

    // ----- items -----

    private static void CompareItems(IReadOnlyList<ItemSnapshot> source, IReadOnlyList<ItemSnapshot> target, List<VerifyDifference> differences)
    {
        var sourceOrdered = source.OrderBy(i => i.Position).ToList();
        var targetOrdered = target.OrderBy(i => i.Position).ToList();

        if (sourceOrdered.Count != targetOrdered.Count)
        {
            AddError(differences, ItemCategory, string.Create(CultureInfo.InvariantCulture,
                $"item count mismatch (source {sourceOrdered.Count}, target {targetOrdered.Count})"));
        }

        foreach (var type in Names(sourceOrdered.Select(i => i.Type), targetOrdered.Select(i => i.Type)))
        {
            var sourceCount = sourceOrdered.Count(i => string.Equals(i.Type, type, StringComparison.Ordinal));
            var targetCount = targetOrdered.Count(i => string.Equals(i.Type, type, StringComparison.Ordinal));
            if (sourceCount != targetCount)
            {
                AddError(differences, ItemCategory, string.Create(CultureInfo.InvariantCulture,
                    $"item count for type {type} mismatch (source {sourceCount}, target {targetCount})"));
            }
        }

        var sourceGroups = GroupByKey(sourceOrdered);
        var targetGroups = GroupByKey(targetOrdered);

        foreach (var (key, items) in sourceGroups)
        {
            if (!targetGroups.TryGetValue(key, out var targetItems))
            {
                AddError(differences, ItemCategory, $"{key} is missing in the target");
                continue;
            }

            if (items.Count != targetItems.Count)
            {
                AddError(differences, ItemCategory, string.Create(CultureInfo.InvariantCulture,
                    $"{key}: occurrence count mismatch (source {items.Count}, target {targetItems.Count})"));
            }

            for (var i = 0; i < Math.Min(items.Count, targetItems.Count); i++)
            {
                CompareItemPair(items[i], targetItems[i], key, differences);
            }
        }

        foreach (var key in targetGroups.Keys.Where(k => !sourceGroups.ContainsKey(k)))
        {
            AddError(differences, ItemCategory, $"{key} exists only in the target");
        }

        // The order is only comparable when both sides contain the same items. Archived
        // items are excluded: updateProjectV2ItemPosition cannot move them, so import
        // cannot restore their position and the API enumerates them wherever it likes.
        var sameMultiset = sourceOrdered.Count == targetOrdered.Count
            && sourceGroups.Count == targetGroups.Count
            && sourceGroups.All(g => targetGroups.TryGetValue(g.Key, out var t) && t.Count == g.Value.Count);
        if (sameMultiset)
        {
            var sourceActive = sourceOrdered.Where(i => !i.IsArchived).ToList();
            var targetActive = targetOrdered.Where(i => !i.IsArchived).ToList();
            for (var i = 0; i < Math.Min(sourceActive.Count, targetActive.Count); i++)
            {
                var sourceKey = ItemKey(sourceActive[i]);
                var targetKey = ItemKey(targetActive[i]);
                if (!string.Equals(sourceKey, targetKey, StringComparison.Ordinal))
                {
                    AddError(differences, ItemCategory, string.Create(CultureInfo.InvariantCulture,
                        $"item order mismatch at position {i}: source has {sourceKey}, target has {targetKey}"));
                    break;
                }
            }
        }
    }

    private static void CompareItemPair(ItemSnapshot source, ItemSnapshot target, string key, List<VerifyDifference> differences)
    {
        if (source.IsArchived != target.IsArchived)
        {
            AddError(differences, ItemCategory, string.Create(CultureInfo.InvariantCulture,
                $"{key}: archived state mismatch (source {source.IsArchived}, target {target.IsArchived})"));
        }

        CompareFieldValues(source, target, key, differences);

        if (source.Draft is not null && target.Draft is not null)
        {
            // Import prepends an attribution note to draft bodies; strip it on both sides.
            var sourceBody = StripAttributionNote(NormalizeBody(source.Draft.Body));
            var targetBody = StripAttributionNote(NormalizeBody(target.Draft.Body));
            if (!string.Equals(sourceBody, targetBody, StringComparison.Ordinal))
            {
                AddError(differences, ItemCategory, $"{key}: draft body mismatch (attribution note excluded)");
            }
        }
    }

    private static void CompareFieldValues(ItemSnapshot source, ItemSnapshot target, string key, List<VerifyDifference> differences)
    {
        var sourceValues = ToValueMap(source.FieldValues);
        var targetValues = ToValueMap(target.FieldValues);

        foreach (var name in Names(sourceValues.Keys, targetValues.Keys))
        {
            sourceValues.TryGetValue(name, out var s);
            targetValues.TryGetValue(name, out var t);
            if (!string.Equals(s ?? string.Empty, t ?? string.Empty, StringComparison.Ordinal))
            {
                AddError(differences, ItemCategory,
                    $"{key}: field '{name}' value mismatch (source {Display(s)}, target {Display(t)})");
            }
        }
    }

    private static Dictionary<string, string> ToValueMap(IReadOnlyList<FieldValueSnapshot> values)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var formatted = FormatValue(value);
            if (!string.IsNullOrEmpty(formatted))
            {
                map.TryAdd(value.FieldName, formatted);
            }
        }

        return map;
    }

    private static string? FormatValue(FieldValueSnapshot value)
        => value.Text
            ?? value.Date
            ?? value.SingleSelectOptionName
            ?? value.IterationTitle
            ?? value.Number?.ToString("R", CultureInfo.InvariantCulture);

    private static string Display(string? value) => value is null ? "(none)" : $"'{value}'";

    private static Dictionary<string, List<ItemSnapshot>> GroupByKey(List<ItemSnapshot> items)
    {
        var groups = new Dictionary<string, List<ItemSnapshot>>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = ItemKey(item);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(item);
        }

        return groups;
    }

    private static string ItemKey(ItemSnapshot item) => item.Type == "DRAFT_ISSUE"
        ? $"draft '{item.Draft?.Title}'"
        : string.Create(CultureInfo.InvariantCulture, $"item {item.Type} {item.Repository}#{item.Number}");

    /// <summary>Strips the attribution note that <c>ItemImporter.BuildDraftBody</c> prepends on import.</summary>
    private static string StripAttributionNote(string body)
    {
        if (!body.StartsWith("> _Originally created", StringComparison.Ordinal))
        {
            return body;
        }

        var separator = body.IndexOf("\n\n", StringComparison.Ordinal);
        return separator < 0 ? string.Empty : body[(separator + 2)..];
    }

    /// <summary>Normalizes line endings and trailing whitespace; null and empty are equivalent.</summary>
    private static string NormalizeBody(string? body)
        => (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    private static bool TextEquals(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static void AddError(List<VerifyDifference> differences, string category, string message)
        => Add(differences, VerifySeverity.Error, category, message);

    private static void Add(List<VerifyDifference> differences, VerifySeverity severity, string category, string message)
        => differences.Add(new VerifyDifference { Severity = severity, Category = category, Message = message });
}
