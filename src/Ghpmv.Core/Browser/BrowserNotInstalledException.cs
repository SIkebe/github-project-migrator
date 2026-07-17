namespace Ghpmv.Core.Browser;

/// <summary>
/// Thrown when the Playwright Chromium browser required for UI automation is not installed.
/// The message points the user at <c>ghpmv setup --browsers</c>.
/// </summary>
public sealed class BrowserNotInstalledException : InvalidOperationException
{
    private const string DefaultMessage =
        "The Playwright Chromium browser is not installed. Run 'ghpmv setup --browsers' to install it.";

    public BrowserNotInstalledException()
        : base(DefaultMessage)
    {
    }

    public BrowserNotInstalledException(string message)
        : base(message)
    {
    }

    public BrowserNotInstalledException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Wraps the original Playwright launch failure with the default guidance message.</summary>
    public BrowserNotInstalledException(Exception innerException)
        : base(DefaultMessage, innerException)
    {
    }
}
