using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Gpm.Core.GitHub;

/// <summary>
/// Minimal GraphQL client for connectivity checks (M0).
/// Will be extended in M1 with pagination, rate limiting and retry policies.
/// </summary>
public sealed class GitHubGraphQLClient : IDisposable
{
    private static readonly Uri DefaultEndpoint = new("https://api.github.com/graphql");

    private readonly HttpClient _httpClient;

    public GitHubGraphQLClient(string token, Uri? baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        _httpClient = new HttpClient { BaseAddress = baseUrl ?? DefaultEndpoint };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("gpm");
    }

    /// <summary>Executes a GraphQL query and returns the "data" element.</summary>
    public async Task<JsonElement> QueryAsync(string query, object? variables = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var payload = JsonSerializer.Serialize(new { query, variables });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync((Uri?)null, content, cancellationToken).ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("errors", out var errors))
        {
            throw new InvalidOperationException($"GraphQL error: {errors.GetRawText()}");
        }

        return document.RootElement.GetProperty("data").Clone();
    }

    /// <summary>Returns the login of the authenticated user (connectivity check).</summary>
    public async Task<string> GetViewerLoginAsync(CancellationToken cancellationToken = default)
    {
        var data = await QueryAsync("query { viewer { login } }", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data.GetProperty("viewer").GetProperty("login").GetString()
            ?? throw new InvalidOperationException("viewer.login was null.");
    }

    public void Dispose() => _httpClient.Dispose();
}
