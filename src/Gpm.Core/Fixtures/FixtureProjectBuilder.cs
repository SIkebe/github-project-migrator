using System.Globalization;
using System.Text;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Fixtures;

/// <summary>Creates the standard API-backed integration-test fixture without PowerShell or gh CLI.</summary>
public sealed class FixtureProjectBuilder
{
    private readonly GitHubGraphQLClient _graphQl;
    private readonly GitHubRestClient _rest;

    public FixtureProjectBuilder(GitHubGraphQLClient graphQl, GitHubRestClient rest)
    {
        ArgumentNullException.ThrowIfNull(graphQl);
        ArgumentNullException.ThrowIfNull(rest);
        _graphQl = graphQl;
        _rest = rest;
    }

    public Action<string>? OnProgress { get; set; }

    public required string OperationLogDirectory { get; init; }

    public async Task<FixtureProjectSetupResult> CreateAsync(
        string organization,
        string title = "gpm-fixture",
        string repositoryName = "fixture-repo",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organization);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryName);

        var existing = await FindProjectByTitleAsync(organization, title, cancellationToken).ConfigureAwait(false);
        var projectLog = await ProjectImportLog.LoadAsync(OperationLogDirectory, cancellationToken).ConfigureAwait(false);
        var itemLog = await ImportLog.LoadAsync(OperationLogDirectory, cancellationToken).ConfigureAwait(false);
        var hasPendingOperations = projectLog.PendingProject is not null
            || projectLog.PendingFields.Count > 0
            || itemLog is { PendingDrafts.Count: > 0 }
            || itemLog is { PendingContents.Count: > 0 };
        if (existing is not null && !hasPendingOperations)
        {
            OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture,
                $"Fixture project already exists: {existing.Url}"));
            return new FixtureProjectSetupResult(existing.Number, existing.Url, Created: false);
        }

        var viewerLogin = await _graphQl.GetViewerLoginAsync(cancellationToken).ConfigureAwait(false);
        var repositoryFullName = $"{organization}/{repositoryName}";
        var pullRequestNumber = await EnsureRepositoryAsync(organization, repositoryName, cancellationToken).ConfigureAwait(false);

        var snapshot = CreateSnapshot(title, repositoryFullName, viewerLogin, pullRequestNumber);
        var projectImporter = new ProjectImporter(_graphQl)
        {
            OnProgress = OnProgress,
            OnConflict = existing is null ? ConflictAction.Fail : ConflictAction.Update,
            OperationLogDirectory = OperationLogDirectory,
            PendingItemProjectId = itemLog is { PendingDrafts.Count: > 0 }
                || itemLog is { PendingContents.Count: > 0 }
                    ? itemLog.ProjectId
                    : null,
            RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [repositoryFullName] = repositoryFullName,
            },
        };
        var project = await projectImporter.ImportAsync(snapshot, organization, cancellationToken).ConfigureAwait(false);

        var itemImporter = new ItemImporter(_graphQl)
        {
            OnProgress = OnProgress,
            RepositoryMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [repositoryFullName] = repositoryFullName,
            },
            UserMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [viewerLogin] = viewerLogin,
            },
        };
        var itemResult = await itemImporter.ImportAsync(snapshot, project, OperationLogDirectory, cancellationToken).ConfigureAwait(false);
        foreach (var warning in itemResult.Warnings)
        {
            OnProgress?.Invoke("warning: " + warning);
        }

        return new FixtureProjectSetupResult(project.ProjectNumber, project.Url, Created: existing is null);
    }

    private async Task<int> EnsureRepositoryAsync(string organization, string repositoryName, CancellationToken cancellationToken)
    {
        var repositoryFullName = $"{organization}/{repositoryName}";
        var repository = await _rest.GetAsync($"repos/{repositoryFullName}", cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            OnProgress?.Invoke($"Creating private repository {repositoryFullName}...");
            repository = await _rest.PostAsync($"orgs/{organization}/repos", new { name = repositoryName, @private = true }, cancellationToken).ConfigureAwait(false);
        }

        await EnsureReadmeAsync(repositoryFullName, repositoryName, cancellationToken).ConfigureAwait(false);
        await EnsureIssuesAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
        await EnsureBugLabelAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
        return await EnsurePullRequestAsync(repositoryFullName, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureReadmeAsync(string repositoryFullName, string repositoryName, CancellationToken cancellationToken)
    {
        if (await _rest.GetAsync($"repos/{repositoryFullName}/contents/README.md", cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        OnProgress?.Invoke("Creating initial commit (README.md)...");
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes($"# {repositoryName}\n\nPermanent fixture repository for gpm integration tests.\n"));
        await _rest.PutAsync(
            $"repos/{repositoryFullName}/contents/README.md",
            new { message = "Initial commit", content },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureIssuesAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        for (var number = 1; number <= 2; number++)
        {
            var existing = await _rest.GetAsync($"repos/{repositoryFullName}/issues/{number}", cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                OnProgress?.Invoke($"Creating Fixture issue {number}...");
                var created = await _rest.PostAsync(
                    $"repos/{repositoryFullName}/issues",
                    new { title = $"Fixture issue {number}", body = $"Permanent fixture issue {number}." },
                    cancellationToken).ConfigureAwait(false);
                EnsureExpectedIssue(created, repositoryFullName, number);
                continue;
            }

            EnsureExpectedIssue(existing.Value, repositoryFullName, number);
        }
    }

    private static void EnsureExpectedIssue(JsonElement issue, string repositoryFullName, int expectedNumber)
    {
        var actualNumber = issue.GetProperty("number").GetInt32();
        if (actualNumber != expectedNumber)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Fixture repository '{repositoryFullName}' must contain Issue #{expectedNumber}, but GitHub created Issue #{actualNumber}. Use an empty fixture repository so Issue #1 and #2 can be reserved for the fixture."));
        }

        if (issue.TryGetProperty("pull_request", out _))
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Fixture repository '{repositoryFullName}' must contain Issue #{expectedNumber}, but #{expectedNumber} is a pull request. Use an empty fixture repository so Issue #1 and #2 can be reserved for the fixture."));
        }
    }

    private async Task EnsureBugLabelAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        if (await _rest.GetAsync($"repos/{repositoryFullName}/labels/bug", cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        OnProgress?.Invoke("Creating label 'bug'...");
        await _rest.PostAsync(
            $"repos/{repositoryFullName}/labels",
            new { name = "bug", color = "d73a4a", description = "Fixture label for Auto-add workflow tests" },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> EnsurePullRequestAsync(string repositoryFullName, CancellationToken cancellationToken)
    {
        var branchName = "fixture-pr-branch";
        var owner = repositoryFullName[..repositoryFullName.IndexOf('/', StringComparison.Ordinal)];
        var head = Uri.EscapeDataString($"{owner}:{branchName}");
        var pulls = await _rest.GetAsync($"repos/{repositoryFullName}/pulls?state=open&head={head}&per_page=1", cancellationToken).ConfigureAwait(false);
        var firstOpen = pulls?.EnumerateArray().FirstOrDefault();
        if (firstOpen is { ValueKind: JsonValueKind.Object } openPullRequest)
        {
            return openPullRequest.GetProperty("number").GetInt32();
        }

        var repository = await _rest.GetAsync($"repos/{repositoryFullName}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository '{repositoryFullName}' was not found after creation.");
        var defaultBranch = repository.GetProperty("default_branch").GetString() ?? "main";
        var baseRef = await _rest.GetAsync($"repos/{repositoryFullName}/git/ref/heads/{defaultBranch}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' was not found in '{repositoryFullName}'.");
        var baseSha = baseRef.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException($"Default branch '{defaultBranch}' returned no SHA.");

        if (await _rest.GetAsync($"repos/{repositoryFullName}/git/ref/heads/{branchName}", cancellationToken).ConfigureAwait(false) is null)
        {
            await _rest.PostAsync(
                $"repos/{repositoryFullName}/git/refs",
                new { @ref = $"refs/heads/{branchName}", sha = baseSha },
                cancellationToken).ConfigureAwait(false);
        }

        var path = $"repos/{repositoryFullName}/contents/fixture-pr.txt";
        var existingFile = await _rest.GetAsync(path + $"?ref={branchName}", cancellationToken).ConfigureAwait(false);
        if (existingFile is null)
        {
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture PR file\n"));
            await _rest.PutAsync(path, new { message = "Add fixture PR file", content, branch = branchName }, cancellationToken).ConfigureAwait(false);
        }

        var pull = await _rest.PostAsync(
            $"repos/{repositoryFullName}/pulls",
            new
            {
                title = "Fixture pull request",
                body = "Permanent fixture PR (kept open for gpm integration tests).",
                head = branchName,
                @base = defaultBranch,
            },
            cancellationToken).ConfigureAwait(false);
        var number = pull.GetProperty("number").GetInt32();
        OnProgress?.Invoke($"Created Fixture pull request #{number}.");
        return number;
    }

    private static ProjectSnapshot CreateSnapshot(string title, string repositoryFullName, string viewerLogin, int pullRequestNumber)
    {
        var today = DateTime.UtcNow.Date;
        var sprint0Start = today.AddDays(-28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint1Start = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint2Start = today.AddDays(14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var sprint3Start = today.AddDays(28).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        return new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = new ProjectInfoSnapshot
            {
                Title = title,
                ShortDescription = "gpm fixture project",
                Readme = "# gpm fixture 📦\n\nPermanent fixture project for gpm integration tests.\n\n- All custom field types (Text / Number / Date / Single-select / Iteration)\n- Drafts with 日本語 values, an Issue, a PR, an archived item and an assigned item\n- Views and workflows can be created by running `gpm setup --fixture-ui` (C# browser module) 🚀",
                Public = false,
                Closed = false,
            },
            Fields =
            [
                new FieldSnapshot
                {
                    Name = "Status",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "todo", Name = "Todo", Color = "GRAY", Description = "Not started" },
                        new SingleSelectOptionSnapshot { Id = "in-progress", Name = "In Progress", Color = "YELLOW", Description = "In progress" },
                        new SingleSelectOptionSnapshot { Id = "done", Name = "Done", Color = "GREEN", Description = "Done" },
                    ],
                },
                new FieldSnapshot { Name = "Fixture Text", DataType = "TEXT" },
                new FieldSnapshot { Name = "Fixture Number", DataType = "NUMBER" },
                new FieldSnapshot { Name = "Fixture Date", DataType = "DATE" },
                new FieldSnapshot
                {
                    Name = "Fixture Select",
                    DataType = "SINGLE_SELECT",
                    Options =
                    [
                        new SingleSelectOptionSnapshot { Id = "alpha", Name = "Alpha", Color = "RED", Description = "First" },
                        new SingleSelectOptionSnapshot { Id = "beta", Name = "Beta", Color = "BLUE", Description = "Second" },
                        new SingleSelectOptionSnapshot { Id = "gamma", Name = "Gamma", Color = "GREEN", Description = "Third" },
                    ],
                },
                new FieldSnapshot
                {
                    Name = "Fixture Sprint",
                    DataType = "ITERATION",
                    IterationConfiguration = new IterationConfigurationSnapshot
                    {
                        Duration = 14,
                        StartDay = 1,
                        CompletedIterations = [new IterationSnapshot { Id = "sprint-0", Title = "Sprint 0", StartDate = sprint0Start, Duration = 14 }],
                        Iterations =
                        [
                            new IterationSnapshot { Id = "sprint-1", Title = "Sprint 1", StartDate = sprint1Start, Duration = 14 },
                            new IterationSnapshot { Id = "sprint-2", Title = "Sprint 2", StartDate = sprint2Start, Duration = 14 },
                            new IterationSnapshot { Id = "sprint-3", Title = "Sprint 3", StartDate = sprint3Start, Duration = 14 },
                        ],
                    },
                },
            ],
            Views = [],
            Workflows = [],
            Items =
            [
                Draft(0, "Fixture draft 1", false, [],
                    Text("日本語テキスト & <special> chars"), Number(3.14), Date(today.AddDays(-21)), Select("Alpha"), Sprint("Sprint 0"), Status("Todo")),
                Draft(1, "Fixture draft 2", false, [],
                    Text("Café emoji 🚀 – em dash"), Number(-42), Date(today.AddDays(4)), Select("Beta"), Sprint("Sprint 1"), Status("In Progress")),
                Draft(2, "Fixture draft 3", false, [],
                    Text("plain ascii text"), Number(0), Date(today.AddDays(26)), Select("Gamma"), Sprint("Sprint 2"), Status("Done")),
                new ItemSnapshot { Type = "ISSUE", Position = 3, IsArchived = false, Repository = repositoryFullName, Number = 1, FieldValues = [Status("Todo")] },
                new ItemSnapshot { Type = "PULL_REQUEST", Position = 4, IsArchived = false, Repository = repositoryFullName, Number = pullRequestNumber, FieldValues = [Status("In Progress")] },
                Draft(5, "Fixture archived draft", true, [], Status("Done")),
                Draft(6, "Fixture assigned draft", false, [viewerLogin], Status("Todo")),
            ],
            LinkedRepositories = [repositoryFullName],
        };

        static ItemSnapshot Draft(int position, string title, bool archived, IReadOnlyList<string> assignees, params FieldValueSnapshot[] values) => new()
        {
            Type = "DRAFT_ISSUE",
            Position = position,
            IsArchived = archived,
            Draft = new DraftIssueSnapshot { Title = title, Body = null, Creator = null, CreatedAt = null, Assignees = assignees },
            FieldValues = values,
        };

        static FieldValueSnapshot Text(string value) => new() { FieldName = "Fixture Text", Text = value };
        static FieldValueSnapshot Number(double value) => new() { FieldName = "Fixture Number", Number = value };
        static FieldValueSnapshot Date(DateTime value) => new() { FieldName = "Fixture Date", Date = value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) };
        static FieldValueSnapshot Select(string value) => new() { FieldName = "Fixture Select", SingleSelectOptionName = value };
        static FieldValueSnapshot Sprint(string value) => new() { FieldName = "Fixture Sprint", IterationTitle = value };
        static FieldValueSnapshot Status(string value) => new() { FieldName = "Status", SingleSelectOptionName = value };
    }

    private async Task<ProjectRef?> FindProjectByTitleAsync(string organization, string title, CancellationToken cancellationToken)
    {
        await foreach (var node in _graphQl.QueryPaginatedAsync(
            """
            query($login: String!, $after: String) {
              organization(login: $login) {
                projectsV2(first: 50, after: $after) {
                  nodes { id number title url }
                  pageInfo { hasNextPage endCursor }
                }
              }
            }
            """,
            new { login = organization },
            "organization.projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(node.GetProperty("title").GetString(), title, StringComparison.Ordinal))
            {
                return new ProjectRef(
                    node.GetProperty("number").GetInt32(),
                    node.GetProperty("url").GetString() ?? string.Empty);
            }
        }

        return null;
    }

    private sealed record ProjectRef(int Number, string Url);
}

public sealed record FixtureProjectSetupResult(int ProjectNumber, string Url, bool Created);
