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
  --repo-mapping repos.csv --user-mapping users.csv --org-mapping orgs.csv

# 3. Verify the migrated project against the snapshot
gpm verify --org target-org --project 12 --in ./snapshot --token $TARGET_TOKEN \
  --repo-mapping repos.csv --user-mapping users.csv --org-mapping orgs.csv
```

Tokens are resolved from `--token`, then the `GITHUB_TOKEN` / `GPM_TOKEN` environment variables.

`verify` reports an overall result and a result for Project, Field, Item, View, Workflow, Collaborator, and LinkedRepository:

| Result | Meaning |
|---|---|
| `Match` | Every available category was verified with no material difference. |
| `Mismatch` | At least one migration-owned value differs. |
| `PartialMatch` | No errors, but a non-fatal warning exists (for example, target-only data). |
| `NotVerified` | Required source or target data was not captured, so full equality cannot be established. |

`Mismatch` and `NotVerified` always produce exit code 1. `--fail-on-warning` also fails when warnings exist. Use `--report-json <path>` for the same overall/category results and counts in machine-readable form. Without browser automation, GraphQL-readable View settings are still compared, but UI-only View/Workflow settings and explicit collaborators are reported as `NotVerified`; use `--enable-browser-automation` when verification must prove those areas too.

| Category | Verification coverage |
|---|---|
| Project | Description, README, visibility, and closed state. A changed title is informational because import supports title overrides. |
| Field | Field presence/type, single-select option order/name/color/description, and iteration dates/durations. |
| Item | Counts/types, issue and pull request identity, draft body, field values, active-item order, and archived state. Archived-item order is excluded because GitHub cannot restore it. |
| View | Name/layout plus GraphQL filter, visible fields/order, grouping, and sorting. Browser mode adds slice, swimlanes, field sums, and roadmap dates/zoom/markers. |
| Workflow | Name/enabled state. Browser mode adds content types, status, filter, and repository. |
| Collaborator | Browser-captured explicit user/team collaborators and roles. Inherited and base-role access is excluded. |
| LinkedRepository | Linked repository identities after repository mapping. |

Insights charts, item/field-value history, and inherited/base-role access are not verified.

### Token permissions

You can use either a classic PAT or a fine-grained PAT. Fine-grained PATs are scoped to a single resource owner, so cross-organization or cross-account migrations usually need separate source and target tokens.

Classic PATs need the `project` and `repo` scopes (plus token authorization for organizations or enterprises that require SSO, including SAML- or OIDC-backed environments).

For fine-grained PATs, create the token for the organization or user that owns the projects you are exporting from or importing into. Grant repository access to every repository that can appear as a project item or linked repository; selecting all repositories for that resource owner is the simplest option during a migration.

| Command | Fine-grained PAT permissions |
|---|---|
| `gpm export` | **Organization/account permissions → Projects: Read-only**. **Repository permissions → Metadata: Read-only**, plus **Issues: Read-only** and **Pull requests: Read-only** for private repositories that contain project items. |
| `gpm import` | **Organization/account permissions → Projects: Read and write**. **Repository permissions → Metadata: Read-only**, plus **Issues: Read-only** and **Pull requests: Read-only** for private repositories referenced by `--repo-mapping` or auto-add workflows. If you import project collaborators that include teams, also grant **Organization permissions → Members: Read-only** when required to resolve those teams. |
| `gpm verify` | Same as `gpm export` for the target project. |

GitHub permissions are still enforced in addition to token permissions: the token owner must be allowed to read the source project and referenced repositories, and must be allowed to create or edit the target project.

`--repo-mapping` / `--user-mapping` / `--org-mapping` map repositories, user logins, and organizations across deployments. They are especially important for EMU targets, where user logins normally gain a `_shortcode` suffix. Repository and organization mappings use the `source,target` header. User mappings use the GitHub Enterprise Importer mannequin reclaim header (`mannequin-user,mannequin-id,target-user`); the mannequin ID is ignored. `gpm export` generates ready-to-fill `repository-mappings.csv`, `organization-mappings.csv`, and, when users are present, `user-mappings.csv`. Candidates include linked and Auto-add repositories plus identifiers found in View and Workflow filters. Existing files are never overwritten, and newly discovered candidates are reported. During browser-assisted import, `assignee:`, `author:`, `repo:`, and `org:` filter values are mapped structurally; other syntax is preserved. Organization mappings are also inferred from repository owners when unambiguous. Browser-assisted import stops before any project write when a supported filter value or Auto-add repository remains unmapped or ambiguous. API-only imports do not replay or validate UI-only Workflow settings. Pass the same mappings to `gpm verify`.

### More import/export options

```bash
# Export ALL projects of the org at once (one snapshot per project under <out>/<number>/)
gpm export --org source-org --out ./snapshots            # add --include-closed to include closed projects
gpm import --org target-org --in ./snapshots/7           # then import each snapshot individually

