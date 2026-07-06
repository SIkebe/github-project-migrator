using System.Globalization;
using System.Text.Json;
using Gpm.Core.GitHub;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Export;

/// <summary>
/// Exports an organization project (Projects V2) into a <see cref="ProjectSnapshot"/> (M2).
/// Reads everything the GraphQL API exposes: project metadata, fields
/// (including single-select options and iteration configuration), views,
/// workflows and all items (archived included) with their field values.
/// UI-only settings (view slice-by/field-sum/roadmap, workflow details) are
/// left null and filled in by the browser module (M6/M7).
/// </summary>
public sealed class ProjectExporter
{
    private const int ItemsPageSize = 50;

    private readonly GitHubGraphQLClient _client;

    public ProjectExporter(GitHubGraphQLClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <summary>Owner type of the source project(s): organization (default) or user.</summary>
    public ProjectOwnerType OwnerType { get; init; } = ProjectOwnerType.Organization;

    /// <summary>Invoked with a human-readable progress message at each export stage.</summary>
    public Action<string>? OnProgress { get; set; }

    /// <summary>
    /// Optional post-processing hook invoked with the GraphQL snapshot; returns the final
    /// snapshot. Used by the browser module (M6) to fill UI-only view settings without
    /// coupling the GraphQL export path to Playwright.
    /// </summary>
    public Func<ProjectSnapshot, CancellationToken, Task<ProjectSnapshot>>? PostExportAsync { get; set; }

    /// <summary>Exports the project identified by owner login and project number.</summary>
    public async Task<ProjectSnapshot> ExportAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        OnProgress?.Invoke($"Fetching project {ownerLogin}/#{projectNumber.ToString(CultureInfo.InvariantCulture)} metadata (fields, views, workflows)...");
        var data = await _client.QueryAsync(MetadataQuery, new { login = ownerLogin, number = projectNumber }, cancellationToken).ConfigureAwait(false);

        var project = data.GetProperty(OwnerField).GetProperty("projectV2");
        if (project.ValueKind == JsonValueKind.Null)
        {
            throw new GitHubGraphQLException($"Project #{projectNumber.ToString(CultureInfo.InvariantCulture)} was not found in {OwnerDescription} '{ownerLogin}'.");
        }

        var projectInfo = ParseProjectInfo(project);
        var fields = ParseFields(project.GetProperty("fields"));
        var views = ParseViews(project.GetProperty("views"));
        var workflows = ParseWorkflows(project.GetProperty("workflows"));
        var linkedRepositories = ParseLinkedRepositories(project.GetProperty("repositories"));
        OnProgress?.Invoke(string.Create(
            CultureInfo.InvariantCulture,
            $"Fetched {fields.Count} fields, {views.Count} views, {workflows.Count} workflows. Fetching items..."));

        var items = await FetchItemsAsync(ownerLogin, projectNumber, cancellationToken).ConfigureAwait(false);
        OnProgress?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Fetched {items.Count} items."));

        var snapshot = new ProjectSnapshot
        {
            SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
            Project = projectInfo,
            Fields = fields,
            Views = views,
            Workflows = workflows,
            Items = items,
            // Collaborators stay null in the API-only path: the GraphQL API has no
            // read field for project collaborators. The browser post-export hook can
            // populate explicit collaborators from Settings → Manage access.
            Collaborators = null,
            LinkedRepositories = linkedRepositories,
        };

        if (PostExportAsync is not null)
        {
            snapshot = await PostExportAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }

        return snapshot;
    }

    /// <summary>Lists the owner's projects (number, title, closed state) for bulk export.</summary>
    public async Task<IReadOnlyList<ProjectListEntry>> ListProjectsAsync(string ownerLogin, bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);

