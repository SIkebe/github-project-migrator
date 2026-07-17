using System.Globalization;

namespace Ghpmv.Integration.Tests;

internal static class IntegrationTestSettings
{
    public static string SourceOrg => Environment.GetEnvironmentVariable("GHPMV_TEST_ORG")
        ?? Environment.GetEnvironmentVariable("GHPMV_SOURCE_ORG")
        ?? "ghpmv-source";

    public static string TargetOrg => Environment.GetEnvironmentVariable("GHPMV_TEST_TARGET_ORG")
        ?? Environment.GetEnvironmentVariable("GHPMV_TARGET_ORG")
        ?? "ghpmv-target";

    public static string FixtureRepositoryName => Environment.GetEnvironmentVariable("GHPMV_TEST_FIXTURE_REPO")
        ?? Environment.GetEnvironmentVariable("GHPMV_FIXTURE_REPO")
        ?? "fixture-repo2";

    public static string TargetFixtureRepositoryName => Environment.GetEnvironmentVariable("GHPMV_TEST_TARGET_FIXTURE_REPO")
        ?? "fixture-repo";

    public static string FixtureRepositoryFullName => $"{SourceOrg}/{FixtureRepositoryName}";

    public static string TargetFixtureRepositoryFullName => $"{TargetOrg}/{TargetFixtureRepositoryName}";

    public static string CreateOperationLogDirectory()
        => Path.Combine(Path.GetTempPath(), $"ghpmv-project-import-{Guid.NewGuid():N}");

    public static int FixtureProjectNumber
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("GHPMV_TEST_PROJECT_NUMBER");
            if (string.IsNullOrWhiteSpace(value))
            {
                return 89;
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var projectNumber))
            {
                return projectNumber;
            }

            throw new FormatException($"GHPMV_TEST_PROJECT_NUMBER must be a valid integer, but was '{value}'.");
        }
    }
}