# Import into an EXISTING project (fields/items are merged; the project keeps its title)
gpm import --org target-org --in ./snapshot --project-number 42 \
  --repo-mapping ./snapshot/repository-mappings.csv

# Create the project under a different title
gpm import --org target-org --in ./snapshot --project-title "Roadmap (migrated)"
```

`--project-number` is mutually exclusive with `--on-conflict` and `--project-title`.

When a project with the same title already exists, `--on-conflict` controls the entire import:

| Value | Result | Existing project changes |
|---|---|---|
| `fail` (default) | Exits with an error | None |
| `skip` | Exits successfully with `result=skipped` | None; items, fields, metadata, collaborators, linked repositories, views, and workflows are not imported |
| `update` | Exits successfully with `result=updated` | Applies the snapshot, including items and browser-assisted views/workflows when enabled |

Creating a new project emits `result=created`. The result line also includes the target project number for machine-readable automation, for example `result=skipped project=42`.

### Recovering from an ambiguous mutation result

Read-only GraphQL queries and explicitly idempotent updates are retried after transient network or server failures. Resource-creation mutations are not: if GitHub may have accepted a mutation but its response was lost, `gpm` exits with `Mutation result is ambiguous` instead of risking a duplicate. The error includes the operation, target, and a non-secret client mutation ID; mutation variables and tokens are never included.

Inspect the named target operation in GitHub before retrying. Rerun with the same snapshot directory so `project-import-log.json` and `import-log.json` can reconcile pending work. Project, custom-field, Draft, and Issue/PR item creation atomically records an operation and matching target baseline before sending. On resume, `gpm` polls for and adopts exactly one new match; no match or multiple matches stop the import for manual reconciliation instead of resending.

### User-owned projects

`export` / `import` / `verify` accept `--owner-type user` to migrate projects owned by a user account instead of an organization (URLs use the `/users/<login>/projects/<n>` form):

```bash
gpm export --org monalisa   --owner-type user --project 4 --out ./snapshot
gpm import --org octocat    --owner-type user --in ./snapshot
gpm verify --org octocat    --owner-type user --project 2 --in ./snapshot
```

### Migrating Views & Workflows (opt-in browser automation)

Views and Workflows have no public API, so `gpm` replays them through the Projects web UI using Playwright with **your own browser session**. This is strictly **opt-in**:

```bash
# One-time setup
gpm setup --browsers            # installs the Playwright Chromium browser
gpm login                       # interactive sign-in; session saved locally

# Then add --enable-browser-automation to export/import/verify
gpm export --org source-org --project 7 --out ./snapshot --enable-browser-automation
gpm import --org target-org --in ./snapshot --enable-browser-automation
gpm verify --org target-org --project 12 --in ./snapshot --enable-browser-automation
```

### Cross-account migration (e.g. non-EMU source → EMU target)

Use named browser profiles when the source and target require different accounts:

```bash
gpm login --profile source                    # sign in with the source account
gpm login --profile target --base-url https://TENANT.ghe.com

gpm export --org source-org --project 7 --out ./snapshot \
  --token $SOURCE_TOKEN --enable-browser-automation --browser-profile source

gpm import --org target-org --in ./snapshot \
  --token $TARGET_TOKEN --target-base-url https://api.TENANT.ghe.com \
  --browser-base-url https://TENANT.ghe.com \
  --repo-mapping ./snapshot/repository-mappings.csv \
  --user-mapping ./snapshot/user-mappings.csv \
  --org-mapping ./snapshot/organization-mappings.csv \
  --enable-browser-automation --browser-profile target

