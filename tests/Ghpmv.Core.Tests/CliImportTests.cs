using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Ghpmv.Core.Snapshot;

namespace Ghpmv.Core.Tests;

public class CliImportTests
{
    [Fact]
    public async Task Verify_reports_category_statuses_and_writes_consistent_json()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-verify-" + Guid.NewGuid().ToString("N"));
        var reportPath = Path.Combine(directory, "report.json");
        await SnapshotFile.SaveAsync(VerifySnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(VerifyProjectResponse, VerifyItemsResponse);
        try
        {
            var result = await RunVerifyCliAsync(directory, server, "--report-json", reportPath);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Project: Match", result.Output, StringComparison.Ordinal);
            Assert.Contains("LinkedRepository: PartialMatch", result.Output, StringComparison.Ordinal);
            Assert.Contains("Collaborator: NotVerified", result.Output, StringComparison.Ordinal);
            Assert.Contains("1 warning(s)", result.Output, StringComparison.Ordinal);
            Assert.EndsWith("NotVerified." + Environment.NewLine, result.Output, StringComparison.Ordinal);

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken));
            Assert.Equal("NotVerified", report.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, report.RootElement.GetProperty("warningCount").GetInt32());
            Assert.Equal(1, report.RootElement.GetProperty("notVerifiedCount").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_skip_with_browser_automation_does_not_run_downstream_importers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-skip-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(SnapshotWithDownstreamContent(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(
                directory,
                server,
                "--on-conflict", "skip",
                "--enable-browser-automation");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=skipped project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("skipped without making changes", result.Error, StringComparison.Ordinal);
            Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", server.RequestBodies[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_fail_returns_error_without_a_result_line()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-fail-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(ExistingProjectResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "fail");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("already exists", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("result=", result.Output, StringComparison.Ordinal);
            var request = Assert.Single(server.RequestBodies);
            Assert.DoesNotContain("mutation", request, StringComparison.OrdinalIgnoreCase);
        }

        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Browser_filter_mapping_preflight_fails_before_any_mutation_by_default()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-filter-preflight-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Views =
            [
                new ViewSnapshot
                {
                    Number = 1,
                    Name = "EMU",
                    Layout = "TABLE_LAYOUT",
                    Filter = "assignee:old-user",
                    GroupByFields = [],
                    SortByFields = [],
                    VerticalGroupByFields = [],
                    VisibleFields = [],
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);

        using var server = new GraphQlStubServer(EmptyProjectsResponse, OwnerResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--enable-browser-automation");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("unmapped assignee value 'old-user'", result.Error, StringComparison.Ordinal);
            Assert.Contains("Filter mapping preflight failed", result.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Conflict_update_emits_stable_result_and_applies_project_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-update-" + Guid.NewGuid().ToString("N"));
        await SnapshotFile.SaveAsync(MinimalSnapshot(), directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse);
        try
        {
            var result = await RunCliAsync(directory, server, "--on-conflict", "update");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains(
                "items: created=0 resumed=0 already-complete=0 skipped=0 warnings=0",
                result.Output,
                StringComparison.Ordinal);
            Assert.Equal(3, server.RequestBodies.Count);
            Assert.Single(server.RequestBodies, request =>
                request.Contains("mutation", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Incomplete_item_log_forces_update_on_default_retry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), "ghpmv-cli-resume-" + Guid.NewGuid().ToString("N"));
        var snapshot = MinimalSnapshot() with
        {
            Items =
            [
                new ItemSnapshot
                {
                    Type = "DRAFT_ISSUE",
                    Position = 0,
                    IsArchived = false,
                    Draft = new DraftIssueSnapshot { Title = "Interrupted", Assignees = [] },
                    FieldValues = [],
                },
            ],
        };
        await SnapshotFile.SaveAsync(snapshot, directory, cancellationToken);
        var log = new Ghpmv.Core.Import.ImportLog
        {
            ProjectId = "PVT_existing",
            SourceSnapshotFingerprint = Ghpmv.Core.Import.ImportLog.ComputeSnapshotFingerprint(snapshot),
        };
        log.Items["0"] = "PVTI_interrupted";
        log.ItemStates["DRAFT_ISSUE:Interrupted::::position:0"] = new Ghpmv.Core.Import.ImportItemState
        {
            TargetItemId = "PVTI_interrupted",
            TargetContentIdentity = "DRAFT_ISSUE:assignees:",
        };
        await log.SaveAsync(directory, cancellationToken);

        using var server = new GraphQlStubServer(
            ExistingProjectResponse,
            UpdateProjectResponse,
            EmptyFieldsResponse,
            PositionResponse);
        try
        {
            var result = await RunCliAsync(directory, server);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("result=updated project=42", result.Output, StringComparison.Ordinal);
            Assert.Contains("resumed=1", result.Output, StringComparison.Ordinal);
            var completed = await Ghpmv.Core.Import.ImportLog.LoadAsync(directory, cancellationToken);
            var state = Assert.Single(completed!.ItemStates).Value;
            Assert.True(state.FieldValuesApplied);
            Assert.True(state.PositionApplied);
            Assert.True(state.ArchiveApplied);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"));
        foreach (var argument in new[]
        {
            "import",
            "--org", "target",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunVerifyCliAsync(
        string directory,
        GraphQlStubServer server,
        params string[] additionalArguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ghpmv.dll"),
            "verify",
            "--org", "target",
            "--project", "42",
            "--in", directory,
            "--token", "dummy-token",
            "--target-base-url", server.GraphQlUrl,
            "--no-update-check",
        })
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var argument in additionalArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the ghpmv process.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        return (process.ExitCode, await output, await error);
    }

    private static ProjectSnapshot MinimalSnapshot() => new()
    {
        SchemaVersion = ProjectSnapshot.CurrentSchemaVersion,
        Project = new ProjectInfoSnapshot
        {
            Title = "Roadmap",
            Public = false,
            Closed = false,
        },
        Fields = [],
        Views = [],
        Workflows = [],
        Items = [],
    };

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
                Filter = "assignee:old-user",
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

    private static ProjectSnapshot VerifySnapshot() => MinimalSnapshot() with
    {
        Collaborators = null,
        LinkedRepositories = [],
    };

    private const string ExistingProjectResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[{"id":"PVT_existing","number":42,"title":"Roadmap","url":"https://github.com/orgs/target/projects/42"}],
          "pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string EmptyProjectsResponse =
        """
        {"data":{"organization":{"projectsV2":{
          "nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}
        }}}}
        """;

    private const string OwnerResponse =
        """{"data":{"organization":{"id":"O_target"}}}""";

    private const string UpdateProjectResponse =
        """{"data":{"updateProjectV2":{"projectV2":{"id":"PVT_existing"}}}}""";

    private const string EmptyFieldsResponse =
        """{"data":{"node":{"fields":{"nodes":[]}}}}""";

    private const string PositionResponse =
        """{"data":{"updateProjectV2ItemPosition":{"clientMutationId":"position"}}}""";

    private const string VerifyProjectResponse =
        """
        {"data":{"organization":{"projectV2":{
          "title":"Roadmap","shortDescription":null,"readme":null,"public":false,"closed":false,
          "fields":{"nodes":[]},"views":{"nodes":[]},"workflows":{"nodes":[]},
          "repositories":{"nodes":[{"nameWithOwner":"target/extra"}]}
        }}}}
        """;

    private const string VerifyItemsResponse =
        """
        {"data":{"organization":{"projectV2":{
          "items":{"nodes":[],"pageInfo":{"hasNextPage":false,"endCursor":null}}
        }}}}
        """;

    private sealed class GraphQlStubServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly string[] _responses;
        private readonly Task _serverTask;

        public GraphQlStubServer(params string[] responses)
        {
            _responses = responses;
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

                var responseIndex = Math.Min(RequestBodies.Count - 1, _responses.Length - 1);
                var response = Encoding.UTF8.GetBytes(_responses[responseIndex]);
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = response.Length;
                await context.Response.OutputStream.WriteAsync(response, cancellationToken);
                context.Response.Close();
            }
        }
    }
}
