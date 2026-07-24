using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;
using Ghpmv.Core.Import;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Integration.Tests;

/// <summary>
/// M3 integration tests: imports snapshots into the target org (gpm-target) via the real
/// GraphQL API. The round-trip test exports the fixture project (gpm-source #3), imports it
/// under a unique title, reads it back with <see cref="ProjectExporter"/> and compares fields.
/// Every created project is deleted in a finally block.
/// Requires the GHPMV_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ProjectImporterTests
{
    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;

    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

    private static string TargetOrg => IntegrationTestSettings.TargetOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GHPMV_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GHPMV_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static string NewTestTitle() => "ghpmv-import-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Full_round_trip_recreates_all_custom_fields_and_status_options()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Export the fixture and retarget it under a unique title.
        var exporter = new ProjectExporter(client);
        var exported = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var source = IntegrationFixtureSnapshot.SelectCanonicalItems(exported);
        var title = NewTestTitle();
        var snapshot = source with { Project = source.Project with { Title = title } };

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        };
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            Assert.True(result.Created);
            Assert.Equal(ProjectImportOutcome.Created, result.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(result.ProjectId));
            Assert.True(result.ProjectNumber > 0);
            Assert.Contains(TargetOrg, result.Url, StringComparison.OrdinalIgnoreCase);

            // Read back the imported project through the same exporter.
            var imported = await exporter.ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);

            Assert.Equal(title, imported.Project.Title);
            Assert.Equal(snapshot.Project.ShortDescription, imported.Project.ShortDescription);
            Assert.Equal(snapshot.Project.Readme, imported.Project.Readme);
            Assert.Equal(snapshot.Project.Public, imported.Project.Public);
            Assert.Equal(snapshot.Project.Closed, imported.Project.Closed);

            string[] creatable = ["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION"];
            foreach (var sourceField in snapshot.Fields.Where(f => creatable.Contains(f.DataType)))
            {
                var importedField = Assert.Single(imported.Fields, f => f.Name == sourceField.Name);
                Assert.Equal(sourceField.DataType, importedField.DataType);

                if (sourceField.Options is { Count: > 0 })
                {
                    Assert.NotNull(importedField.Options);
                    Assert.Equal(sourceField.Options.Select(o => o.Name), importedField.Options.Select(o => o.Name));
                    Assert.Equal(sourceField.Options.Select(o => o.Color), importedField.Options.Select(o => o.Color));
                    Assert.Equal(
                        sourceField.Options.Select(o => o.Description ?? string.Empty),
                        importedField.Options.Select(o => o.Description ?? string.Empty));

                    // Fresh option ids must have been issued and mapped.
                    Assert.True(result.OptionIds.ContainsKey(sourceField.Name));
                    Assert.Equal(
                        sourceField.Options.Select(o => o.Name).Order(StringComparer.Ordinal),
                        result.OptionIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
                }

                if (sourceField.IterationConfiguration is { } sourceConfig)
                {
                    Assert.NotNull(importedField.IterationConfiguration);
                    Assert.Equal(sourceConfig.Duration, importedField.IterationConfiguration.Duration);

                    // The API reclassifies iterations by date on read, so compare the unions.
                    static IEnumerable<(string Title, string StartDate, int Duration)> Union(IterationConfigurationSnapshot c)
                        => c.Iterations.Concat(c.CompletedIterations)
                            .Select(i => (i.Title, i.StartDate, i.Duration))
                            .OrderBy(i => i.StartDate, StringComparer.Ordinal)
                            .ThenBy(i => i.Title, StringComparer.Ordinal);

                    Assert.Equal(Union(sourceConfig), Union(importedField.IterationConfiguration));

                    Assert.True(result.IterationIds.ContainsKey(sourceField.Name));
                    Assert.Equal(
                        Union(sourceConfig).Select(i => i.Title).Order(StringComparer.Ordinal),
                        result.IterationIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
                }

                // All created fields must be present in the id map for M4.
                Assert.True(result.FieldIds.ContainsKey(sourceField.Name));
            }

            foreach (var sourceField in snapshot.Fields.Where(field => field.IssueField is not null))
            {
                Assert.True(result.IssueFieldIds.ContainsKey(sourceField.Name));
                Assert.True(result.FieldIds.ContainsKey(sourceField.Name));
                Assert.Equal(
                    (sourceField.Options ?? []).Select(option => option.Name).Order(StringComparer.Ordinal),
                    result.IssueFieldOptionIds[sourceField.Name].Keys.Order(StringComparer.Ordinal));
            }
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
        }
    }

    [Fact]
    public async Task Import_with_conflict_fail_throws_when_title_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var title = NewTestTitle();
        var snapshot = MinimalSnapshot(title);

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }; // OnConflict defaults to Fail.
        var first = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => importer.ImportAsync(snapshot, TargetOrg, cancellationToken));
            Assert.Contains(title, exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            await DeleteProjectAsync(client, first.ProjectId);
        }
    }

    [Fact]
    public async Task Import_with_conflict_skip_returns_existing_without_duplicating()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var title = NewTestTitle();
        var snapshot = MinimalSnapshot(title);

        var first = await new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var second = await new ProjectImporter(client)
            {
                OnConflict = ConflictAction.Skip,
                OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
            }
                .ImportAsync(snapshot, TargetOrg, cancellationToken);

            Assert.False(second.Created);
            Assert.Equal(ProjectImportOutcome.Skipped, second.Outcome);
            Assert.Equal(first.ProjectId, second.ProjectId);
            Assert.Equal(first.ProjectNumber, second.ProjectNumber);
            Assert.Empty(second.FieldIds);
        }
        finally
        {
            await DeleteProjectAsync(client, first.ProjectId);
        }
    }

    [Fact]
    public async Task Import_into_existing_project_by_number_merges_fields_and_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Create an empty target project directly through the API.
        var title = NewTestTitle();
        var orgData = await client.QueryAsync(
            "query($login: String!) { organization(login: $login) { id } }",
            new { login = TargetOrg },
            cancellationToken);
        var created = await client.QueryAsync(
            """
            mutation($ownerId: ID!, $title: String!) {
              createProjectV2(input: { ownerId: $ownerId, title: $title }) {
                projectV2 { id number }
              }
            }
            """,
            new { ownerId = orgData.GetProperty("organization").GetProperty("id").GetString(), title },
            cancellationToken);
        var emptyProject = created.GetProperty("createProjectV2").GetProperty("projectV2");
        var emptyProjectId = emptyProject.GetProperty("id").GetString()!;
        var emptyProjectNumber = emptyProject.GetProperty("number").GetInt32();

        var logDirectory = Path.Combine(Path.GetTempPath(), "ghpmv-into-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(logDirectory);
        try
        {
            // Export the fixture and apply it to the existing project by number.
            var exporter = new ProjectExporter(client);
            var exported = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
            var snapshot = IntegrationFixtureSnapshot.SelectCanonicalItems(exported);

            var importer = new ProjectImporter(client)
            {
                OperationLogDirectory = logDirectory,
            };
            var result = await importer.ImportIntoAsync(snapshot, TargetOrg, emptyProjectNumber, cancellationToken);

            Assert.False(result.Created);
            Assert.Equal(ProjectImportOutcome.Updated, result.Outcome);
            Assert.Equal(emptyProjectId, result.ProjectId);
            Assert.Equal(emptyProjectNumber, result.ProjectNumber);

            // Items go in through the normal item import path (identity repo mapping).
            var repositories = snapshot.Items.Where(i => i.Repository is not null).Select(i => i.Repository!).Distinct(StringComparer.OrdinalIgnoreCase);
            var itemImporter = new ItemImporter(client)
            {
                RepositoryMapping = repositories.ToDictionary(r => r, r => r, StringComparer.OrdinalIgnoreCase),
            };
            var itemResult = await itemImporter.ImportAsync(snapshot, result, logDirectory, cancellationToken);
            Assert.Equal(snapshot.Items.Count, itemResult.Created);

            // The existing project keeps its own title but gains the snapshot's custom fields.
            var readBack = await exporter.ExportAsync(TargetOrg, emptyProjectNumber, cancellationToken);
            Assert.Equal(title, readBack.Project.Title);
            string[] creatable = ["TEXT", "NUMBER", "DATE", "SINGLE_SELECT", "ITERATION"];
            foreach (var field in snapshot.Fields.Where(f => creatable.Contains(f.DataType)))
            {
                Assert.Contains(readBack.Fields, f => f.Name == field.Name && f.DataType == field.DataType);
            }
        }
        finally
        {
            await DeleteProjectAsync(client, emptyProjectId);
            Directory.Delete(logDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Import_into_missing_project_number_throws()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        var importer = new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportIntoAsync(MinimalSnapshot(NewTestTitle()), TargetOrg, 999_999, cancellationToken));
        Assert.Contains("999999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_with_overridden_title_creates_project_with_new_title()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Same rewrite the CLI applies for --project-title.
        var overriddenTitle = NewTestTitle();
        var snapshot = MinimalSnapshot("ghpmv-original-title");
        snapshot = snapshot with { Project = snapshot.Project with { Title = overriddenTitle } };

        var result = await new ProjectImporter(client)
        {
            OperationLogDirectory = IntegrationTestSettings.CreateOperationLogDirectory(),
        }.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            Assert.True(result.Created);
            var readBack = await new ProjectExporter(client).ExportAsync(TargetOrg, result.ProjectNumber, cancellationToken);
            Assert.Equal(overriddenTitle, readBack.Project.Title);
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
        }
    }

    private static ProjectSnapshot MinimalSnapshot(string title) => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = title,
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }
}
