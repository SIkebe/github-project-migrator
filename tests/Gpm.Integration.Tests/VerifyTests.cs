using System.Globalization;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;
using Gpm.Core.Verify;

namespace Gpm.Integration.Tests;

/// <summary>
/// M5 integration tests: exports the fixture project, imports it into gpm-target and
/// verifies that <see cref="ProjectVerifier"/> reports no differences beyond the
/// views/workflows that only the browser module migrates (errors since M6/M7; this
/// test imports via the API only). Then drifts the target on purpose — deletes one
/// custom field via <c>deleteProjectV2Field</c> and changes an item's Status value —
/// and asserts both differences are detected as errors. The target project is deleted
/// in a finally block. Requires the GPM_TEST_TOKEN environment variable (SSO-authorized).
/// </summary>
public class VerifyTests
{
    private static int FixtureProjectNumber => IntegrationTestSettings.FixtureProjectNumber;
    private static string FixtureRepo => IntegrationTestSettings.FixtureRepositoryFullName;
    private const string StatusFieldName = "Status";

    private static string SourceOrg => IntegrationTestSettings.SourceOrg;

    private static string TargetOrg => IntegrationTestSettings.TargetOrg;

    private static string Token
    {
        get
        {
            var token = Environment.GetEnvironmentVariable("GPM_TEST_TOKEN");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(token), "GPM_TEST_TOKEN is not set; skipping real-API test.");
            return token!;
        }
    }

    [Fact]
    public async Task Verify_matches_after_import_then_detects_deleted_field_and_changed_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);

        var exported = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var source = IntegrationFixtureSnapshot.SelectCanonicalItems(exported);

        // Guard against silent null==null passes: the enriched fixture must actually carry
        // the elements this test claims to verify end-to-end.
        Assert.Equal("gpm fixture project", source.Project.ShortDescription);
        Assert.False(string.IsNullOrWhiteSpace(source.Project.Readme));
        Assert.Contains(
            source.Fields.Single(f => f.Name == "Fixture Sprint").IterationConfiguration!.CompletedIterations,
            i => i.Title == "Sprint 0");
        Assert.Equal(7, source.Items.Count);
        Assert.Contains(source.Items, i => i.Type == "ISSUE");
        Assert.Contains(source.Items, i => i.Type == "PULL_REQUEST");
        Assert.Contains(source.Items, i => i.IsArchived);
        Assert.Contains(source.Items, i => i.Draft?.Assignees is { Count: > 0 });
        Assert.NotNull(source.LinkedRepositories);

        var snapshot = source with { Project = source.Project with { Title = "gpm-verify-test-" + Guid.NewGuid().ToString("N") } };

        var result = await new ProjectImporter(client).ImportAsync(snapshot, TargetOrg, cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("gpm-m5-").FullName;
        try
        {
            var repoMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [FixtureRepo] = FixtureRepo };
            await new ItemImporter(client) { RepositoryMapping = repoMapping }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);
            await IntegrationFixtureSnapshot.RemoveUnexpectedItemsAsync(
                client, TargetOrg, result.ProjectNumber, snapshot, cancellationToken);

            var postExportCalled = false;
            var verifier = new ProjectVerifier(client)
            {
                PostExportAsync = (target, _) =>
                {
                    postExportCalled = true;
                    return Task.FromResult(target);
                },
            };

            // 1) Right after a full API import the target matches the snapshot except for
            //    views/workflows, which only the browser module migrates (errors since M7).
            //    Items are eventually consistent, so poll until no other error remains.
            var matchReport = await VerifyUntilAsync(verifier, snapshot, result.ProjectNumber, r => !HasNonBrowserError(r), cancellationToken);
            Assert.True(postExportCalled);
            Assert.DoesNotContain(matchReport.Differences, d => d.Severity == VerifySeverity.Error && !IsBrowserCategory(d));
            Assert.Contains(matchReport.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "View");
            Assert.Contains(matchReport.Differences, d => d.Severity == VerifySeverity.Error && d.Category == "Workflow");

            // 2) Drift the target: delete one custom TEXT field...
            var fieldName = snapshot.Fields.First(f => f.DataType == "TEXT").Name;
            await client.QueryAsync(
                """
                mutation($fieldId: ID!) {
                  deleteProjectV2Field(input: { fieldId: $fieldId }) { clientMutationId }
                }
                """,
                new { fieldId = result.FieldIds[fieldName] },
                cancellationToken);

            // ...and flip the Status value of one imported (non-archived) item.
            var log = await ImportLog.LoadAsync(logDirectory, cancellationToken);
            Assert.NotNull(log);
            var statusItem = snapshot.Items
                .OrderBy(i => i.Position)
                .First(i => !i.IsArchived && i.FieldValues.Any(v => v.FieldName == StatusFieldName && v.SingleSelectOptionName is not null));
            var itemId = log.Items[statusItem.Position.ToString(CultureInfo.InvariantCulture)];
            var currentStatus = statusItem.FieldValues.First(v => v.FieldName == StatusFieldName).SingleSelectOptionName!;
            var otherOptionId = result.OptionIds[StatusFieldName].First(kvp => !string.Equals(kvp.Key, currentStatus, StringComparison.Ordinal)).Value;
            await client.QueryAsync(
                """
                mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
                  updateProjectV2ItemFieldValue(input: {
                    projectId: $projectId, itemId: $itemId, fieldId: $fieldId,
                    value: { singleSelectOptionId: $optionId }
                  }) { projectV2Item { id } }
                }
                """,
                new { projectId = result.ProjectId, itemId, fieldId = result.FieldIds[StatusFieldName], optionId = otherOptionId },
                cancellationToken);

            // 3) Both drifts are reported as errors.
            var driftReport = await VerifyUntilAsync(verifier, snapshot, result.ProjectNumber, r =>
                r.Differences.Any(d => d.Severity == VerifySeverity.Error && d.Category == "Field" && d.Message.Contains($"'{fieldName}'", StringComparison.Ordinal))
                && r.Differences.Any(d => d.Severity == VerifySeverity.Error && d.Category == "Item" && d.Message.Contains($"'{StatusFieldName}' value mismatch", StringComparison.Ordinal)),
                cancellationToken);

            Assert.False(driftReport.IsMatch, Describe(driftReport));
            Assert.Contains(driftReport.Differences, d =>
                d.Severity == VerifySeverity.Error
                && d.Category == "Field"
                && d.Message.Contains($"'{fieldName}'", StringComparison.Ordinal)
                && d.Message.Contains("missing in the target", StringComparison.Ordinal));
            Assert.Contains(driftReport.Differences, d =>
                d.Severity == VerifySeverity.Error
                && d.Category == "Item"
                && d.Message.Contains($"'{StatusFieldName}' value mismatch", StringComparison.Ordinal));
        }
        finally
        {
            await DeleteProjectAsync(client, result.ProjectId);
            TryDeleteDirectory(logDirectory);
        }
    }

    /// <summary>The target is eventually consistent after writes; re-verify until the predicate holds.</summary>
    private static async Task<VerifyReport> VerifyUntilAsync(
        ProjectVerifier verifier, ProjectSnapshot snapshot, int projectNumber, Func<VerifyReport, bool> predicate, CancellationToken cancellationToken)
    {
        VerifyReport report = null!;
        for (var attempt = 0; attempt < 7; attempt++)
        {
            report = await verifier.VerifyAsync(snapshot, TargetOrg, projectNumber, cancellationToken);
            if (predicate(report))
            {
                return report;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        return report;
    }

    private static string Describe(VerifyReport report)
        => string.Join(Environment.NewLine, report.Differences.Select(d => $"{d.Severity} {d.Category}: {d.Message}"));

    /// <summary>Views/workflows are migrated only by the browser module, not by an API-only import.</summary>
    private static bool IsBrowserCategory(VerifyDifference difference)
        => difference.Category is "View" or "Workflow";

    private static bool HasNonBrowserError(VerifyReport report)
        => report.Differences.Any(d => d.Severity == VerifySeverity.Error && !IsBrowserCategory(d));

    private static async Task DeleteProjectAsync(GitHubGraphQLClient client, string projectId)
    {
        await client.QueryAsync(
            "mutation($projectId: ID!) { deleteProjectV2(input: { projectId: $projectId }) { projectV2 { id } } }",
            new { projectId },
            CancellationToken.None);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of the temp log directory.
        }
    }
}
