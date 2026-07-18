# Browser Automation 詳細設計: Views / Workflows の解析と再現

M6/M7(Playwright による View・Workflow 移行)の詳細設計と実装記録。
M6/M7 は実装済みで、現行コードは `src/Ghpmv.Core/Browser/`、実 UI の確定事項は [projects-ui-discovery.md](ui-maps/projects-ui-discovery.md) を正とする。全体プランは [PLAN.md](../PLAN.md) を参照。

- 根拠にした一次情報:
  - GitHub Docs「[Managing your views](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/managing-your-views)」「[Changing the layout of a view](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/changing-the-layout-of-a-view)」
  - GitHub Docs「[Using the built-in automations](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-built-in-automations)」「[Adding items automatically](https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/adding-items-automatically)」
  - GraphQL スキーマ(`ProjectV2View` / `ProjectV2Workflow`)— PLAN.md §1.2 で検証済み

---

## 0. 全体像: どこまで API で読めて、何を UI でやるのか

**大原則: 読めるものは GraphQL で読む。UI 操作は「API に無い読み取り」と「すべての書き込み」だけ。**

### View のプロパティ別ソースマップ

| プロパティ | export(読み) | import(書き) | 備考 |
|---|---|---|---|
| name / layout / number | GraphQL `ProjectV2View.name/layout/number` | **UI** | layout enum: `TABLE_LAYOUT` / `BOARD_LAYOUT` / `ROADMAP_LAYOUT` |
| filter 文字列 | GraphQL `ProjectV2View.filter` | **UI**(フィルターバーに入力) | |
| 表示フィールドと列順 | GraphQL `ProjectV2View.fields`(orderBy: POSITION) | **UI** | |
| group-by(Table)/ swimlane(Board) | GraphQL `groupByFields` | **UI** | |
| Board の列フィールド | GraphQL `verticalGroupByFields` | **UI**("Column by") | |
| sort(複数キー+方向) | GraphQL `sortByFields`(`ProjectV2SortByField.direction`) | **UI** | |
| **Slice by** | ❌ API に無い → **UI で読む** | **UI** | |
| **Field sum** | ❌ API に無い → **UI で読む** | **UI** | |
| **Roadmap 設定(Dates / Zoom / Markers)** | ❌ API に無い → **UI で読む** | **UI** | |
| タブの並び順 | GraphQL `views`(orderBy: POSITION) | **UI**(タブの drag & drop)| v1 では省略可(§8) |

### Workflow のプロパティ別ソースマップ

| プロパティ | export(読み) | import(書き) |
|---|---|---|
| name / number / enabled | GraphQL `ProjectV2Workflow` | **UI**(enable は保存操作に内包) |
| トリガー条件・対象(issue/PR)・Set する Status 値・フィルター・対象リポジトリ | ❌ API に無い → **UI で読む** | **UI** |

つまり **export 側にも Playwright が必要**(Slice by / Field sum / Roadmap 設定 / Workflow 詳細)。export の UI 解析と import の UI 操作は同じページを扱うため、セレクター資産を共有する。

---

## 1. 前提条件と共通基盤

### 1.1 URL 構造(`{base}` = `https://github.com`。移行先が GHEC with data residency の場合は `https://{tenant}.ghe.com`。GHES は非サポート)

```
組織プロジェクト   {base}/orgs/{org}/projects/{number}
ユーザープロジェクト {base}/users/{user}/projects/{number}
特定 View        {base}/orgs/{org}/projects/{number}/views/{viewNumber}
Workflows 一覧    {base}/orgs/{org}/projects/{number}/workflows
特定 Workflow     {base}/orgs/{org}/projects/{number}/workflows/{workflowNumber}
設定             {base}/orgs/{org}/projects/{number}/settings
```

