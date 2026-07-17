# Browser Automation 詳細設計: Views / Workflows の解析と再現

M6/M7(Playwright による View・Workflow 移行)の実装者向け詳細プラン。
本書だけで実装に着手できることを目標とする。全体プランは [PLAN.md](../PLAN.md) を参照。

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
context.SetDefaultTimeout(15_000);
```

- 操作間ウェイト: 連続 UI 操作の間に 300ms(`Task.Delay`)。並列ページは使わない(1 セッション 1 ページ直列)。ToS 配慮とレース回避を兼ねる。
- **失敗時診断(全操作共通)**: 例外捕捉時に ①`page.Url` ②`page.ScreenshotAsync()` ③`page.Locator("body").AriaSnapshotAsync()` ④console ログ、を `./ghpmv-diagnostics/{timestamp}/` に保存してから再スロー。リトライは**ページリロード → 操作全体をやり直し**を 1 回まで(要素単位の盲目リトライはしない)。

### 1.5 セレクターレジストリ(単一ファイル集中管理)

ロジック内へのセレクター直書きを禁止し、`Selectors.cs` に集約。各エントリは「主セレクター(role/name)+ フォールバック(data-testid)+ D0 で確認した日付」をコメントで持つ。

```csharp
internal static class Sel
{
    // === View タブ周り ===
    public static ILocator NewViewTab(IPage p) => p.GetByRole(AriaRole.Tab, new() { Name = "New view" });
    public static ILocator ViewTab(IPage p, string name) => p.GetByRole(AriaRole.Tab, new() { NameRegex = new($"^{Regex.Escape(name)}") }); // 未保存ドットで末尾が変わるため前方一致
    public static ILocator ViewOptionsButton(IPage p) => p.GetByRole(AriaRole.Button, new() { Name = "View options" }); // docs の octicon aria-label より。D0 で要確認
    // === View options メニュー内(D0 で名称確定) ===
    public static ILocator MenuItem(IPage p, string name) => p.GetByRole(AriaRole.MenuItem, new() { Name = name });
    // === Workflows ===
    public static ILocator WorkflowNavItem(IPage p, string name) => p.GetByRole(AriaRole.Link, new() { Name = name });
    public static ILocator EditWorkflowButton(IPage p) => p.GetByRole(AriaRole.Button, new() { Name = "Edit" });
    public static ILocator SaveAndTurnOn(IPage p) => p.GetByRole(AriaRole.Button, new() { NameRegex = new("^Save and turn on") });
}
```

---

## 2. D0: Discovery フェーズ(実装の最初の 2〜3 日でやること)

セレクターの最終確定は実 UI でしか出来ない。以下を**成果物付き**で実施する。

1. フィクスチャープロジェクト(§7)を `GHPMV_TEST_ORG` に手動 or スクリプトで作成
2. `pwsh playwright.ps1 codegen --load-storage=storage-state.json https://github.com/orgs/$env:GHPMV_TEST_ORG/projects/1` で以下の操作を一通り録画し、生成されたセレクターを控える:
   - New view → レイアウト切替 → Fields 変更 → Group by → Sort by → Slice by → Field sum → filter 入力 → Save changes → Rename → Delete
   - Workflows ページ → 各 workflow を開く → Edit → 設定変更 → Save and turn on workflow → Disable
3. 各画面状態で `AriaSnapshotAsync()` を取得し `docs/ui-maps/{screen}.yaml` に**コミット**(以後の drift 検出の基準となる)
4. 各操作の**ネットワークトレース(HAR)**を保存(`context.RouteFromHARAsync` 用ではなく解析用)。memex 内部 JSON API(view 作成/更新の XHR)のエンドポイントと payload を記録する
   - ※ 内部 API の直接呼び出しは既定では採用しない(非公開・無保証)。ただし UI 操作が困難な設定項目が見つかった場合の**代替手段として調査結果を残す**
