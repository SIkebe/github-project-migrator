using System.CommandLine;
using System.Globalization;
using System.Reflection;
using Gpm.Core;
using Gpm.Core.Browser;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;
using Gpm.Core.Verify;
using Microsoft.Playwright;

var rootCommand = new RootCommand("gpm — GitHub Projects V2 migration tool (org-to-org, including Views and Workflows).");

// export
var orgOption = new Option<string>("--org")
{
    Description = "Source organization login.",
    Required = true,
};
var projectOption = new Option<int>("--project")
{
    Description = "Project number in the source organization.",
    Required = true,
};
var outOption = new Option<string>("--out")
{
    Description = "Output directory for the snapshot.",
    DefaultValueFactory = _ => "./gpm-export",
};
var tokenOption = new Option<string?>("--token")
{
    Description = "GitHub token. Defaults to the GITHUB_TOKEN, then GPM_TOKEN environment variable.",
};
var enableBrowserOption = new Option<bool>("--enable-browser-automation")
{
    Description = "Also migrate UI-only view settings with browser automation (requires 'gpm setup --browsers' and 'gpm login').",
};
var browserProfileOption = new Option<string?>("--browser-profile")
{
    Description = "Named browser profile from 'gpm login --profile <name>'. Use different profiles for source and target when they use different accounts (e.g. non-EMU source, EMU target).",
};
var noUpdateCheckOption = new Option<bool>("--no-update-check")
{
    Description = "Skip the update check against GitHub Releases (also disabled by the GPM_NO_UPDATE_CHECK environment variable).",
};

var exportCommand = new Command("export", "Export a project from the source organization to a JSON snapshot.")
{
    orgOption,
    projectOption,
    outOption,
    tokenOption,
    enableBrowserOption,
    browserProfileOption,
    noUpdateCheckOption,
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(orgOption)!;
    var projectNumber = parseResult.GetValue(projectOption);
    var outDirectory = parseResult.GetValue(outOption)!;
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var enableBrowserAutomation = parseResult.GetValue(enableBrowserOption);
    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GPM_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GPM_TOKEN.");
        return 1;
    }

    using var client = new GitHubGraphQLClient(token);
    client.OnRetry = Console.Error.WriteLine;
    var exporter = new ProjectExporter(client) { OnProgress = Console.Error.WriteLine };

    BrowserSession? session = null;
    try
    {
        ViewUiExporter? uiExporter = null;
        WorkflowUiExporter? workflowExporter = null;
        if (enableBrowserAutomation)
        {
            session = new BrowserSession(new BrowserSessionOptions
            {
                Profile = parseResult.GetValue(browserProfileOption),
            });
            uiExporter = new ViewUiExporter(session) { OnProgress = Console.Error.WriteLine };
            workflowExporter = new WorkflowUiExporter(session) { OnProgress = Console.Error.WriteLine };
            exporter.PostExportAsync = async (snapshot, ct) =>
            {
                snapshot = await uiExporter.EnrichAsync(snapshot, org, projectNumber, ct);
                return await workflowExporter.EnrichAsync(snapshot, org, projectNumber, ct);
            };
        }

        var snapshot = await exporter.ExportAsync(org, projectNumber, cancellationToken);
        foreach (var warning in (uiExporter?.Warnings ?? []).Concat(workflowExporter?.Warnings ?? []))
        {
            Console.Error.WriteLine($"warning: {warning}");
        }

        var path = await SnapshotFile.SaveAsync(snapshot, outDirectory, cancellationToken);
        Console.Error.WriteLine($"Snapshot written to {path}");
        await NotifyUpdateAsync(updateCheck);
        return 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or PlaywrightException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
    finally
    {
        if (session is not null)
        {
            await session.DisposeAsync();
        }
    }
});

rootCommand.Subcommands.Add(exportCommand);

// import
var importOrgOption = new Option<string>("--org")
{
    Description = "Target organization login.",
    Required = true,
};
var inOption = new Option<string>("--in")
{
    Description = "Directory containing the snapshot.",
    DefaultValueFactory = _ => "./gpm-export",
};
var onConflictOption = new Option<string>("--on-conflict")
{
    Description = "What to do when a project with the same title already exists: skip, update or fail.",
    DefaultValueFactory = _ => "fail",
};
onConflictOption.Validators.Add(result =>
{
    if (!ConflictActions.TryParse(result.GetValueOrDefault<string>(), out _))
    {
        result.AddError("--on-conflict must be one of: skip, update, fail.");
    }
});
var repoMappingOption = new Option<string?>("--repo-mapping")
{
    Description = "CSV file mapping source repositories to target repositories (header: source,target; rows: org/repo,org/repo).",
};
var userMappingOption = new Option<string?>("--user-mapping")
{
    Description = "CSV file mapping source user logins to target logins (header: source,target).",
};

var importCommand = new Command("import", "Import a JSON snapshot into the target organization.")
{
    importOrgOption,
    inOption,
    onConflictOption,
    repoMappingOption,
    userMappingOption,
    tokenOption,
    enableBrowserOption,
    browserProfileOption,
    noUpdateCheckOption,
};

importCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(importOrgOption)!;
    var inDirectory = parseResult.GetValue(inOption)!;
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var enableBrowserAutomation = parseResult.GetValue(enableBrowserOption);
    if (!ConflictActions.TryParse(parseResult.GetValue(onConflictOption), out var onConflict))
    {
        Console.Error.WriteLine("error: --on-conflict must be one of: skip, update, fail.");
        return 1;
    }

    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GPM_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GPM_TOKEN.");
        return 1;
    }

    using var client = new GitHubGraphQLClient(token);
    client.OnRetry = Console.Error.WriteLine;
    var importer = new ProjectImporter(client) { OnConflict = onConflict, OnProgress = Console.Error.WriteLine };

    try
    {
        var repoMappingPath = parseResult.GetValue(repoMappingOption);
        var userMappingPath = parseResult.GetValue(userMappingOption);
        var repoMapping = repoMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(repoMappingPath);
        var userMapping = userMappingPath is null
            ? System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty
            : CsvMapping.Load(userMappingPath);

        var snapshot = await SnapshotFile.LoadAsync(inDirectory, cancellationToken);
        var result = await importer.ImportAsync(snapshot, org, cancellationToken);

        var itemImporter = new ItemImporter(client)
        {
            RepositoryMapping = repoMapping,
            UserMapping = userMapping,
            OnProgress = Console.Error.WriteLine,
        };
        var itemResult = await itemImporter.ImportAsync(snapshot, result, inDirectory, cancellationToken);

        var viewWarnings = 0;
        var workflowWarnings = 0;
        var workflowsImported = 0;
        if (enableBrowserAutomation)
        {
            await using var session = new BrowserSession(new BrowserSessionOptions
            {
                Profile = parseResult.GetValue(browserProfileOption),
            });
            var viewImporter = new ViewUiImporter(session) { OnProgress = Console.Error.WriteLine };
            await viewImporter.ImportAsync(snapshot, org, result.ProjectNumber, cancellationToken);
            foreach (var warning in viewImporter.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            viewWarnings = viewImporter.Warnings.Count;

            var workflowImporter = new WorkflowUiImporter(session)
            {
                RepositoryMapping = repoMapping,
                OnProgress = Console.Error.WriteLine,
            };
            await workflowImporter.ImportAsync(snapshot, org, result.ProjectNumber, cancellationToken);
            foreach (var warning in workflowImporter.Warnings)
            {
                Console.Error.WriteLine($"warning: {warning}");
            }

            workflowWarnings = workflowImporter.Warnings.Count;
            workflowsImported = workflowImporter.ImportedCount;
        }

        Console.WriteLine(result.Url);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"items: created={itemResult.Created} skipped={itemResult.Skipped} warnings={itemResult.Warnings.Count}"));
        if (enableBrowserAutomation)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"views: imported={snapshot.Views.Count} warnings={viewWarnings}"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"workflows: imported={workflowsImported} warnings={workflowWarnings}"));
        }

        await NotifyUpdateAsync(updateCheck);
        return 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or IOException or FormatException or PlaywrightException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(importCommand);

// verify
var verifyOrgOption = new Option<string>("--org")
{
    Description = "Target organization login.",
    Required = true,
};
var verifyProjectOption = new Option<int>("--project")
{
    Description = "Project number in the target organization.",
    Required = true,
};

var verifyCommand = new Command("verify", "Verify a migrated project against the source snapshot and report differences.")
{
    verifyOrgOption,
    verifyProjectOption,
    inOption,
    tokenOption,
    noUpdateCheckOption,
};

verifyCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(verifyOrgOption)!;
    var projectNumber = parseResult.GetValue(verifyProjectOption);
    var inDirectory = parseResult.GetValue(inOption)!;
    var updateCheck = StartUpdateCheck(parseResult.GetValue(noUpdateCheckOption));
    var token = parseResult.GetValue(tokenOption)
        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN")
        ?? Environment.GetEnvironmentVariable("GPM_TOKEN");

    if (string.IsNullOrWhiteSpace(token))
    {
        Console.Error.WriteLine("error: no token provided. Use --token or set GITHUB_TOKEN / GPM_TOKEN.");
        return 1;
    }

    using var client = new GitHubGraphQLClient(token);
    client.OnRetry = Console.Error.WriteLine;
    var verifier = new ProjectVerifier(client) { OnProgress = Console.Error.WriteLine };

    try
    {
        var snapshot = await SnapshotFile.LoadAsync(inDirectory, cancellationToken);
        var report = await verifier.VerifyAsync(snapshot, org, projectNumber, cancellationToken);
        WriteVerifyReport(report);
        await NotifyUpdateAsync(updateCheck);
        return report.IsMatch ? 0 : 1;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or IOException or FormatException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(verifyCommand);

// login: interactive browser sign-in, storage state saved for later automation (M6).
var baseUrlOption = new Option<string>("--base-url")
{
    Description = "GitHub web base URL (e.g. https://TENANT.ghe.com for GHEC with data residency).",
    DefaultValueFactory = _ => "https://github.com",
};
var statePathOption = new Option<string?>("--state-path")
{
    Description = "File to store the browser sign-in state. Defaults to GPM_BROWSER_STATE, then %APPDATA%/gpm/browser-state.json.",
};
var profileOption = new Option<string?>("--profile")
{
    Description = "Named profile for the sign-in state (e.g. 'source', 'target'). Stored as %APPDATA%/gpm/browser-state.<profile>.json. Use with cross-account migrations.",
};

var loginCommand = new Command("login", "Sign in interactively and store browser state for UI automation.")
{
    baseUrlOption,
    statePathOption,
    profileOption,
};

loginCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var baseUrl = parseResult.GetValue(baseUrlOption)!;
    var statePath = parseResult.GetValue(statePathOption);

    await using var session = new BrowserSession(new BrowserSessionOptions
    {
        Headless = false,
        BaseUrl = baseUrl,
        StatePath = statePath,
        Profile = parseResult.GetValue(profileOption),
    });

    try
    {
        Console.Error.WriteLine("Opening a browser window. Complete the GitHub sign-in there (2FA/SSO/passkey included)...");
        var login = await session.LoginAsync(TimeSpan.FromMinutes(5), cancellationToken);
        Console.Error.WriteLine($"Signed in as '{login}'. Browser state saved to {session.StatePath}");
        return 0;
    }
    catch (Exception exception) when (exception is PlaywrightException or InvalidOperationException or System.TimeoutException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(loginCommand);

// setup: install prerequisites (Playwright Chromium).
var browsersOption = new Option<bool>("--browsers")
{
    Description = "Install the Playwright Chromium browser used for UI automation.",
};

var setupCommand = new Command("setup", "Install prerequisites such as Playwright browsers.")
{
    browsersOption,
};

setupCommand.SetAction(parseResult =>
{
    if (!parseResult.GetValue(browsersOption))
    {
        Console.Error.WriteLine("Nothing to install. Use 'gpm setup --browsers' to install the Playwright Chromium browser.");
        return 1;
    }

    Console.Error.WriteLine("Installing the Playwright Chromium browser...");
    var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
    Console.Error.WriteLine(exitCode == 0
        ? "Chromium installed."
        : string.Create(CultureInfo.InvariantCulture, $"Playwright install failed with exit code {exitCode}."));
    return exitCode;
});

rootCommand.Subcommands.Add(setupCommand);

return await rootCommand.Parse(args).InvokeAsync();

// Starts a fire-and-forget update check against GitHub Releases (opt-out via
// --no-update-check or GPM_NO_UPDATE_CHECK). Never throws; sends no telemetry.
static Task<string?> StartUpdateCheck(bool disabled)
{
    if (disabled || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GPM_NO_UPDATE_CHECK")))
    {
        return Task.FromResult<string?>(null);
    }

    var current = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
    return UpdateChecker.CheckForNewerVersionAsync(current);
}

// Prints a one-line notice to stderr when the update check found a newer version.
static async Task NotifyUpdateAsync(Task<string?> updateCheck)
{
    var latest = await updateCheck;
    if (latest is not null)
    {
        Console.Error.WriteLine($"note: gpm {latest} is available: https://github.com/SIkebe/github-project-migrator/releases/latest (disable this check with --no-update-check or GPM_NO_UPDATE_CHECK=1)");
    }
}

// Writes the verify report to stdout as a human-readable table plus a summary line.
static void WriteVerifyReport(VerifyReport report)
{
    if (report.Differences.Count == 0)
    {
        Console.WriteLine("OK: the target project matches the snapshot.");
        return;
    }

    const string SeverityHeader = "SEVERITY";
    const string CategoryHeader = "CATEGORY";
    var severityWidth = Math.Max(SeverityHeader.Length, report.Differences.Max(d => d.Severity.ToString().Length));
    var categoryWidth = Math.Max(CategoryHeader.Length, report.Differences.Max(d => d.Category.Length));

    Console.WriteLine($"{SeverityHeader.PadRight(severityWidth)}  {CategoryHeader.PadRight(categoryWidth)}  MESSAGE");
    foreach (var difference in report.Differences)
    {
        Console.WriteLine($"{difference.Severity.ToString().PadRight(severityWidth)}  {difference.Category.PadRight(categoryWidth)}  {difference.Message}");
    }

    var errors = report.Differences.Count(d => d.Severity == VerifySeverity.Error);
    var warnings = report.Differences.Count(d => d.Severity == VerifySeverity.Warning);
    var infos = report.Differences.Count(d => d.Severity == VerifySeverity.Info);
    Console.WriteLine();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"{errors} error(s), {warnings} warning(s), {infos} info(s). {(errors == 0 ? "Match." : "Mismatch.")}"));
}
