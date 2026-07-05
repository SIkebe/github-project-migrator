using Gpm.Core.GitHub;

namespace Gpm.Core.Tests;

public class GitHubGraphQLClientTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_rejects_empty_token(string token)
    {
        Assert.Throws<ArgumentException>(() => new GitHubGraphQLClient(token));
    }

    [Fact]
    public void Constructor_rejects_null_token()
    {
        Assert.Throws<ArgumentNullException>(() => new GitHubGraphQLClient(null!));
    }

    [Fact]
    public async Task QueryAsync_rejects_empty_query()
    {
        using var client = new GitHubGraphQLClient("dummy-token");

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.QueryAsync("", cancellationToken: TestContext.Current.CancellationToken));
    }
}
