namespace Gpm.Integration.Tests;

internal static class IntegrationTestSettings
{
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