        var entries = new List<ProjectListEntry>();
        await foreach (var node in _client.QueryPaginatedAsync(
            ListProjectsQuery,
            new { login = ownerLogin, first = 50 },
            OwnerField + ".projectsV2",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var closed = node.GetProperty("closed").GetBoolean();
            if (closed && !includeClosed)
            {
                continue;
            }

            entries.Add(new ProjectListEntry(
                node.GetProperty("number").GetInt32(),
                node.GetProperty("title").GetString() ?? string.Empty,
                closed));
        }

        return entries;
    }

    private string OwnerField => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private string OwnerDescription => OwnerType == ProjectOwnerType.User ? "user" : "organization";

    private async Task<List<ItemSnapshot>> FetchItemsAsync(string ownerLogin, int projectNumber, CancellationToken cancellationToken)
    {
        var items = new List<ItemSnapshot>();
        await foreach (var node in _client.QueryPaginatedAsync(
            ItemsQuery,
            new { login = ownerLogin, number = projectNumber, first = ItemsPageSize },
            OwnerField + ".projectV2.items",
            cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            items.Add(ParseItem(node, position: items.Count));
        }

        return items;
    }

    private static ProjectInfoSnapshot ParseProjectInfo(JsonElement project) => new()
    {
        Title = project.GetProperty("title").GetString() ?? string.Empty,
        ShortDescription = GetOptionalString(project, "shortDescription"),
        Readme = GetOptionalString(project, "readme"),
        Public = project.GetProperty("public").GetBoolean(),
        Closed = project.GetProperty("closed").GetBoolean(),
    };

    private static List<FieldSnapshot> ParseFields(JsonElement connection)
    {
        var fields = new List<FieldSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            fields.Add(new FieldSnapshot
            {
                Name = node.GetProperty("name").GetString() ?? string.Empty,
                DataType = node.GetProperty("dataType").GetString() ?? string.Empty,
                Options = node.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array
                    ? ParseSingleSelectOptions(options)
                    : null,
                IterationConfiguration = node.TryGetProperty("configuration", out var configuration) && configuration.ValueKind == JsonValueKind.Object
                    ? ParseIterationConfiguration(configuration)
                    : null,
            });
        }

        return fields;
    }

    private static List<SingleSelectOptionSnapshot> ParseSingleSelectOptions(JsonElement options)
    {
        var result = new List<SingleSelectOptionSnapshot>();
        foreach (var option in options.EnumerateArray())
        {
            result.Add(new SingleSelectOptionSnapshot
            {
                Id = option.GetProperty("id").GetString() ?? string.Empty,
                Name = option.GetProperty("name").GetString() ?? string.Empty,
                Color = option.GetProperty("color").GetString() ?? string.Empty,
                Description = GetOptionalString(option, "description"),
            });
        }

        return result;
    }

    private static IterationConfigurationSnapshot ParseIterationConfiguration(JsonElement configuration) => new()
    {
        Duration = configuration.GetProperty("duration").GetInt32(),
        StartDay = configuration.GetProperty("startDay").GetInt32(),
        Iterations = ParseIterations(configuration.GetProperty("iterations")),
        CompletedIterations = ParseIterations(configuration.GetProperty("completedIterations")),
    };

    private static List<IterationSnapshot> ParseIterations(JsonElement iterations)
    {
        var result = new List<IterationSnapshot>();
        foreach (var iteration in iterations.EnumerateArray())
        {
            result.Add(new IterationSnapshot
            {
                Id = iteration.GetProperty("id").GetString() ?? string.Empty,
                Title = iteration.GetProperty("title").GetString() ?? string.Empty,
                StartDate = iteration.GetProperty("startDate").GetString() ?? string.Empty,
                Duration = iteration.GetProperty("duration").GetInt32(),
            });
        }

        return result;
    }

    private static List<ViewSnapshot> ParseViews(JsonElement connection)
    {
        var views = new List<ViewSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            views.Add(new ViewSnapshot
            {
                Number = node.GetProperty("number").GetInt32(),
                Name = node.GetProperty("name").GetString() ?? string.Empty,
                Layout = node.GetProperty("layout").GetString() ?? string.Empty,
                Filter = GetOptionalString(node, "filter"),
                GroupByFields = ParseFieldNameConnection(node, "groupByFields"),
                SortByFields = ParseSortByFields(node),
                VerticalGroupByFields = ParseFieldNameConnection(node, "verticalGroupByFields"),
                VisibleFields = ParseFieldNameConnection(node, "fields"),
            });
        }

