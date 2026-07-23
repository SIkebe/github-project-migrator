---
name: ghpmv-e2e-validation
description: ghpmv の実環境動作確認を、ビルド、Playwright準備、source/target fixture、browser profile、export、mapping、import、verifyまで一問一答で安全に案内する。「動作確認したい」「ステップバイステップでガイド」「実環境で試したい」「fixtureを作って移行テスト」「browser automationを含めて検証」「E2E migration test」などの依頼で使用する。
---

# ghpmv E2E Validation

`ghpmv` の GitHub.com / GHEC 実環境テストを、一度に一段だけ案内する。最終目標は browser automation を含む `export` → `import` → `verify` を完了し、`Match`、または説明可能な `PartialMatch` を得ること。

詳細仕様と手動チェック項目は次を参照する。

- `README.md` の Token permissions と browser automation
- `docs/MANUAL_TEST_PLAN.md`
- `.github/copilot-instructions.md` の build / test command

## 最重要原則

1. **一度に一つのステップだけ案内する。** コマンドを提示したら結果を確認し、成功するまで次へ進まない。
2. **質問は一つずつ行う。** 選択肢を提示できる場合は対話用質問ツールを使う。
3. **token 値を会話へ貼らせない。** PowerShell の `Read-Host -MaskInput` などでローカル環境変数へ設定させる。
4. **実リソース作成前に作成物を明示する。** repository、Issue、PR、Project、Views、Workflows が作成されることを伝える。
5. **削除は明示的な同意なしに行わない。** cleanup は URL / name を再確認してから案内する。
6. **既存変更を壊さない。** branch、working tree、snapshot directory、mapping CSV を勝手に reset、削除、上書きしない。
7. **warning を成功扱いしない。** 対象 category と欠落情報を説明し、ユーザーが許容するか確認する。

## セッション状態

会話中は次を記録し、未確定値を推測しない。

| 値 | 例 |
|---|---|
| source organization / owner type | `gpm-source`, `organization` |
| target organization / owner type | `gpm-target`, `organization` |
| source / target browser profile | `source`, `target` |
| source fixture repository | `ghpmv-demo-20260722` |
| target repository | `ghpmv-demo-target-20260722` |
| source / target Project number | `33`, `1068` |
| snapshot directory | `$env:TEMP\ghpmv-demo-snapshot-...` |
| source / target token environment variable | `SOURCE_TOKEN`, `TARGET_TOKEN` |
| target user login | EMU suffixを含む実 login |
| repository preparation mode | `GEI` または `fixture-seed` |
| validation mode | `build-only`, `read-only`, `api-only`, `browser-e2e` |
| fixture preparation | `existing` または `create` |
| source / target token type | `classic` または `fine-grained` |

## Step 1: 確認範囲を決める

次から一つ選んでもらう。

1. build + deterministic tests + CLI smoke test
2. 実 Project の read-only export
3. API-only export / import / verify
4. browser automation を含む end-to-end test

選択結果を `validation mode` として記録し、次の経路以外へ進めない。

| validation mode | 実行する Step | 終了条件 |
|---|---|---|
| `build-only` | 2 | Step 2 完了後に終了する。token、browser、fixture、実環境操作を案内しない。 |
| `read-only` | 2, 4, 6 | source token だけを準備し、Step 6 の browser option なしの export 完了後に終了する。Step 3, 5, 7-10 は実行しない。 |
| `api-only` | 2, 4, 必要な場合だけ 5, 6-10 | browser profile を準備せず、browser option をすべて外して実行する。 |
| `browser-e2e` | 2-4, 必要な場合だけ 5, 6-10 | browser profile と source / target token を分けて実行する。 |

`api-only` または `browser-e2e` では、既存 source Project を使うか fixture を作るかを一問で確認し、`fixture preparation` として記録する。`existing` の場合は Step 5 を実行せず、fixture 作成用権限を要求しない。

同じ mode では、target repository を GEI で移行するか fixture seed で作るかも Step 4 より前に一問で確認し、`repository preparation mode` として記録する。token の用途が決まるまで PAT の入力を求めない。

browser automation を選んだ場合は、source / target が同じアカウント・同じ host か、別アカウントか、GHEC data residency かを一問ずつ確認する。別アカウントなら `source` / `target` profile と token を分ける。

