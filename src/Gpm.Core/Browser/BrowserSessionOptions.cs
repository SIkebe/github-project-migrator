namespace Gpm.Core.Browser;

/// <summary>Options for <see cref="BrowserSession"/>.</summary>
public sealed record BrowserSessionOptions
{
    /// <summary>Run the browser headless (default). Set to false for interactive flows such as <c>gpm login</c>.</summary>
    public bool Headless { get; init; } = true;

    /// <summary>
    /// Web UI base URL. Defaults to GitHub.com; use <c>https://{tenant}.ghe.com</c> for
    /// GHEC with data residency.
    /// </summary>
    public string BaseUrl { get; init; } = "https://github.com";

    /// <summary>
    /// Path of the storage-state file. When null, falls back to
    /// <see cref="Profile"/>-based resolution: <c>%APPDATA%/gpm/browser-state.&lt;profile&gt;.json</c>
    /// when a profile is set, otherwise the <c>GPM_BROWSER_STATE</c> environment variable,
    /// then <c>%APPDATA%/gpm/browser-state.json</c>.
    /// </summary>
    public string? StatePath { get; init; }

    /// <summary>
    /// Named browser profile for cross-account migrations (e.g. "source" and "target").
    /// Lets a non-EMU source account and an EMU target account coexist even though both
    /// live on github.com. Ignored when <see cref="StatePath"/> is set explicitly.
    /// </summary>
    public string? Profile { get; init; }

    /// <summary>Slow-motion delay in milliseconds between Playwright operations (debugging aid).</summary>
    public float? SlowMoMs { get; init; }
}
