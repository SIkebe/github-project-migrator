using System.Globalization;
using Microsoft.Playwright;

namespace Gpm.Core.Browser;

/// <summary>
/// Lazily-initialized Playwright session (M6). The Chromium browser is launched on first
/// use, and the context is created from a storage-state file
/// (default: <c>GPM_BROWSER_STATE</c>, then <c>%APPDATA%/gpm/browser-state.json</c>).
/// A missing browser installation is surfaced as <see cref="BrowserNotInstalledException"/>.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    private readonly BrowserSessionOptions _options;
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IBrowserContext? _context;
    private IPage? _page;

    public BrowserSession(BrowserSessionOptions? options = null)
    {
        _options = options ?? new BrowserSessionOptions();
        BaseUrl = _options.BaseUrl.TrimEnd('/');
    }

    /// <summary>Web UI base URL without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Resolved storage-state file path.</summary>
    public string StatePath => _options.StatePath ?? DefaultStatePath(_options.Profile);

    /// <summary>
    /// Default storage-state path. With a profile: <c>%APPDATA%/gpm/browser-state.&lt;profile&gt;.json</c>.
    /// Without: the <c>GPM_BROWSER_STATE</c> environment variable, then <c>%APPDATA%/gpm/browser-state.json</c>.
    /// </summary>
    public static string DefaultStatePath(string? profile = null)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("GPM_BROWSER_STATE");
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                return fromEnvironment;
            }
        }

        var fileName = string.IsNullOrWhiteSpace(profile)
            ? "browser-state.json"
            : $"browser-state.{profile}.json";

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "gpm",
            fileName);
    }

    /// <summary>
    /// Returns the single page of this session, launching Playwright/Chromium and creating
    /// the context (with the stored sign-in state, when the state file exists) on first call.
    /// </summary>
    public async Task<IPage> GetPageAsync(CancellationToken cancellationToken = default)
    {
        if (_page is not null)
        {
            return _page;
        }

        cancellationToken.ThrowIfCancellationRequested();
        _playwright ??= await Playwright.CreateAsync().ConfigureAwait(false);

        if (_browser is null)
        {
            try
            {
                _browser = await _playwright.Chromium.LaunchAsync(new()
                {
                    Headless = _options.Headless,
                    SlowMo = _options.SlowMoMs,
                }).ConfigureAwait(false);
            }
            catch (PlaywrightException exception) when (
                exception.Message.Contains("Executable doesn't exist", StringComparison.OrdinalIgnoreCase)
                || exception.Message.Contains("playwright install", StringComparison.OrdinalIgnoreCase))
            {
                throw new BrowserNotInstalledException(exception);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var statePath = StatePath;
        _context = await _browser.NewContextAsync(new()
        {
            StorageStatePath = File.Exists(statePath) ? statePath : null,
            // A narrow viewport collapses the column menus (BROWSER_AUTOMATION_PLAN §1.4).
            ViewportSize = new() { Width = 1600, Height = 1000 },
        }).ConfigureAwait(false);
        // 30s: generous enough to absorb slow SPA hydration under CPU contention
        // (e.g. browser E2E running in parallel with the integration test suite).
        _context.SetDefaultTimeout(30_000);
        _page = await _context.NewPageAsync().ConfigureAwait(false);
        return _page;
    }

    /// <summary>
    /// Navigates the session page to <paramref name="url"/>, fails fast on a login
    /// redirect and transparently completes the enterprise SSO "Single sign-on to ..."
    /// interstitial (M7 discovery: clicking "Continue" re-authenticates through the
    /// stored IdP session without interaction and returns to the original URL).
    /// </summary>
    public async Task<IPage> GotoAsync(string url, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var page = await GetPageAsync(cancellationToken).ConfigureAwait(false);
        await page.GotoAsync(url).ConfigureAwait(false);
        EnsureSignedIn(page);

        if (await Sel.SsoHeading(page).CountAsync().ConfigureAwait(false) > 0)
        {
            await Sel.SsoContinueButton(page).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForURLAsync(url, new() { Timeout = 30_000 }).ConfigureAwait(false);
            EnsureSignedIn(page);
        }

        return page;
    }

    /// <summary>Saves the current context's storage state to <see cref="StatePath"/>.</summary>
    public async Task SaveStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_context is null)
        {
            throw new InvalidOperationException("The browser session has not been started.");
        }

        var path = StatePath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await _context.StorageStateAsync(new() { Path = path }).ConfigureAwait(false);
    }

    /// <summary>
    /// Interactive sign-in (headful): navigates to <c>{base}/login</c>, waits for the user
    /// to complete the sign-in manually (2FA/SSO/passkey included) until the logged-in
    /// avatar button / <c>user-login</c> meta appears, then saves the storage state.
    /// Returns the signed-in login name.
    /// </summary>
    public async Task<string> LoginAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(cancellationToken).ConfigureAwait(false);
        await page.GotoAsync(BaseUrl + "/login").ConfigureAwait(false);

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var login = await page.EvaluateAsync<string?>(
                    "() => document.querySelector('meta[name=\"user-login\"]')?.content || null").ConfigureAwait(false);
                var avatarVisible = await Sel.AvatarButton(page).CountAsync().ConfigureAwait(false) > 0;
                if (!string.IsNullOrEmpty(login) || avatarVisible)
                {
                    await SaveStateAsync(cancellationToken).ConfigureAwait(false);
                    return string.IsNullOrEmpty(login) ? "(unknown)" : login;
                }
            }
            catch (Exception exception) when (exception is PlaywrightException or TimeoutException)
            {
                // The user is navigating through login/2FA/SSO pages; keep polling.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        throw new System.TimeoutException(string.Create(CultureInfo.InvariantCulture,
            $"Sign-in was not completed within {timeout.TotalMinutes:0} minutes."));
    }

    /// <summary>
    /// Fails fast when the current page was redirected to the login screen
    /// (expired or missing browser state).
    /// </summary>
    public static void EnsureSignedIn(IPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (page.Url.Contains("/login", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The browser session is not signed in (redirected to the login page). Run 'gpm login' to save a fresh sign-in state.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.CloseAsync().ConfigureAwait(false);
            _context = null;
        }

        if (_browser is not null)
        {
            await _browser.CloseAsync().ConfigureAwait(false);
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;
        _page = null;
    }
}