## Step 2: ローカル baseline

リポジトリ root で .NET SDK と branch / working tree を確認する。既存変更は報告するだけで触らない。

```powershell
dotnet --version
git status --short --branch
```

続けて repository 指定の baseline を実行する。

```powershell
dotnet restore Ghpmv.slnx
dotnet build Ghpmv.slnx -c Release --no-restore -warnaserror
dotnet test tests\Ghpmv.Core.Tests\Ghpmv.Core.Tests.csproj -c Release --no-build
dotnet test tests\Ghpmv.Browser.Tests\Ghpmv.Browser.Tests.csproj -c Release --no-build --filter "Category!=E2E"
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- --version
```

失敗したら、その段階で停止して原因を解消する。実環境操作へ進まない。

`build-only` はここで完了報告を行い、終了する。

## Step 3: Browser 準備

`browser-e2e` の場合だけ実行する。他の mode ではこの Step をスキップし、browser profile を確認しない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup --browsers
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile source
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile target
```

GHEC の profile には対応する `--base-url` を付ける。ログインユーザーと、その profile で使用する API token の所有者が一致することを確認する。

## Step 4: Token を準備する

**PAT の入力を求める前に、現在の経路に必要な権限を classic / fine-grained の両方で提示する。** ユーザーに source / target の token type を一つずつ選んでもらい、必要な権限を準備できたことを確認してから `Read-Host` へ進む。

mode ごとに必要な token だけを準備する。

- `read-only`: source Project を export できる source token だけ
- `api-only`: source export 用 token と target import / verify 用 token
- `browser-e2e`: browser profile と同じユーザーの source / target token

`setup --fixture` で organization repository を自動作成する完全自動経路では、確実性を優先する場合は classic PAT を推奨する。fine-grained PAT を選んだ場合は、下記の permission 設定だけで成功とみなさず、fixture 実行前に repository を作成しない preflight を必ず行う。

### Fine-grained PAT 作成 URL

ユーザーが fine-grained PAT を選んだ場合は、permission を手作業で列挙させるだけでなく、GitHub の [pre-filled fine-grained PAT URL](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens#pre-filling-fine-grained-personal-access-token-details-using-url-parameters) を現在の経路に合わせて生成し、クリック可能な完全な URL として提示する。`target_name` には確認済みの organization login を設定し、`name`、`description`、`expires_in=30` と次の permission query parameter を付ける。

| token / 経路 | 必須 query parameter | 条件付き query parameter |
|---|---|---|
| source: 既存 Project の export | `organization_projects=read`, `metadata=read` | private repository item を読む場合は `issues=read`, `pull_requests=read` |
| source: `setup --fixture` + export | `administration=write`, `contents=write`, `issues=write`, `pull_requests=write`, `organization_projects=write`, `metadata=read` | なし |
| target: 既存または GEI 後 repository への import / verify | `organization_projects=write`, `metadata=read` | linked repository には `contents=write`、private repository item には `issues=read`, `pull_requests=read`、team collaborator には `members=read` |
| target: fixture seed + import / verify | `administration=write`, `contents=write`, `issues=write`, `pull_requests=write`, `organization_projects=write`, `metadata=read` | team collaborator には `members=read` |

すべての値を URL encode し、placeholder のまま提示しない。例:

```text
https://github.com/settings/personal-access-tokens/new?name=ghpmv-source-export&description=Export+an+organization+Project+with+ghpmv&target_name=octo-org&expires_in=30&organization_projects=read&metadata=read
```

作成 URL では **Repository access** を指定できない。URL を開いた後、現在の経路に応じて参照される全 repository または fixture 用の **All repositories** をユーザー自身に選んでもらい、permission と expiration を確認してから生成する。organization approval が必要なら **Active** になるまで待つ。classic PAT と GEI token にはこの URL を使わず、scope と SSO authorization を従来どおり案内する。

### Classic PAT

| token / 経路 | 必要な scope |
|---|---|
| source: 既存 Project の export | `read:project`。private repository の item / linked repository を読む場合は `repo` も追加。 |
| source: `setup --fixture` + export | `repo`, `project`, `read:org`。 |
| target: 既存または GEI 後 repository への import / verify | `project`, `read:org`。private target repository の item / linked repository を解決する場合は `repo` も追加。 |
| target: fixture seed + import / verify | `repo`, `project`, `read:org`。 |

Organization が要求する場合は classic PAT を SSO authorize する。

### Fine-grained PAT

fine-grained PAT は organization-owned Project にだけ使用する。GitHub は user-owned Project へのアクセスを current limitation としているため、`--owner-type user` では classic PAT を選ぶ。

| token / 経路 | Resource owner / repository access | 必要な permission |
|---|---|---|
| source: 既存 Project の export | source Project の owner。参照される全 repository を選択。 | Organization **Projects: Read-only**。Repository **Metadata: Read-only**。private repository item には **Issues: Read-only** と **Pull requests: Read-only**。 |
| source: `setup --fixture` + export | source organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**。 |
| target: 既存または GEI 後 repository への import / verify | target Project の owner。mapping / Workflow が参照する全 target repository を選択。 | Organization **Projects: Read and write**。Repository **Metadata: Read-only**、linked repository には **Contents: Read and write**、private repository item には **Issues: Read-only** と **Pull requests: Read-only**。team collaborator を import する場合は Organization **Members: Read-only**。 |
| target: fixture seed + import / verify | target organization。**All repositories**。 | Repository **Administration: Read and write**、**Contents: Read and write**、**Issues: Read and write**、**Pull requests: Read and write**。Organization **Projects: Read and write**。team collaborator を import する場合は Organization **Members: Read-only**。 |

Organization が fine-grained PAT approval を要求する場合は承認済みであることを確認する。**既存 Project の export / import / verify だけを行うユーザーに fixture 作成用 permission を要求してはならない。**

GitHub は Projects GraphQL mutation ごとの fine-grained PAT permission を公開していない。`linkProjectV2ToRepository` に対する Repository **Contents: Read and write** は実環境で確認した要件として案内し、PAT 向け公式要件として断定しない。

### GEI 専用 token

`repository preparation mode` が `GEI` の場合、GEI は fine-grained PAT を使用できないため、`SOURCE_TOKEN` / `TARGET_TOKEN` とは別に classic PAT を用意することを推奨する。

| GEI token | token owner の role | 必要な classic PAT scope |
|---|---|---|
| source | Organization owner または source organization の migrator | `admin:org`, `repo` |
| destination | Organization owner | `repo`, `admin:org`, `workflow` |
| destination | destination organization の migrator | `repo`, `read:org`, `workflow` |

同じ classic PAT を `ghpmv` と GEI で再利用する場合は、該当する scope の和集合が必要になる。不要な `admin:org` を `ghpmv` 専用 token に追加させない。

`read-only`:

```powershell
$env:SOURCE_TOKEN = Read-Host "Source PAT" -MaskInput
```

`api-only` と `browser-e2e`:

```powershell
$env:SOURCE_TOKEN = Read-Host "Source PAT" -MaskInput
$env:TARGET_TOKEN = Read-Host "Target PAT" -MaskInput
```

`GEI`:

```powershell
$env:GEI_SOURCE_TOKEN = Read-Host "GEI source classic PAT" -MaskInput
$env:GEI_TARGET_TOKEN = Read-Host "GEI target classic PAT" -MaskInput
```

### Fine-grained fixture token の preflight

`fixture preparation` が `create` で source に fine-grained PAT を選んだ場合、`setup --fixture` より先に次を実行する。target の `fixture-seed` でも organization と token を置き換えて同じ確認を行う。

```powershell
$previousGhToken = $env:GH_TOKEN
$env:GH_TOKEN = $env:SOURCE_TOKEN
try {
    gh api --include --method POST "orgs/<source-org>/repos"
}
finally {
    $env:GH_TOKEN = $previousGhToken
}
```

`name` を渡さないため repository は作成されない。`422 Validation Failed` なら endpoint permission は認識されているため続行できる。`403 Resource not accessible by personal access token` なら、設定画面で **Administration: Read and write**、**All repositories**、organization approval を再確認する。token owner の organization role、member の repository creation policy、organization の PAT restriction も別に確認する。原因を一つに断定しない。再作成しても 403 の場合は `setup --fixture` を実行せず、次のどちらかを選んでもらう。

1. classic PAT (`repo`, `project`, `read:org`) に切り替える
2. 空の private repository を先に作り、fine-grained PAT で残りの fixture を作成する

fine-grained PAT の **Administration** または **All repositories** を付与できない場合だけ、空 repository を先に作成し、その repository を選択した token に Administration 以外の fixture 権限を付ける。

## Step 5: Source fixture

`api-only` または `browser-e2e` で `fixture preparation` が `create` の場合だけ実行する。`read-only` と `existing` の経路ではスキップし、source resource を作成しない。

source organization、衝突しない fixture title / repository name を一つずつ確認する。作成物を説明してから実行する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture `
  --fixture-org <source-org> `
  --fixture-title <unique-title> `
  --fixture-repo <unique-repo> `
  --token $env:SOURCE_TOKEN
```

