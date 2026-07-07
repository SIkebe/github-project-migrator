# 手動テストプラン: GEI + gpm end-to-end 移行検証

このドキュメントは、GitHub Enterprise Importer(GEI) でリポジトリ / Issue / Pull Request を先に移行し、その後 `gpm` で GitHub Projects V2 を移行・検証するための手動テスト手順です。

想定する主シナリオは次の通りです。

- 移行元: GitHub.com の source organization / account
- 移行先: GitHub.com または GHEC / EMU の target organization / account
- リポジトリ移行: GEI
- Project V2 移行: `gpm export` → `gpm import` → `gpm verify`
- Views / Workflows / collaborator export: `--enable-browser-automation` を使う

> 注意: トークン値、organization 名、repository 名、project number は環境ごとに置き換えてください。トークンはチャットやログに貼らないでください。

---

## 1. 目的と合格基準

### 1.1 目的

1. GEI で source repository を target organization へ移行できることを確認する。
2. GEI 移行後の target repository に対して、`gpm` が source Project の Issue / PR item を repository mapping + 同一番号で再リンクできることを確認する。
3. `gpm` が Project metadata / fields / items / values / order / archived state / linked repositories / explicit collaborators / Views / Workflows を移行できることを確認する。
4. `gpm verify` で移行結果を検証し、既知の恒久制限以外に error が出ないことを確認する。
5. セッション失効、mapping 不足、browser automation 無効時など、手動運用で起きやすい失敗が分かりやすく検出されることを確認する。

### 1.2 合格基準

この手動テストは、以下を満たしたら合格です。

- GEI の repository migration が `Succeeded` になる。
- target repository に Issue #1 / Issue #2 / open PR #3 相当が存在し、source と同じ number で参照できる。
- `gpm export --enable-browser-automation` が snapshot を作成し、Views / Workflows / explicit collaborators の warning が想定範囲内である。
- `repository-mappings.csv` と必要に応じて `user-mappings.csv` を補完したうえで、`gpm import --enable-browser-automation` が完了する。
- `gpm verify` が `OK: the target project matches the snapshot.`、または既知制限に由来する warning のみを出す。
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
| Repository / Issues / Pull Requests | GEI + 目視 / `gh` | `gpm` のスコープ外。Project item relink の前提条件。 |
| Project metadata | `gpm verify` + 目視 | title / shortDescription / README / public / closed state。 |
| Fields | `gpm verify` + 目視 | Text / Number / Date / Single-select / Iteration / Status options。 |
| Items | `gpm verify` + 目視 | Draft / Issue / PR / archived / assigned draft。 |
| Field values | `gpm verify` + 目視 | Unicode、emoji、number、date、option、iteration。 |
| Item order | `gpm verify` + 目視 | archived item の position は GitHub API 制限により対象外。 |
| Linked repositories | `gpm verify` warning 確認 + 目視 | `--repo-mapping` が必須。 |
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
| Source browser profile | `source` | source Project と source repo を読めるアカウントで `gpm login`。 |
| Target browser profile | `target` | target Project を編集できるアカウントで `gpm login`。 |

EMU / SAML / OIDC backed organization の場合は、PAT と browser session の両方で SSO authorization を完了してください。

### 3.2 Fixture repository / Project

推奨 fixture:

- Source repository: `fixture-repo`
- Target repository: `fixture-repo-gei-target` または衝突しない任意名
- Source Project: `gpm-fixture`

`gpm setup --fixture` は source org に以下を作成します。

- private repository `fixture-repo`
- Issue 2 件
- open Pull Request 1 件
- Project `gpm-fixture`
- custom fields(Text / Number / Date / Single-select / Iteration)
- draft items、Issue item、PR item、archived draft、assigned draft
- linked repository

Views / Workflows は public API だけでは作成できませんが、`gpm setup --fixture-ui` が C# の Playwright layer で標準テスト用 View / Workflow を作成します。手動で UI をぽちぽち濃くする必要はありません。fixture setup の実装本体は C# CLI に一本化されています。

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
| `GPM_SOURCE_TOKEN` | `gpm export` と GEI source 用。 |
| `GPM_TARGET_TOKEN` | `gpm import` / `gpm verify` と GEI target 用。 |
| `GPM_TEST_TOKEN` | 既存 integration / browser E2E tests を手動で回す場合。 |
| `GPM_SOURCE_ORG` | source organization login。 |
| `GPM_TARGET_ORG` | target organization login。 |
| `GPM_FIXTURE_REPO` | source fixture repository 名。 |
| `GPM_TARGET_REPO` | GEI で作る target repository 名。 |
| `GPM_SNAPSHOT_DIR` | `gpm export` の出力先。 |

PowerShell で `.env` を読み込む例:

```powershell
Get-Content .env | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
}
```

### 4.3 Token / SSO チェック

`gh auth login` / `gh auth refresh` を使う場合、Project 操作に必要な scope を含めます。

