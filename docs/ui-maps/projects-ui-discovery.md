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

- Views:
  - 1=View 1 (TABLE): filter=`status:Todo`, Sort by=Fixture Number (asc), Slice by=Fixture Select, visibleFields=既定 5 + Fixture Text + Fixture Date(Fixture Number はソート由来の仮想列のため visibleFields に入らない — 下記 E2E 知見 8)
  - 2=Fixture Board (BOARD): Column by=Fixture Select, Swimlanes=Status(GraphQL groupByFields に反映), Field sum=`Fixture Number` (Count は uncheck 済み)
  - 3=Fixture Roadmap (ROADMAP): Dates=Fixture Date → Fixture Sprint end, Zoom=Quarter, Markers=[Fixture Date]
- Workflows 9(GraphQL 可視分): 既定 6 enabled + Auto-add to project (#7: repo=fixture-repo, filter=`is:issue is:open`) + **Auto-add secondary**(repo=fixture-repo, filter=`is:issue label:bug`, enabled)+ **Code changes requested**(保存済み disabled, Set value=In Progress)
- fixture-repo: private, Issue #1/#2(gpm-target 側にも同名 repo あり — workflow E2E 用)

## M7 E2E 実走で確定した追加知見(2026-07-05)

1. **Status options を API で上書きすると、既定 workflow の値バインディングが外れる**。外れた状態の "Set value" ボタンの accessible name は **"Set valueundefined"**(GitHub UI の quirk)+ "A value is required" 表示。→ import では全 workflow の Set value 再設定が必須(実装済み)
2. **セレクター regex はプレフィックス一致のみにする**。accessible name は改行を含むことがあり `$` アンカーは不一致を起こす("Set value : "・"When ... : " の値サフィックスはバインディング消失時に消えるため必須にしない)
3. **編集モードで実効差分が無いと Save ボタンは disabled のまま** → クリック待ちでハング。disabled なら Discard で抜ける(SaveWorkflowAsync 実装済み)
4. **リポジトリ picker は入力後に非同期で再フィルター**(デバウンス+fetch)→ option は CountAsync 即時判定でなく WaitForAsync(10s) で待つ
5. View タブの**リネーム用ダブルクリックは新規タブ作成直後に不発になることがある** → textbox 出現を 5s 待ち×3 リトライ
6. **EMU/SAML のセッションは短命**(数時間で失効)。失効時は `/login` リダイレクトではなく **enterprise SSO インタースティシャル**("Single sign-on to <Enterprise>" + Continue)が出る。BrowserSession.GotoAsync は Continue 自動クリックで IdP セッションが生きていれば透過再認証、死んでいれば失敗 → `ghpmv login` 再実行が必要(IdP セッションまで失効すると素の `/login` リダイレクトになる)
7. 並列テスト実行時(browser E2E + integration 同時)は SPA ハイドレーションが遅くなる → Playwright 既定タイムアウトは 30s に設定

## Project collaborators UI export discovery (2026-07-06)

GraphQL has `updateProjectV2Collaborators` but no read field for current project collaborators. The web UI exposes **explicit** collaborators at:

`/orgs/{org}/projects/{number}/settings/access`

Confirmed with `ravel-maurice-uo_sde` temporarily added as a WRITER collaborator to `gpm-source/projects/3`:

```yaml
- heading "Manage access" [level=3]
- checkbox "Select all collaborators. 1 member"
- checkbox "Select ravel-maurice-uo_sde"
- img "ravel-maurice-uo_sde"
- link "Ravel Maurice":
  - /url: /ravel-maurice-uo_sde
- text: ravel-maurice-uo_sde
- 'button "Role: Write"'
- button "Remove"
```

Selectors/parse strategy:

| Data | UI source |
|---|---|
| User collaborator login | `checkbox "Select <login>"` + profile URL `/login` |
| Team collaborator slug | `checkbox "Select <team display name>"` + team URL `/orgs/{org}/teams/{slug}` |
| Role | adjacent `button "Role: Read|Write|Admin"` |

Important limitations:

- This captures only **explicit collaborators** listed under Manage access.
- Inherited access (organization owners, base role, enterprise policies, repository/team inheritance) is not represented as collaborator rows and is intentionally not exported.
- Adding the exporting user as a collaborator may not create a visible row if they already have inherited admin access.

## E2E カバレッジ強化で確定した追加知見(2026-07-06)

1. **Board の横グルーピングは「Group by」ではなく「Swimlanes」メニュー項目**(`menuitem "Swimlanes: <value>"`)。Board のメニューは `Fields / Column by / Swimlanes / Sort by / Field sum / Slice by` の 6 項目で "Group by" は存在しない。GraphQL の `groupByFields` は board では Swimlanes を反映する → import は board のとき Swimlanes メニューで適用する(ViewUiExporter/Importer 対応済み、ViewUiSnapshot.Swimlanes 追加)
2. **Field sum はチェックボックスオーバーレイ**(`menuitemcheckbox`: "Count" + 数値フィールド名)。menuitem の accessible name は "Field sum: Count and Fixture Number" のようにラベル結合されるため値はメニューを開かず読める。Count は uncheck 可能
3. **UI のリスト値は散文形式**: "A and B" / "A, B, and C"(カンマ区切りとは限らない)→ ParseListValue は `,` と `" and "` の両方で分割する
4. **Fields オーバーレイのエントリーは `option` ロール + aria-checked**(Field sum / Markers の `menuitemcheckbox` とは異なる)→ チェックボックス走査は両ロール対応が必要(ToggleCheckboxesAsync 対応済み)
5. **Markers オーバーレイには表示オプションが混在**: Truncate titles / Show date fields(表示設定)+ Milestone / date・iteration フィールド名(マーカー)。menuitem テキスト "Markers: <値>" にはマーカーだけが出る
6. **未保存 workflow のページには enable toggle が存在しない**(URL は GUID)。保存済み workflow の URL は数値 ID だが、この ID は GraphQL workflow number とは独立している。export は GraphQL の enabled 値を使い、詳細ページはサイドバーの name 一致 link で開く。toggle の accessible name も workflow 名とは限らないため、import は main detail pane 内の stateful control (`aria-pressed` / `aria-checked` / checkbox) へ fallback する
7. **未保存 disabled workflow は Edit → "Save and turn on workflow"(設定変更なしでも押せる)→ トグル off で「保存済み disabled」にできる**。未保存状態には toggle がないため、設定値が既に一致する enabled workflow も toggle を探さずこの保存経路で有効化する。保存済み disabled workflow は GraphQL の `workflows` に enabled=false で現れ、閲覧モードで設定値も読める(export 可能)。import は未保存の場合にこの save-once 経路を通す(WorkflowUiImporter.ApplyBuiltInAsync / ApplyDisabledAsync)
8. **ソートキーのフィールドは仮想列として表示される**: Fields オーバーレイで aria-checked=true になるが GraphQL `visibleFields` には永続化されない(uncheck→再 check でも変わらない)。import 側は desired 集合にソート列を含めて誤 uncheck を防止する
9. **Duplicate 直後の workflow は編集モードで開く**("Edit" ボタンが無い)→ import は Save ボタンの有無で編集モードを判定してから Edit をクリックする
10. **Playwright 1.61 の wait タイムアウトは `System.TimeoutException`**(`Microsoft.Playwright.TimeoutException` は存在せず、`PlaywrightException` の派生でもない)→ ブラウザーモジュールの catch は `exception is PlaywrightException or TimeoutException` で両方受ける(リトライ・warning 化がタイムアウトでも機能するように修正済み)
