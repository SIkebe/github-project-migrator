using System.Globalization;
using Gpm.Core.GitHub;

namespace Gpm.Core.Browser;

internal static class BrowserProjectUrl
{
    public static string Build(
        string baseUrl,
        string ownerLogin,
        ProjectOwnerType ownerType,
        int projectNumber,
        string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var ownerPath = ownerType == ProjectOwnerType.User ? "users" : "orgs";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{baseUrl.TrimEnd('/')}/{ownerPath}/{ownerLogin}/projects/{projectNumber}/{relativePath.TrimStart('/')}");
    }
}