```powershell
gh auth status
gh auth refresh -s project,read:enterprise,repo
```

SAML SSO organization では、GitHub CLI の OAuth application または PAT の organization access を明示的に Authorize してください。認可漏れがあると `saml_failure` や GraphQL の permission error になります。

### 4.4 `gpm` のビルドとブラウザー準備

```powershell
dotnet restore
dotnet build -c Release
dotnet run --project src/Gpm.Cli -- --version
dotnet run --project src/Gpm.Cli -- setup --browsers
```

Source / target の browser profile を作成します。

```powershell
dotnet run --project src/Gpm.Cli -- login --profile source
dotnet run --project src/Gpm.Cli -- login --profile target
```

GHEC with data residency target の場合は、target profile に tenant host を指定します。

```powershell
dotnet run --project src/Gpm.Cli -- login --profile target --base-url https://TENANT.ghe.com
```

---

## 5. Source fixture の作成

### 5.1 API で作れる部分を C# で作成

```powershell
dotnet run --project src/Gpm.Cli -- setup `
  --fixture `
  --fixture-org $env:GPM_SOURCE_ORG `
  --fixture-title gpm-fixture `
  --fixture-repo $env:GPM_FIXTURE_REPO `
  --token $env:GPM_SOURCE_TOKEN
```

出力された Project URL と project number を控えます。

```text
Source project URL: https://github.com/orgs/<source-org>/projects/<source-project-number>
Source project number: <source-project-number>
```

### 5.2 UI-only fixture を C# / Playwright で作成する

`gpm setup --fixture` は public API で作れる repository / fields / items までを作ります。Views / Workflows は public API が無いため、続けて `gpm setup --fixture-ui` を実行し、C# の `ViewUiImporter` / `WorkflowUiImporter` が Playwright で GitHub UI を操作して作成します。

```powershell
dotnet run --project src/Gpm.Cli -- setup `
  --fixture-ui `
  --fixture-org $env:GPM_SOURCE_ORG `
  --fixture-project <source-project-number> `
  --fixture-repo $env:GPM_FIXTURE_REPO `
  --browser-profile source
```

API-backed fixture 作成と UI-only fixture 作成を 1 回で実行する場合は、`--fixture` と `--fixture-ui` を併用できます。この場合、`--fixture-project` は不要です。

```powershell
dotnet run --project src/Gpm.Cli -- setup `
  --fixture `
  --fixture-ui `
  --fixture-org $env:GPM_SOURCE_ORG `
  --fixture-title gpm-fixture `
  --fixture-repo $env:GPM_FIXTURE_REPO `
  --token $env:GPM_SOURCE_TOKEN `
  --browser-profile source
```

既に同名 Project が存在する場合、`--fixture --fixture-ui` の組み合わせでは Views / Workflows の重複作成を避けるため UI 適用は自動で skip されます。既存 Project に UI-only fixture を強制的に適用する場合だけ、`--fixture` を外して `--fixture-ui --fixture-project <source-project-number>` を明示してください。

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

- `dotnet run --project src/Gpm.Cli -- setup --browsers` が完了している。
- `dotnet run --project src/Gpm.Cli -- login --profile source` で source org を編集できる browser session が保存されている。
- 対象 Project は `gpm setup --fixture` で作成済みで、`Fixture Text` / `Fixture Number` / `Fixture Date` / `Fixture Select` / `Fixture Sprint` fields と `$env:GPM_FIXTURE_REPO` repository が存在する。

`gpm setup --fixture-ui` が GitHub UI 変更などで失敗した場合のみ、フォールバックとして Source Project を開き、以下を手動で設定します。

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
- inherited access は `gpm` の対象外なので、目視確認では explicit collaborator のみを判定する。

---

## 6. GEI による repository migration

### 6.1 事前確認

source repository に Issue / PR が存在することを確認します。

```powershell
gh repo view "$env:GPM_SOURCE_ORG/$env:GPM_FIXTURE_REPO"
gh issue list --repo "$env:GPM_SOURCE_ORG/$env:GPM_FIXTURE_REPO" --state all
gh pr list --repo "$env:GPM_SOURCE_ORG/$env:GPM_FIXTURE_REPO" --state all
```

target repository 名が未使用であることを確認します。

```powershell
gh repo view "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO"
```

存在する場合は、別名にするか cleanup してから進めます。

### 6.2 GEI migration を queue / 実行

`gh gei migrate-repo --help` で現在の extension の引数を確認してから実行してください。代表例:

```powershell
gh gei migrate-repo `
  --github-source-org $env:GPM_SOURCE_ORG `
  --source-repo $env:GPM_FIXTURE_REPO `
  --github-target-org $env:GPM_TARGET_ORG `
  --target-repo $env:GPM_TARGET_REPO `
  --github-source-pat $env:GPM_SOURCE_TOKEN `
  --github-target-pat $env:GPM_TARGET_TOKEN `
  --target-repo-visibility private
```

