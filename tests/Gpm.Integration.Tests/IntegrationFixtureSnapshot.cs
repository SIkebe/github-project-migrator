using Gpm.Core.Snapshot;

namespace Gpm.Integration.Tests;

internal static class IntegrationFixtureSnapshot
{
    public static ProjectSnapshot SelectCanonicalItems(ProjectSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var repository = IntegrationTestSettings.FixtureRepositoryFullName;
        ItemSnapshot[] items =
        [
            Draft(snapshot, "Fixture draft 1"),
            Draft(snapshot, "Fixture draft 2"),
            Draft(snapshot, "Fixture draft 3"),
            snapshot.Items.Single(item =>
                item.Type == "ISSUE"
                && string.Equals(item.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && item.Number == 1),
            snapshot.Items.Single(item =>
                item.Type == "PULL_REQUEST"
                && string.Equals(item.Repository, repository, StringComparison.OrdinalIgnoreCase)
                && item.Number == 3),
            Draft(snapshot, "Fixture archived draft"),
            Draft(snapshot, "Fixture assigned draft"),
        ];

        return snapshot with
        {
            Items = items.Select((item, position) => item with { Position = position }).ToArray(),
        };
    }

    private static ItemSnapshot Draft(ProjectSnapshot snapshot, string title)
        => snapshot.Items.Single(item =>
            item.Type == "DRAFT_ISSUE"
            && string.Equals(item.Draft?.Title, title, StringComparison.Ordinal));
}
