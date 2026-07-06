namespace Gpm.Core.Snapshot;

/// <summary>
/// Root of the JSON snapshot produced by <c>gpm export</c> (M2).
/// Serialized as an indented UTF-8 <c>snapshot.json</c> file.
/// </summary>
public sealed record ProjectSnapshot
{
    /// <summary>The schema version written by the current tool.</summary>
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required ProjectInfoSnapshot Project { get; init; }

    public required IReadOnlyList<FieldSnapshot> Fields { get; init; }

    public required IReadOnlyList<ViewSnapshot> Views { get; init; }

    public required IReadOnlyList<WorkflowSnapshot> Workflows { get; init; }

    public required IReadOnlyList<ItemSnapshot> Items { get; init; }

    /// <summary>
    /// Project collaborators (users/teams with an explicit project role). Null when not
    /// captured: the GraphQL API has no read field for project collaborators
    /// (<c>ProjectV2ActorConnection</c> appears only on the
    /// <c>updateProjectV2Collaborators</c> mutation payload). Browser automation can
    /// populate explicit collaborators from Settings → Manage access; inherited/base-role
    /// access is not represented here. Hand-authored snapshots can also set it and import
    /// applies it.
    /// </summary>
    public IReadOnlyList<CollaboratorSnapshot>? Collaborators { get; init; }

    /// <summary>
    /// Repositories linked to the project, in "owner/name" form. Null when the snapshot
    /// predates this field (schema additions are backward compatible within version 1).
    /// </summary>
    public IReadOnlyList<string>? LinkedRepositories { get; init; }
}

/// <summary>A project collaborator: a user or a team with an explicit role.</summary>
public sealed record CollaboratorSnapshot
{
    /// <summary>Collaborator kind: USER or TEAM.</summary>
    public required string Type { get; init; }

    /// <summary>User login, or team slug for TEAM collaborators.</summary>
    public required string Login { get; init; }

    /// <summary>GraphQL <c>ProjectV2Roles</c> (READER, WRITER, ADMIN).</summary>
    public required string Role { get; init; }
}

/// <summary>Project-level metadata (title, description, README, visibility).</summary>
public sealed record ProjectInfoSnapshot
{
    public required string Title { get; init; }

    public string? ShortDescription { get; init; }

    public string? Readme { get; init; }

    public required bool Public { get; init; }

    public required bool Closed { get; init; }
}

/// <summary>
/// A project field (built-in or custom). <see cref="Options"/> is set for
/// SINGLE_SELECT fields and <see cref="IterationConfiguration"/> for ITERATION fields.
/// </summary>
public sealed record FieldSnapshot
{
    public required string Name { get; init; }

    /// <summary>GraphQL <c>ProjectV2FieldType</c> (TEXT, NUMBER, DATE, SINGLE_SELECT, ITERATION, TITLE, ...).</summary>
    public required string DataType { get; init; }

    public IReadOnlyList<SingleSelectOptionSnapshot>? Options { get; init; }

    public IterationConfigurationSnapshot? IterationConfiguration { get; init; }
}

public sealed record SingleSelectOptionSnapshot
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>GraphQL <c>ProjectV2SingleSelectFieldOptionColor</c> (GRAY, RED, BLUE, GREEN, ...).</summary>
    public required string Color { get; init; }

    public string? Description { get; init; }
}

/// <summary>
/// Iteration field configuration. Note: the GraphQL read side exposes
/// <c>startDay</c> (day of week) on the configuration; per-iteration start dates
/// live on the iterations themselves.
/// </summary>
public sealed record IterationConfigurationSnapshot
{
    /// <summary>Default duration of new iterations, in days.</summary>
    public required int Duration { get; init; }

    /// <summary>Day of the week new iterations start on (1 = Monday ... 7 = Sunday).</summary>
    public required int StartDay { get; init; }

    public required IReadOnlyList<IterationSnapshot> Iterations { get; init; }

    public required IReadOnlyList<IterationSnapshot> CompletedIterations { get; init; }
}

public sealed record IterationSnapshot
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>ISO 8601 date (yyyy-MM-dd).</summary>
    public required string StartDate { get; init; }

    /// <summary>Duration in days.</summary>
    public required int Duration { get; init; }
}

/// <summary>
/// A project view. GraphQL-readable settings are captured here;
/// UI-only settings (Slice by, Field sum, Roadmap dates/zoom/markers) are
/// reserved in <see cref="Ui"/> and populated by the browser module (M6).
/// </summary>
public sealed record ViewSnapshot
{
    public required int Number { get; init; }

    public required string Name { get; init; }

    /// <summary>GraphQL <c>ProjectV2ViewLayout</c> (TABLE_LAYOUT, BOARD_LAYOUT, ROADMAP_LAYOUT).</summary>
    public required string Layout { get; init; }

    public string? Filter { get; init; }

