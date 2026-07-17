using System.Collections.ObjectModel;
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
    private readonly List<string> _warnings = [];
    private ProjectImportLog? _operationLog;

    public ProjectImporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Behavior when the target owner already has a project with the snapshot's title.</summary>
    public ConflictAction OnConflict { get; init; } = ConflictAction.Fail;

    /// <summary>Owner type of the target: organization (default) or user.</summary>
    public ProjectOwnerType OwnerType { get; init; } = ProjectOwnerType.Organization;

    /// <summary>Source "org/repo" → target "org/repo" mapping for linked repositories. Unmapped repositories are linked by their source name.</summary>
    public IReadOnlyDictionary<string, string> RepositoryMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Source login → target login mapping for user collaborators. Unmapped logins are resolved as-is.</summary>
    public IReadOnlyDictionary<string, string> UserMapping { get; init; } = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Warnings accumulated by the last import (unresolvable collaborators, unlinkable repositories).</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Invoked with a human-readable progress message at each import stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>Invoked after conflict resolution and immediately before the first mutation.</summary>
    public Func<CancellationToken, Task>? BeforeWriteAsync { get; set; }

    /// <summary>Directory for durable project and field creation operation state.</summary>
    public required string OperationLogDirectory { get; init; }

    /// <summary>Imports the snapshot into <paramref name="ownerLogin"/> and returns the target project identity and field mappings.</summary>
    public async Task<ImportResult> ImportAsync(ProjectSnapshot snapshot, string ownerLogin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);

        var title = snapshot.Project.Title;
        OnProgress?.Invoke($"Checking {OwnerDescription} '{ownerLogin}' for an existing project titled '{title}'...");
        var matches = await FindProjectsByTitleAsync(ownerLogin, title, cancellationToken).ConfigureAwait(false);
        ProjectRef? existing;
        if (_operationLog?.PendingProject is { } pendingProject)
        {
            if (!string.Equals(pendingProject.OwnerLogin, ownerLogin, StringComparison.Ordinal)
                || !string.Equals(pendingProject.Title, title, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pendingProject.OperationId}' does not match the current import target.");
            }

            existing = await ReconcilePendingProjectAsync(pendingProject, matches, cancellationToken).ConfigureAwait(false);
            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
            ValidatePendingFieldOperations(snapshot, existing.Id);
            return await ApplySnapshotAsync(snapshot, ownerLogin, existing, ProjectImportOutcome.Created, cancellationToken).ConfigureAwait(false);
        }

        existing = matches.FirstOrDefault();

        if (existing is not null)
        {
            switch (OnConflict)
            {
                case ConflictAction.Fail:
                    throw new InvalidOperationException(
                        string.Create(CultureInfo.InvariantCulture,
                            $"A project titled '{title}' already exists in {OwnerDescription} '{ownerLogin}' (#{existing.Number}). Use --on-conflict skip or update to proceed."));

                case ConflictAction.Skip:
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); skipping (on-conflict=skip)."));
                    return BuildSkippedResult(existing);

                case ConflictAction.Update:
                    ValidatePendingFieldOperations(snapshot, existing.Id);
                    OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                        $"Project '{title}' already exists (#{existing.Number}); applying snapshot to it (on-conflict=update)."));
                    await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
                    return await ApplySnapshotAsync(snapshot, ownerLogin, existing, ProjectImportOutcome.Updated, cancellationToken).ConfigureAwait(false);
            }
        }

        ValidatePendingFieldOperations(snapshot, projectId: null);
        var ownerId = await GetOwnerIdAsync(ownerLogin, cancellationToken).ConfigureAwait(false);
        await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
        OnProgress?.Invoke($"Creating project '{title}' in '{ownerLogin}'...");
        var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        if (_operationLog is not null)
        {
            _operationLog.PendingProject = new PendingProjectOperation
            {
                OperationId = operationId,
                OwnerLogin = ownerLogin,
                Title = title,
                ExistingProjectIds = [.. matches.Select(project => project.Id)],
            };
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        ProjectRef project;
        try
        {
            project = await CreateProjectAsync(ownerId, title, operationId, cancellationToken).ConfigureAwait(false);
        }
        catch (AmbiguousMutationResultException)
        {
            throw;
        }
        catch
        {
            if (_operationLog is not null)
            {
                _operationLog.PendingProject = null;
                await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }

        if (_operationLog is not null)
        {
            _operationLog.PendingProject = null;
            await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ApplySnapshotAsync(snapshot, ownerLogin, project, ProjectImportOutcome.Created, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports the snapshot into an existing project identified by number: skips the
    /// title lookup/creation and merges fields like the on-conflict=update path
    /// (the existing project keeps its title).
    /// </summary>
    public async Task<ImportResult> ImportIntoAsync(ProjectSnapshot snapshot, string ownerLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        await LoadOperationLogAsync(cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Looking up project #{projectNumber} in {OwnerDescription} '{ownerLogin}'..."));
        var project = await FindProjectByNumberAsync(ownerLogin, projectNumber, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Project #{projectNumber} was not found in {OwnerDescription} '{ownerLogin}'."));

        if (_operationLog?.PendingProject is { } pendingProject)
        {
            throw new InvalidOperationException(
                $"Pending project operation '{pendingProject.OperationId}' must be resumed by project title before importing into project #{projectNumber}.");
        }

        ValidatePendingFieldOperations(snapshot, project.Id);
        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Applying snapshot to existing project #{project.Number}..."));
        await InvokeBeforeWriteAsync(cancellationToken).ConfigureAwait(false);
        return await ApplySnapshotAsync(snapshot, ownerLogin, project, ProjectImportOutcome.Updated, cancellationToken).ConfigureAwait(false);
    }

    private Task InvokeBeforeWriteAsync(CancellationToken cancellationToken)
        => BeforeWriteAsync?.Invoke(cancellationToken) ?? Task.CompletedTask;

    private void ValidatePendingFieldOperations(ProjectSnapshot snapshot, string? projectId)
    {
        if (_operationLog is null || _operationLog.PendingFields.Count == 0)
        {
            return;
        }

        var snapshotFields = snapshot.Fields.ToDictionary(field => field.Name, StringComparer.Ordinal);
        foreach (var (name, pending) in _operationLog.PendingFields)
        {
            if (projectId is null
                || !string.Equals(pending.ProjectId, projectId, StringComparison.Ordinal)
                || !snapshotFields.TryGetValue(name, out var field)
                || !string.Equals(pending.Name, field.Name, StringComparison.Ordinal)
                || !string.Equals(pending.DataType, field.DataType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Pending field operation '{pending.OperationId}' does not match the selected project and snapshot. Resume the original import or reconcile it manually.");
            }
        }
    }

    /// <summary>Applies metadata, custom fields and Status options to the target project and builds the result.</summary>
    private async Task<ImportResult> ApplySnapshotAsync(
        ProjectSnapshot snapshot,
        string ownerLogin,
        ProjectRef project,
        ProjectImportOutcome outcome,
        CancellationToken cancellationToken)
    {
        _warnings.Clear();
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

            if (_operationLog?.PendingFields.TryGetValue(field.Name, out var pendingField) == true)
            {
                if (!string.Equals(pendingField.ProjectId, project.Id, StringComparison.Ordinal)
                    || !string.Equals(pendingField.DataType, field.DataType, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Pending field operation '{pendingField.OperationId}' does not match field '{field.Name}'.");
                }

                if (!existingFields.TryGetValue(field.Name, out var reconciled)
                    || !string.Equals(reconciled.DataType, pendingField.DataType, StringComparison.Ordinal)
                    || pendingField.ExistingFieldIds.Contains(reconciled.Id, StringComparer.Ordinal))
                {
                    reconciled = await ReconcilePendingFieldAsync(project.Id, field, maps, pendingField, cancellationToken).ConfigureAwait(false);
                    existingFields[field.Name] = reconciled;
                }

                _operationLog.PendingFields.Remove(field.Name);
                await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
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
                var operationId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                if (_operationLog is not null)
                {
                    _operationLog.PendingFields[field.Name] = new PendingFieldOperation
                    {
                        OperationId = operationId,
                        ProjectId = project.Id,
                        Name = field.Name,
                        DataType = field.DataType,
                        ExistingFieldIds = [],
                    };
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    await CreateFieldAsync(project.Id, field, maps, operationId, cancellationToken).ConfigureAwait(false);
                }
                catch (AmbiguousMutationResultException)
                {
                    throw;
                }
                catch
                {
                    if (_operationLog is not null)
                    {
                        _operationLog.PendingFields.Remove(field.Name);
                        await SaveOperationLogAsync(CancellationToken.None).ConfigureAwait(false);
                    }

                    throw;
                }

                if (_operationLog is not null)
                {
                    _operationLog.PendingFields.Remove(field.Name);
                    await SaveOperationLogAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await ApplyCollaboratorsAsync(project.Id, ownerLogin, snapshot.Collaborators, cancellationToken).ConfigureAwait(false);
        await ApplyLinkedRepositoriesAsync(project.Id, snapshot.LinkedRepositories, cancellationToken).ConfigureAwait(false);

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Import finished: project #{project.Number}, {maps.FieldIds.Count} fields mapped."));
        return maps.ToResult(project, outcome);
    }

    /// <summary>
    /// Applies snapshot collaborators through a single <c>updateProjectV2Collaborators</c>
    /// call. User logins go through <see cref="UserMapping"/> (unmapped logins are used
    /// as-is); team slugs are resolved in the target organization. Unresolvable
    /// collaborators are skipped with a warning. Note: exports never populate
    /// collaborators (the API has no read field), so this only runs for hand-authored
    /// snapshots.
    /// </summary>
    private async Task ApplyCollaboratorsAsync(string projectId, string ownerLogin, IReadOnlyList<CollaboratorSnapshot>? collaborators, CancellationToken cancellationToken)
    {
        if (collaborators is not { Count: > 0 })
        {
            return;
        }

        var inputs = new List<object>();
        foreach (var collaborator in collaborators)
        {
            if (string.Equals(collaborator.Type, "USER", StringComparison.OrdinalIgnoreCase))
            {
                var login = UserMapping.TryGetValue(collaborator.Login, out var mapped) ? mapped : collaborator.Login;
                var userId = await ResolveUserIdAsync(login, cancellationToken).ConfigureAwait(false);
                if (userId is null)
                {
                    Warn($"collaborator user '{login}' was not found; skipping.");
                    continue;
                }

                inputs.Add(new { userId, role = collaborator.Role });
            }
            else if (string.Equals(collaborator.Type, "TEAM", StringComparison.OrdinalIgnoreCase))
            {
                if (OwnerType == ProjectOwnerType.User)
                {
                    Warn($"collaborator team '{collaborator.Login}': team collaborators are not supported on user projects; skipping.");
                    continue;
                }

                var teamId = await ResolveTeamIdAsync(ownerLogin, collaborator.Login, cancellationToken).ConfigureAwait(false);
                if (teamId is null)
                {
                    Warn($"collaborator team '{collaborator.Login}' was not found in organization '{ownerLogin}'; skipping.");
                    continue;
                }

                inputs.Add(new { teamId, role = collaborator.Role });
            }
            else
            {
                Warn($"collaborator '{collaborator.Login}': unknown type '{collaborator.Type}'; skipping.");
            }
        }

        if (inputs.Count == 0)
        {
            return; // The mutation rejects an empty collaborators list.
        }

        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
            $"Applying {inputs.Count} project collaborators..."));
        await _client.MutationAsync(
            "updateProjectV2Collaborators",
            """
            mutation($projectId: ID!, $collaborators: [ProjectV2Collaborator!]!, $clientMutationId: String!) {
              updateProjectV2Collaborators(input: { projectId: $projectId, collaborators: $collaborators, clientMutationId: $clientMutationId }) {
                collaborators(first: 100) { nodes { __typename } }
              }
            }
            """,
            new { projectId, collaborators = inputs.ToArray() },
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "collaborators.nodes",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Links the snapshot's linked repositories to the target project via
    /// <c>linkProjectV2ToRepository</c>. Repository names go through
    /// <see cref="RepositoryMapping"/> (unmapped names are used as-is); repositories
    /// that cannot be resolved or linked (e.g. not visible to the target account, or
    /// outside a GHEC-DR tenant) are skipped with a warning.
    /// </summary>
    private async Task ApplyLinkedRepositoriesAsync(string projectId, IReadOnlyList<string>? repositories, CancellationToken cancellationToken)
    {
        if (repositories is not { Count: > 0 })
        {
            return;
        }

        foreach (var repository in repositories)
        {
            var mapped = RepositoryMapping.TryGetValue(repository, out var target) ? target : repository;
            var separator = mapped.IndexOf('/', StringComparison.Ordinal);
            if (separator <= 0 || separator == mapped.Length - 1)
            {
                Warn($"linked repository '{mapped}' is not in 'owner/name' form; skipping.");
                continue;
            }

            var repositoryId = await ResolveRepositoryIdAsync(mapped[..separator], mapped[(separator + 1)..], cancellationToken).ConfigureAwait(false);
            if (repositoryId is null)
            {
                Warn($"linked repository '{mapped}' was not found; skipping.");
                continue;
            }

            try
            {
                await _client.MutationAsync(
                    "linkProjectV2ToRepository",
                    """
                    mutation($projectId: ID!, $repositoryId: ID!, $clientMutationId: String!) {
                      linkProjectV2ToRepository(input: { projectId: $projectId, repositoryId: $repositoryId, clientMutationId: $clientMutationId }) {
                        repository { nameWithOwner }
                      }
                    }
                    """,
                    new { projectId, repositoryId },
                    MutationRetryPolicy.Idempotent,
                    target: projectId,
                    requiredResultPath: "repository.nameWithOwner",
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                OnProgress?.Invoke($"Linked repository '{mapped}'.");
            }
            catch (GitHubGraphQLException exception)
            {
                Warn($"could not link repository '{mapped}': {exception.Message}");
            }
        }
    }

    private async Task<string?> ResolveUserIdAsync(string login, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($login: String!) { user(login: $login) { id } }",
                new { login },
                cancellationToken).ConfigureAwait(false);

            var user = data.GetProperty("user");
            return user.ValueKind == JsonValueKind.Object ? user.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<string?> ResolveTeamIdAsync(string organizationLogin, string teamSlug, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($login: String!, $slug: String!) { organization(login: $login) { team(slug: $slug) { id } } }",
                new { login = organizationLogin, slug = teamSlug },
                cancellationToken).ConfigureAwait(false);

            var organization = data.GetProperty("organization");
            if (organization.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var team = organization.GetProperty("team");
            return team.ValueKind == JsonValueKind.Object ? team.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<string?> ResolveRepositoryIdAsync(string owner, string name, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                "query($owner: String!, $name: String!) { repository(owner: $owner, name: $name) { id } }",
                new { owner, name },
                cancellationToken).ConfigureAwait(false);

            var repository = data.GetProperty("repository");
            return repository.ValueKind == JsonValueKind.Object ? repository.GetProperty("id").GetString() : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private void Warn(string message)
    {
        _warnings.Add(message);
        OnProgress?.Invoke("warning: " + message);
    }

    private static ImportResult BuildSkippedResult(ProjectRef project) => new()
    {
        ProjectId = project.Id,
        ProjectNumber = project.Number,
        Url = project.Url,
        Outcome = ProjectImportOutcome.Skipped,
        FieldIds = ReadOnlyDictionary<string, string>.Empty,
        OptionIds = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty,
        IterationIds = ReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>.Empty,
    };

    private string OwnerField => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private string OwnerDescription => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private async Task<string> GetOwnerIdAsync(string ownerLogin, CancellationToken cancellationToken)
    {
        var query = OwnerType == ProjectOwnerType.User
            ? "query($login: String!) { user(login: $login) { id } }"
            : "query($login: String!) { organization(login: $login) { id } }";

        var data = await _client.QueryAsync(query, new { login = ownerLogin }, cancellationToken).ConfigureAwait(false);

        return data.GetProperty(OwnerField).GetProperty("id").GetString()
            ?? throw new GitHubGraphQLException($"{(OwnerType == ProjectOwnerType.User ? "User" : "Organization")} '{ownerLogin}' was not found.");
    }

    private async Task<List<ProjectRef>> FindProjectsByTitleAsync(string ownerLogin, string title, CancellationToken cancellationToken)
    {
        var projects = new List<ProjectRef>();
        await foreach (var node in _client.QueryPaginatedAsync(
            FindProjectQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal),
            new { login = ownerLogin, first = 50 },
            OwnerField + ".projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                projects.Add(ParseProjectRef(node));
            }
        }

        return projects;
    }

    private async Task<ProjectRef?> FindProjectByNumberAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken)
    {
        try
        {
            var data = await _client.QueryAsync(
                FindProjectByNumberQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal),
                new { login = ownerLogin, number = projectNumber },
                cancellationToken).ConfigureAwait(false);

            var project = data.GetProperty(OwnerField).GetProperty("projectV2");
            return project.ValueKind == JsonValueKind.Object ? ParseProjectRef(project) : null;
        }
        catch (GitHubGraphQLException exception) when (exception.ErrorType == "NOT_FOUND")
        {
            return null;
        }
    }

    private async Task<ProjectRef> CreateProjectAsync(
        string ownerId,
        string title,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "createProjectV2",
            """
            mutation($ownerId: ID!, $title: String!, $clientMutationId: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title, clientMutationId: $clientMutationId }) {
                projectV2 { id number title url }
              }
            }
            """,
            new { ownerId, title },
            target: ownerId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseProjectRef(data.GetProperty("createProjectV2").GetProperty("projectV2"));
    }

    private async Task UpdateProjectMetadataAsync(string projectId, ProjectInfoSnapshot info, CancellationToken cancellationToken)
    {
        await _client.MutationAsync(
            "updateProjectV2",
            """
            mutation($projectId: ID!, $shortDescription: String, $readme: String, $public: Boolean, $closed: Boolean, $clientMutationId: String!) {
              updateProjectV2(input: { projectId: $projectId, shortDescription: $shortDescription, readme: $readme, public: $public, closed: $closed, clientMutationId: $clientMutationId }) {
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
            MutationRetryPolicy.Idempotent,
            target: projectId,
            requiredResultPath: "projectV2.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);
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

    private async Task CreateFieldAsync(
        string projectId,
        FieldSnapshot field,
        FieldMaps maps,
        string clientMutationId,
        CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "createProjectV2Field",
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
            target: projectId,
            clientMutationId: clientMutationId,
            requiredResultPath: "projectV2Field.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        maps.Register(data.GetProperty("createProjectV2Field").GetProperty("projectV2Field"));
    }

    private async Task LoadOperationLogAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(OperationLogDirectory);
        _operationLog = await ProjectImportLog.LoadAsync(OperationLogDirectory, cancellationToken).ConfigureAwait(false);
    }

    private Task SaveOperationLogAsync(CancellationToken cancellationToken)
        => _operationLog is not null
            ? _operationLog.SaveAsync(OperationLogDirectory, cancellationToken)
            : Task.CompletedTask;

    private async Task<ProjectRef> ReconcilePendingProjectAsync(
        PendingProjectOperation pending,
        IReadOnlyList<ProjectRef> initialMatches,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectRef> matches = initialMatches;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var baseline = new HashSet<string>(pending.ExistingProjectIds, StringComparer.Ordinal);
            var candidates = matches.Where(project => !baseline.Contains(project.Id)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending project operation '{pending.OperationId}' matches multiple new projects. Reconcile the target manually.");
            }

            if (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
                matches = await FindProjectsByTitleAsync(pending.OwnerLogin, pending.Title, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"Pending project operation '{pending.OperationId}' is not visible after reconciliation polling. Do not resend it until the target is reconciled manually.");
    }

    private async Task<TargetField> ReconcilePendingFieldAsync(
        string projectId,
        FieldSnapshot field,
        FieldMaps maps,
        PendingFieldOperation pending,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }

            var fields = await FetchFieldsAsync(projectId, maps, cancellationToken).ConfigureAwait(false);
            var candidates = fields.Values.Where(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.Ordinal)
                && string.Equals(candidate.DataType, field.DataType, StringComparison.Ordinal)
                && !pending.ExistingFieldIds.Contains(candidate.Id, StringComparer.Ordinal)).ToArray();
            if (candidates.Length == 1)
            {
                return candidates[0];
            }

            if (candidates.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Pending field operation '{pending.OperationId}' matches multiple new fields. Reconcile the target manually.");
            }
        }

        throw new InvalidOperationException(
            $"Pending field operation '{pending.OperationId}' is not visible after reconciliation polling. Do not resend it until the target is reconciled manually.");
    }

    private async Task UpdateSingleSelectOptionsAsync(string fieldId, IReadOnlyList<SingleSelectOptionSnapshot> options, FieldMaps maps, CancellationToken cancellationToken)
    {
        var data = await _client.MutationAsync(
            "updateProjectV2Field",
            """
            mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]!, $clientMutationId: String!) {
              updateProjectV2Field(input: { fieldId: $fieldId, singleSelectOptions: $options, clientMutationId: $clientMutationId }) {
                projectV2Field {
                  ... on ProjectV2FieldCommon { id name dataType }
                  ... on ProjectV2SingleSelectField { options { id name } }
                }
              }
            }
            """,
            new { fieldId, options = BuildOptionInputs(options) },
            MutationRetryPolicy.Idempotent,
            target: fieldId,
            requiredResultPath: "projectV2Field.id",
            cancellationToken: cancellationToken).ConfigureAwait(false);

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

        public ImportResult ToResult(ProjectRef project, ProjectImportOutcome outcome) => new()
        {
            ProjectId = project.Id,
            ProjectNumber = project.Number,
            Url = project.Url,
            Outcome = outcome,
            FieldIds = FieldIds,
            OptionIds = OptionIds,
            IterationIds = IterationIds,
        };
    }

    private const string FindProjectQueryTemplate =
        """
        query($login: String!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectsV2(first: $first, after: $after) {
              nodes { id number title url }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string FindProjectByNumberQueryTemplate =
        """
        query($login: String!, $number: Int!) {
          __OWNER__(login: $login) {
            projectV2(number: $number) { id number title url }
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
        mutation($projectId: ID!, $name: String!, $dataType: ProjectV2CustomFieldType!, $options: [ProjectV2SingleSelectFieldOptionInput!], $iterationConfiguration: ProjectV2IterationFieldConfigurationInput, $clientMutationId: String!) {
          createProjectV2Field(input: { projectId: $projectId, name: $name, dataType: $dataType, singleSelectOptions: $options, iterationConfiguration: $iterationConfiguration, clientMutationId: $clientMutationId }) {
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
