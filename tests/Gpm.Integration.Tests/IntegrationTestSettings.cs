namespace Gpm.Integration.Tests;

internal static class IntegrationTestSettings
{
    public static string SourceOrg => Environment.GetEnvironmentVariable("GPM_TEST_ORG")
        ?? Environment.GetEnvironmentVariable("GPM_SOURCE_ORG")
        ?? "gpm-source";

    public static string TargetOrg => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_ORG")
        ?? Environment.GetEnvironmentVariable("GPM_TARGET_ORG")
        ?? "gpm-target";

    public static string FixtureRepositoryName => Environment.GetEnvironmentVariable("GPM_TEST_FIXTURE_REPO")
        ?? Environment.GetEnvironmentVariable("GPM_FIXTURE_REPO")
        ?? "fixture-repo2";

    public static string TargetFixtureRepositoryName => Environment.GetEnvironmentVariable("GPM_TEST_TARGET_FIXTURE_REPO")
        ?? "fixture-repo";

    public static string FixtureRepositoryFullName => $"{SourceOrg}/{FixtureRepositoryName}";

    public static string TargetFixtureRepositoryFullName => $"{TargetOrg}/{TargetFixtureRepositoryName}";

    public static int FixtureProjectNumber
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("GPM_TEST_PROJECT_NUMBER");
            if (string.IsNullOrWhiteSpace(value))
            {
                return 89;
            }

            return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
