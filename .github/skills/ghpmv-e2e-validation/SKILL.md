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

## Step 1: 確認範囲を決める

次から一つ選んでもらう。

1. build + deterministic tests + CLI smoke test
2. 実 Project の read-only export
3. API-only export / import / verify
4. browser automation を含む end-to-end test

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

## Step 3: Browser 準備

browser mode の場合だけ実行する。

```powershell
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- setup --browsers
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile source
dotnet run --project src\Ghpmv.Cli -c Release --no-build -- login --profile target
```

GHEC の profile には対応する `--base-url` を付ける。ログインユーザーと、その profile で使用する API token の所有者が一致することを確認する。

## Step 4: Token を準備する

通常の `export` / `import` / `verify` は `README.md` の command 別 classic / fine-grained PAT permission 表に従う。

**既存 Project の export / import / verify だけを行うユーザーに、fixture 作成用の権限を要求してはならない。** `setup --fixture` を実行するデモ経路をユーザーが選んだ場合だけ、次の広い権限を案内する。

`setup --fixture` で organization repository も自動作成する場合は、source / target それぞれに classic PAT を使う。

- `repo`
- `project`
- `read:org`
- Organization が要求する場合は SSO authorization

```powershell
$env:SOURCE_TOKEN = Read-Host "Source PAT" -MaskInput
$env:TARGET_TOKEN = Read-Host "Target PAT" -MaskInput
```

Fine-grained PAT を使い続ける場合、空 repository を Web UI などで先に作成し、その repository の Contents / Issues / Pull requests と Organization Projects への必要な write 権限を付ける。Fine-grained PAT に Administration permission を追加するだけで organization repository の自動作成が成功すると案内してはならない。

## Step 5: Source fixture

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

browser mode では続けて UI fixture を適用する。

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

```powershell
$env:GHPMV_DEMO_SNAPSHOT = Join-Path $env:TEMP "ghpmv-demo-snapshot-$(Get-Date -Format yyyyMMdd-HHmmss)"
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

## Step 7: Target repository を準備する

次から一つ選んでもらう。

1. **GEI**: 本番同等。Issue / PR number が維持されたことを確認する。
2. **fixture seed**: `ghpmv` 自体の短時間デモ。GEI の検証にはならず、補助 Project が一つ増える。

fixture seed:

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

生成済み CSV を必ず読み、空の target 値を列挙する。固定の一行だけで置き換えない。

- `repository-mappings.csv`: full name と short name の候補を含め、全行を target `owner/repo` へ対応付ける。
- `organization-mappings.csv`: source owner を target owner へ対応付ける。
- `user-mappings.csv`: source login / mannequin user を実 target login へ対応付ける。

target login は token 値ではなくユーザー名だけを確認する。EMU suffix を省略しない。編集後に CSV を再読し、空の target 値がないことを確認する。

## Step 9: Import

存在する mapping file をすべて渡す。

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

Import と同じ mapping / browser profile を渡す。

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
| `Resource not accessible by personal access token` during `setup --fixture` | classic PAT (`repo`, `project`, `read:org`) を使うか、repository を先に作成する。 |
| `INSUFFICIENT_SCOPES`, `id`, `read:org` | classic PAT に `read:org` を追加し、必要なら SSO を再承認する。 |
| `The browser session is not signed in to 'github.com'` | 該当 profile で `login` を再実行し、API token と同じユーザーでログインする。 |
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
