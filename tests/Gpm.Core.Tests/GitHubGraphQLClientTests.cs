using System.Net;
using System.Text;
using System.Text.Json;
using Gpm.Core.GitHub;

namespace Gpm.Core.Tests;

public class GitHubGraphQLClientTests
{
    private const string ViewerData = """{"data":{"viewer":{"login":"octocat"}}}""";

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

    [Theory]
    [InlineData("https://api.tenant.ghe.com", "https://api.tenant.ghe.com/graphql")]
    [InlineData("https://api.tenant.ghe.com/", "https://api.tenant.ghe.com/graphql")]
    [InlineData("https://api.tenant.ghe.com/graphql", "https://api.tenant.ghe.com/graphql")]
    [InlineData("https://api.tenant.ghe.com/graphql/", "https://api.tenant.ghe.com/graphql")]
    [InlineData(" https://api.github.com/graphql ", "https://api.github.com/graphql")]
    [InlineData("http://localhost:8080", "http://localhost:8080/graphql")]
    public void NormalizeBaseUrl_appends_graphql_when_missing(string input, string expected)
    {
        Assert.Equal(new Uri(expected), GitHubGraphQLClient.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("api.tenant.ghe.com")]
    [InlineData("ftp://api.tenant.ghe.com")]
    [InlineData("http://api.tenant.ghe.com")]
    [InlineData("not a url")]
    public void NormalizeBaseUrl_rejects_invalid_or_insecure_non_loopback_urls(string input)
    {
        Assert.Throws<FormatException>(() => GitHubGraphQLClient.NormalizeBaseUrl(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void NormalizeBaseUrl_rejects_empty_input(string input)
    {
        Assert.Throws<ArgumentException>(() => GitHubGraphQLClient.NormalizeBaseUrl(input));
    }

    [Fact]
    public async Task Forbidden_with_RetryAfter_waits_and_retries()
    {
        var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message":"rate limited"}""", Encoding.UTF8, "application/json"),
        };
        forbidden.Headers.Add("Retry-After", "7");

        using var handler = new StubHandler(forbidden, JsonResponse(HttpStatusCode.OK, ViewerData));
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler, delays);

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", login);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.Equal([TimeSpan.FromSeconds(7)], delays);
    }

    [Fact]
    public async Task Secondary_rate_limit_uses_exponential_backoff()
    {
        var secondary = """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""";
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.Forbidden, secondary),
            JsonResponse(HttpStatusCode.Forbidden, secondary),
            JsonResponse(HttpStatusCode.Forbidden, secondary),
            JsonResponse(HttpStatusCode.OK, ViewerData));
        var delays = new List<TimeSpan>();
        var retryMessages = new List<string>();
        using var client = CreateClient(handler, delays);
        client.OnRetry = retryMessages.Add;

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", login);
        Assert.Equal(4, handler.RequestBodies.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
        Assert.Equal(3, retryMessages.Count);
        Assert.All(retryMessages, m => Assert.Contains("Secondary rate limit", m, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Server_errors_retry_three_times_then_throw_with_status()
    {
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.BadGateway, "bad gateway"),
            JsonResponse(HttpStatusCode.BadGateway, "bad gateway"),
            JsonResponse(HttpStatusCode.BadGateway, "bad gateway"),
            JsonResponse(HttpStatusCode.BadGateway, "bad gateway"));
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler, delays);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(4, handler.RequestBodies.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Fact]
    public async Task GraphQL_errors_are_surfaced_with_errors_json_and_type()
    {
        var body = """{"data":null,"errors":[{"type":"NOT_FOUND","message":"Could not resolve to an Organization."}]}""";
        using var handler = new StubHandler(JsonResponse(HttpStatusCode.OK, body));
        using var client = CreateClient(handler, []);

        var exception = await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => client.QueryAsync("query { nothing }", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("NOT_FOUND", exception.ErrorType);
        Assert.NotNull(exception.ErrorsJson);
        Assert.Contains("Could not resolve to an Organization.", exception.ErrorsJson, StringComparison.Ordinal);
        Assert.Null(exception.StatusCode);
    }

    [Fact]
    public async Task QueryPaginatedAsync_enumerates_all_nodes_across_pages()
    {
        var page1 = """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"title":"P1"},{"title":"P2"}],
              "pageInfo":{"hasNextPage":true,"endCursor":"CURSOR-1"}}}}}
            """;
        var page2 = """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"title":"P3"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}}}}}
            """;
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, page1),
            JsonResponse(HttpStatusCode.OK, page2));
        using var client = CreateClient(handler, []);

        var titles = new List<string?>();
        await foreach (var node in client.QueryPaginatedAsync(
            "query($login: String!, $after: String) { organization(login: $login) { projectsV2(first: 2, after: $after) { nodes { title } pageInfo { hasNextPage endCursor } } } }",
            new { login = "gpm-source" },
            "organization.projectsV2",
            cancellationToken: TestContext.Current.CancellationToken))
        {
            titles.Add(node.GetProperty("title").GetString());
        }

        Assert.Equal(["P1", "P2", "P3"], titles);
        Assert.Equal(2, handler.RequestBodies.Count);

        // First request sends a null cursor; second one carries the endCursor of page 1.
        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("variables").GetProperty("after").ValueKind);
        Assert.Equal("gpm-source", first.RootElement.GetProperty("variables").GetProperty("login").GetString());

        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("CURSOR-1", second.RootElement.GetProperty("variables").GetProperty("after").GetString());
    }

    [Fact]
    public async Task Primary_rate_limit_exhaustion_waits_until_reset()
    {
        var reset = DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds();
        var response = JsonResponse(HttpStatusCode.OK, ViewerData);
        response.Headers.Add("X-RateLimit-Remaining", "0");
        response.Headers.Add("X-RateLimit-Reset", reset.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var handler = new StubHandler(response);
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler, delays);

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", login);
        var delay = Assert.Single(delays);
        Assert.InRange(delay, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));
    }

    private static GitHubGraphQLClient CreateClient(StubHandler handler, List<TimeSpan> delays)
        => new("dummy-token", baseUrl: null, handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task Temporary_conflict_errors_are_retried()
    {
        const string conflict = """{"data":null,"errors":[{"type":"UNPROCESSABLE","message":"Your attempt to move this item created a temporary conflict. Please try again."}]}""";
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, conflict),
            JsonResponse(HttpStatusCode.OK, ViewerData));
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler, delays);

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", login);
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
    }

    [Fact]
    public async Task Create_mutation_transport_failure_is_not_retried()
    {
        using var handler = new FlakyHandler(
            failuresBeforeSuccess: int.MaxValue,
            () => JsonResponse(HttpStatusCode.OK, """{"data":{"createThing":{"id":"created"}}}"""));
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($name: String!, $clientMutationId: String!) { createThing(input: { name: $name, clientMutationId: $clientMutationId }) { id } }",
                new { name = "secret-name" },
                target: "target-project",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Attempts);
        Assert.Equal("createThing", exception.OperationName);
        Assert.Equal("target-project", exception.Target);
        Assert.NotEmpty(exception.ClientMutationId);
        Assert.Null(exception.StatusCode);
        Assert.DoesNotContain("secret-name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Create_mutation_server_error_is_not_retried()
    {
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.BadGateway, "bad gateway"),
            JsonResponse(HttpStatusCode.OK, """{"data":{"createThing":{"id":"duplicate"}}}"""));
        using var client = CreateClient(handler, []);

        var exception = await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { id } }",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task Create_mutation_timeout_is_ambiguous_and_not_retried()
    {
        using var handler = new TimeoutHandler();
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, (_, _) => Task.CompletedTask);

        await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { id } }",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task Create_mutation_malformed_success_response_is_ambiguous_and_not_retried()
    {
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, """{"data":"""),
            JsonResponse(HttpStatusCode.OK, """{"data":{"createThing":{"id":"duplicate"}}}"""));
        using var client = CreateClient(handler, []);

        await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { id } }",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Single(handler.RequestBodies);
    }

    [Theory]
    [InlineData("""{"data":null}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"createThing":null}}""")]
    [InlineData("""{"data":{"createThing":{}}}""")]
    public async Task Create_mutation_incomplete_success_response_is_ambiguous(string responseBody)
    {
        using var handler = new StubHandler(JsonResponse(HttpStatusCode.OK, responseBody));
        using var client = CreateClient(handler, []);

        var exception = await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { id } }",
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains(exception.AttemptedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), exception.Message, StringComparison.Ordinal);
        Assert.Single(handler.RequestBodies);
    }

    [Fact]
    public async Task Create_mutation_missing_required_nested_result_is_ambiguous()
    {
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, """{"data":{"createThing":{"thing":null}}}"""));
        using var client = CreateClient(handler, []);

        await Assert.ThrowsAsync<AmbiguousMutationResultException>(
            () => client.MutationAsync(
                "createThing",
                "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { thing { id } } }",
                requiredResultPath: "thing.id",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Idempotent_mutation_retries_malformed_success_response()
    {
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, """{"data":"""),
            JsonResponse(HttpStatusCode.OK, """{"data":{"updateThing":{"id":"updated"}}}"""));
        var delays = new List<TimeSpan>();
        using var client = CreateClient(handler, delays);

        var data = await client.MutationAsync(
            "updateThing",
            "mutation($clientMutationId: String!) { updateThing(input: { clientMutationId: $clientMutationId }) { id } }",
            retryPolicy: MutationRetryPolicy.Idempotent,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("updated", data.GetProperty("updateThing").GetProperty("id").GetString());
        Assert.Equal([TimeSpan.FromSeconds(1)], delays);
        Assert.Equal(2, handler.RequestBodies.Count);
    }

    [Fact]
    public async Task Idempotent_mutation_retries_transport_failures()
    {
        using var handler = new FlakyHandler(
            failuresBeforeSuccess: 2,
            () => JsonResponse(HttpStatusCode.OK, """{"data":{"updateThing":{"id":"updated"}}}"""));
        var delays = new List<TimeSpan>();
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var data = await client.MutationAsync(
            "updateThing",
            "mutation($clientMutationId: String!) { updateThing(input: { clientMutationId: $clientMutationId }) { id } }",
            retryPolicy: MutationRetryPolicy.Idempotent,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("updated", data.GetProperty("updateThing").GetProperty("id").GetString());
        Assert.Equal(3, handler.Attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Create_mutation_retries_explicit_temporary_conflict_with_same_client_mutation_id()
    {
        const string conflict = """{"data":null,"errors":[{"type":"UNPROCESSABLE","message":"Temporary conflict. Please try again."}]}""";
        using var handler = new StubHandler(
            JsonResponse(HttpStatusCode.OK, conflict),
            JsonResponse(HttpStatusCode.OK, """{"data":{"createThing":{"id":"created"}}}"""));
        using var client = CreateClient(handler, []);

        var data = await client.MutationAsync(
            "createThing",
            "mutation($clientMutationId: String!) { createThing(input: { clientMutationId: $clientMutationId }) { id } }",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("created", data.GetProperty("createThing").GetProperty("id").GetString());
        Assert.Equal(2, handler.RequestBodies.Count);
        using var first = JsonDocument.Parse(handler.RequestBodies[0]);
        using var second = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(
            first.RootElement.GetProperty("variables").GetProperty("clientMutationId").GetString(),
            second.RootElement.GetProperty("variables").GetProperty("clientMutationId").GetString());
    }

    [Fact]
    public async Task Transient_network_errors_are_retried_with_backoff()
    {
        using var handler = new FlakyHandler(
            failuresBeforeSuccess: 2,
            () => JsonResponse(HttpStatusCode.OK, ViewerData));
        var delays = new List<TimeSpan>();
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var login = await client.GetViewerLoginAsync(TestContext.Current.CancellationToken);

        Assert.Equal("octocat", login);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Persistent_network_errors_throw_after_retry_budget()
    {
        using var handler = new FlakyHandler(
            failuresBeforeSuccess: int.MaxValue,
            () => JsonResponse(HttpStatusCode.OK, ViewerData));
        var delays = new List<TimeSpan>();
        using var client = new GitHubGraphQLClient("dummy-token", baseUrl: null, handler, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<GitHubGraphQLException>(
            () => client.GetViewerLoginAsync(TestContext.Current.CancellationToken));
        Assert.Equal(3, delays.Count);
    }

    private sealed class FlakyHandler(int failuresBeforeSuccess, Func<HttpResponseMessage> onSuccess) : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _attempts++;
            return _attempts <= failuresBeforeSuccess
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("The response ended prematurely."))
                : Task.FromResult(onSuccess());
        }
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromException<HttpResponseMessage>(new TaskCanceledException("The request timed out."));
        }
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string body)
        => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public StubHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return _responses.Dequeue();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                while (_responses.Count > 0)
                {
                    _responses.Dequeue().Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}