gpm verify --org target-org --project 12 --in ./snapshot \
  --token $TARGET_TOKEN --target-base-url https://api.TENANT.ghe.com \
  --browser-base-url https://TENANT.ghe.com \
  --repo-mapping ./snapshot/repository-mappings.csv \
  --user-mapping ./snapshot/user-mappings.csv \
  --org-mapping ./snapshot/organization-mappings.csv \
  --enable-browser-automation --browser-profile target
```

For GHEC with data residency, point `gpm export --base-url` (source) or `gpm import`/`gpm verify` `--target-base-url` (target) at the tenant API endpoint, e.g. `https://api.TENANT.ghe.com` (a trailing `/graphql` is added automatically). Browser-enabled export/import/verify derives `https://TENANT.ghe.com` from that API URL; `--browser-base-url` can set it explicitly and is rejected when it names a different deployment. `setup --fixture-ui` applies the same derivation and validation to `--api-base-url`. Before browser reads or writes, `gpm` also verifies that the selected browser profile is signed in on that host as the same login used by the API token. Cloud API and browser origins must use HTTPS; HTTP is accepted only for loopback test origins. GHEC with data residency is designed to work but requires the manual tenant validation described below.

### Proxies

`gpm` uses the standard .NET `HttpClient`, which honors the `HTTPS_PROXY` / `HTTP_PROXY` (and `NO_PROXY`) environment variables by default — no extra configuration is needed behind a corporate proxy.

## Supported environments

| Source | Target | Status |
|---|---|---|
| GitHub.com (non-EMU) | GitHub.com (non-EMU) | ✅ Supported |
| GitHub.com (non-EMU) | GitHub.com (EMU) | ✅ Supported (user mapping to `_shortcode` logins) |
| GitHub.com (EMU) | GitHub.com (EMU / non-EMU) | ✅ Supported |
| GitHub.com | GHEC with data residency (`*.ghe.com`) | ⚠️ Designed to work, **not yet verified** |
| GitHub Enterprise Server (GHES) | any | ❌ Not supported |

Organization projects and user-owned projects (`--owner-type user`) are both supported.

## What gpm can migrate today

`gpm` is intended for migrating **Projects V2 configuration and project membership** after the underlying repositories, issues and pull requests have already been moved or made visible to the target account, typically with GitHub Enterprise Importer (GEI). Issue/PR content and metadata (labels, milestones, assignees, review state, parent/sub-issue relationships, comments, history, and so on) are not rewritten by `gpm`; project views show whatever exists on the target issues and pull requests.

### Project structure

| Area | Supported? | Notes |
|---|---:|---|
| Organization-owned projects | ✅ | Default mode. |
| User-owned projects | ✅ | Use `--owner-type user`. |
| Existing target project | ✅ | Use `gpm import --project-number <n>` to merge into a project you created beforehand, for example from a template. |
| Project title override | ✅ | Use `--project-title` when creating the target project. |
| Project description / README / public / closed state | ✅ | Migrated through the GraphQL API. |
| Linked repositories | ✅ | Exported and re-linked during import when the target repository can be resolved through `--repo-mapping`. |
| Project collaborators | ✅ with browser automation / API import-only | GitHub exposes a write API but no read API for project collaborators. With `--enable-browser-automation`, `gpm` exports explicitly listed project collaborators from Settings → Manage access and imports them through the API. Inherited/base-role access is outside `gpm`'s scope and is expected to come from GEI, organization/team/repository settings, or enterprise policy. |
| Project templates | ❌ | Template status is not part of v1. Use `--project-number` with a pre-created template project as a workaround. |

### Fields and field values

| Area | Supported? | Notes |
|---|---:|---|
| Text fields and values | ✅ | Includes Unicode, emoji and special characters. |
| Number fields and values | ✅ | Includes decimals, negative values and zero. |
| Date fields and values | ✅ | |
| Single-select fields and values | ✅ | Option name, color and description are migrated. |
| Iteration fields and values | ✅ | Active and completed iterations are migrated. Completed iterations are recreated by using past dates. |
| Built-in fields such as Title / Assignees / Repository / Labels / Milestone | ✅ for project/view configuration | Built-in fields are not recreated as custom fields. `gpm` preserves their use in project views where GitHub exposes them, but Issue/PR metadata values come from the target issues and pull requests (usually migrated by GEI). Draft issue title and draft assignees are handled separately. |
| Field value history | ❌ | GitHub has no API to write historical field changes. Only current values are migrated. |

