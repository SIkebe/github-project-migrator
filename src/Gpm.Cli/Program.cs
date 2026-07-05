using System.CommandLine;

var rootCommand = new RootCommand("gpm — GitHub Projects V2 migration tool (org-to-org, including Views and Workflows).");

var exportCommand = new Command("export", "Export a project from the source organization to a JSON snapshot.");
var importCommand = new Command("import", "Import a JSON snapshot into the target organization.");
var verifyCommand = new Command("verify", "Verify a migrated project against the source snapshot.");
var loginCommand = new Command("login", "Sign in interactively and store browser state for UI automation.");
var setupCommand = new Command("setup", "Install prerequisites such as Playwright browsers.");

foreach (var command in new[] { exportCommand, importCommand, verifyCommand, loginCommand, setupCommand })
{
    command.SetAction(_ =>
    {
        Console.Error.WriteLine($"'{command.Name}' is not implemented yet. See PLAN.md for the roadmap.");
        return 1;
    });
    rootCommand.Subcommands.Add(command);
}

return rootCommand.Parse(args).Invoke();
