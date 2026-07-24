using System.Globalization;
using Microsoft.Playwright;

namespace Ghpmv.Core.Browser;

/// <summary>
/// Lazily-initialized Playwright session (M6). The Chromium browser is launched on first
/// use, and the context is created from a storage-state file
/// (default: <c>GHPMV_BROWSER_STATE</c>, then <c>%APPDATA%/ghpmv/browser-state.json</c>).
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
        BaseUrl = BrowserBaseUrl.NormalizeStandalone(_options.BaseUrl);
    }

    /// <summary>Web UI base URL without a trailing slash.</summary>
    public string BaseUrl { get; }

    /// <summary>Resolved storage-state file path.</summary>
    public string StatePath => _options.StatePath ?? DefaultStatePath(_options.Profile);

    /// <summary>
    /// Default storage-state path. With a profile: <c>%APPDATA%/ghpmv/browser-state.&lt;profile&gt;.json</c>.
    /// Without: the <c>GHPMV_BROWSER_STATE</c> environment variable, then <c>%APPDATA%/ghpmv/browser-state.json</c>.
    /// </summary>
    public static string DefaultStatePath(string? profile = null)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            var fromEnvironment = Environment.GetEnvironmentVariable("GHPMV_BROWSER_STATE");
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
            "ghpmv",
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
            StorageStatePath = ResolveStorageStatePath(_options.LoadStoredState, statePath),
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
        EnsureExpectedHost(page);
        EnsureSignedIn(page);

        if (await Sel.SsoHeading(page).CountAsync().ConfigureAwait(false) > 0)
        {
            await Sel.SsoContinueButton(page).First.ClickAsync().ConfigureAwait(false);
            await page.WaitForURLAsync(url, new() { Timeout = 30_000 }).ConfigureAwait(false);
            EnsureExpectedHost(page);
            EnsureSignedIn(page);
        }

        return page;
    }

    /// <summary>
    /// Verifies that the stored browser state is signed in to this session's host as the
    /// same account used by the API client. Call before any migration writes.
    /// </summary>
    public async Task ValidateAuthenticationAsync(string expectedLogin, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedLogin);
        var page = await GotoAsync(BaseUrl, cancellationToken).ConfigureAwait(false);
        var actualLogin = await page.EvaluateAsync<string?>(
            "() => document.querySelector('meta[name=\"user-login\"]')?.content || null").ConfigureAwait(false);
        EnsureAuthenticationResult(BaseUrl, page.Url, actualLogin, expectedLogin);
    }

    internal static void EnsureAuthenticationResult(
        string baseUrl, string actualUrl, string? actualLogin, string expectedLogin)
    {
        EnsureExpectedOrigin(baseUrl, actualUrl);
        if (string.IsNullOrWhiteSpace(actualLogin))
        {
            throw new InvalidOperationException(
                $"The browser session is not signed in to '{new Uri(baseUrl).Host}'. Run 'ghpmv login' for this browser base URL.");
        }

        if (!string.Equals(actualLogin, expectedLogin, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Browser profile account '{actualLogin}' does not match API token account '{expectedLogin}'. Use matching credentials before continuing.");
        }
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
    /// When <paramref name="expectedLogin"/> is set, a different account fails before the
    /// storage state is saved. Returns the signed-in login name.
    /// </summary>
    public Task<string> LoginAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        => LoginAsync(timeout, expectedLogin: null, cancellationToken);

    public async Task<string> LoginAsync(
        TimeSpan timeout,
        string? expectedLogin,
        CancellationToken cancellationToken = default)
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
                    if (!string.IsNullOrWhiteSpace(expectedLogin))
                    {
                        if (string.IsNullOrWhiteSpace(login))
                        {
                            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        EnsureExpectedLogin(login, expectedLogin);
                    }

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

    internal static string? ResolveStorageStatePath(bool loadStoredState, string statePath)
        => loadStoredState && File.Exists(statePath) ? statePath : null;

    internal static void EnsureExpectedLogin(string actualLogin, string expectedLogin)
    {
        if (!string.Equals(actualLogin, expectedLogin, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Signed in as '{actualLogin}', but expected '{expectedLogin}'. The browser state was not saved. Retry and sign in with the expected account.");
        }
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
                "The browser session is not signed in (redirected to the login page). Run 'ghpmv login' to save a fresh sign-in state.");
        }
    }

    private void EnsureExpectedHost(IPage page)
        => EnsureExpectedOrigin(BaseUrl, page.Url);

    internal static void EnsureExpectedOrigin(string baseUrl, string actualUrl)
    {
        if (!Uri.TryCreate(actualUrl, UriKind.Absolute, out var actual)
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var expected)
            || !string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            || actual.Port != expected.Port)
        {
            throw new InvalidOperationException(
                $"Browser navigation left the configured GitHub host '{baseUrl}' and reached '{actualUrl}'. Check the browser base URL and profile.");
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
