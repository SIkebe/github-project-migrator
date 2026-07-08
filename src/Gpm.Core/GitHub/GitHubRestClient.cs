using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Gpm.Core.GitHub;

/// <summary>Minimal GitHub REST client used by fixture setup for repository/Issue/PR bootstrapping.</summary>
public sealed class GitHubRestClient : IDisposable
{
    private static readonly Uri DefaultBaseUri = new("https://api.github.com/");

    private readonly HttpClient _httpClient;

    public GitHubRestClient(string token, Uri? baseUri = null)
        : this(token, baseUri, new HttpClientHandler())
    {
    }

    internal GitHubRestClient(string token, Uri? baseUri, HttpMessageHandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(handler);
        _httpClient = new HttpClient(handler) { BaseAddress = EnsureTrailingSlash(baseUri ?? DefaultBaseUri) };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("gpm");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public static Uri ToRestBaseUri(Uri graphQlEndpoint)
    {
        ArgumentNullException.ThrowIfNull(graphQlEndpoint);
        return new Uri(graphQlEndpoint, ".");
    }

    private static Uri EnsureTrailingSlash(Uri baseUri)
    {
        if (baseUri.AbsolutePath.EndsWith('/'))
        {
            return baseUri;
        }

        var builder = new UriBuilder(baseUri)
        {
            Path = baseUri.AbsolutePath + "/",
        };
        return builder.Uri;
    }

    public async Task<JsonElement?> GetAsync(string path, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> PostAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PostAsync(path, CreateJsonContent(body), cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> PutAsync(string path, object body, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.PutAsync(path, CreateJsonContent(body), cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _httpClient.Dispose();

    private static StringContent CreateJsonContent(object body)
        => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"REST error {(int)response.StatusCode} {response.ReasonPhrase}: {text}",
                null,
                response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