出力された source Project number を記録する。

`browser-e2e` では続けて UI fixture を適用する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture-ui `
  --fixture-org <source-org> `
  --fixture-project <source-project-number> `
  --fixture-repo <source-repo> `
  --browser-profile source `
  --token $env:SOURCE_TOKEN
```

### Fixture UI 再実行

同じ Project に明示的に再実行すると non-default Views が重複する。次のどちらかを選んでもらう。

1. 新しい fixture Project を作る（推奨）
2. `View 1` を残し、既存の `Fixture Board` / `Fixture Roadmap` を手動削除して再実行する

Workflow は再設定できる。warning が出た場合は、目視だけで終了せず、後続 export が UI settings を警告なしで取得できるか確認する。

## Step 6: Source export

再実行時は新しい directory を使う。mapping CSV は既存ファイルを上書きしない。

`read-only` と `api-only` では browser option を付けない。

```powershell
$env:GHPMV_DEMO_SNAPSHOT = Join-Path $env:TEMP "ghpmv-demo-snapshot-$(Get-Date -Format yyyyMMdd-HHmmss)"
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
  --org <source-org> `
  --project <source-project-number> `
  --out $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:SOURCE_TOKEN
```

`browser-e2e` では同じ export に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- export `
  --org <source-org> `
  --project <source-project-number> `
  --out $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:SOURCE_TOKEN `
  --enable-browser-automation `
  --browser-profile source
```