- `viewNumber` / `workflowNumber` は GraphQL の `ProjectV2View.number` / `ProjectV2Workflow.number` と一致する。**UI 内でタブを探し回らず、GraphQL で number を取得して URL 直接遷移すること**(タブ数が多い場合 UI 上は省略されるため)。

### 1.2 認証

- `ghpmv login [--profile <name>] [--base-url <url>]`: headful Chromium を開き、ユーザーが手動ログイン(2FA/SSO/passkey 込み)。ログイン完了を検知すると `IBrowserContext.StorageStateAsync()` で状態を保存する。
- 既定の保存先はプラットフォームの ApplicationData 配下にある `ghpmv/browser-state.json`、名前付きプロファイルでは `ghpmv/browser-state.<profile>.json`。任意の場所を使う場合は `--state-path`、既定プロファイルを環境変数で指定する場合は `GHPMV_BROWSER_STATE` を使う。
- 以降の browser-assisted export/import/verify は `--enable-browser-automation --browser-profile <name>` で保存済みプロファイルを選ぶ。セッションが失効した場合は同じ `ghpmv login --profile <name>` を再実行する。
- GHEC with data residency などホストやアカウントが異なる移行では、`ghpmv login --profile source` と `ghpmv login --profile target --base-url https://{tenant}.ghe.com` でセッションを分け、各コマンドの `--browser-profile` で使い分ける。

### 1.3 ロケール

GitHub Web UI は英語のみのため、アクセシブルネームによるセレクターは環境非依存で安定。`Accept-Language` の考慮は不要。

### 1.4 Playwright 共通設定

```csharp
// BrowserSession.cs(共通基盤)
var browser = await playwright.Chromium.LaunchAsync(new()
{
    Headless = !options.Headful,          // --headful で目視デバッグ可能に
    SlowMo = options.SlowMoMs,            // 既定 0、デバッグ時 300
});
var context = await browser.NewContextAsync(new()
{
    StorageStatePath = storageStatePath,
    ViewportSize = new() { Width = 1600, Height = 1000 },  // 狭いと列メニューが折りたたまれる
});
context.SetDefaultTimeout(30_000);
```

- 操作間ウェイト: 連続 UI 操作の間に 300ms(`Task.Delay`)。並列ページは使わない(1 セッション 1 ページ直列)。ToS 配慮とレース回避を兼ねる。
- **失敗時処理**: 回復可能な UI 操作失敗は warning に追加して続行する。UI write は transaction ではないため、途中まで適用された view / workflow が残る場合があり、移行後の browser-assisted `verify` が必須。診断ダンプとページ全体の reload retry は未実装。SPA の race には view 作成確認(最大 3 回)、rename textbox 待機(5 秒 × 3 回)、repository option 待機(10 秒)など対象要素単位の待機・再試行を使う。

### 1.5 セレクターレジストリ

複数フローで再利用するセレクターは `Sel.cs` に集約する。dialog 内の確定ボタンなど、その操作だけで使う one-off selector は実装箇所に残している。D0 Discovery は 2026-07-05 に完了し、role/name を中心とする実測済みセレクターと UI quirk を記録している。

以下は構成を示す簡略化した擬似コードであり、コンパイル可能なコピーではない。実装は `Sel.cs` を正とする。

```text
internal static class Sel
{
    public static ILocator ViewMenuButton(IPage page)
        => page.GetByRole(AriaRole.Button, new() { NameRegex = new("^(Unsaved changes )?View$") }).First;
    public static ILocator NewViewTab(IPage page)
        => page.GetByRole(AriaRole.Tab, new() { Name = "New view" });
    public static ILocator ViewTab(IPage page, string name)
        => page.GetByRole(AriaRole.Tab, new() { NameRegex = new($"^{Regex.Escape(name)}") });
    public static ILocator ViewLayoutButton(IPage page, string layoutName)
        => page.GetByRole(AriaRole.List, new() { Name = "Layout" })
            .GetByRole(AriaRole.Button, new() { Name = layoutName, Exact = true });
    public static ILocator WorkflowLink(IPage page, string name) => /* sidebar link by name */;
    public static ILocator EditWorkflowButton(IPage page) => /* exact "Edit" button */;
    public static ILocator SaveWorkflowButton(IPage page) => /* "Save workflow" or "Save and turn on workflow" */;
}
```

