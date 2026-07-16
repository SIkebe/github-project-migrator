namespace Gpm.Core.Browser;

/// <summary>Resolves and validates the GitHub web URL paired with a GraphQL API endpoint.</summary>
public static class BrowserBaseUrl
{
    private static readonly Uri DefaultApiUrl = new("https://api.github.com/graphql");

    /// <summary>
    /// Derives the web URL from a GitHub.com or GHE.com API URL. When an explicit web URL
    /// is supplied, it must identify the same GitHub deployment.
    /// </summary>
    public static string Resolve(Uri? apiBaseUrl, string? explicitBaseUrl = null)
    {
        var expected = FromApiUrl(apiBaseUrl ?? DefaultApiUrl);
        if (string.IsNullOrWhiteSpace(explicitBaseUrl))
        {
            return expected.AbsoluteUri.TrimEnd('/');
        }

        var actual = Normalize(explicitBaseUrl);
        if (!HasSameOrigin(actual, expected))
        {
            throw new ArgumentException(
                $"Browser base URL '{actual.AbsoluteUri.TrimEnd('/')}' does not match API base URL '{(apiBaseUrl ?? DefaultApiUrl).AbsoluteUri}'. Expected '{expected.AbsoluteUri.TrimEnd('/')}'.",
                nameof(explicitBaseUrl));
        }

        return actual.AbsoluteUri.TrimEnd('/');
    }

    /// <summary>Normalizes a standalone GitHub web base URL.</summary>
    public static string NormalizeStandalone(string baseUrl)
        => Normalize(baseUrl).AbsoluteUri.TrimEnd('/');

    private static Uri FromApiUrl(Uri apiBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(apiBaseUrl);
        if (apiBaseUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"API base URL '{apiBaseUrl.AbsoluteUri}' must use HTTPS.",
                nameof(apiBaseUrl));
        }

        var host = apiBaseUrl.Host;
        string webHost;
        if (string.Equals(host, "api.github.com", StringComparison.OrdinalIgnoreCase))
        {
            webHost = "github.com";
        }
        else if (host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
            && host.EndsWith(".ghe.com", StringComparison.OrdinalIgnoreCase)
            && host.Length > "api..ghe.com".Length)
        {
            webHost = host["api.".Length..];
        }
        else
        {
            throw new ArgumentException(
                $"Cannot derive a GitHub web URL from API host '{host}'. Supported API hosts are api.github.com and api.<tenant>.ghe.com.",
                nameof(apiBaseUrl));
        }

        return new UriBuilder(apiBaseUrl.Scheme, webHost, apiBaseUrl.IsDefaultPort ? -1 : apiBaseUrl.Port).Uri;
    }

    private static Uri Normalize(string baseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException($"'{baseUrl}' is not an absolute http(s) URL.");
        }

        if (!string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath.Length > 1 && uri.AbsolutePath != "/"))
        {
            throw new FormatException($"'{baseUrl}' must be an origin URL without a path, query, or fragment.");
        }

        if (uri.Scheme == Uri.UriSchemeHttp && !uri.IsLoopback)
        {
            throw new FormatException($"'{baseUrl}' must use HTTPS. HTTP is allowed only for loopback test origins.");
        }

        return new UriBuilder(uri.Scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    private static bool HasSameOrigin(Uri left, Uri right)
        => string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
            && left.Port == right.Port;
}