確認するもの:

- `snapshot.json`
- `repository-mappings.csv`
- `organization-mappings.csv`
- 必要な場合 `user-mappings.csv`
- View / Workflow / collaborator warning

warning がある場合、どの UI-only field が欠落したかを示して続行可否を確認する。

`read-only` はここで完了報告を行い、終了する。target resource の準備、mapping の編集、import、verify は案内しない。

## Step 7: Target repository を準備する

`api-only` と `browser-e2e` だけが実行する。

Step 1 で記録した `repository preparation mode` の経路だけを実行する。

### GEI

`docs/MANUAL_TEST_PLAN.md` の §6 に従い、`GEI_SOURCE_TOKEN` / `GEI_TARGET_TOKEN` で repository migration を完了する。destination の ruleset がある場合、**Repository migrations** bypass を **Exempt** にする。既定の **Always allow** のまま進めない。

target repository full name を記録し、target の Issue / PR number が source と一致することを確認する。downloadable migration log は完了後 24 時間以内に保存する。target repository の Issues が無効なら `Migration Log` Issue は作成されない。migration 成功と number 維持を確認できるまで Step 8 へ進まない。

### Fixture seed

`ghpmv` 自体の短時間デモ用であり、GEI の検証にはならず、補助 Project が一つ増えることを説明してから実行する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup `
  --fixture `
  --fixture-org <target-org> `
  --fixture-title <unique-target-seed-title> `
  --fixture-repo <target-repo> `
  --token $env:TARGET_TOKEN
```

target 側の `setup --fixture-ui` は不要。

## Step 8: Mapping を完成させる

`api-only` と `browser-e2e` だけが実行する。

生成済み CSV を必ず読み、空の target 値を列挙する。固定の一行だけで置き換えない。

- `repository-mappings.csv`: full name と short name の候補を含め、全行を target `owner/repo` へ対応付ける。
- `organization-mappings.csv`: source owner を target owner へ対応付ける。
- `user-mappings.csv`: source login / mannequin user を実 target login へ対応付ける。

