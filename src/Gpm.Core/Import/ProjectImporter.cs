using System.Globalization;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Import;

/// <summary>
/// Imports a <see cref="ProjectSnapshot"/> into a target organization (M3):
/// creates the project, applies metadata (README, description, visibility, closed state),
/// creates all custom fields (TEXT/NUMBER/DATE/SINGLE_SELECT/ITERATION) and overwrites
/// the built-in Status field options with the snapshot's options.
/// Completed iterations are recreated as past-dated iterations; the API accepts past
/// start dates and reclassifies them into <c>completedIterations</c> on read (verified by PoC).
/// </summary>
public sealed class ProjectImporter
{
    private const string StatusFieldName = "Status";

    /// <summary>Data types that <c>createProjectV2Field</c> supports; everything else is a built-in field.</summary>
    private static readonly HashSet<string> CreatableDataTypes =
        new(["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION"], StringComparer.Ordinal);

    private readonly GitHubGraphQLClient _client;

    public ProjectImporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Behavior when the target organization already has a project with the snapshot's title.</summary>
    public ConflictAction OnConflict { get; init; } = ConflictAction.Fail;

    /// <summary>Invoked with a human-readable progress message at each import stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Imports the snapshot into <paramref name="orgLogin"/> and returns the target project identity and field mappings.</summary>
    public async Task<ImportResult> ImportAsync(ProjectSnapshot snapshot, string orgLogin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(orgLogin);

        var title = snapshot.Project.Title;
        OnProgress?.Invoke($"Checking organization '{orgLogin}' for an existing project titled '{title}'...");
        var orgId = await GetOrganizationIdAsync(orgLogin, cancellationToken).ConfigureAwait(false);
        var existing = await FindProjectByTitleAsync(orgLogin, title, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            switch (OnConflict)
            {
                case ConflictAction.Fail:
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"A project titled '{title}' already exists in organization '{orgLogin}' (#{existing.Number}). Use --on-conflict skip or update to proceed."));

                case ConflictAction.Skip:
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); skipping (on-conflict=skip)."));
                    return await BuildResultForExistingAsync(existing, created: false, cancellationToken).ConfigureAwait(false);

