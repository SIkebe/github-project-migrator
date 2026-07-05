# gpm — GitHub Projects V2 Migrator

`gpm` is a CLI that migrates **GitHub Projects V2** between organizations — **including Views and Workflows**, which have no public API.

Most existing tools (e.g. [timrogers/gh-migrate-project](https://github.com/timrogers/gh-migrate-project)) migrate what the GraphQL API exposes: fields, items and field values. Views (layout, filters, grouping, slicing, roadmap settings, …) and Workflows (auto-add, item-closed automation, …) must then be recreated by hand. `gpm` closes that gap with an **opt-in browser automation module** (Playwright + your own signed-in session) so a project can be migrated end-to-end:

| Capability | gh-migrate-project | gpm |
|---|---|---|
| Fields / items / field values | ✅ | ✅ |
| Draft issues (with author note) | ✅ | ✅ |
| Iteration fields incl. completed iterations | ➖ | ✅ |
| Item order & archived state | ➖ | ✅ |
| **Views (all layouts, filters, grouping, slicing, roadmap)** | ❌ | ✅ (opt-in browser automation) |
| **Workflows (auto-add, auto-archive, item state automations)** | ❌ | ✅ (opt-in browser automation) |
| Post-migration verification (`gpm verify`) | ❌ | ✅ |

## Installation

Requires no runtime for the self-contained builds; the portable build and the global tool require the [.NET 10 runtime/SDK](https://dotnet.microsoft.com/download).

### Option 1: Self-contained archive (no .NET required)

Download the archive for your platform from [Releases](https://github.com/SIkebe/github-project-migrator/releases), verify it against `SHA256SUMS.txt`, extract it and run `gpm` (`gpm.exe` on Windows):

- `gpm-vX.Y.Z-win-x64.zip`
- `gpm-vX.Y.Z-win-arm64.zip`
- `gpm-vX.Y.Z-linux-x64.tar.gz`

### Option 2: Framework-dependent archive (portable, needs .NET 10)

Download `gpm-vX.Y.Z-portable.zip`, extract, and run:

```
dotnet gpm.dll --version
```

### Option 3: .NET global tool

```
dotnet tool install -g gpm
gpm --version
```

> NuGet.org publishing may lag behind GitHub Releases; the release assets are always the source of truth.

## Quick start

```bash
# 1. Export the source project to a JSON snapshot
gpm export --org source-org --project 7 --out ./snapshot --token $SOURCE_TOKEN

# 2. Import the snapshot into the target organization
gpm import --org target-org --in ./snapshot --token $TARGET_TOKEN \
  --repo-mapping repos.csv --user-mapping users.csv

# 3. Verify the migrated project against the snapshot
gpm verify --org target-org --project 12 --in ./snapshot --token $TARGET_TOKEN
```

Tokens are resolved from `--token`, then the `GITHUB_TOKEN` / `GPM_TOKEN` environment variables. Classic PATs need the `project` and `repo` scopes (plus SSO authorization for SAML-protected organizations).

`--repo-mapping` / `--user-mapping` are CSV files (`source,target` header) that map repositories (`org/repo,org/repo`) and user logins across organizations — effectively required for cross-organization moves and EMU targets (`user_shortcode` logins).

### Migrating Views & Workflows (opt-in browser automation)

Views and Workflows have no public API, so `gpm` replays them through the Projects web UI using Playwright with **your own browser session**. This is strictly **opt-in**:

```bash
# One-time setup
gpm setup --browsers            # installs the Playwright Chromium browser
gpm login                       # interactive sign-in; session saved locally

# Then add --enable-browser-automation to export/import
gpm export --org source-org --project 7 --out ./snapshot --enable-browser-automation
gpm import --org target-org --in ./snapshot --enable-browser-automation
```

### Cross-account migration (e.g. non-EMU source → EMU target)

Use named browser profiles when the source and target require different accounts:

```bash
gpm login --profile source                    # sign in with the source account
gpm login --profile target                    # sign in with the target (e.g. EMU) account

gpm export --org source-org --project 7 --out ./snapshot \
  --token $SOURCE_TOKEN --enable-browser-automation --browser-profile source

gpm import --org target-org --in ./snapshot \
  --token $TARGET_TOKEN --enable-browser-automation --browser-profile target
```

For GHEC with data residency targets, sign in with `gpm login --profile target --base-url https://TENANT.ghe.com`.

## Supported environments

| Source | Target | Status |
|---|---|---|
| GitHub.com (non-EMU) | GitHub.com (non-EMU) | ✅ Supported |
| GitHub.com (non-EMU) | GitHub.com (EMU) | ✅ Supported (user mapping to `_shortcode` logins) |
| GitHub.com (EMU) | GitHub.com (EMU / non-EMU) | ✅ Supported |
| GitHub.com | GHEC with data residency (`*.ghe.com`) | ⚠️ Designed to work, **not yet verified** |
| GitHub Enterprise Server (GHES) | any | ❌ Not supported |

Organization projects only (user-owned projects are on the roadmap).

## Update check

`export` / `import` / `verify` asynchronously check GitHub Releases for a newer version (2-second timeout; failures are silently ignored; **no telemetry is ever sent**). Opt out with `--no-update-check` or by setting the `GPM_NO_UPDATE_CHECK` environment variable.

## Limitations

Permanent limitations (cannot be solved by any tool):

- **Original author / creation date of draft issues and status updates** cannot be preserved — the API always attributes writes to the token owner. `gpm` prepends a note with the original metadata instead.
- **Item change history** and past field values cannot be migrated (no write API for history).
- **Issues / pull requests themselves are not migrated** — that is the job of [GitHub Enterprise Importer](https://docs.github.com/en/migrations/using-github-enterprise-importer). `gpm` re-links items via the repository mapping CSV.
- **GHES is not supported** (by design).
- **Redacted items** (items the exporting user cannot see) cannot be exported; their count is reported as a warning.

Browser automation notes:

- The module is **opt-in** (`--enable-browser-automation`) and uses **your own interactive session** stored locally (`%APPDATA%/gpm/browser-state*.json`). Nothing is sent anywhere except to GitHub itself.
- Automating the web UI is not covered by the public API's stability guarantees, and you are responsible for using it in a way consistent with the [GitHub Terms of Service](https://docs.github.com/site-policy/github-terms/github-terms-of-service). `gpm` performs low-rate, human-scale, strictly sequential UI operations against your own projects — no scraping of other users' data.
- UI selectors can break when GitHub updates the Projects UI; failures degrade to warnings plus manual instructions rather than corrupting data.
- View tab order is not reproduced (views are created in view-number order); a warning is emitted.

### Auto-add workflow limits per plan

The number of Auto-add workflows per project is limited by the target organization's plan. Extra instances beyond the limit are reported as warnings with manual steps:

| Plan | Auto-add workflows per project |
|---|---|
| Free | 1 |
| Pro / Team | 5 |
| Enterprise (GHEC) | 20 |

## License

[MIT](LICENSE) © SIkebe
