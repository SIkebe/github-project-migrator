using System.Globalization;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;
using Gpm.Core.Verify;

namespace Gpm.Integration.Tests;

/// <summary>
/// M5 integration tests: exports the fixture project, imports it into gpm-target and
/// verifies that <see cref="ProjectVerifier"/> reports a match (views/workflows produce
/// warnings only until M6/M7). Then drifts the target on purpose — deletes one custom
/// field via <c>deleteProjectV2Field</c> and changes an item's Status value — and asserts
/// both differences are detected as errors. The target project is deleted in a finally
/// block. Requires the GPM_TEST_TOKEN environment variable (SSO-authorized).
/// </summary>
public class VerifyTests
{
    private const int FixtureProjectNumber = 3;
    private const string FixtureRepo = "gpm-source/fixture-repo";
    private const string StatusFieldName = "Status";

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

    [Fact]
    public async Task Verify_matches_after_import_then_detects_deleted_field_and_changed_status()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = new GitHubGraphQLClient(Token);
        var exporter = new ProjectExporter(client);

        var source = await exporter.ExportAsync(SourceOrg, FixtureProjectNumber, cancellationToken);
        var snapshot = source with { Project = source.Project with { Title = "gpm-verify-test-" + Guid.NewGuid().ToString("N") } };

        var result = await new ProjectImporter(client).ImportAsync(snapshot, TargetOrg, cancellationToken);
        var logDirectory = Directory.CreateTempSubdirectory("gpm-m5-").FullName;
        try
        {
            var repoMapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [FixtureRepo] = FixtureRepo };
            await new ItemImporter(client) { RepositoryMapping = repoMapping }
                .ImportAsync(snapshot, result, logDirectory, cancellationToken);

            var verifier = new ProjectVerifier(client);

            // 1) Right after a full import the target matches the snapshot (items are
            //    eventually consistent, so poll until the report has no errors).
            var matchReport = await VerifyUntilAsync(verifier, snapshot, result.ProjectNumber, r => r.IsMatch, cancellationToken);
            Assert.True(matchReport.IsMatch, Describe(matchReport));
            Assert.DoesNotContain(matchReport.Differences, d => d.Severity == VerifySeverity.Error);

            // Views/workflows are not migrated until M6/M7 — warnings only.
            Assert.Contains(matchReport.Differences, d => d.Severity == VerifySeverity.Warning && d.Category == "View");

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
