using Gpm.Core.Browser;
using Gpm.Core.GitHub;

namespace Gpm.Core.Tests;

public class BrowserProjectUrlTests
{
    [Theory]
    [InlineData(ProjectOwnerType.Organization, "orgs")]
    [InlineData(ProjectOwnerType.User, "users")]
    public void Builds_all_browser_verification_routes_for_owner_type(
        ProjectOwnerType ownerType,
        string ownerPath)
    {
        const string BaseUrl = "https://github.example.com/";
        const string Owner = "octocat";
        const int ProjectNumber = 42;

        Assert.Equal(
            $"https://github.example.com/{ownerPath}/{Owner}/projects/{ProjectNumber}/views/7",
            BrowserProjectUrl.Build(BaseUrl, Owner, ownerType, ProjectNumber, "views/7"));
        Assert.Equal(
            $"https://github.example.com/{ownerPath}/{Owner}/projects/{ProjectNumber}/workflows",
            BrowserProjectUrl.Build(BaseUrl, Owner, ownerType, ProjectNumber, "workflows"));
        Assert.Equal(
            $"https://github.example.com/{ownerPath}/{Owner}/projects/{ProjectNumber}/settings/access",
            BrowserProjectUrl.Build(BaseUrl, Owner, ownerType, ProjectNumber, "settings/access"));
    }
}
