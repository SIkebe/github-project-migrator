using System.CommandLine;
using Gpm.Core.Export;
using Gpm.Core.GitHub;
using Gpm.Core.Import;
using Gpm.Core.Snapshot;

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

var exportCommand = new Command("export", "Export a project from the source organization to a JSON snapshot.")
{
    orgOption,
    projectOption,
    outOption,
    tokenOption,
};

exportCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(orgOption)!;
    var projectNumber = parseResult.GetValue(projectOption);
    var outDirectory = parseResult.GetValue(outOption)!;
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

    try
    {
        var snapshot = await exporter.ExportAsync(org, projectNumber, cancellationToken);
        var path = await SnapshotFile.SaveAsync(snapshot, outDirectory, cancellationToken);
        Console.Error.WriteLine($"Snapshot written to {path}");
        return 0;
    }
    catch (GitHubGraphQLException exception)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
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

var importCommand = new Command("import", "Import a JSON snapshot into the target organization.")
{
    importOrgOption,
    inOption,
    onConflictOption,
    tokenOption,
};

importCommand.SetAction(async (parseResult, cancellationToken) =>
{
    var org = parseResult.GetValue(importOrgOption)!;
    var inDirectory = parseResult.GetValue(inOption)!;
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
        var snapshot = await SnapshotFile.LoadAsync(inDirectory, cancellationToken);
        var result = await importer.ImportAsync(snapshot, org, cancellationToken);
        Console.WriteLine(result.Url);
        return 0;
    }
    catch (Exception exception) when (exception is GitHubGraphQLException or InvalidOperationException or IOException)
    {
        Console.Error.WriteLine($"error: {exception.Message}");
        return 1;
    }
});

rootCommand.Subcommands.Add(importCommand);

// Not implemented yet.
var verifyCommand = new Command("verify", "Verify a migrated project against the source snapshot.");
var loginCommand = new Command("login", "Sign in interactively and store browser state for UI automation.");
var setupCommand = new Command("setup", "Install prerequisites such as Playwright browsers.");

foreach (var command in new[] { verifyCommand, loginCommand, setupCommand })
{
    command.SetAction(_ =>
    {
        Console.Error.WriteLine($"'{command.Name}' is not implemented yet. See PLAN.md for the roadmap.");
        return 1;
    });
    rootCommand.Subcommands.Add(command);
}

return await rootCommand.Parse(args).InvokeAsync();