5. `Selectors.cs` の全エントリを実測値で確定し、本書 §3〜§6 の「D0 確認」印を消し込む
6. Workflow の全種類についてビュー(閲覧)モードの DOM を確認し、**Edit を押さずに設定値が読み取れるか**を判定(読み取れない場合、export は Edit モードで開いて読み取り→変更せず離脱、に変更)

**D0 の完了条件**: `docs/ui-maps/` に全画面の aria snapshot、`Selectors.cs` に確認日付入りの全セレクター、動作する codegen 由来のスパイクコード 1 本。

---

## 3. 解析すべきパターン: View 編

### 3.1 View インベントリ(この組み合わせを全て扱う)

| # | パターン | 設定項目 |
|---|---|---|
| V-1 | Table 基本 | 表示フィールド選択と列順 |
| V-2 | Table + group-by | 任意フィールド 1 つ(Status/Single-select/Iteration など) |
| V-3 | Table + 複数 sort | 最大 2 キー + 各 ASC/DESC |
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
2. Sel.ViewOptionsButton(page).ClickAsync() → メニューの AriaSnapshot を取得
3. メニュー項目のラベルから現在値を読む:
   - "Slice by: <field>" 形式のサブラベル(D0 確認)→ ViewSpec.SliceBy
   - "Field sum: <fields>"(D0 確認)→ ViewSpec.FieldSum
   - Roadmap のみ: "Dates: <...>", "Zoom level: <Month|Quarter|Year>", "Markers: <...>"(D0 確認)
4. Esc でメニューを閉じる
```

実装メモ: メニューの各項目は「設定名 + 現在値」がアクセシブルネームに含まれる想定(D0 確認)。含まれない場合はサブメニューを開いて `aria-checked`/`aria-selected` な項目を読む。

### 3.3 import: View 作成の操作シーケンス

ViewSpec 1 件あたりの手順。**各ステップの後に 300ms ウェイト**。

```
CreateView(spec):
 1. プロジェクトルートへ goto
 2. Sel.NewViewTab.Click()                          → 新タブ "View {n}" が active になるのを待つ
 3. Rename: ViewOptions → メニュー "Rename view"(pencil)→ テキストボックスに spec.Name → Enter
 4. Layout: ViewOptions → "Layout" セクションで spec.Layout("Table"|"Board"|"Roadmap")をクリック
 5. Fields: ViewOptions → "Fields" → ダイアログで:
      - 現在の表示フィールドの集合と spec.VisibleFields を突き合わせ、不足を "+"、余剰を hide(D0 で UI 形状確定)
      - 列順: v1 は「フィールドを一旦すべて隠す → spec 順に追加」で順序を再現(D&D 回避)。不可なら列ヘッダーの D&D(§8 リスク)
 6. Column by(Board のみ): ViewOptions → "Column by" → spec.VerticalGroupBy を選択
 7. Group by: ViewOptions → "Group by" → spec.GroupBy を選択(未指定なら "None" を選択)
 8. Sort by: ViewOptions → "Sort by" → 第 1 キー選択 → 方向トグル(D0 確認)→ 第 2 キーがあれば追加
 9. Slice by: ViewOptions → "Slice by" → spec.SliceBy(未指定なら None)
10. Field sum: ViewOptions → "Field sum" → spec.FieldSum の各フィールドをチェック
11. Roadmap のみ: "Dates" → 開始/終了フィールド対 or iteration を選択、"Zoom level"、"Markers" のチェック群
12. Filter: フィルターバー(role=textbox, D0 で名称確定)をクリック → spec.Filter を Fill → Enter
13. 保存: Control+S(未保存ドットの消滅を expect で待つ)。効かなければ ViewOptions → "Save changes"
14. 検証(§6): GraphQL read-back + UI-only 項目は §3.2 の読み取りルーチンで再取得して spec と diff
```

冪等性: 実行前にターゲットの views を GraphQL で列挙し、同名 view が存在したら `--on-conflict skip|replace`(replace = 既存 view を ViewOptions → "Delete view" で削除して作り直し)。

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
| W-10 | Auto-add sub-issues to project(存在有無を D0 確認) | 有効/無効のみ想定 |

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
 4. spec.Enabled == true → "Save and turn on workflow" をクリック
    spec.Enabled == false → 保存後にトグルで無効化(または Discard して設定のみ記録)。
    ※「設定を保存しつつ無効のまま」の UI 動線は D0 で確認。無理なら
      「disabled な workflow は設定を適用しない(名前と無効状態のみ記録)」に仕様を落とす
 5. 検証: GraphQL `workflows` で name+enabled を照合。設定詳細は §4.2 の読み取りルーチンで再取得して diff
```

