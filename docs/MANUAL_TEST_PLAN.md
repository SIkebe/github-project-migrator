# 手動テストプラン: GEI + ghpmv end-to-end 移行検証

このドキュメントは、GitHub Enterprise Importer(GEI) でリポジトリ / Issue / Pull Request を先に移行し、その後 `ghpmv` で GitHub Projects V2 を移行・検証するための手動テスト手順です。

想定する主シナリオは次の通りです。

- 移行元: GitHub.com の source organization / account
- 移行先: GitHub.com または GHEC / EMU の target organization / account
- リポジトリ移行: GEI
- Project V2 移行: `ghpmv export` → `ghpmv import` → `ghpmv verify`
- Views / Workflows / collaborator の export / verify: `--enable-browser-automation` を使う

> 注意: トークン値、organization 名、repository 名、project number は環境ごとに置き換えてください。トークンはチャットやログに貼らないでください。

GitHub Copilot に一問一答で案内させる場合は、repository-local Skill [ghpmv-e2e-validation](../.github/skills/ghpmv-e2e-validation/SKILL.md) を使用できます。「ghpmv を実環境でステップバイステップ検証したい」と依頼すると、このテストプランに沿って一段ずつ進めます。

---

## 1. 目的と合格基準

### 1.1 目的

1. GEI で source repository を target organization へ移行できることを確認する。
2. GEI 移行後の target repository に対して、`ghpmv` が source Project の Issue / PR item を repository mapping + 同一番号で再リンクできることを確認する。
3. `ghpmv` が Project metadata / fields / items / values / order / archived state / linked repositories / explicit collaborators / Views / Workflows を移行できることを確認する。
4. `ghpmv verify` で移行結果を検証し、既知の恒久制限以外に error が出ないことを確認する。
5. セッション失効、mapping 不足、browser automation 無効時など、手動運用で起きやすい失敗が分かりやすく検出されることを確認する。

### 1.2 合格基準

この手動テストは、以下を満たしたら合格です。

- GEI の repository migration が `Succeeded` になる。
- target repository に Issue #1 / Issue #2 / open PR #3 相当が存在し、source と同じ number で参照できる。
- `ghpmv export --enable-browser-automation` が snapshot を作成し、Views / Workflows / explicit collaborators の warning が想定範囲内である。
- `repository-mappings.csv` と必要に応じて `user-mappings.csv` を補完したうえで、`ghpmv import --enable-browser-automation` が完了する。
- `ghpmv verify` が `OK: the target project matches the snapshot.`、または既知制限に由来する warning のみを出す。
- target Project の UI 目視確認で、少なくとも以下が一致する。
  - Project title / description / README
  - custom fields と options / iterations
  - draft / issue / PR items と主要 field values
  - archived item の archived state
  - linked repository
  - explicit project collaborators
  - Table / Board / Roadmap views
  - enabled / disabled workflows と Auto-add settings

---

## 2. テスト対象範囲

### 2.1 対象

| 領域 | 検証方法 | 備考 |
|---|---|---|
| Repository / Issues / Pull Requests | GEI + 目視 / `gh` | `ghpmv` のスコープ外。Project item relink の前提条件。 |
| Project metadata | `ghpmv verify` + 目視 | title / shortDescription / README / public / closed state。 |
| Fields | `ghpmv verify` + 目視 | Text / Number / Date / Single-select / Iteration / Status options。 |
| Items | `ghpmv verify` + 目視 | Draft / Issue / PR / archived / assigned draft。 |
| Field values | `ghpmv verify` + 目視 | Unicode、emoji、number、date、option、iteration。 |
| Item order | `ghpmv verify` + 目視 | archived item の position は GitHub API 制限により対象外。 |
| Linked repositories | `ghpmv verify` warning 確認 + 目視 | `--repo-mapping` が必須。 |
| Explicit project collaborators | browser export/import + 目視 | inherited access は対象外。 |
| Views | browser export/import + 目視 | Table / Board / Roadmap、filter、sort、slice、field sum など。 |
| Workflows | browser export/import + 目視 | built-in workflows、Auto-add、disabled workflow。 |

### 2.2 対象外または warning 許容

- Issue / PR の本文、コメント、labels、milestones、review state などの repository metadata 自体の差分修正。
- Draft issue の元作成者 / 作成日時の完全保持。
- item / field value の履歴。
- inherited / base-role / org owner / enterprise policy 由来の project access。
- View tab の drag-and-drop 順序。
- Insights charts。
- REDACTED / 権限不足で見えない items。
- archived item の position。

