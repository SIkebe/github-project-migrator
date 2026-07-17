using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Gpm.Core.Snapshot;

namespace Gpm.Core.Tests;

[Collection("Console")]
public class CliImportTests
{
    [Fact]
    public async Task Conflict_skip_with_browser_automation_does_not_run_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "gpm-cli-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithDownstreamContent(), directory, cancellationToken);

        using var server = new GraphQlStubServer();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            Console.SetOut(output);
            Console.SetError(error);

            var entryPoint = Assembly.Load("gpm").EntryPoint
                ?? throw new InvalidOperationException("The gpm entry point was not found.");
            var invocation = entryPoint.Invoke(
                null,
                [
                    new[]
                    {
                        "import",
                        "--org", "target",
                        "--in", directory,
                        "--token", "dummy-token",
                        "--target-base-url", server.GraphQlUrl,
                        "--on-conflict", "skip",
                        "--enable-browser-automation",
                        "--no-update-check",
                    },
                ]);
            var exitCode = await Assert.IsType<Task<int>>(invocation);

            Assert.Equal(0, exitCode);
            Assert.Contains("result=skipped project=42", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", error.ToString(), StringComparison.Ordinal);
            Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", server.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ProjectSnapshot SnapshotWithDownstreamContent() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views =
        [
            new ViewSnapshot
            {
                Number = 1,
                Name = "Table",
                Layout = "TABLE_LAYOUT",
                GroupByFields = [],
                SortByFields = [],
                VerticalGroupByFields = [],
                VisibleFields = [],
            },
        ],
        Workflows =
        [
            new WorkflowSnapshot
            {
                Number = 1,
                Name = "Item added to project",
                Enabled = true,
            },
        ],
        Items =
        [
            new ItemSnapshot
            {
                Type = "DRAFT_ISSUE",
                Position = 0,
                IsArchived = false,
                Draft = new DraftIssueSnapshot
                {
                    Title = "Must not be imported",
                    Assignees = [],
                },
                FieldValues = [],
            },
        ],
    };

    private sealed class GraphQlStubServer : IDisposable
    {
        private const string ExistingProjectResponse =
            """
            {"data":{"organization":{"projectsV2":{
              "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
              "pageInfo":{"hasNextPage":false,"endCursor":null}
            }}}}
            """;

        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _serverTask;

        public GraphQlStubServer()
        {
            using var portReservation = new TcpListener(IPAddress.Loopback, 0);
            portReservation.Start();
            var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
            portReservation.Stop();

            var prefix = $"http://127.0.0.1:{port}/";
            GraphQlUrl = prefix + "graphql";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _serverTask = ServeAsync(_cancellation.Token);
        }

        public string GraphQlUrl { get; }

        public List<string> RequestBodies { get; } = [];

        public void Dispose()
        {
            _cancellation.Cancel();
            _listener.Close();
            try
            {
                _serverTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }

            _cancellation.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                RequestBodies.Add(await reader.ReadToEndAsync(cancellationToken));

                var response = Encoding.UTF8.GetBytes(ExistingProjectResponse);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response, cancellationToken);
                context.Response.Close();
            }
        }
    }
}

[CollectionDefinition("Console", DisableParallelization = true)]
public sealed class ConsoleCollection;