---

## 2. D0: Discovery フェーズ(2026-07-05 完了)

セレクターの最終確定は実 UI でしかできないため、次を実施した。

1. フィクスチャープロジェクトを `GHPMV_TEST_ORG` に作成
2. Playwright codegen と実 UI 操作で次の操作を確認:
   - New view → レイアウト切替 → Fields 変更 → Group by → Sort by → Slice by → Field sum → filter 入力 → Save changes → Rename → Delete
   - Workflows ページ → 各 workflow を開く → Edit → 設定変更 → Save and turn on workflow → Disable
3. accessibility tree と UI quirk を `docs/ui-maps/projects-ui-discovery.md` に記録
4. `Sel.cs` の全エントリを実測値で確定
5. Workflow の設定値を閲覧モードの DOM から読み取れることを確認

**D0 の成果物**: `docs/ui-maps/projects-ui-discovery.md` と `src/Ghpmv.Core/Browser/Sel.cs`。

---

## 3. 解析すべきパターン: View 編

### 3.1 View インベントリ(この組み合わせを全て扱う)

| # | パターン | 設定項目 |
|---|---|---|
| V-1 | Table 基本 | 表示フィールド選択と列順 |
| V-2 | Table + group-by | 任意フィールド 1 つ(Status/Single-select/Iteration など) |
| V-3 | Table + sort | export は複数キーを保持。v1 browser import が適用するのは先頭キーのみ |
| V-4 | Table + Field sum | グループ見出しに合計表示する Number フィールド群 |
| V-5 | Board | Column by(Status / 任意 single-select / iteration) |
| V-6 | Board + swimlane | Group by(横帯)との組み合わせ |
| V-7 | Roadmap | Dates(date フィールド対 or iteration)、Zoom(Month/Quarter/Year)、Markers |
| V-8 | 全レイアウト共通 | filter 文字列(そのまま転記。フィールド名は移行済み前提で互換) |
| V-9 | 全レイアウト共通 | Slice by |
| V-10 | View の name / タブ並び順 | 並び順は v1 スコープ外(§8) |

### 3.2 export: UI からの読み取り手順(API に無い 3 項目のみ)

対象: Slice by / Field sum / Roadmap 設定。GraphQL export の後、view ごとに 1 回だけページを開いて補完する。

```
手順(view ごと):
1. {project}/views/{viewNumber} へ goto、NetworkIdle 待ち
2. `Sel.ViewMenuButton(page).ClickAsync()` → 開いた menu の accessible name / checked state を取得
3. メニュー項目のラベルから現在値を読む:
   - "Slice by: <field>" → `ViewUiSnapshot.SliceBy`
   - "Field sum: <fields>" → `ViewUiSnapshot.FieldSum`
   - Roadmap のみ: "Dates: <...>", "Zoom level: <Month|Quarter|Year>", "Markers: <...>"
4. Esc でメニューを閉じる
```

実装メモ: メニュー項目は「設定名 + 現在値」を accessible name に含むため、label prefix で特定する。複数選択項目は overlay の `aria-checked` を読む。

### 3.3 import: View 作成の操作シーケンス

ViewSpec 1 件あたりの手順。**各ステップの後に 300ms ウェイト**。