---

## 3. 推奨テスト環境

### 3.1 Organization / account

最小構成:

| 用途 | 例 | 要件 |
|---|---|---|
| Source org | `gpm-source` | Project と fixture repository を作れること。 |
| Target org | `gpm-target` | GEI の migration target にでき、Project を作れること。 |
| Source browser profile | `source` | source Project と source repo を読めるアカウントで `ghpmv login`。 |
| Target browser profile | `target` | target Project を編集できるアカウントで `ghpmv login`。 |

EMU / SAML / OIDC backed organization の場合は、PAT と browser session の両方で SSO authorization を完了してください。

### 3.2 Fixture repository / Project

推奨 fixture:

- Source repository: `fixture-repo`
- Target repository: `fixture-repo-gei-target` または衝突しない任意名
- Source Project: `gpm-fixture`

`ghpmv setup --fixture` は source org に以下を作成します。

- private repository `fixture-repo`
- Issue 2 件
- open Pull Request 1 件
- Project `gpm-fixture`
- custom fields(Text / Number / Date / Single-select / Iteration)
- draft items、Issue item、PR item、archived draft、assigned draft
- linked repository

Views / Workflows は public API だけでは作成できませんが、`ghpmv setup --fixture-ui` が C# の Playwright layer で標準テスト用 View / Workflow を作成します。手動で UI をぽちぽち濃くする必要はありません。fixture setup の実装本体は C# CLI に一本化されています。

---

## 4. 事前準備

### 4.1 ローカルツール

Windows PowerShell で以下を確認します。

```powershell
dotnet --info
gh --version
git --version
```

GEI extension が未インストールの場合:

```powershell
gh extension install github/gh-gei
gh gei --help
```

既に入っている場合は更新します。

```powershell
gh extension upgrade github/gh-gei
```

### 4.2 環境変数

`.env` に次のプレースホルダーを用意しています。実行前にローカルで値を入れてください。

| 変数 | 用途 |
|---|---|
| `GHPMV_SOURCE_TOKEN` | source fixture 作成と `ghpmv export` に使う token。 |
| `GHPMV_TARGET_TOKEN` | target fixture 作成と `ghpmv import` / `ghpmv verify` に使う token。 |
| `GHPMV_GEI_SOURCE_TOKEN` | GEI source 用の classic PAT。 |
| `GHPMV_GEI_TARGET_TOKEN` | GEI destination 用の classic PAT。 |
| `GHPMV_TEST_TOKEN` | 既存 integration / browser E2E tests を手動で回す場合。 |
| `GHPMV_TEST_ORG` | integration / browser E2E tests の source organization login。未指定時は `GHPMV_SOURCE_ORG`、それも無ければ `gpm-source`。CI repo variable でも指定。 |
| `GHPMV_TEST_TARGET_ORG` | integration tests の target organization login。未指定時は `GHPMV_TARGET_ORG`、それも無ければ `gpm-target`。CI repo variable でも指定。 |
| `GHPMV_TEST_PROJECT_NUMBER` | integration tests が export 元として使う fixture Project number。未指定時は現在の shared fixture `89`。CI repo variable でも指定。 |
| `GHPMV_TEST_FIXTURE_REPO` | integration tests が期待する source fixture repository short name。未指定時は `GHPMV_FIXTURE_REPO`、それも無ければ現在の shared fixture repo `fixture-repo2`。CI repo variable でも指定。 |
| `GHPMV_TEST_TARGET_FIXTURE_REPO` | integration tests が linked repository remap 先として期待する target fixture repository short name。未指定時は `fixture-repo`。CI repo variable でも指定。 |
| `GHPMV_SOURCE_ORG` | source organization login。 |
| `GHPMV_TARGET_ORG` | target organization login。 |
| `GHPMV_FIXTURE_REPO` | source fixture repository 名。 |
| `GHPMV_TARGET_REPO` | GEI で作る target repository 名。 |
| `GHPMV_SNAPSHOT_DIR` | `ghpmv export` の出力先。 |

PowerShell で `.env` を読み込む例:

```powershell
Get-Content .env | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
```

<a id="fixture-token-permissions"></a>

### 4.3 移行用 token と fixture 作成用 token を分ける