        return views;
    }

    private static List<string> ParseFieldNameConnection(JsonElement view, string propertyName)
    {
        var names = new List<string>();
        if (!view.TryGetProperty(propertyName, out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return names;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                names.Add(name.GetString()!);
            }
        }

        return names;
    }

    private static List<SortByFieldSnapshot> ParseSortByFields(JsonElement view)
    {
        var result = new List<SortByFieldSnapshot>();
        if (!view.TryGetProperty("sortByFields", out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            result.Add(new SortByFieldSnapshot
            {
                Field = node.GetProperty("field").GetProperty("name").GetString() ?? string.Empty,
                Direction = node.GetProperty("direction").GetString() ?? string.Empty,
            });
        }

        return result;
    }

    private static List<string> ParseLinkedRepositories(JsonElement connection)
    {
        var repositories = new List<string>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("nameWithOwner", out var name) && name.ValueKind == JsonValueKind.String)
            {
                repositories.Add(name.GetString()!);
            }
        }

        return repositories;
    }

    private static List<WorkflowSnapshot> ParseWorkflows(JsonElement connection)
    {
        var workflows = new List<WorkflowSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            workflows.Add(new WorkflowSnapshot
            {
                Number = node.GetProperty("number").GetInt32(),
                Name = node.GetProperty("name").GetString() ?? string.Empty,
                Enabled = node.GetProperty("enabled").GetBoolean(),
            });
        }

        return workflows;
    }

    private static ItemSnapshot ParseItem(JsonElement node, int position)
    {
        var type = node.GetProperty("type").GetString() ?? string.Empty;
        var content = node.GetProperty("content");

        string? repository = null;
        int? number = null;
        DraftIssueSnapshot? draft = null;

        if (content.ValueKind == JsonValueKind.Object)
        {
            if (content.TryGetProperty("repository", out var repositoryElement) && repositoryElement.ValueKind == JsonValueKind.Object)
            {
                repository = repositoryElement.GetProperty("nameWithOwner").GetString();
                number = content.GetProperty("number").GetInt32();
            }
            else if (content.TryGetProperty("title", out var draftTitle))
            {
                draft = new DraftIssueSnapshot
                {
                    Title = draftTitle.GetString() ?? string.Empty,
                    Body = GetOptionalString(content, "body"),
                    Creator = content.TryGetProperty("creator", out var creator) && creator.ValueKind == JsonValueKind.Object
                        ? GetOptionalString(creator, "login")
                        : null,
                    CreatedAt = GetOptionalString(content, "createdAt"),
                    Assignees = ParseAssignees(content),
                };
            }
        }

        return new ItemSnapshot
        {
            Type = type,
            Position = position,
            IsArchived = node.GetProperty("isArchived").GetBoolean(),
            Repository = repository,
            Number = number,
            Draft = draft,
            FieldValues = ParseFieldValues(node.GetProperty("fieldValues")),
        };
    }

    private static List<string> ParseAssignees(JsonElement content)
    {
        var assignees = new List<string>();
        if (!content.TryGetProperty("assignees", out var connection) || connection.ValueKind != JsonValueKind.Object)
        {
            return assignees;
        }

        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("login", out var login) && login.ValueKind == JsonValueKind.String)
            {
                assignees.Add(login.GetString()!);
            }
        }

        return assignees;
    }

    private static List<FieldValueSnapshot> ParseFieldValues(JsonElement connection)
    {
        var values = new List<FieldValueSnapshot>();
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            var typeName = node.GetProperty("__typename").GetString();
            if (typeName is not ("ProjectV2ItemFieldTextValue"
                or "ProjectV2ItemFieldNumberValue"
                or "ProjectV2ItemFieldDateValue"
                or "ProjectV2ItemFieldSingleSelectValue"
                or "ProjectV2ItemFieldIterationValue"))
            {
                continue;
            }

            var fieldName = node.GetProperty("field").GetProperty("name").GetString() ?? string.Empty;
            values.Add(typeName switch
            {
                "ProjectV2ItemFieldTextValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    Text = GetOptionalString(node, "text"),
                },
                "ProjectV2ItemFieldNumberValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    Number = node.GetProperty("number").ValueKind == JsonValueKind.Number ? node.GetProperty("number").GetDouble() : null,
                },
                "ProjectV2ItemFieldDateValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    Date = GetOptionalString(node, "date"),
                },
                "ProjectV2ItemFieldSingleSelectValue" => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    SingleSelectOptionName = GetOptionalString(node, "name"),
                },
                _ => new FieldValueSnapshot
                {
                    FieldName = fieldName,
                    IterationTitle = GetOptionalString(node, "title"),
                },
            });
        }

        return values;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private string MetadataQuery => MetadataQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private string ItemsQuery => ItemsQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private string ListProjectsQuery => ListProjectsQueryTemplate.Replace("__OWNER__", OwnerField, StringComparison.Ordinal);

    private const string ListProjectsQueryTemplate =
        """
        query($login: String!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectsV2(first: $first, after: $after) {
              nodes { number title closed }
              pageInfo { hasNextPage endCursor }
            }
          }
        }
        """;

    private const string MetadataQueryTemplate =
        """
        query($login: String!, $number: Int!) {
          __OWNER__(login: $login) {
            projectV2(number: $number) {
              title
              shortDescription
              readme
              public
              closed
              fields(first: 50) {
                nodes {
                  ... on ProjectV2FieldCommon { name dataType }
                  ... on ProjectV2SingleSelectField {
                    options { id name color description }
                  }
                  ... on ProjectV2IterationField {
                    configuration {
                      duration
                      startDay
                      iterations { id title startDate duration }
                      completedIterations { id title startDate duration }
                    }
                  }
                }
              }
              views(first: 50) {
                nodes {
                  number
                  name
                  layout
                  filter
                  groupByFields(first: 10) { nodes { ... on ProjectV2FieldCommon { name } } }
                  verticalGroupByFields(first: 10) { nodes { ... on ProjectV2FieldCommon { name } } }
                  sortByFields(first: 10) { nodes { direction field { ... on ProjectV2FieldCommon { name } } } }
                  fields(first: 50) { nodes { ... on ProjectV2FieldCommon { name } } }
                }
              }
              workflows(first: 50) {
                nodes { number name enabled }
              }
              repositories(first: 100) {
                nodes { nameWithOwner }
              }
            }
          }
        }
        """;

    private const string ItemsQueryTemplate =
        """
        query($login: String!, $number: Int!, $first: Int!, $after: String) {
          __OWNER__(login: $login) {
            projectV2(number: $number) {
              items(first: $first, after: $after, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                nodes {
                  type
                  isArchived
                  content {
                    ... on Issue { number repository { nameWithOwner } }
                    ... on PullRequest { number repository { nameWithOwner } }
                    ... on DraftIssue { title body createdAt creator { login } assignees(first: 20) { nodes { login } } }
                  }
                  fieldValues(first: 50) {
                    nodes {
                      __typename
                      ... on ProjectV2ItemFieldTextValue { text field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldNumberValue { number field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldDateValue { date field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldSingleSelectValue { name field { ... on ProjectV2FieldCommon { name } } }
                      ... on ProjectV2ItemFieldIterationValue { title field { ... on ProjectV2FieldCommon { name } } }
                    }
                  }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;
}

/// <summary>A project listed by <see cref="ProjectExporter.ListProjectsAsync"/>.</summary>
public sealed record ProjectListEntry(int Number, string Title, bool Closed);