```
CreateView(spec):
 1. プロジェクトルートへ goto
 2. Sel.NewViewTab.Click()                          → 新タブ "View {n}" が active になるのを待つ
 3. Rename: 選択中タブを double-click → "Change view name" textbox → spec.Name → Enter
 4. Layout: View menu → "Layout" セクションで spec.Layout("Table"|"Board"|"Roadmap")をクリック
 5. Fields: ViewOptions → "Fields" → ダイアログで:
      - 現在の表示フィールド集合と spec.VisibleFields を突き合わせて checked state を一致させる
      - 列順は明示的に並べ替えない(v1 best effort)
 6. Column by(Board のみ): ViewOptions → "Column by" → spec.VerticalGroupBy を選択
 7. Group by: ViewOptions → "Group by" → spec.GroupBy を選択(未指定なら "None" を選択)
 8. Sort by: View menu → "Sort by" → 先頭キーを選択 → 必要なら方向トグル
 9. Slice by: ViewOptions → "Slice by" → spec.SliceBy(未指定なら None)
10. Field sum: ViewOptions → "Field sum" → spec.FieldSum の各フィールドをチェック
11. Roadmap のみ: "Dates" → 開始/終了フィールド対 or iteration を選択、"Zoom level"、"Markers" のチェック群
12. Filter: フィルターバー(role=textbox, D0 で名称確定)をクリック → spec.Filter を Fill → Enter
13. 保存: View menu → "Save view" → alertdialog の "Save"(dialog が出ない UI variant では直接保存)
14. 検証は後続の `ghpmv verify --enable-browser-automation` または browser E2E で行う
```

Project conflict は browser stage より前に `--on-conflict skip|update|fail` または `--project-number` で解決する。browser importer は選択された target project に view を適用し、個別設定の失敗は warning にする。

作成順序: **スナップショットの view number 昇順**で作成(タブ順が概ね再現される)。デフォルトで作られる "View 1" は、スナップショット先頭の view で上書き(rename + 設定)して消費する。

---

## 4. 解析すべきパターン: Workflow 編

### 4.1 Workflow インベントリ(built-in 全種)

| # | Workflow 名(= UI 表示名 = GraphQL name) | 設定形状(export/import で扱う値) |
|---|---|---|
| W-1 | Item added to project | 対象(issues / pull requests のチェック)+ Set Status = 値 |
| W-2 | Item reopened | 同上 |
| W-3 | Item closed | 対象 + Set Status = 値 ※既定で有効 |
| W-4 | Code changes requested | Set Status = 値(PR 固定) |
| W-5 | Code review approved | Set Status = 値(PR 固定) |
| W-6 | Pull request merged | Set Status = 値(PR 固定)※既定で有効 |
| W-7 | Auto-close issue | Status が 値 になったら close |
| W-8 | Auto-archive items | フィルター文字列(`is:` / `reason:` / `updated:` のサブセット) |
| W-9 | Auto-add to project | 対象リポジトリ + フィルター文字列。**複数インスタンス可**(プラン上限: Free 1 / Pro・Team 5 / GHEC(DR 含む)20) |
| W-10 | Auto-add sub-issues to project | 有効/無効と UI で公開される設定 |

Workflow filter で確認済みの qualifier は `is:` `label:` `reason:` `updated:` `assignee:` `author:` `repo:` `org:` `no:`(negation 可)。`assignee:` / `author:` / `repo:` / `org:` の識別子は user / repository / organization mapping を構造的に適用し、その他の qualifier と構文は保持する。未解決の識別子または曖昧な Auto-add repository は browser-assisted import の最初の mutation 前にエラーとする。

### 4.2 export: Workflow 詳細の UI 読み取り

GraphQL で `workflows { name number enabled }` を取得後、**enabled かどうかに関わらず全件**について:

```
ReadWorkflow(number):
 1. {project}/workflows/{number} へ goto
 2. 閲覧モードのまま本文の AriaSnapshot を取得し、以下をパース:
    - "When" 節: 対象種別チェック状態(issue / pull request)
    - "Set" / "Filters" 節: Status 値、フィルター文字列、対象リポジトリ名
 3. 読み取れない項目があれば Edit を押して読み、Discard/戻るで離脱(D0 で要否判定)
 4. Auto-add 複製分: workflows 一覧サイドバーに "Auto-add to project" 系が複数並ぶ。
    一覧の全リンク(role=link)を列挙して W-9 型を複数収集(カスタム名も保持)
```