実行環境や GEI extension version によっては `--queue-only` / `--wait` / migration log download 系のオプションを併用してください。

### 6.3 GEI 結果確認

target repository が作成され、Issue / PR number が維持されていることを確認します。

```powershell
gh repo view "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO"
gh issue view 1 --repo "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO"
gh issue view 2 --repo "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO"
gh pr view 3 --repo "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO"
```

PR number が異なる場合、`gpm` v1 は source issue / PR number と target issue / PR number の個別 mapping を持たないため、その item の relink はできません。この場合はテスト結果を失敗ではなく「前提条件未達」として記録します。

---

## 7. `gpm` export / mapping / import / verify

### 7.1 Source Project を export

```powershell
dotnet run --project src/Gpm.Cli -- export `
  --org $env:GPM_SOURCE_ORG `
  --project <source-project-number> `
  --out $env:GPM_SNAPSHOT_DIR `
  --token $env:GPM_SOURCE_TOKEN `
  --enable-browser-automation `
  --browser-profile source `
  --no-update-check
```

確認ポイント:

- `$env:GPM_SNAPSHOT_DIR/snapshot.json` が作成される。
- `repository-mappings.csv` が生成される。
- source UI の Views / Workflows / collaborators に関する warning がない、または想定内である。

### 7.2 Repository mapping を補完

`repository-mappings.csv` の target column を GEI 移行後 repository に合わせます。

```csv
source,target
gpm-source/fixture-repo,gpm-target/fixture-repo-gei-target
```

PowerShell で簡易生成する場合:

```powershell
@"
source,target
$env:GPM_SOURCE_ORG/$env:GPM_FIXTURE_REPO,$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO
"@ | Set-Content -Encoding UTF8 "$env:GPM_SNAPSHOT_DIR/repository-mappings.csv"
```

`user-mappings.csv` が生成されている場合は、EMU target login に合わせて `target-user` を補完します。

### 7.3 Target Project へ import

新規 Project として import します。

```powershell
dotnet run --project src/Gpm.Cli -- import `
  --org $env:GPM_TARGET_ORG `
  --in $env:GPM_SNAPSHOT_DIR `
  --token $env:GPM_TARGET_TOKEN `
  --repo-mapping "$env:GPM_SNAPSHOT_DIR/repository-mappings.csv" `
  --user-mapping "$env:GPM_SNAPSHOT_DIR/user-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --project-title "gpm-fixture migrated $(Get-Date -Format yyyyMMdd-HHmmss)" `
  --no-update-check
```

`user-mappings.csv` が存在しない場合は `--user-mapping` を外してください。

出力された target Project URL と project number を控えます。

```text
Target project URL: https://github.com/orgs/<target-org>/projects/<target-project-number>
Target project number: <target-project-number>
```

### 7.4 Verify

```powershell
dotnet run --project src/Gpm.Cli -- verify `
  --org $env:GPM_TARGET_ORG `
  --project <target-project-number> `
  --in $env:GPM_SNAPSHOT_DIR `
  --token $env:GPM_TARGET_TOKEN `
  --no-update-check
```

期待値:

```text
OK: the target project matches the snapshot.
```

warning / error が出た場合は、次の観点で切り分けます。

| 症状 | よくある原因 | 対応 |
|---|---|---|
| Issue / PR item が skip | `repository-mappings.csv` 不足、target repo 不可視、Issue / PR number 不一致 | mapping、token visibility、GEI 結果を確認。 |
| `saml_failure` | PAT の organization SSO authorization 漏れ | GitHub settings で token / GitHub CLI を Authorize。 |
| Browser session expired | `gpm login --profile ...` 未実施または期限切れ | source / target profile で再ログイン。 |
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
| N-6 | `verify` 前に target field value を手動変更 | `gpm verify` が差分を error として検出する。 |

---

## 10. クリーンアップ

手動テストごとに作った target Project / target repository は、結果記録後に削除します。

```powershell
gh repo delete "$env:GPM_TARGET_ORG/$env:GPM_TARGET_REPO" --yes
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

gpm commit/version:
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

1. target browser profile を tenant host で作成する。
2. `gpm import` / `gpm verify` に `--target-base-url https://api.TENANT.ghe.com` を指定する。
3. target repository mapping は tenant 内 repository にする。GHEC-DR tenant 外の repository link は不可。
4. GEI 側の target organization / enterprise 設定が DR tenant に向いていることを GEI の公式手順で確認する。

例:

```powershell
dotnet run --project src/Gpm.Cli -- import `
  --org $env:GPM_TARGET_ORG `
  --in $env:GPM_SNAPSHOT_DIR `
  --token $env:GPM_TARGET_TOKEN `
  --target-base-url https://api.TENANT.ghe.com `
  --repo-mapping "$env:GPM_SNAPSHOT_DIR/repository-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --no-update-check
```

GHEC-DR は host / SSO / storage state の切り分けが失敗しやすいため、GitHub.com target の通常シナリオが green になってから実施してください。