順序: W-9(Auto-add)はプラン上限チェックを先に行い、超過分は警告してスキップ(超過は GraphQL では分からないため、Duplicate メニューの無効化状態で検知)。

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

## 6. 検証ループ(エージェントの自己確認手段)

各 view / workflow の適用直後に **read-back 検証**を行い、不一致は即失敗させる:

1. **API で読める項目**: GraphQL で対象 view を `views(first:50)` から number 一致で取得し、`layout / filter / groupByFields / sortByFields / verticalGroupByFields / fields(POSITION順)` を spec と比較
2. **UI でしか読めない項目**: §3.2 / §4.2 の export 用読み取りルーチンを**そのまま再利用**してターゲットを再スクレイプし、spec.ui と比較
3. 差分は `verify` コマンドと同じレポーター(期待値/実測値/対象)で出力

この「import 実装」と「export 実装」が互いの検証器になる構造により、E2E テストは
`fixture project → export → 空プロジェクトへ import → export(再)→ スナップショット同士を diff`
という**ラウンドトリップ 1 本**で全パターンを回帰できる。

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
| 列順再現の D&D | v1 は「全 hide → spec 順に追加」で回避。それでも順序が再現できない場合のみ `Locator.DragToAsync` を実装 |
| View タブの並び順(D&D のみ) | v1 スコープ外。import 後に警告で「手動で並び替えてください」と案内 |
| disabled workflow への設定適用 | D0 の調査結果次第で仕様確定(§4.3) |
| memex 内部 API の直接利用 | 既定では不採用。D0 で HAR を記録し、UI 操作不能項目が出た場合のみ個別に検討 |
| UI 変更による破損 | nightly E2E + `docs/ui-maps/` の aria snapshot 差分で検知。破損時は該当項目を warning + 手動手順書生成にフォールバック |

## 9. 実装タスク分解(この順で実装し、各タスク末尾の検証を green にしてから次へ)

| ID | タスク | 検証(自動テスト) |
|---|---|---|
| B0 | D0 Discovery(§2)完了、`Selectors.cs` 確定 | ui-maps コミット + スパイクが fixture project で成功 |
| B1 | BrowserSession 基盤(起動/ストレージ/診断ダンプ/ウェイト) | ログイン済み state でプロジェクトが開ける E2E 1 本 |
| B2 | `ghpmv login` / `ghpmv setup --browsers` | 手動確認 + セッション失効検知の E2E |
| B3 | View UI-export(§3.2: sliceBy/fieldSum/roadmap 読み取り) | fixture の 4 view で期待値一致 |
| B4 | View import(§3.3)Table 系(V-1〜V-4, V-8, V-9) | 空プロジェクトへ適用 → §6 read-back 一致 |
| B5 | View import Board / Roadmap(V-5〜V-7) | 同上 |
| B6 | Workflow UI-export(§4.2) | fixture の全 workflow で期待値一致 |
| B7 | Workflow import(§4.3)W-1〜W-8 | 適用 → read-back 一致 |
| B8 | Workflow import W-9(Auto-add 複数 + 上限処理) | 2 本適用 + 上限超過時の警告テスト |
| B9 | ラウンドトリップ E2E(§6 の diff 1 本)を nightly CI に登録 | export→import→re-export の snapshot diff が空 |
