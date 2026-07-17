# Copilot instructions

## Build, test, and lint

This repository targets .NET 10.

```powershell
dotnet restore Ghpmv.slnx
dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror
```

Run the deterministic suites used on every CI platform:

```powershell
dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --no-build
dotnet test tests/Ghpmv.Browser.Tests/Ghpmv.Browser.Tests.csproj -c Release --no-build --filter "Category!=E2E"
```

Run one test by fully qualified name:

```powershell
dotnet test tests/Ghpmv.Core.Tests/Ghpmv.Core.Tests.csproj -c Release --filter "FullyQualifiedName~Ghpmv.Core.Tests.ProjectImporterLogicTests.Conflict_skip_returns_skipped_without_sending_mutations"
```

`tests/Ghpmv.Integration.Tests` calls the real GitHub API. Tests skip when `GHPMV_TEST_TOKEN` is absent; fixture settings come from `GHPMV_TEST_ORG`, `GHPMV_TEST_TARGET_ORG`, `GHPMV_TEST_PROJECT_NUMBER`, `GHPMV_TEST_FIXTURE_REPO`, and `GHPMV_TEST_TARGET_FIXTURE_REPO`.

```powershell
dotnet test tests/Ghpmv.Integration.Tests/Ghpmv.Integration.Tests.csproj -c Release
```

Browser E2E tests require both `GHPMV_BROWSER_STATE` and `GHPMV_TEST_TOKEN`. `BrowserRoundTripTests` also expects source project `#3` to contain the collaborator, View, and Workflow fixture data asserted by the tests, and the target organization must permit temporary project creation/deletion. `GHPMV_TEST_ORG` and `GHPMV_TEST_TARGET_ORG` select those organizations; `GHPMV_TEST_PROJECT_NUMBER` does not change this suite's hardcoded project number. Create browser state through the CLI rather than editing it:

```powershell
dotnet run --project src/Ghpmv.Cli -- setup --browsers
dotnet run --project src/Ghpmv.Cli -- login --profile source
$appData = [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData)
$env:GHPMV_BROWSER_STATE = [IO.Path]::Combine($appData, 'ghpmv', 'browser-state.source.json')
dotnet test tests/Ghpmv.Browser.Tests/Ghpmv.Browser.Tests.csproj -c Release --filter "Category=E2E"
```

GitHub Actions workflows are linted with the version pinned in `tools/ghalint/go.mod`:

```powershell
go install github.com/suzuki-shunsuke/ghalint/cmd/ghalint@v1.5.6
ghalint run
```

For CLI packaging changes, reproduce CI's self-contained and portable smoke tests:

```powershell
dotnet publish src/Ghpmv.Cli -c Release --self-contained true -r win-x64 -o artifacts/sc-win-x64
artifacts/sc-win-x64/ghpmv.exe --version
dotnet publish src/Ghpmv.Cli -c Release --self-contained false -o artifacts/fdd
dotnet artifacts/fdd/ghpmv.dll --version
```

## Architecture

- `src/Ghpmv.Cli/Program.cs` is the composition root. It defines the `System.CommandLine` commands, resolves tokens and base URLs, wires progress to stderr, keeps stable result summaries on stdout, and composes the API and optional browser stages.
- `src/Ghpmv.Core/GitHub` owns GitHub transport. `GitHubGraphQLClient` centralizes GraphQL execution, cursor pagination, endpoint normalization, rate-limit handling, and transient retries. Migration components consume this client rather than issuing independent HTTP requests.
- `src/Ghpmv.Core/Snapshot` is the migration contract. `ProjectExporter` reads API-visible project state into `ProjectSnapshot`; `SnapshotFile` persists it as `snapshot.json` through the source-generated `SnapshotJsonContext`.
- Import is intentionally staged. `ProjectImporter` creates or updates project metadata, fields, collaborators, and linked repositories and returns target field/option/iteration ID maps. `ItemImporter` then relinks issues and pull requests, creates drafts, applies values and ordering, archives items, and writes `import-log.json` after each creation for resumability.
- `ProjectVerifier` re-exports the target through `ProjectExporter`, normalizes repository/user mappings, and performs a pure snapshot comparison. Keep comparison logic usable without network access.
- `src/Ghpmv.Core/Browser` supplements gaps in the public API. Exporters enrich snapshots through `PostExportAsync`; importers replay Views and Workflows after API import. Browser automation is opt-in and must not become a dependency of the API-only path.
- Test projects mirror external dependencies: `Ghpmv.Core.Tests` is deterministic, `Ghpmv.Integration.Tests` uses live GitHub APIs, and `Ghpmv.Browser.Tests` contains both deterministic browser logic tests and `[Trait("Category", "E2E")]` Playwright tests.

## Repository conventions

- Preserve the API/browser boundary. Add API-readable state to the GraphQL exporter/importer first; use browser enrichment only for UI-only data. Before browser-assisted writes, validate that the browser profile host and login match the API token through `BeforeWriteAsync`.
- Keep all GitHub Projects UI selectors in `Browser/Sel.cs`; do not inline selectors in exporter/importer logic. When UI assumptions change, update the relevant implementation tests and `docs/BROWSER_AUTOMATION_PLAN.md` or `docs/ui-maps/`.
- Treat `ProjectSnapshot` as a persisted compatibility contract. Snapshot records use `required` init properties, nullable fields for optional/backward-compatible additions, camelCase JSON, omitted nulls, and source-generated serialization. Update `SnapshotJsonContext` and snapshot round-trip/backward-compatibility tests when the schema changes.
- Project field, option, iteration, repository, and user identities are remapped by names, not copied node IDs. Issue/PR imports require repository mappings and preserve issue/PR numbers; verifier mappings must normalize the same identities.
- Preserve import ordering and resume semantics: persist a created item's target ID before applying its values, restore positions before archived state, and do not duplicate entries already present in `import-log.json`.
- Core components report progress and recoverable migration gaps through `OnProgress`/warning collections. Transport or contract failures use typed exceptions. The CLI converts expected failures to `error:` messages on stderr and exit code 1; avoid changing machine-readable stdout lines without updating CLI regression tests.
- Real-API and browser tests use `Assert.SkipWhen` when credentials/state are unavailable and clean up any created GitHub resources in `finally`. Pass `TestContext.Current.CancellationToken` through async test operations.
- Package versions belong in `Directory.Packages.props`, not individual project files. The build enables nullable reference types, latest recommended analyzers, invariant globalization, and warnings as errors.
- Commit messages must explain why the change was necessary, not only describe what changed.