                case ConflictAction.Update:
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); applying snapshot to it (on-conflict=update)."));
                    return await ApplySnapshotAsync(snapshot, existing, created: false, cancellationToken).ConfigureAwait(false);
            }
        }

        OnProgress?.Invoke($"Creating project '{title}' in '{orgLogin}'...");
        var project = await CreateProjectAsync(orgId, title, cancellationToken).ConfigureAwait(false);
        return await ApplySnapshotAsync(snapshot, project, created: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Applies metadata, custom fields and Status options to the target project and builds the result.</summary>
    private async Task<ImportResult> ApplySnapshotAsync(ProjectSnapshot snapshot, ProjectRef project, bool created, CancellationToken cancellationToken)
    {
        OnProgress?.Invoke("Applying project metadata (description, README, visibility, closed state)...");
        await UpdateProjectMetadataAsync(project.Id, snapshot.Project, cancellationToken).ConfigureAwait(false);

        var maps = new FieldMaps();
        var existingFields = await FetchFieldsAsync(project.Id, maps, cancellationToken).ConfigureAwait(false);

        foreach (var field in snapshot.Fields)
        {
            if (!CreatableDataTypes.Contains(field.DataType))
            {
                continue; // Built-in field (Title, Assignees, Labels, Repository, Milestone, Reviewers, ...).
            }

            if (existingFields.TryGetValue(field.Name, out var target))
            {
                if (!string.Equals(target.DataType, field.DataType, StringComparison.Ordinal))
                {
                    OnProgress?.Invoke($"warning: field '{field.Name}' exists with data type {target.DataType} (snapshot: {field.DataType}); leaving it unchanged.");
                }
                else if (field.DataType == "SINGLE_SELECT" && field.Options is { Count: > 0 })
                {
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Overwriting options of existing field '{field.Name}' with {field.Options.Count} snapshot options..."));
                    await UpdateSingleSelectOptionsAsync(target.Id, field.Options, maps, cancellationToken).ConfigureAwait(false);
                }
                else if (field.DataType == "ITERATION")
                {
                    OnProgress?.Invoke($"warning: iteration field '{field.Name}' already exists; iterations are not merged, leaving it unchanged.");
                }
                else
                {
                    OnProgress?.Invoke($"Field '{field.Name}' already exists; skipping.");
                }
            }
            else
            {
                OnProgress?.Invoke($"Creating {field.DataType} field '{field.Name}'...");
                await CreateFieldAsync(project.Id, field, maps, cancellationToken).ConfigureAwait(false);
            }
        }

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Import finished: project #{project.Number}, {maps.FieldIds.Count} fields mapped."));
        return maps.ToResult(project, created);
    }

    /// <summary>Skip path: reads the existing project's fields to build the mappings without modifying anything.</summary>
    private async Task<ImportResult> BuildResultForExistingAsync(ProjectRef project, bool created, CancellationToken cancellationToken)
    {
        var maps = new FieldMaps();
        await FetchFieldsAsync(project.Id, maps, cancellationToken).ConfigureAwait(false);
        return maps.ToResult(project, created);
    }

    private async Task<string> GetOrganizationIdAsync(string orgLogin, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = orgLogin },
            cancellationToken).ConfigureAwait(false);

        return data.GetProperty("organization").GetProperty("id").GetString()
            ?? throw new GitHubGraphQLException($"Organization '{orgLogin}' was not found.");
    }

    private async Task<ProjectRef?> FindProjectByTitleAsync(string orgLogin, string title, CancellationToken cancellationToken)
    {
        await foreach (var node in _client.QueryPaginatedAsync(
            FindProjectQuery,
            new { login = orgLogin, first = 50 },
            "organization.projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                return ParseProjectRef(node);
            }
        }

        return null;
    }

    private async Task<ProjectRef> CreateProjectAsync(string ownerId, string title, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            """
            mutation($ownerId: ID!, $title: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                projectV2 { id number title url }
              }
            }
            """,
            new { ownerId, title },
            cancellationToken).ConfigureAwait(false);

        return ParseProjectRef(data.GetProperty("createProjectV2").GetProperty("projectV2"));
    }

    private async Task UpdateProjectMetadataAsync(string projectId, ProjectInfoSnapshot info, CancellationToken cancellationToken)
    {
        await _client.QueryAsync(
            """
            mutation($projectId: ID!, $shortDescription: String, $readme: String, $public: Boolean, $closed: Boolean) {
              updateProjectV2(input: { projectId: $projectId, shortDescription: $shortDescription, readme: $readme, public: $public, closed: $closed }) {
                projectV2 { id }
              }
            }
            """,
            new
            {
                projectId,
                shortDescription = info.ShortDescription,
                readme = info.Readme,
                @public = info.Public,
                closed = info.Closed,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches the target project's fields, registers them in the maps and returns them by name.</summary>
    private async Task<Dictionary<string, TargetField>> FetchFieldsAsync(string projectId, FieldMaps maps, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(FieldsQuery, new { id = projectId }, cancellationToken).ConfigureAwait(false);

        var fields = new Dictionary<string, TargetField>(StringComparer.Ordinal);
        foreach (var node in data.GetProperty("node").GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            var field = maps.Register(node);
            fields[field.Name] = field;
        }

        return fields;
    }

    private async Task CreateFieldAsync(string projectId, FieldSnapshot field, FieldMaps maps, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            CreateFieldMutation,
            new
            {
                projectId,
                name = field.Name,
                dataType = field.DataType,
                options = field.DataType == "SINGLE_SELECT" ? BuildOptionInputs(field.Options ?? []) : null,
                iterationConfiguration = field.DataType == "ITERATION" && field.IterationConfiguration is { } configuration
                    ? BuildIterationConfigurationInput(field.Name, configuration)
                    : null,
            },
            cancellationToken).ConfigureAwait(false);

        maps.Register(data.GetProperty("createProjectV2Field").GetProperty("projectV2Field"));
    }

    private async Task UpdateSingleSelectOptionsAsync(string fieldId, IReadOnlyList<SingleSelectOptionSnapshot> options, FieldMaps maps, CancellationToken cancellationToken)
    {
        var data = await _client.QueryAsync(
            """
            mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]!) {
              updateProjectV2Field(input: { fieldId: $fieldId, singleSelectOptions: $options }) {
                projectV2Field {
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                }
              }
            }
            """,
            new { fieldId, options = BuildOptionInputs(options) },
            cancellationToken).ConfigureAwait(false);

        maps.Register(data.GetProperty("updateProjectV2Field").GetProperty("projectV2Field"));
    }

    /// <summary>Builds option inputs without ids so the target issues fresh option IDs (PLAN §1.2).</summary>
    private static object[] BuildOptionInputs(IReadOnlyList<SingleSelectOptionSnapshot> options)
        => [.. options.Select(o => new { name = o.Name, color = o.Color, description = o.Description ?? string.Empty })];

    /// <summary>
    /// Builds the iteration configuration input. All iterations (completed included) are
    /// recreated in chronological order; the API accepts past start dates and reclassifies
    /// them as completed on read (verified by PoC against the real API).
    /// </summary>
    private object BuildIterationConfigurationInput(string fieldName, IterationConfigurationSnapshot configuration)
    {
        // completedIterations are returned newest-first by the API; order everything chronologically.
        var ordered = configuration.CompletedIterations
            .Concat(configuration.Iterations)
            .OrderBy(i => i.StartDate, StringComparer.Ordinal)
            .ToList();

        if (configuration.CompletedIterations.Count > 0)
        {
            OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"Field '{fieldName}': recreating {configuration.CompletedIterations.Count} completed iterations as past-dated iterations."));
        }

        var startDate = ordered.Count > 0
            ? ordered[0].StartDate
            : DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new
        {
            duration = configuration.Duration,
            startDate,
            iterations = ordered.Select(i => new { title = i.Title, startDate = i.StartDate, duration = i.Duration }).ToArray(),
        };
    }

    private static ProjectRef ParseProjectRef(JsonElement node) => new(
        node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Project id was null."),
        node.GetProperty("number").GetInt32(),
        node.GetProperty("url").GetString() ?? string.Empty);

    private sealed record ProjectRef(string Id, int Number, string Url);

    private sealed record TargetField(string Id, string Name, string DataType);

    /// <summary>Accumulates fieldName → id, optionName → id and iterationTitle → id mappings.</summary>
    private sealed class FieldMaps
    {
        public Dictionary<string, string> FieldIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyDictionary<string, string>> OptionIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, IReadOnlyDictionary<string, string>> IterationIds { get; } = new(StringComparer.Ordinal);

        /// <summary>Registers a field node (from a query or mutation response) and returns its identity.</summary>
        public TargetField Register(JsonElement node)
        {
            var id = node.GetProperty("id").GetString() ?? throw new GitHubGraphQLException("Field id was null.");
            var name = node.GetProperty("name").GetString() ?? throw new GitHubGraphQLException("Field name was null.");
            var dataType = node.GetProperty("dataType").GetString() ?? string.Empty;

            FieldIds[name] = id;

            if (node.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var option in options.EnumerateArray())
                {
                    map[option.GetProperty("name").GetString() ?? string.Empty] = option.GetProperty("id").GetString() ?? string.Empty;
                }

                OptionIds[name] = map;
            }

            if (node.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var propertyName in (string[])["iterations", "completedIterations"])
                {
                    foreach (var iteration in configuration.GetProperty(propertyName).EnumerateArray())
                    {
                        map[iteration.GetProperty("title").GetString() ?? string.Empty] = iteration.GetProperty("id").GetString() ?? string.Empty;
                    }
                }

                IterationIds[name] = map;
            }

            return new TargetField(id, name, dataType);
        }

        public ImportResult ToResult(ProjectRef project, bool created) => new()
        {
            ProjectId = project.Id,
            ProjectNumber = project.Number,
            Url = project.Url,
            Created = created,
            FieldIds = FieldIds,
            OptionIds = OptionIds,
            IterationIds = IterationIds,
        };
    }

    private const string FindProjectQuery =
        """
        query($login: String!, $first: Int!, $after: String) {
          organization(login: $login) {
            projectsV2(first: $first, after: $after) {
              nodes { id number title url }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string FieldsQuery =
        """
        query($id: ID!) {
          node(id: $id) {
            ... on ProjectV2 {
              fields(first: 50) {
                nodes {
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                  ... on ProjectV2IterationField {
                    configuration {
                      iterations { id title }
                      completedIterations { id title }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string CreateFieldMutation =
        """
        mutation($projectId: ID!, $name: String!, $dataType: ProjectV2CustomFieldType!, $options: [ProjectV2SingleSelectFieldOptionInput!], $iterationConfiguration: ProjectV2IterationFieldConfigurationInput) {
          createProjectV2Field(input: { projectId: $projectId, name: $name, dataType: $dataType, singleSelectOptions: $options, iterationConfiguration: $iterationConfiguration }) {
            projectV2Field {
              ... on ProjectV2FieldCommon { id name dataType }
              ... on ProjectV2SingleSelectField { options { id name } }
              ... on ProjectV2IterationField {
                configuration {
                  iterations { id title }
                  completedIterations { id title }
                }
              }
            }
          }
        }
        """;
}