    /// <summary>Horizontal group-by field names (table layout).</summary>
    public required IReadOnlyList<string> GroupByFields { get; init; }

    public required IReadOnlyList<SortByFieldSnapshot> SortByFields { get; init; }

    /// <summary>Vertical group-by field names (board columns).</summary>
    public required IReadOnlyList<string> VerticalGroupByFields { get; init; }

    /// <summary>Visible field names in column order.</summary>
    public required IReadOnlyList<string> VisibleFields { get; init; }

    /// <summary>UI-only settings, scraped by the browser module (M6). Null until then.</summary>
    public ViewUiSnapshot? Ui { get; init; }
}

public sealed record SortByFieldSnapshot
{
    public required string Field { get; init; }

    /// <summary>GraphQL <c>OrderDirection</c> (ASC or DESC).</summary>
    public required string Direction { get; init; }
}

/// <summary>UI-only view settings (populated in M6 via browser automation).</summary>
public sealed record ViewUiSnapshot
{
    /// <summary>Current "Group by" value read from the view configuration menu (null when none).</summary>
    public string? GroupBy { get; init; }

    /// <summary>Current "Sort by" value read from the view configuration menu (null when none).</summary>
    public string? SortBy { get; init; }

    public string? SliceBy { get; init; }

    /// <summary>Board "Swimlanes" field (null when none; boards use Swimlanes, not Group by).</summary>
    public string? Swimlanes { get; init; }

    /// <summary>Board "Field sum" entries (e.g. "Count", number field names).</summary>
    public IReadOnlyList<string>? FieldSum { get; init; }

    public RoadmapSettingsSnapshot? Roadmap { get; init; }

    public DateTimeOffset? ScrapedAt { get; init; }
}

/// <summary>Roadmap-only UI settings (populated in M6 via browser automation).</summary>
public sealed record RoadmapSettingsSnapshot
{
    public string? StartField { get; init; }

    public string? TargetField { get; init; }

    /// <summary>Zoom level (Month, Quarter or Year).</summary>
    public string? Zoom { get; init; }

    public IReadOnlyList<string>? Markers { get; init; }
}

/// <summary>
/// A project workflow. GraphQL exposes only number/name/enabled;
/// detailed settings are reserved in <see cref="Ui"/> (M7).
/// </summary>
public sealed record WorkflowSnapshot
{
    public required int Number { get; init; }

    public required string Name { get; init; }

    public required bool Enabled { get; init; }

    /// <summary>UI-only workflow settings, scraped by the browser module (M7). Null until then.</summary>
    public WorkflowUiSnapshot? Ui { get; init; }
}

/// <summary>UI-only workflow settings (populated in M7 via browser automation).</summary>
public sealed record WorkflowUiSnapshot
{
    /// <summary>Content types the workflow applies to (ISSUE, PULL_REQUEST).</summary>
    public IReadOnlyList<string>? ContentTypes { get; init; }

    public string? StatusValue { get; init; }

    public string? Filter { get; init; }

    /// <summary>Auto-add target repository (short name; the picker is scoped to the org).</summary>
    public string? Repository { get; init; }

    public DateTimeOffset? ScrapedAt { get; init; }
}

/// <summary>
/// A project item. <see cref="Repository"/>/<see cref="Number"/> identify
/// Issues and Pull Requests; <see cref="Draft"/> carries draft issue content.
/// </summary>
public sealed record ItemSnapshot
{
    /// <summary>GraphQL <c>ProjectV2ItemType</c> (ISSUE, PULL_REQUEST, DRAFT_ISSUE).</summary>
    public required string Type { get; init; }

    /// <summary>Zero-based position in enumeration order.</summary>
    public required int Position { get; init; }

    public required bool IsArchived { get; init; }

    /// <summary>Repository in "owner/name" form (Issue/PR items only).</summary>
    public string? Repository { get; init; }

    /// <summary>Issue/PR number (Issue/PR items only).</summary>
    public int? Number { get; init; }

    public DraftIssueSnapshot? Draft { get; init; }

    public required IReadOnlyList<FieldValueSnapshot> FieldValues { get; init; }
}

public sealed record DraftIssueSnapshot
{
    public required string Title { get; init; }

    public string? Body { get; init; }

    /// <summary>Login of the original author (used for the attribution note on import).</summary>
    public string? Creator { get; init; }

    /// <summary>ISO 8601 creation timestamp (used for the attribution note on import).</summary>
    public string? CreatedAt { get; init; }

    public required IReadOnlyList<string> Assignees { get; init; }
}

/// <summary>A single field value on an item. Exactly one typed value is set.</summary>
public sealed record FieldValueSnapshot
{
    public required string FieldName { get; init; }

    public string? Text { get; init; }

    public double? Number { get; init; }

    /// <summary>ISO 8601 date (yyyy-MM-dd).</summary>
    public string? Date { get; init; }

    public string? SingleSelectOptionName { get; init; }

    public string? IterationTitle { get; init; }
}
