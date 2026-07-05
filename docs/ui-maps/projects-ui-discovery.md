# Projects UI Discovery (D0) — 2026-07-05

実 UI(GHEC, gpm-source/projects/3)での Playwright 操作から得た確定情報。M6/M7 実装の一次資料。

## セレクター確定事項

| 操作 | セレクター | 備考 |
|---|---|---|
| View タブ | `getByRole('tab', { name: <viewName> })` | tablist は `navigation "Select view"` 内 |
| 新規 View 作成 | `getByRole('tab', { name: 'New view' })` → menu `New view` → `menuitem "Table"/"Board"/"Roadmap"` | 選択と同時に view 作成・遷移(保存不要) |
| View リネーム | タブをダブルクリック → `getByRole('textbox', { name: 'Change view name' })` → fill → Enter | 即時保存 |
| View 設定メニュー | フィルターバーの `button "View"`(exact)| `menu > group "Configuration"` に `menuitem "Group by: <val>" / "Markers: <val>" / "Sort by: <val>" / "Dates: <val>" / "Zoom level: <val>" / "Slice by: <val>"`。**ラベルと現在値が name に結合**されるため部分一致(`name: /^Group by:/` 等)で特定する |
| Roadmap 日付フィールド | `menuitem "Dates: ..."` → `dialog "Select date fields"` → group "Start date" / "Target date" の `menuitemradio` | Iteration フィールドは "<name> start" / "<name> end" の 2 radio に展開される |
| View 設定の保存 | `button "Save view"` → **確認 alertdialog** "Save display options for <view>?" → `button "Save"` | 設定変更は「Unsaved changes」status で検出可能。保存は 2 段階 |
| Workflow 一覧 | `/orgs/{org}/projects/{n}/workflows`。サイドバー `list "Default workflows"` 内の link | |
| Workflow 編集 | `button "Edit"`(viewing mode)→ 編集 → `button "Save and turn on workflow"` | |
| Auto-add フィルター | `combobox "Filters"`(編集時)/ `textbox "Filters" [disabled]`(閲覧時) | 閲覧モードでも値は読める(UI-export 可能) |
| Auto-add リポジトリ | `button "When the filter matches a new or updated item : <repo>"` | name にリポジトリ名が結合 |

## 挙動の発見(export/import 設計に影響)

1. **プロジェクト作成時に 6 つの workflow が既定で有効**(Item closed / PR merged / Auto-close issue / Auto-add sub-issues / PR linked / Item added)。import 時は「作成→差分適用」になる
2. **workflow の URL ID は保存前は GUID(揮発性・リロードごとに変化)、保存後は数値 ID に変わる**。GraphQL の `number` とは別物。URL 直接遷移は保存済み workflow のみ信頼できる → 未設定 workflow はサイドバーの link name で辿るのが安全
3. **Auto-add workflow は org にリポジトリが 1 つ以上ないと設定不可**("No repositories found")。import 側で前提チェックが必要
4. Workflow 閲覧モードでも設定値(フィルター文字列・対象リポジトリ・Set value)が DOM に出る → **Edit を押さずに UI-export 可能**
5. View 系の設定変更は SPA 内で「Unsaved changes」になり、明示保存が必要(タブ名変更は例外で即時保存)
6. GraphQL read-back: views { number name layout } / workflows { number name enabled } は UI 操作直後に反映される(遅延なし)

## フィクスチャー最終状態(gpm-source/projects/3)

- Views: 1=View 1 (TABLE), 2=Fixture Board (BOARD), 3=Fixture Roadmap (ROADMAP, Dates: Fixture Date → Fixture Sprint end)
- Workflows: 7 enabled(既定 6 + Auto-add to project #7: repo=fixture-repo, filter=`is:issue is:open`)
- fixture-repo: private, Issue #1/#2(gpm-target 側にも同名 repo あり — workflow E2E 用)

## M7 E2E 実走で確定した追加知見(2026-07-05)

1. **Status options を API で上書きすると、既定 workflow の値バインディングが外れる**。外れた状態の "Set value" ボタンの accessible name は **"Set valueundefined"**(GitHub UI の quirk)+ "A value is required" 表示。→ import では全 workflow の Set value 再設定が必須(実装済み)
2. **セレクター regex はプレフィックス一致のみにする**。accessible name は改行を含むことがあり `$` アンカーは不一致を起こす("Set value : "・"When ... : " の値サフィックスはバインディング消失時に消えるため必須にしない)
3. **編集モードで実効差分が無いと Save ボタンは disabled のまま** → クリック待ちでハング。disabled なら Discard で抜ける(SaveWorkflowAsync 実装済み)
4. **リポジトリ picker は入力後に非同期で再フィルター**(デバウンス+fetch)→ option は CountAsync 即時判定でなく WaitForAsync(10s) で待つ
5. View タブの**リネーム用ダブルクリックは新規タブ作成直後に不発になることがある** → textbox 出現を 5s 待ち×3 リトライ
6. **EMU/SAML のセッションは短命**(数時間で失効)。失効時は `/login` リダイレクトではなく **enterprise SSO インタースティシャル**("Single sign-on to <Enterprise>" + Continue)が出る。BrowserSession.GotoAsync は Continue 自動クリックで IdP セッションが生きていれば透過再認証、死んでいれば失敗 → `gpm login` 再実行が必要
7. 並列テスト実行時(browser E2E + integration 同時)は SPA ハイドレーションが遅くなる → Playwright 既定タイムアウトは 30s に設定