target login は token 値ではなくユーザー名だけを確認する。EMU suffix を省略しない。編集後に CSV を再読し、空の target 値がないことを確認する。

## Step 9: Import

`api-only` と `browser-e2e` だけが実行する。

存在する mapping file をすべて渡す。

`api-only` では browser option を付けない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
  --org <target-org> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv"
```

`browser-e2e` では同じ import に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- import `
  --org <target-org> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target
```

生成されなかった optional mapping file の引数だけを外す。出力の `result` と target Project number を記録する。

## Step 10: Verify

`api-only` と `browser-e2e` だけが実行する。

Import と同じ mapping / browser profile を渡す。

`api-only` では browser option を付けない。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
  --org <target-org> `
  --project <target-project-number> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
```

`browser-e2e` では同じ verify に browser option を追加する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- verify `
  --org <target-org> `
  --project <target-project-number> `
  --in $env:GHPMV_DEMO_SNAPSHOT `
  --token $env:TARGET_TOKEN `
  --repo-mapping "$env:GHPMV_DEMO_SNAPSHOT\repository-mappings.csv" `
  --user-mapping "$env:GHPMV_DEMO_SNAPSHOT\user-mappings.csv" `
  --org-mapping "$env:GHPMV_DEMO_SNAPSHOT\organization-mappings.csv" `
  --enable-browser-automation `
  --browser-profile target `
  --report-json "$env:GHPMV_DEMO_SNAPSHOT\verify-report.json"
```

結果を category ごとに確認する。

- `Match`: 成功
- `PartialMatch`: warning の内容と許容理由を記録
- `Mismatch`: 差分を直して再検証
- `NotVerified`: 必要データが capture できていないため成功扱いにしない

## Troubleshooting

| エラー / 症状 | 対応 |
|---|---|
| fine-grained PAT preflight / `setup --fixture` で `Resource not accessible by personal access token` | **Administration: Read and write**、**All repositories**、organization approval に加え、token owner の role、repository creation / PAT policy、SSO を確認する。原因を permission 一つに断定しない。解決できなければ repository を先に作成するか classic PAT (`repo`, `project`, `read:org`) へ切り替える。 |
| `INSUFFICIENT_SCOPES`, `id`, `read:org` | classic PAT に `read:org` を追加し、必要なら SSO を再承認する。 |
| `The browser session is not signed in to 'github.com'` | 該当 profile で `login` を再実行し、API token と同じユーザーでログインする。 |
| `Viewer not authorized to change project visibility` | target Project の現在値と snapshot の visibility を確認する。差分がある場合は、organization owner または visibility 変更を許可された organization role の token owner を使う。値が同じなのに発生した場合は、no-op visibility mutation を省略する版の `ghpmv` で再実行する。 |
| `linkProjectV2ToRepository` で `Resource not accessible by personal access token` | 実環境で確認した対処として、target fine-grained PAT で対象 repository を選択し、Repository **Contents: Read and write** を追加する。permission 変更後に organization approval が **Active** であることも確認する。GitHub はこの mutation の PAT permission を個別には文書化していない。 |
| Collaborator が `NotVerified`、`Manage access` 待機が timeout、または `/settings/access` が 404 | target browser/token user が Project の **Settings → Manage access** を開けるか確認する。開けない member profile ではなく、同じ login の organization-owner または十分な project-admin token / browser profile で verify を再実行する。 |
| UI fixture 再実行で View が重複 | 新規 fixture を使うか、`View 1` 以外の fixture Views を削除してから再実行する。 |
| mapping 不足 | 生成された全 CSV を再読し、空 target と short-name 候補を補完する。 |
| Issue / PR item skip | target repository の可視性、mapping、Issue / PR number 維持を確認する。 |
| Workflow warning | Auto-add 上限、repository visibility、filter mapping、現在の UI selector を確認し、browser export で capture 可否を再確認する。 |

## 完了報告

最後に次だけを簡潔に報告する。

- build / deterministic test 結果
- source / target Project URL または番号
- export / import result
- verify overall / category result
- 許容した warning
- 作成した一時リソースと snapshot directory

cleanup はユーザーが明示的に希望した場合だけ、`docs/MANUAL_TEST_PLAN.md` の手順で行う。PR、commit、push は別途依頼されるまで行わない。