この手動テストでは、通常の Project 移行より広い権限で demo repository / Issue / pull request / Project を作成します。通常の利用者が既存 Project を移行するだけなら、fixture 作成用権限は不要です。

| 用途 | コマンド | 権限 |
|---|---|---|
| 通常の Project 移行 | `export` / `import` / `verify` | README の [Token permissions](../README.md#token-permissions) にある command 別の最小権限。 |
| API-backed test fixture 作成 | `setup --fixture` | 下記の fine-grained PAT、または classic PAT。 |
| UI-only fixture 作成 | `setup --fixture-ui` | Project API を読める token と、同じユーザーで保存した browser profile。 |
| GEI source | `gh gei migrate-repo --github-source-pat` | 下記の GEI source role / classic PAT scope。 |
| GEI destination | `gh gei migrate-repo --github-target-pat` | 下記の GEI destination role / classic PAT scope。 |

source と target の resource owner またはアカウントが異なる場合、token と browser profile はそれぞれ別に用意します。

fine-grained PAT は organization-owned Project にだけ使用します。GitHub は user-owned Project へのアクセスを [fine-grained PAT の制限事項](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#fine-grained-personal-access-token-limitations) としているため、`--owner-type user` には classic PAT を使用します。

fine-grained PAT を作成する場合は、README の [pre-filled token creation URL](../README.md#fine-grained-pats) を使い、resource owner と必要な permission を事前入力します。URL では repository access を指定できないため、フォームを開いた後に対象 repository または **All repositories** を選択し、permission、expiration、organization approval を確認してから token を生成します。

`setup --fixture` の完全自動パスは private organization repository を新規作成するため、通常の migration command より広い権限が必要です。確実性を優先する場合は classic PAT を推奨します。GitHub の [fine-grained PAT permission matrix](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens#repository-permissions-for-administration) では `POST /orgs/{org}/repos` に Repository **Administration: write** が必要です。fine-grained PAT を使う場合は resource owner に対象 organization を選び、次を付与します。

- Repository access: **All repositories**
- Repository permissions: **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**
- Organization permissions: **Projects: Read and write**
- Organization が要求する token approval

この設定は token owner 自身の権限を超えません。token owner の organization role、member に許可された repository visibility、organization の PAT 制限・承認ポリシー、SSO authorization は別に確認します。`setup --fixture` の前に SKILL の permission preflight を実行し、403 の場合は原因を断定せず、これらの設定を確認します。

**Administration** または **All repositories** を付与できない場合は、空 repository を先に作成し、その repository を選択した fine-grained PAT に Administration 以外の fixture 権限を付与します。classic PAT を使う場合は次の scope を使用します。

- `repo`
- `project`
- `read:org`

classic PAT に `read:org` がない場合、fixture Project の存在確認で Organization ID を取得する GraphQL query が `INSUFFICIENT_SCOPES` になります。

GEI repository migration は fixture setup と別の権限体系です。GitHub の [Managing access for a migration between GitHub products](https://docs.github.com/en/migrations/using-github-enterprise-importer/migrating-between-github-products/managing-access-for-a-migration-between-github-products#required-scopes-for-personal-access-tokens) に従い、fine-grained PAT ではなく classic PAT を使います。

| GEI token | 実行ユーザーの role | 必要な classic PAT scope |
|---|---|---|
| source | Organization owner または source organization の migrator | `admin:org`, `repo` |
| destination | Organization owner | `repo`, `admin:org`, `workflow` |
| destination | destination organization の migrator | `repo`, `read:org`, `workflow` |

GEI には `GHPMV_GEI_SOURCE_TOKEN` / `GHPMV_GEI_TARGET_TOKEN` を分けて用意することを推奨します。同じ classic PAT を fixture、`ghpmv`、GEI で再利用する場合は、実行する全用途の scope の和集合が必要です。fixture 用の `repo`, `project`, `read:org` だけでは GEI migration を queue できません。source / destination のどちらでも、token のユーザーが Organization owner または対象 organization に明示的に付与された migrator role を持つことを確認してください。

`gh auth login` / `gh auth refresh` を使う場合も、Project 操作に必要な scope を含めます。

```powershell
gh auth status
gh auth refresh -s project,read:org,repo
```

[SAML SSO organization](https://docs.github.com/en/authentication/authenticating-with-single-sign-on/authorizing-a-personal-access-token-for-use-with-single-sign-on) では、classic PAT または GitHub CLI の OAuth application を organization に明示的に Authorize してください。fine-grained PAT は resource owner を選択した作成時に SSO authorization が行われますが、organization の token approval は別に必要な場合があります。認可漏れがあると `saml_failure` や GraphQL の permission error になります。

### 4.4 `ghpmv` のビルドとブラウザー準備

```powershell
dotnet restore Ghpmv.slnx
dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- --version
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- setup --browsers
```

Source / target の browser profile を作成します。

```powershell
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- login --profile source
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- login --profile target
```

GHEC with data residency target の場合は、target profile に tenant host を指定します。

```powershell
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- login --profile target --base-url https://TENANT.ghe.com
```

---

## 5. Source fixture の作成

### 5.1 API で作れる部分を C# で作成

```powershell
dotnet run --project src/Ghpmv.Cli -- setup `
  --fixture `
  --fixture-org $env:GHPMV_SOURCE_ORG `
  --fixture-title gpm-fixture `
  --fixture-repo $env:GHPMV_FIXTURE_REPO `
  --token $env:GHPMV_SOURCE_TOKEN
```

出力された Project URL と project number を控えます。

```text
Source project URL: https://github.com/orgs/<source-org>/projects/<source-project-number>
Source project number: <source-project-number>
```

### 5.2 UI-only fixture を C# / Playwright で作成する

`ghpmv setup --fixture` は public API で作れる repository / fields / items までを作ります。Views / Workflows は public API が無いため、続けて `ghpmv setup --fixture-ui` を実行し、C# の `ViewUiImporter` / `WorkflowUiImporter` が Playwright で GitHub UI を操作して作成します。

```powershell
dotnet run --project src/Ghpmv.Cli -- setup `
  --fixture-ui `
  --fixture-org $env:GHPMV_SOURCE_ORG `
  --fixture-project <source-project-number> `
  --fixture-repo $env:GHPMV_FIXTURE_REPO `
  --token $env:GHPMV_SOURCE_TOKEN `
  --browser-profile source
```

API-backed fixture 作成と UI-only fixture 作成を 1 回で実行する場合は、`--fixture` と `--fixture-ui` を併用できます。この場合、`--fixture-project` は不要です。

```powershell
dotnet run --project src/Ghpmv.Cli -- setup `
  --fixture `
  --fixture-ui `
  --fixture-org $env:GHPMV_SOURCE_ORG `
  --fixture-title gpm-fixture `
  --fixture-repo $env:GHPMV_FIXTURE_REPO `
  --token $env:GHPMV_SOURCE_TOKEN `
  --browser-profile source
```

既に同名 Project が存在する場合、`--fixture --fixture-ui` の組み合わせでは Views / Workflows の重複作成を避けるため UI 適用は自動で skip されます。既存 Project に UI-only fixture を強制的に適用する場合だけ、`--fixture` を外して `--fixture-ui --fixture-project <source-project-number>` を明示してください。

> **再実行時の注意:** `setup --fixture-ui` は built-in Workflows を再設定できますが、既存の non-default Views を名前で再利用しません。同じ Project へ明示的に再実行すると `Fixture Board` / `Fixture Roadmap` が重複するため、最も安全なのは新しい fixture Project を使うことです。同じ Project で再検証する場合は、`View 1` を残して既存の `Fixture Board` / `Fixture Roadmap` を削除してから実行してください。

このコマンドは、既存 Project に対して標準テスト用の以下を作成します。

- Views
  - `View 1`: Table、filter、sort、Slice by、visible fields
  - `Fixture Board`: Board、Column by、Swimlanes、Field sum
  - `Fixture Roadmap`: Roadmap、date fields、Quarter zoom、markers
- Workflows
  - item state 系 built-in workflows
  - `Auto-add to project`
  - `Auto-add secondary`
  - saved-but-disabled workflow

前提:

- `dotnet run --project src/Ghpmv.Cli -- setup --browsers` が完了している。
- `dotnet run --project src/Ghpmv.Cli -- login --profile source` で source org を編集できる browser session が保存されている。
- 対象 Project は `ghpmv setup --fixture` で作成済みで、`Fixture Text` / `Fixture Number` / `Fixture Date` / `Fixture Select` / `Fixture Sprint` fields と `$env:GHPMV_FIXTURE_REPO` repository が存在する。

`ghpmv setup --fixture-ui` が GitHub UI 変更などで失敗した場合のみ、フォールバックとして Source Project を開き、以下を手動で設定します。

Views:

- Table view
  - filter を設定
  - hidden / visible fields を調整
  - 2 key sort
  - group by Status
  - Field sum に Number field を設定
- Board view
  - Column by Status または Single-select field
  - Swimlanes / Slice by を設定
- Roadmap view
  - Date field または Iteration を設定
  - Zoom を Quarter に設定
  - markers を有効化

Workflows:

- Item added to project → Status を特定値へ設定
- Item closed / Pull request merged → Status を特定値へ設定
- Auto-add to project → source repository + filter を設定
- 可能であれば Auto-add を 2 本に増やす
- disabled workflow を 1 本用意する

Collaborators:

- 明示的な project collaborator を 1 人以上追加し、role を Reader / Writer / Admin のいずれかで確認する。
- inherited access は `ghpmv` の対象外なので、目視確認では explicit collaborator のみを判定する。

---

## 6. GEI による repository migration

### 6.1 事前確認

source repository に Issue / PR が存在することを確認します。

```powershell
gh repo view "$env:GHPMV_SOURCE_ORG/$env:GHPMV_FIXTURE_REPO"
gh issue list --repo "$env:GHPMV_SOURCE_ORG/$env:GHPMV_FIXTURE_REPO" --state all
gh pr list --repo "$env:GHPMV_SOURCE_ORG/$env:GHPMV_FIXTURE_REPO" --state all
```

target repository 名が未使用であることを確認します。

```powershell
gh repo view "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO"
```

存在する場合は、別名にするか cleanup してから進めます。

destination organization または enterprise に repository ruleset がある場合は、各 ruleset の bypass list に **Repository migrations** を追加し、mode を **Exempt** にします。既定の **Always allow** のままでは migration push の評価が timeout する可能性があります。詳細は [Setting ruleset bypasses for repository migrations](https://docs.github.com/en/enterprise-cloud@latest/migrations/troubleshooting/setting-ruleset-bypasses-for-repository-migrations) を参照してください。

### 6.2 GEI migration を queue / 実行

4.3 の GEI role と classic PAT scope を source / destination の両方で確認します。満たしていない場合はここで停止し、fixture 用 token のまま migration を実行しません。

`gh gei migrate-repo --help` で現在の extension の引数を確認してから実行してください。代表例:

```powershell
gh gei migrate-repo `
  --github-source-org $env:GHPMV_SOURCE_ORG `
  --source-repo $env:GHPMV_FIXTURE_REPO `
  --github-target-org $env:GHPMV_TARGET_ORG `
  --target-repo $env:GHPMV_TARGET_REPO `
  --github-source-pat $env:GHPMV_GEI_SOURCE_TOKEN `
  --github-target-pat $env:GHPMV_GEI_TARGET_TOKEN `
  --target-repo-visibility private
```

実行環境や GEI extension version によっては `--queue-only` / `--wait` / migration log download 系のオプションを併用してください。downloadable migration log は完了後 24 時間だけ取得できます。また、target repository で Issues が無効な場合は `Migration Log` Issue が作成されません。詳細は [Accessing your migration logs for GitHub Enterprise Importer](https://docs.github.com/en/migrations/using-github-enterprise-importer/completing-your-migration-with-github-enterprise-importer/accessing-your-migration-logs-for-github-enterprise-importer) を参照してください。

### 6.3 GEI 結果確認

target repository が作成され、Issue / PR number が維持されていることを確認します。

```powershell
gh repo view "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO"
gh issue view 1 --repo "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO"
gh issue view 2 --repo "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO"
gh pr view 3 --repo "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO"
```

PR number が異なる場合、`ghpmv` v1 は source issue / PR number と target issue / PR number の個別 mapping を持たないため、その item の relink はできません。この場合はテスト結果を失敗ではなく「前提条件未達」として記録します。

### 6.4 GEI を使わない短時間デモ

repository migration 自体ではなく `ghpmv` の end-to-end 動作を短時間で確認する場合、target 側にも `setup --fixture` を実行して同じ Issue / PR number を持つ対応 repository を作れます。この方法は GEI の検証を代替しません。また、対応 repository と同時に補助 Project が 1 つ作成されるため、テスト後に削除してください。

```powershell
dotnet run --project src/Ghpmv.Cli -c Release --no-build -- setup `
  --fixture `
  --fixture-org $env:GHPMV_TARGET_ORG `
  --fixture-title "gpm-target-seed-$(Get-Date -Format yyyyMMdd-HHmmss)" `
  --fixture-repo $env:GHPMV_TARGET_REPO `
  --token $env:GHPMV_TARGET_TOKEN
```

このパスでも `GHPMV_TARGET_TOKEN` には 4.3 の fixture setup 用権限が必要です。`setup --fixture-ui` は補助 Project には不要です。

---

## 7. `ghpmv` export / mapping / import / verify

### 7.1 Source Project を export

再実行時は新しい snapshot directory を使用してください。`ghpmv export` は既存の mapping CSV を上書きしないため、古い directory を再利用すると新しい候補が未設定のまま残ることがあります。

```powershell
dotnet run --project src/Ghpmv.Cli -- export `
  --org $env:GHPMV_SOURCE_ORG `
  --project <source-project-number> `
  --out $env:GHPMV_SNAPSHOT_DIR `
  --token $env:GHPMV_SOURCE_TOKEN `
  --enable-browser-automation `
  --browser-profile source `
  --no-update-check
```

確認ポイント:

- `$env:GHPMV_SNAPSHOT_DIR/snapshot.json` が作成される。
- `repository-mappings.csv` が生成される。
- source UI の Views / Workflows / collaborators に関する warning がない、または想定内である。

### 7.2 Mapping CSV を補完

生成された `repository-mappings.csv` の **すべての空の target column** を GEI 移行後 repository、または 6.4 で作成した target fixture repository に合わせます。linked repository の完全名だけでなく、Workflow filter などから repository short name の候補行が生成されることがあります。両方の行を同じ target repository へ対応付けてください。

```csv
source,target
gpm-source/fixture-repo,gpm-target/fixture-repo-gei-target
fixture-repo,gpm-target/fixture-repo-gei-target
```

PowerShell で簡易生成する場合:

```powershell
@"
source,target
$env:GHPMV_SOURCE_ORG/$env:GHPMV_FIXTURE_REPO,$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO
$env:GHPMV_FIXTURE_REPO,$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO
"@ | Set-Content -Encoding UTF8 "$env:GHPMV_SNAPSHOT_DIR/repository-mappings.csv"
```

`user-mappings.csv` が生成されている場合は、EMU target login に合わせて `target-user` を補完します。`user-mappings.csv` には draft issue assignee と explicit user collaborator が含まれます。既存ファイルは re-export しても上書きされないため、collaborator 追加後にテンプレート行が増えない場合は、手動で行を追加するか、編集内容を退避してから既存 `user-mappings.csv` を削除して再 export してください。

`organization-mappings.csv` の候補も補完します。

```csv
source,target
gpm-source,gpm-target
```

import と verify には同じ 3 種類の mapping file を渡します。生成されなかった optional file の引数だけを外してください。

### 7.3 Target Project へ import

新規 Project として import します。

```powershell
dotnet run --project src/Ghpmv.Cli -- import `
  --org $env:GHPMV_TARGET_ORG `
  --in $env:GHPMV_SNAPSHOT_DIR `
  --token $env:GHPMV_TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_SNAPSHOT_DIR/repository-mappings.csv" `
  --user-mapping "$env:GHPMV_SNAPSHOT_DIR/user-mappings.csv" `
  --org-mapping "$env:GHPMV_SNAPSHOT_DIR/organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --project-title "gpm-fixture migrated $(Get-Date -Format yyyyMMdd-HHmmss)" `
  --no-update-check
```

`user-mappings.csv` が存在しない場合は `--user-mapping` を外してください。その他の mapping file も生成されなかった場合だけ、対応する引数を外します。

出力された target Project URL と project number を控えます。

```text
Target project URL: https://github.com/orgs/<target-org>/projects/<target-project-number>
Target project number: <target-project-number>
```

### 7.4 Verify

```powershell
dotnet run --project src/Ghpmv.Cli -- verify `
  --org $env:GHPMV_TARGET_ORG `
  --project <target-project-number> `
  --in $env:GHPMV_SNAPSHOT_DIR `
  --token $env:GHPMV_TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_SNAPSHOT_DIR/repository-mappings.csv" `
  --user-mapping "$env:GHPMV_SNAPSHOT_DIR/user-mappings.csv" `
  --org-mapping "$env:GHPMV_SNAPSHOT_DIR/organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --report-json "$env:GHPMV_SNAPSHOT_DIR/verify-report.json" `
  --no-update-check
```

`--enable-browser-automation` を付けた verify は、比較前に target の View / Workflow UI 設定と explicit collaborators を再取得します。選択した profile が target host に未認証、または API token と別アカウントの場合は、target の読み取り開始前に明確なエラーと非ゼロ終了になります。

source / target の repository 名または user login が異なる場合、`verify` にも import と同じ `--repo-mapping` / `--user-mapping` を渡してください。これにより Issue / PR item、linked repository、explicit user collaborator は target 側の名前へ正規化して比較されます。`user-mappings.csv` が存在しない場合は `--user-mapping` を外してください。

期待値:

```text
OK: the target project matches the snapshot.
```

warning / error が出た場合は、次の観点で切り分けます。

| 症状 | よくある原因 | 対応 |
|---|---|---|
| Issue / PR item が skip | `repository-mappings.csv` 不足、target repo 不可視、Issue / PR number 不一致 | mapping、token visibility、GEI 結果を確認。 |
| `saml_failure` | PAT の organization SSO authorization 漏れ | GitHub settings で token / GitHub CLI を Authorize。 |
| Browser session expired | `ghpmv login --profile ...` 未実施または期限切れ | source / target profile で再ログイン。 |
| `The browser session is not signed in to 'github.com'` | 保存済み target browser session の失効、または profile 間違い | `ghpmv login --profile target` を再実行し、target token と同じユーザーでログイン。 |
| `Resource not accessible by personal access token` (`setup --fixture`) | token permission、token owner の role、repository creation / PAT policy、approval、SSO のいずれか | 4.3 の各条件を確認し、原因を permission 一つに断定しない。解決できない場合は空 repository を先に作成するか、classic PAT を使用。 |
| `INSUFFICIENT_SCOPES` と `id` / `read:org` | fixture setup 用 classic PAT に `read:org` がない | token に `read:org` を追加し、必要なら SSO を再承認。 |
| Workflow 保存失敗 | target plan の Auto-add 上限、target repo 不可視、filter 不正 | warning 内容と UI を確認。 |
| Collaborator skip | target user / team が存在しない、権限不足 | mapping と target org membership を確認。 |

---

## 8. 目視確認チェックリスト

### 8.1 GEI / repository

- [ ] target repository が private / public など想定 visibility で作成されている。
- [ ] Issue #1 / #2 が target に存在する。
- [ ] PR #3 が target に存在し、open state が維持されている。
- [ ] labels / milestones / assignees など、Project filter に使う metadata が target repository に存在する。

### 8.2 Project metadata / fields

- [ ] Project title が import 時指定どおり。
- [ ] short description が一致。
- [ ] README が改行・emoji を含めて概ね一致。
- [ ] Text / Number / Date / Single-select / Iteration fields が存在する。
- [ ] Single-select options の name / color / description が一致。
- [ ] Iteration の completed / current / future 相当が再現されている。

### 8.3 Items / values

- [ ] Draft items が存在する。
- [ ] Draft item の original author / timestamp note が本文先頭に追加されている。
- [ ] Issue item が target repository の Issue にリンクしている。
- [ ] PR item が target repository の PR にリンクしている。
- [ ] Unicode / emoji text value が壊れていない。
- [ ] Number value の decimal / negative / zero が維持されている。
- [ ] Date / Single-select / Iteration values が維持されている。
- [ ] archived draft が archived state になっている。
- [ ] assigned draft の assignee が user mapping 後の target user になっている、または mapping 不足 warning が出ている。

### 8.4 Views

- [ ] Table view の filter / visible fields / sort / group by / field sum が一致。
- [ ] Board view の Column by / Swimlanes / Slice by が一致。
- [ ] Roadmap view の date fields / zoom / markers が一致。
- [ ] View 名が一致。
- [ ] View tab order は v1 対象外として warning または手動補正対象に記録。

### 8.5 Workflows

- [ ] Item added / Item closed / Pull request merged などの built-in workflow が設定されている。
- [ ] Status value binding が target Status option に向いている。
- [ ] Auto-add workflow の target repository が GEI 後 repository になっている。
- [ ] Auto-add filter が source と一致。
- [ ] disabled workflow が disabled のまま、または仕様どおり一時有効化後に disabled へ戻っている。
- [ ] target plan 上限を超える Auto-add が warning + skip になる。

### 8.6 Access / linked repositories

- [ ] linked repository が target repository に置き換わっている。
- [ ] explicit project collaborator と role が一致。
- [ ] inherited access は判定対象外として記録。

---

## 9. 追加のネガティブテスト

必要に応じて、同じ fixture で次のテストを実施します。

| ID | 手順 | 期待結果 |
|---|---|---|
| N-1 | `--enable-browser-automation` なしで export/import | API-only 項目は移行され、Views / Workflows UI-only 項目は warning または未移行として扱われる。 |
| N-2 | `repository-mappings.csv` から fixture repo 行を削除して import | Issue / PR item が warning + skip され、Draft items は作成される。 |
| N-3 | target token を source token に差し替えて import | 権限不足で失敗し、Project を壊さない。 |
| N-4 | browser profile を間違える | ログイン / 権限エラーで失敗し、再ログイン案内が出る。 |
| N-5 | Auto-add 上限に近い Project へ import | 超過分が warning + skip される。 |
| N-6 | `verify` 前に target field value を手動変更 | `ghpmv verify` が差分を error として検出する。 |

---

## 10. クリーンアップ

手動テストごとに作った target Project / target repository は、結果記録後に削除します。

```powershell
gh repo delete "$env:GHPMV_TARGET_ORG/$env:GHPMV_TARGET_REPO" --yes
```

Project は GitHub UI から削除するか、GraphQL mutation で削除します。誤削除防止のため、Project URL と title を確認してから実行してください。

Source fixture は継続利用するため、通常は削除しません。作り直す場合のみ、source Project / source repository を明示的に削除します。

---

## 11. 実施記録テンプレート

```text
Date:
Tester:

Source org:
Target org:
Source repo:
Target repo:
Source project number:
Target project number:

GEI command/version:
GEI migration status:
GEI log URL/file:

ghpmv commit/version:
Export result:
Import result:
Verify result:

Warnings:
-

Manual UI differences:
-

Known limitations accepted:
-

Pass/Fail:
Follow-up issues:
-
```

---

## 12. GHEC with data residency を含める場合

GHEC with data residency target を検証する場合は、通常手順に以下を追加します。

GEI repository migration に必要な organization role と classic PAT scope は GitHub.com target と同じです。data residency 固有の hostname と IP allow list を追加で確認します。

1. target browser profile を tenant host で作成する。
2. `ghpmv import` / `ghpmv verify` に `--target-base-url https://api.TENANT.ghe.com` と `--browser-base-url https://TENANT.ghe.com` を指定する。
3. target repository mapping は tenant 内 repository にする。GHEC-DR tenant 外の repository link は不可。
4. GEI 側の target organization / enterprise 設定が DR tenant に向いていることを GEI の公式手順で確認する。

例:

```powershell
dotnet run --project src/Ghpmv.Cli -- import `
  --org $env:GHPMV_TARGET_ORG `
  --in $env:GHPMV_SNAPSHOT_DIR `
  --token $env:GHPMV_TARGET_TOKEN `
  --target-base-url https://api.TENANT.ghe.com `
  --browser-base-url https://TENANT.ghe.com `
  --repo-mapping "$env:GHPMV_SNAPSHOT_DIR/repository-mappings.csv" `
  --user-mapping "$env:GHPMV_SNAPSHOT_DIR/user-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --no-update-check
```

続けて browser-assisted verify を実行します。

```powershell
dotnet run --project src/Ghpmv.Cli -- verify `
  --org $env:GHPMV_TARGET_ORG `
  --project <target-project-number> `
  --in $env:GHPMV_SNAPSHOT_DIR `
  --token $env:GHPMV_TARGET_TOKEN `
  --target-base-url https://api.TENANT.ghe.com `
  --browser-base-url https://TENANT.ghe.com `
  --repo-mapping "$env:GHPMV_SNAPSHOT_DIR/repository-mappings.csv" `
  --user-mapping "$env:GHPMV_SNAPSHOT_DIR/user-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --no-update-check
```

異なる tenant の `--browser-base-url`、GitHub.com 用 profile、または API token と異なるアカウントの profile を指定した negative test では、UI 読み取り前に非ゼロ終了し、host/account mismatch がエラーに含まれることを確認します。

GHEC-DR は host / SSO / storage state の切り分けが失敗しやすいため、GitHub.com target の通常シナリオが green になってから実施してください。
