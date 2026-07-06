using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Gpm.Core.GitHub;

/// <summary>
/// GraphQL client for the GitHub API (M1).
/// Supports cursor pagination, primary/secondary rate-limit handling and 5xx retries.
/// </summary>
public sealed class GitHubGraphQLClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://api.github.com/graphql");

    private const int MaxSecondaryRateLimitRetries = 5;
    private const int MaxServerErrorRetries = 3;
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public GitHubGraphQLClient(string token, Uri? baseUrl = null)
        : this(token, baseUrl, new HttpClientHandler(), delayAsync: null)
    {
    }

    internal GitHubGraphQLClient(string token, Uri? baseUrl, HttpMessageHandler handler, Func<TimeSpan, CancellationToken, Task>? delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);

        _httpClient = new HttpClient(handler) { BaseAddress = baseUrl ?? DefaultEndpoint };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("gpm");
        _delayAsync = delayAsync ?? (static (delay, ct) => Task.Delay(delay, ct));
    }

    /// <summary>Invoked with a human-readable message before every rate-limit/retry wait.</summary>
    public Action<string>? OnRetry { get; set; }

    /// <summary>
    /// Normalizes a GraphQL API base URL given on the command line: accepts both
    /// "https://api.TENANT.ghe.com" and "https://api.TENANT.ghe.com/graphql" (with or
    /// without a trailing slash) and returns the endpoint URI ending in "/graphql".
    /// </summary>
    public static Uri NormalizeBaseUrl(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException($"'{baseUrl}' is not an absolute http(s) URL.");
        }

        if (!trimmed.EndsWith("/graphql", StringComparison.OrdinalIgnoreCase))
        {
            trimmed += "/graphql";
        }

        return new Uri(trimmed);
    }

    /// <summary>Executes a GraphQL query and returns the "data" element.</summary>
    public async Task<JsonElement> QueryAsync(string query, object? variables = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var payload = JsonSerializer.Serialize(new { query, variables });
        var temporaryConflictRetries = 0;

        while (true)
        {
            using var document = await ExecuteAsync(payload, cancellationToken).ConfigureAwait(false);

            if (!document.RootElement.TryGetProperty("errors", out var errors))
            {
                return document.RootElement.GetProperty("data").Clone();
            }

            var errorsJson = errors.GetRawText();

            // Projects V2 mutations occasionally fail with UNPROCESSABLE
            // "…temporary conflict. Please try again." — the API explicitly asks
            // for a retry and the failed attempt has no side effects.
            if (temporaryConflictRetries < MaxServerErrorRetries
                && errorsJson.Contains("temporary conflict", StringComparison.OrdinalIgnoreCase))
            {
                var backoff = GetBackoff(temporaryConflictRetries);
                temporaryConflictRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Temporary conflict reported by the API; retrying in {backoff.TotalSeconds:0}s (attempt {temporaryConflictRetries}/{MaxServerErrorRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            string? errorType = null;
            if (errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0
                && errors[0].TryGetProperty("type", out var typeElement)
                && typeElement.ValueKind == JsonValueKind.String)
            {
                errorType = typeElement.GetString();
            }

            throw new GitHubGraphQLException($"GraphQL error: {errorsJson}")
            {
                ErrorsJson = errorsJson,
                ErrorType = errorType,
            };
        }
    }

    /// <summary>
    /// Executes a cursor-paginated GraphQL query and yields every node of the connection
    /// found at <paramref name="connectionPath"/> (dot-separated path inside "data",
    /// e.g. "organization.projectsV2"). The connection must select
    /// "nodes" and "pageInfo { hasNextPage endCursor }", and the query must declare a
    /// cursor variable (default name "after").
    /// </summary>
    public async IAsyncEnumerable<JsonElement> QueryPaginatedAsync(
        string query,
        object? variables,
        string connectionPath,
        string cursorVariableName = "after",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(cursorVariableName);

        var variableMap = ToVariableMap(variables);
        string? cursor = null;

        while (true)
        {
            variableMap[cursorVariableName] = cursor;
            var data = await QueryAsync(query, variableMap, cancellationToken).ConfigureAwait(false);

            var connection = NavigatePath(data, connectionPath);
            if (connection.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            {
                foreach (var node in nodes.EnumerateArray())
                {
                    yield return node;
                }
            }

            var pageInfo = connection.GetProperty("pageInfo");
            if (!pageInfo.GetProperty("hasNextPage").GetBoolean())
            {
                yield break;
            }

            cursor = pageInfo.GetProperty("endCursor").GetString();
        }
    }

    /// <summary>Returns the login of the authenticated user (connectivity check).</summary>
    public async Task<string> GetViewerLoginAsync(CancellationToken cancellationToken = default)
    {
        var data = await QueryAsync("query { viewer { login } }", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data.GetProperty("viewer").GetProperty("login").GetString()
            ?? throw new GitHubGraphQLException("viewer.login was null.");
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>Sends the payload with the full retry/rate-limit policy and returns the parsed body.</summary>
    private async Task<JsonDocument> ExecuteAsync(string payload, CancellationToken cancellationToken)
    {
        var secondaryRateLimitRetries = 0;
        var serverErrorRetries = 0;

        // A sent StringContent cannot be reused, so each attempt builds a fresh request.
        HttpRequestMessage CreateRequest() => new(HttpMethod.Post, (Uri?)null)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        while (true)
        {
            using var request = CreateRequest();
            HttpResponseMessage? response = null;
            string body;
            HttpStatusCode status;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                status = response.StatusCode;
                body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                // Transient transport failures (connection reset, "response ended
                // prematurely", DNS blips) get the same backoff budget as 5xx.
                response?.Dispose();
                if (serverErrorRetries >= MaxServerErrorRetries)
                {
                    throw new GitHubGraphQLException(
                        $"Network error persisted after {MaxServerErrorRetries} retries: {exception.Message}");
                }

                var networkBackoff = GetBackoff(serverErrorRetries);
                serverErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Network error ({exception.Message}); backing off {networkBackoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                    networkBackoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            using var _ = response;

            if (response.IsSuccessStatusCode)
            {
                await WaitForPrimaryRateLimitResetAsync(response, cancellationToken).ConfigureAwait(false);
                return JsonDocument.Parse(body);
            }

            // (a) 403/429 with an explicit Retry-After header.
            if (status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && GetRetryAfter(response) is { } retryAfter)
            {
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Rate limited ({(int)status}); honoring Retry-After: waiting {retryAfter.TotalSeconds:0}s before retrying."),
                    retryAfter,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // (b) Secondary rate limit without Retry-After: exponential backoff 1s, 2s, 4s... capped at 60s.
            if (status == HttpStatusCode.Forbidden
                && body.Contains("secondary rate limit", StringComparison.OrdinalIgnoreCase))
            {
                if (secondaryRateLimitRetries >= MaxSecondaryRateLimitRetries)
                {
                    throw new GitHubGraphQLException($"Secondary rate limit persisted after {MaxSecondaryRateLimitRetries} retries.")
                    {
                        StatusCode = status,
                    };
                }

                var backoff = GetBackoff(secondaryRateLimitRetries);
                secondaryRateLimitRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Secondary rate limit hit; backing off {backoff.TotalSeconds:0}s (attempt {secondaryRateLimitRetries}/{MaxSecondaryRateLimitRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // (c) Transient server errors: exponential backoff, up to 3 retries.
            if ((int)status >= 500)
            {
                if (serverErrorRetries >= MaxServerErrorRetries)
                {
                    throw new GitHubGraphQLException($"Server error {(int)status} persisted after {MaxServerErrorRetries} retries.")
                    {
                        StatusCode = status,
                    };
                }

                var backoff = GetBackoff(serverErrorRetries);
                serverErrorRetries++;
                await NotifyAndDelayAsync(
                    string.Create(CultureInfo.InvariantCulture, $"Server error {(int)status}; backing off {backoff.TotalSeconds:0}s (attempt {serverErrorRetries}/{MaxServerErrorRetries})."),
                    backoff,
                    cancellationToken).ConfigureAwait(false);
                continue;
            }

            // Non-retryable HTTP failure (401, 404, ...).
            throw new GitHubGraphQLException($"GraphQL request failed with HTTP {(int)status} ({status}).")
            {
                StatusCode = status,
            };
        }
    }

    /// <summary>(d) If the primary rate limit is exhausted, waits until X-RateLimit-Reset.</summary>
    private async Task WaitForPrimaryRateLimitResetAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!TryGetHeader(response, "X-RateLimit-Remaining", out var remaining) || remaining != "0")
        {
            return;
        }

        if (!TryGetHeader(response, "X-RateLimit-Reset", out var resetValue)
            || !long.TryParse(resetValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var resetEpoch))
        {
            return;
        }

        var wait = DateTimeOffset.FromUnixTimeSeconds(resetEpoch) - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
        {
            return;
        }

        await NotifyAndDelayAsync(
            string.Create(CultureInfo.InvariantCulture, $"Primary rate limit exhausted; waiting {wait.TotalSeconds:0}s until reset."),
            wait,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyAndDelayAsync(string message, TimeSpan delay, CancellationToken cancellationToken)
    {
        OnRetry?.Invoke(message);
        await _delayAsync(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan GetBackoff(int retryCount)
    {
        var backoff = TimeSpan.FromSeconds(Math.Pow(2, retryCount));
        return backoff > MaxBackoff ? MaxBackoff : backoff;
    }

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        if (retryAfter.Delta is { } delta)
        {
            return delta >= TimeSpan.Zero ? delta : TimeSpan.Zero;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait >= TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool TryGetHeader(HttpResponseMessage response, string name, out string? value)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            value = values.FirstOrDefault();
            return value is not null;
        }

        value = null;
        return false;
    }

    /// <summary>Converts an arbitrary variables object into a mutable map so the cursor can be injected.</summary>
    private static Dictionary<string, object?> ToVariableMap(object? variables)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (variables is null)
        {
            return map;
        }

        var element = JsonSerializer.SerializeToElement(variables);
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("variables must serialize to a JSON object.", nameof(variables));
        }

        foreach (var property in element.EnumerateObject())
        {
            map[property.Name] = property.Value.Clone();
        }

        return map;
    }

    /// <summary>Walks a dot-separated property path (e.g. "organization.projectsV2") inside "data".</summary>
    private static JsonElement NavigatePath(JsonElement data, string connectionPath)
    {
        var current = data;
        foreach (var segment in connectionPath.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                throw new GitHubGraphQLException($"Connection path '{connectionPath}' not found in response (missing segment '{segment}').");
            }

            current = next;
        }

        return current;
    }
}