### 4.3 import: Workflow 設定の操作シーケンス

```
ApplyWorkflow(spec):
 1. {project}/workflows へ goto → サイドバー "Default workflows" から spec.Name のリンクをクリック
    (Auto-add の 2 個目以降: 既存 "Auto-add to project" の行のケバブメニュー → "Duplicate" →
     名前入力ダイアログに spec.Name → 作成後にそのページへ)
 2. Sel.EditWorkflowButton.Click()
 3. spec の形状に応じて設定:
    - 対象種別: "issues" / "pull requests" のチェックボックス(D0 で role 確認)
    - Status 値: "Set" 節のドロップダウン → role=option から spec.StatusValue を選択
      ※ 前提: Status option は M3(API)で移行済みのため同名 option が必ず存在する。
        無ければ即エラー(移行順序バグの検出)
    - リポジトリ選択(W-9): リポジトリピッカーに入力して候補選択。
      リポジトリマッピング CSV で変換したターゲット側リポジトリ名を使う
    - フィルター(W-8/W-9): テキストボックスに Fill
 4. 設定を "Save workflow" / "Save and turn on workflow" で保存
    spec.Enabled == false の場合は保存後に toggle off へ戻す
 5. 検証は後続の `ghpmv verify --enable-browser-automation` または browser E2E で行う
```

順序: W-9(Auto-add)は実装上限 20 を preflight で確認し、超過分を warning + skip する。target plan が 20 未満の場合は GitHub UI が Duplicate を拒否した操作失敗を warning として報告する。

---

## 5. スナップショットのデータモデル拡張

`snapshot.json` に追加するセクション(M2 の JSON スキーマに反映):

```jsonc
{
  "views": [{
    "number": 1, "name": "Backlog", "layout": "TABLE_LAYOUT",
    "filter": "is:issue -status:Done",
    "visibleFields": ["Title", "Assignees", "Status", "Priority"],   // 列順そのまま
    "groupBy": ["Status"], "verticalGroupBy": [], 
    "sortBy": [{ "field": "Priority", "direction": "DESC" }],
    "ui": {                                    // ← UI でしか読めない部分(export 時に Playwright で補完)
      "sliceBy": "Assignees",
      "fieldSum": ["Estimate"],
      "roadmap": { "startField": "Start", "targetField": "End", "zoom": "Quarter", "markers": ["Milestone"] },
      "scrapedAt": "2026-07-05T00:00:00Z"      // 補完に失敗した場合は ui: null + warnings[] に記録
    }
  }],
  "workflows": [{
    "number": 3, "name": "Item closed", "enabled": true,
    "ui": {
      "contentTypes": ["ISSUE", "PULL_REQUEST"],
      "statusValue": "Done",
      "filter": null, "repository": null
    }
  }]
}
```

`ghpmv export` は既定で API のみを使用する。UI-only データも取得する場合だけ `--enable-browser-automation` を指定し、API-only snapshot を import する場合は取得されていない UI-only 項目をスキップして警告する。

---

## 6. 検証ループ

browser importer 自体は各 view / workflow の適用直後に完全な read-back diff を行わない。移行後は `ghpmv verify --enable-browser-automation` が次を比較する:

1. **API で読める項目**: GraphQL で対象 view を `views(first:50)` から number 一致で取得し、`layout / filter / groupByFields / sortByFields / verticalGroupByFields / fields(POSITION順)` を spec と比較
2. **UI でしか読めない項目**: §3.2 / §4.2 の export 用読み取りルーチンを**そのまま再利用**してターゲットを再スクレイプし、spec.ui と比較
3. 差分は `verify` コマンドと同じレポーター(期待値/実測値/対象)で出力

