using Gpm.Core.Snapshot;
using Gpm.Core.GitHub;

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

    public static async Task RemoveUnexpectedItemsAsync(
        GitHubGraphQLClient client,
        string org,
        int projectNumber,
        ProjectSnapshot expected,
        CancellationToken cancellationToken)
    {
        var expectedKeys = expected.Items.Select(ItemKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unexpectedKeys = [];
        // Keep observing for the full window: Auto-add can create an unexpected item
        // several seconds after an initially clean read.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var (projectId, nodes) = await QueryItemsAsync();
            var unexpectedNodes = nodes
                .Where(node => !expectedKeys.Contains(ItemKey(node)))
                .ToArray();
            unexpectedKeys = unexpectedNodes
                .Select(ItemKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (attempt == 7 && unexpectedNodes.Length == 0)
            {
                return;
            }

            foreach (var node in unexpectedNodes)
            {
                await client.QueryAsync(
                    """
                    mutation($projectId: ID!, $itemId: ID!) {
                      deleteProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
                        deletedItemId
                      }
                    }
                    """,
                    new { projectId, itemId = node.GetProperty("id").GetString()! },
                    cancellationToken);
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        var (_, finalNodes) = await QueryItemsAsync();
        unexpectedKeys = finalNodes
            .Where(node => !expectedKeys.Contains(ItemKey(node)))
            .Select(ItemKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unexpectedKeys.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Project #{projectNumber} kept adding unexpected items: [{string.Join(", ", unexpectedKeys)}].");

        async Task<(string ProjectId, System.Text.Json.JsonElement[] Nodes)> QueryItemsAsync()
        {
            var data = await client.QueryAsync(
                """
                query($org: String!, $number: Int!) {
                  organization(login: $org) {
                    projectV2(number: $number) {
                      id
                      items(first: 100, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                        nodes {
                          id
                          type
                          content {
                            ... on DraftIssue { title }
                            ... on Issue { number repository { nameWithOwner } }
                            ... on PullRequest { number repository { nameWithOwner } }
                          }
                        }
                      }
                    }
                  }
                }
                """,
                new { org, number = projectNumber },
                cancellationToken);
            var project = data.GetProperty("organization").GetProperty("projectV2");
            return (
                project.GetProperty("id").GetString()!,
                project.GetProperty("items").GetProperty("nodes").EnumerateArray().ToArray());
        }
    }

    private static string ItemKey(ItemSnapshot item) => item.Type == "DRAFT_ISSUE"
        ? $"DRAFT_ISSUE:{item.Draft?.Title}"
        : $"{item.Type}:{item.Repository}#{item.Number}";

    private static string ItemKey(System.Text.Json.JsonElement item)
    {
        var type = item.GetProperty("type").GetString()!;
        var content = item.GetProperty("content");
        return type == "DRAFT_ISSUE"
            ? $"DRAFT_ISSUE:{content.GetProperty("title").GetString()}"
            : $"{type}:{content.GetProperty("repository").GetProperty("nameWithOwner").GetString()}#{content.GetProperty("number").GetInt32()}";
    }
}
