using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

/// <summary>
/// M3 integration tests: imports snapshots into the target org (gpm-target) via the real
/// GraphQL API. The round-trip test exports the fixture project (gpm-source #3), imports it
/// under a unique title, reads it back with <see cref="ProjectExporter"/> and compares fields.
/// Every created project is deleted in a finally block.
/// Requires the GPM_TEST_TOKEN environment variable (SSO-authorized for the test orgs).
/// </summary>
public class ProjectImporterTests
{
    private const int FixtureProjectNumber = 3;

    private static string SourceOrg => Environment.GetEnvironmentVariable("GPM_TEST_ORG") ?? "gpm-source";

    private static string TargetOrg => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_ORG") ?? "gpm-target";

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    private static string NewTestTitle() => "gpm-import-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Full_round_trip_recreates_all_custom_fields_and_status_options()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);

        // Export the fixture and retarget it under a unique title.
        var exporter = new ProjectExporter(client);
        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var title = NewTestTitle();
        var snapshot = source with { Project = source.Project with { Title = title } };

        var importer = new ProjectImporter(client);
        var result = await importer.ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            Assert.True(result.Created);
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

        var importer = new ProjectImporter(client); // OnConflict defaults to Fail.
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

        var first = await new ProjectImporter(client).ImportAsync(snapshot, TargetOrg, cancellationToken);
        try
        {
            var second = await new ProjectImporter(client) { OnConflict = ConflictAction.Skip }
                .ImportAsync(snapshot, TargetOrg, cancellationToken);

            Assert.False(second.Created);
            Assert.Equal(first.ProjectId, second.ProjectId);
            Assert.Equal(first.ProjectNumber, second.ProjectNumber);

            // The skip path still returns the field maps (built-in Status included).
            Assert.True(second.FieldIds.ContainsKey("Status"));
        }
        finally
        {
            await DeleteProjectAsync(client, first.ProjectId);
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