手動実行する `BrowserRoundTripTests` は View と Workflow のラウンドトリップを別々のテストに分け、それぞれ
`fixture project → export → 空プロジェクトへ import → export(再) → snapshot diff`
を行う。explicit collaborator export は別の E2E テストで検証する。

---

## 7. フィクスチャープロジェクト仕様(テストデータ)

`GHPMV_TEST_ORG` に作る基準プロジェクト(セットアップスクリプトは可能な限り GraphQL、View/Workflow 部分は初回手動 + 本ツール自身でのブートストラップ):

- フィールド: Status(custom option 4 つ、色・説明付き)/ Priority(single-select)/ Estimate(number)/ Start・End(date)/ Sprint(iteration, 2 週間, 完了済み 1 + 未来 2)/ Notes(text)
- Views(§3.1 の V-1〜V-9 を全て網羅する 4 view):
  1. "Backlog" — Table, filter, hidden fields, 2 キー sort, Field sum(Estimate), group-by Status
  2. "Board" — Board, Column by Priority, swimlane = Sprint, Slice by Assignees
  3. "Roadmap" — Roadmap, Dates = Start/End, Zoom = Quarter, Markers 有効
  4. "Everything" — Table, 設定ほぼデフォルト(デフォルト値の透過を確認)
- Workflows: W-1〜W-8 を非デフォルト Status 値で有効化、W-9 を 2 本(別リポ + 別フィルター)。1 つは disabled のまま設定を持たせる(§4.3 の D0 論点の検証用)
- Items: issue 10 / PR 3 / draft 3(archived 2 を含む)

## 8. 既知のリスクと v1 スコープ外

v1 対象外項目の将来対応方針(v1.x / v2)は [PLAN.md §8「スコープとロードマップ」](../PLAN.md#8-スコープとロードマップv1-対象外と将来対応) で一元管理する。本表は判断の実装的背景のみ記載。

| 項目 | 判断 |
|---|---|
| 表示フィールドの列順 | v1 は表示フィールドの membership のみ一致させ、列順は明示的に再現しない。将来対応では `Locator.DragToAsync` を検討 |
| View タブの並び順(D&D のみ) | v1 スコープ外。import 後に警告で「手動で並び替えてください」と案内 |
| disabled workflow への設定適用 | 対応済み。設定保存後に toggle off へ戻す |
| memex 内部 API の直接利用 | 既定では不採用。HAR は現時点で成果物として記録していない。UI 操作不能項目が出た場合に調査・取得を検討 |
| UI 変更による破損 | リリース前の手動 browser E2E と `docs/ui-maps/` の実測記録で確認。回復可能な破損は warning + 対象設定の skip。scheduled/nightly CI は未実装 |

## 9. 実装タスク分解と現在の状態

| ID | タスク | 状態 |
|---|---|---|
| B0 | D0 Discovery(§2)、`Sel.cs` 確定 | 完了 |
| B1 | BrowserSession 基盤(起動/ストレージ/ウェイト) | 完了。診断ダンプは未実装 |
| B2 | `ghpmv login` / `ghpmv setup --browsers` | 完了 |
| B3 | View UI-export(§3.2: sliceBy/fieldSum/roadmap 読み取り) | 完了 |
| B4 | View import Table 系(V-1〜V-4, V-8, V-9) | 完了。複数 sort / field order は best effort |
| B5 | View import Board / Roadmap(V-5〜V-7) | 完了 |
| B6 | Workflow UI-export(§4.2) | 完了 |
| B7 | Workflow import(§4.3)W-1〜W-8 | 完了 |
| B8 | Workflow import W-9(Auto-add 複数 + 上限処理) | 完了。実装上限は 20 |
| B9 | ラウンドトリップ E2E(§6) | テスト実装済み・手動実行。scheduled/nightly CI は未実装 |
