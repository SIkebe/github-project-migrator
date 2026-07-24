using System.Net;
using System.Text;
using Ghpmv.Core.Export;
using Ghpmv.Core.GitHub;

namespace Ghpmv.Core.Tests;

public class ProjectExporterTests
{
    [Fact]
    public async Task Export_reads_projected_multi_select_issue_fields()
    {
        using var handler = new StubHandler(
            """
            {"data":{"organization":{"projectV2":{
              "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
              "fields":{"nodes":[
                {"__typename":"ProjectV2Field","name":"Title","dataType":"TITLE"}
              ]},
              "views":{"nodes":[]},"workflows":{"nodes":[]},"repositories":{"nodes":[]}
            }}}}
            """,
            """
            {"data":{"organization":{"projectV2":{"items":{
              "nodes":[{
                "type":"ISSUE","isArchived":false,
                "content":{"number":7,"repository":{"nameWithOwner":"source/repo"}},
                "fieldValues":{"nodes":[{
                  "__typename":"ProjectV2ItemIssueFieldValue",
                  "field":{"name":"Teams"},
                  "issueFieldValue":{
                    "__typename":"IssueFieldMultiSelectValue",
                    "options":[{"name":"Platform"},{"name":"SDK"}]
                  }
                }]}
              }],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}}
            """,
            """
            {"data":{"organization":{"issueFields":{
              "nodes":[
                {
                  "__typename":"IssueFieldMultiSelect","id":"IFM_teams","name":"Teams",
                  "dataType":"MULTI_SELECT","description":"Teams involved","visibility":"ALL",
                  "options":[
                    {"id":"IFO_platform","name":"Platform","color":"PURPLE","description":"Platform work"},
                    {"id":"IFO_sdk","name":"SDK","color":"GREEN","description":null}
                  ]
                },
                {
                  "__typename":"IssueFieldMultiSelect","id":"IFM_unrelated","name":"Unrelated",
                  "dataType":"MULTI_SELECT","description":null,"visibility":"ALL","options":[]
                }
              ],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """);
        using var client = new GitHubGraphQLClient(
            "dummy-token",
            new Uri("https://example.test/graphql"),
            handler,
            delayAsync: null);

        var snapshot = await new ProjectExporter(client).ExportAsync(
            "source",
            1,
            TestContext.Current.CancellationToken);

        var field = snapshot.Fields.Single(candidate => candidate.Name == "Teams");
        Assert.Equal("MULTI_SELECT", field.DataType);
        Assert.Equal(["Platform", "SDK"], field.Options!.Select(option => option.Name));
        Assert.Equal("Teams involved", field.IssueField!.Description);
        Assert.Equal("ALL", field.IssueField.Visibility);
        Assert.DoesNotContain(snapshot.Fields, candidate => candidate.Name == "Unrelated");

        var item = Assert.Single(snapshot.Items);
        Assert.Equal(
            ["Platform", "SDK"],
            Assert.Single(item.FieldValues).MultiSelectOptionNames);
        Assert.Equal(3, handler.RequestBodies.Count);
    }

    private sealed class StubHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
            };
        }
    }
}
