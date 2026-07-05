using System.Text.Json;

namespace Gpm.Core;

/// <summary>
/// Checks GitHub Releases for a newer gpm version. Best-effort: any failure
/// (offline, rate limit, timeout) yields <c>null</c>. No telemetry is sent.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/SIkebe/github-project-migrator/releases/latest";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Returns the latest release version (e.g. "0.2.0") when it is newer than
    /// <paramref name="currentVersion"/>; otherwise <c>null</c>.
    /// </summary>
    public static async Task<string?> CheckForNewerVersionAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("gpm");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            using var response = await http.GetAsync(new Uri(LatestReleaseUrl), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (!document.RootElement.TryGetProperty("tag_name", out var tagName))
            {
                return null;
            }

            var latest = NormalizeVersion(tagName.GetString() ?? "");
            return IsNewer(currentVersion, latest) ? latest : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="latest"/> is a strictly newer
    /// Major.Minor.Patch version than <paramref name="current"/>. A leading "v"
    /// and any pre-release/build suffix ("-", "+") are ignored. Unparseable
    /// input yields <c>false</c>.
    /// </summary>
    public static bool IsNewer(string current, string latest)
    {
        if (!TryParse(current, out var currentParts) || !TryParse(latest, out var latestParts))
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (latestParts[i] != currentParts[i])
            {
                return latestParts[i] > currentParts[i];
            }
        }

        return false;
    }

    private static bool TryParse(string version, out int[] parts)
    {
        parts = new int[3];
        var core = NormalizeVersion(version);
        var segments = core.Split('.');
        if (segments.Length != 3)
        {
            return false;
        }

        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(segments[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out parts[i]))
            {
                return false;
            }
        }

        return true;
    }

    // "v1.2.3-rc.1+abc" -> "1.2.3"
    private static string NormalizeVersion(string version)
    {
        var core = version.Trim();
        if (core.StartsWith('v') || core.StartsWith('V'))
        {
            core = core[1..];
        }

        var end = core.IndexOfAny(['-', '+']);
        return end < 0 ? core : core[..end];
    }
}