### Project items

| Area | Supported? | Notes |
|---|---:|---|
| Draft issues | ✅ | Original author and timestamp are added as a note at the top of the body. |
| Draft issue assignees | ✅ | Use `--user-mapping` when logins differ, especially for EMU targets. Unmapped users are dropped with a warning. |
| Issues | ✅ | Re-linked through `--repo-mapping`. The target account must be able to see the target repository and item. The target repository is expected to contain the same issue number as the source (the normal GEI outcome). |
| Pull requests | ✅ | Same repository mapping, visibility and same-number requirements as issues. |
| Item field values | ✅ | Text, number, date, single-select, iteration and Status values are restored. |
| Item order | ✅ | Restored for non-archived items in the project-level order exposed by GitHub. |
| Archived items | ✅ | Archived state is restored after values are applied. Archived item position is not restored because GitHub does not allow moving archived items. |
| Redacted / inaccessible items | ❌ | If GitHub hides an item from the exporting user, `gpm` cannot migrate it. |

### Views (with `--enable-browser-automation`)

Views are migrated through the GitHub Projects web UI because GitHub has no public API to create or update views.

| Area | Supported? | Notes |
|---|---:|---|
| Table views | ✅ | Name, filter, sorting, visible fields, Slice by and related display options are tested. |
| Board views | ✅ | Column by, Swimlanes and Field sum are tested. |
| Roadmap views | ✅ | Date fields, zoom level and markers are tested. |
| View UI-only settings | ✅ | Exported/imported by browser automation where the UI exposes them. |
| View tab order | ❌ | Views are recreated, but tab drag-and-drop ordering is not reproduced in v1. |
| Insights charts | ❌ | Out of scope for v1. They require a separate UI automation design. |

### Workflows (with `--enable-browser-automation`)

Workflows are migrated through the GitHub Projects web UI because GitHub has no public API to create or update workflows.

| Area | Supported? | Notes |
|---|---:|---|
| Built-in item-state workflows | ✅ | Examples: item closed, pull request merged, item added to project. Status bindings are reapplied after field migration. |
| Auto-add workflows | ✅ | Repository and filter settings are migrated. Multiple Auto-add workflows are supported up to the target plan limit. |
| Auto-archive workflows | ✅ / best effort | Supported where the current GitHub UI exposes the filter in a stable way. |
| Duplicated Auto-add workflows | ✅ | Tested with two Auto-add workflows. |
| Disabled workflows | ✅ | Saved disabled workflows are migrated by saving once and toggling off when needed. |
| Unsaved default workflows that have never been configured | ⚠️ | GitHub shows some default workflows in the sidebar before they exist in GraphQL. `gpm` can configure or skip them, but they are not part of the GraphQL-only snapshot until saved. |

### Verification and safety features

| Area | Supported? | Notes |
|---|---:|---|
| `gpm verify` | ✅ | Compares target project against the snapshot. GraphQL View settings are always checked; `--enable-browser-automation` re-reads UI-only View / Workflow settings and explicit collaborators. Supports category statuses, warning exit policy, and JSON reports. |
| Resume after interruption | ✅ | Item import writes `import-log.json` so reruns do not duplicate already-created items. |
| Mapping CSV templates | ✅ | `export` writes repository and user mapping templates without overwriting existing files. |
| Bulk export | ✅ | Omit `--project` to export every project owned by the organization/user into `<out>/<number>/`. |
| Update check opt-out | ✅ | Use `--no-update-check` or `GPM_NO_UPDATE_CHECK`. No telemetry is sent. |

## What you must prepare before migrating

`gpm` is a Projects migrator, not a repository or issue migrator. A successful migration usually looks like this:

1. **Move or create the target repositories first.** Use GitHub Enterprise Importer or another migration tool for repository contents, issues, pull requests and their metadata. `gpm` assumes the target issues/PRs already exist; for linked issue/PR project items it resolves the target by repository mapping plus the same issue/PR number.
2. **Generate a snapshot** with `gpm export`.
3. **Fill in `repository-mappings.csv`.** Every issue/PR project item needs a source repository mapped to a target repository visible to the target token. If your repository migration did not preserve issue/PR numbers, `gpm` cannot relink those items in v1.
4. **Fill in `user-mappings.csv` if generated.** This is important for Enterprise Managed Users where target logins usually have a `_shortcode` suffix.
5. **Use browser automation only when you need Views or Workflows.** Run `gpm setup --browsers` and `gpm login` first, then pass `--enable-browser-automation` to both export and import.

When source and target repository or user names differ, pass the same `--repo-mapping` and `--user-mapping` files to `gpm verify` as well. This lets verification compare imported Issue / Pull Request items, linked repositories, and explicit user collaborators after remapping.

If you do not enable browser automation, `gpm` still migrates projects, fields, items, values, ordering, archived state and linked repositories, but **Views and Workflows will not be fully recreated**.

## What gpm intentionally does not support

The following are out of scope for this project, either by design or because GitHub does not expose the required write APIs:

| Area | Why not? | Workaround |
|---|---|---|
| GitHub Enterprise Server (GHES) | `gpm` is designed for GitHub.com and GHEC with data residency. | Use `gh-migrate-project` for GHES scenarios. |
| Migrating repositories, issues, pull requests or their metadata | This is outside the Projects API. | Use GitHub Enterprise Importer first, then map the resulting repositories. `gpm` expects issue/PR numbers to be preserved for project item relinking. |
| Original author and creation timestamp for draft issues | GitHub always attributes newly-created draft issues to the importing user. | `gpm` prepends a note with the original metadata. |
| Item history / field history | GitHub has no API to recreate historical field changes. | Only the current state is migrated. |
| Exporting inherited/base-role project access | GitHub's access page separates explicit project collaborators from inherited access (organization owners, base role, enterprise policy, repository/team inheritance). `gpm` exports explicit collaborators with browser automation, but not inherited access. | Handle inherited access through GEI and the target organization/team/repository/enterprise policy model. |
| View tab drag-and-drop order | UI-only and fragile; not part of v1. | Reorder tabs manually if the order matters. |
| Insights charts | No public API; UI is more complex than Views/Workflows. | Future v2 candidate. |
| Redacted items | The exporting account cannot see them. | Export with an account that has access to all project content. |

## Update check

`export` / `import` / `verify` asynchronously check GitHub Releases for a newer version (2-second timeout; failures are silently ignored; **no telemetry is ever sent**). Opt out with `--no-update-check` or by setting the `GPM_NO_UPDATE_CHECK` environment variable.

## Development docs

- [Test strategy](docs/TEST_STRATEGY.md) is a Japanese summary of the automated, browser, CI, packaging and manual release validation layers.
- [Manual test plan](docs/MANUAL_TEST_PLAN.md) walks through the GEI + `gpm` end-to-end migration validation flow.

## Limitations

Permanent limitations (cannot be solved by any tool):

- **Original author / creation date of draft issues and status updates** cannot be preserved — the API always attributes writes to the token owner. `gpm` prepends a note with the original metadata instead.
- **Item change history** and past field values cannot be migrated (no write API for history).
- **Issues / pull requests themselves are not migrated** — that is the job of [GitHub Enterprise Importer](https://docs.github.com/en/migrations/using-github-enterprise-importer). `gpm` re-links project items via the repository mapping CSV and expects the target issue/PR number to match the source number.
- **Issue/PR metadata is not rewritten by `gpm`** — labels, milestones, assignees, reviewers, linked PRs, parent/sub-issue relationships, comments and issue/PR history are GEI/repository-migration concerns. Project filters that reference those metadata values are migrated as strings and rely on the target issues/PRs having equivalent metadata.
- **Project collaborators cannot be exported through the API** — the GraphQL API has no read field for them (write-only via `updateProjectV2Collaborators`). With `--enable-browser-automation`, `gpm` exports explicitly listed project collaborators from the web UI and imports them through the API (`collaborators` array: `{ "type": "USER"|"TEAM", "login": "...", "role": "READER"|"WRITER"|"ADMIN" }`). Inherited access (organization owners, base role, policies, repository/team inheritance) is outside `gpm`'s scope and should be handled through GEI and the target organization/team/repository/enterprise policy model.
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
