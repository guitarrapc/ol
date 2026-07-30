# development scope の依存へ追加ライセンスを許可する `check` ポリシー

## 背景

`ol check` のライセンスポリシーは、解決済みの全コンポーネントへ一つの `--allow-licenses` を適用する。これは全 dependency scope を一律に検査する場合には明快だが、開発ツールと、その推移的依存だけに現れるライセンスを区別できない。

再現例（2026-07 に npm で lockfile を生成し registry のライセンスを確認）:

- `vite` 単体（5/6/7 いずれも）: 推移依存はすべて MIT/ISC/BSD-3-Clause。**LGPL は存在しない。**
- `vite` + `@vitejs/plugin-react` + `typescript` + `eslint` + `vitest` + `sass` の現実的な dev ツールチェーン: 255 エントリ（dev 250 / prod 5）。ここに `caniuse-lite`(**CC-BY-4.0**) と Python-2.0 のパッケージが **dev=true** で現れる。どちらも本番 artifact に影響しないが、`--allow-licenses MIT,Apache-2.0,BSD-3-Clause` を fail させる。

つまり「permissive allow-list を弾く dev-only 推移依存」という課題は実在する。ただし当初想定した LGPL ではなく、実測では CC-BY-4.0 などが該当する。本 plan の機構はライセンス非依存（`--allow-dev-licenses` は任意の SPDX Identifier を受け付ける）なので、対象が LGPL でも CC-BY-4.0 でも同じ設計で扱える。

組織のポリシーとして「resolver が development scope だけから到達すると記録した依存では、指定した追加ライセンスを許可する」は成立し得る。現在はこの差を表現する手段がなく、利用者は次のどちらかを選ばざるを得ない。

- `--allow-licenses` へ dev 専用ライセンスを加え、runtime 依存にも同じ許可を広げる。
- development scope の違反を受け入れず、`check` を CI policy として使用しない。

dev scope の依存を検査対象から除外する方法は採らない。依存 inventory とライセンス証拠には残し、どのポリシーによって許可されたかを可視化する。

## 結論

通常の allow-list とは別に、入力が development scope だけからの到達を明示できる依存へ適用する追加 allow-list を設ける。

```text
ol check --input package-lock.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --allow-dev-licenses CC-BY-4.0,LGPL-2.1-only
```

`--allow-dev-licenses` は任意とする。省略時の `check` verdict、text、SARIF violation 集合は現在と変えない。指定時も `--allow-licenses` を置き換えず、resolver 上で development scope に限定されたコンポーネントにだけ追加する。

**canonical report schema は version 1 のまま**、typed usage を保持する形へ直接拡張する。ol は未リリースなので v1→v2 の互換維持や移行分岐は設けず、最初から最も好ましい形へ一直線で向かう。

この plan は組織全体の scope policy を扱う。特定パッケージだけを承認する例外は [exact versioned PURL のライセンスポリシー例外](plan_package_license_exceptions.md) で別に扱う。

## このポリシーが証明しないこと

package manager の `dev`、`require-dev`、または同等の情報は、resolver 上の dependency scope を表す。次は証明しない。

- application source がその package を import していないこと
- bundler、code generator、plugin が package の code、asset、生成物を製品 artifact へ取り込まないこと
- development container、CI image、社内配布 tool などが別の配布境界を作らないこと
- LGPL その他のライセンス義務が発生しないこと

したがって `--allow-dev-licenses` は利用者が明示する組織 policy であり、Ol による artifact inclusion や法的効果の判定ではない。release gate では、production artifact または production 用に生成した SBOM を `--allow-dev-licenses` なしの通常 allow-list で別途検査する。development scope の check はこの artifact check を代替しない。

## ポリシー契約

### 評価順序

解決済みライセンスを持つ各コンポーネントを次の順序で評価する。

1. `--allow-licenses` だけで SPDX expression を評価する。
2. 通常 allow-list で許可されれば、依存 usage に関係なく通過する。
3. 通常 allow-list で許可されず、コンポーネントが resolver 上の `DevelopmentOnly` と判定できる場合だけ、通常 allow-list と `--allow-dev-licenses` の和集合で同じ SPDX expression を再評価する。
4. 追加 allow-list でも許可されなければ、従来どおり `NotAllowed` violation とする。

SPDX expression の意味は既存契約を変えない。たとえば通常 allow-list が `MIT`、development allow-list が `LGPL-2.1-only` のとき、resolver 上の `DevelopmentOnly` である `MIT AND LGPL-2.1-only` は通過する。`OR`、`AND`、`WITH` は既存の `SpdxExpression.TryEvaluatePolicy` と同じ意味で評価する。

`unknown`、`ambiguous`、`conflict`、`invalid`、`error` は development allow-list の対象にしない。これらは解決済みライセンスの scope policy ではなく、証拠の不確実性または収集失敗である。baseline の acknowledgeability も変更しない。

### development scope の判定

`DevelopmentOnly` は artifact inclusion を意味しない、resolver scope の分類名とする。コンポーネントの名前や direct dependency の名前から推測せず、dependency input が提供した解決情報だけから判定する。

一つの report component に対応する全 occurrence を、全 resolution context にわたって集約する。

- occurrence が一つ以上存在し、すべて `Development` と判定できる場合だけ `DevelopmentOnly` とする。
- `Runtime` が一つでもあれば通常ポリシーを適用する。
- `Unknown` が一つでもあれば通常ポリシーを適用する。
- 同一 package/version が dev と runtime の両方から到達する場合、dev 側の occurrence を理由に追加許可してはならない。
- graph または occurrence usage を提供しない入力は `DevelopmentOnly` とみなさない。

「最短 dependency path が dev tool を通る」は十分な証明ではない。runtime へ至る別経路を見落とすため、全 occurrence を対象にする。

### 入力形式ごとの初期対応

初期実装は、parser が resolver 上の `DevelopmentOnly` reachability を明示的に確定できる次の入力に限定する。

| 入力 | 初期判定 |
|---|---|
| npm `package-lock.json` | lockfile の `dev` semantics に基づく |
| pnpm `pnpm-lock.yaml` | importer から計算済みの strictly-dev reachability に基づく |
| Composer `composer.json` + `composer.lock` | root の `require` / `require-dev` を区別した graph reachability に基づく |

Composer の `packages-dev` は監査情報と整合性検証に使用するが、単独では `Development` の根拠にしない。現在の parser は root の `require` と `require-dev` を同じ requirement に潰しているため、初期対応ではこの区別を保持してから graph を解決する。

- `require` から一つでも到達できる package は `Runtime` とする。
- `require-dev` だけから到達できる package は `Development` とする。
- 両方から到達できる package は `Runtime` とする。
- production reachability と `packages-dev` の所属が矛盾する bundle は、development policy を適用せず入力不整合の command error にする。
- `composer.lock` の bucket だけを変更した stale/manual-merge input が runtime package を `Development` にしないことを negative fixture で固定する。

Yarn は当初 `Unknown` としていたが、`yarn.lock` の隣にある `package.json` を optional companion として読めば非 workspace は対応できる（Slice 5a で実装）。`yarn.lock` 単体では dev scope を持たないため、companion が無ければ従来どおり `Unknown`（非破壊）。workspace（複数 package.json）は root manifest 一つでは各 workspace の scope を判定できないため対象外（Slice 5b）。

次は初期対応に含めない。

- CycloneDX / SPDX: 現在の共通 inventory が development usage として正規化していないため、SBOM 固有 scope を直ちに policy へ流用しない。
- Maven: `test`、`provided`、`optional` は同じ意味ではない。`DevelopmentOnly` として認める scope を仕様化してから追加する。
- Cargo: `dev`、`build`、target condition と複数 incoming kind の意味を整理し、全到達経路を証明できるようにしてから追加する。
- NuGet、Go、pip、Bundler: 入力が明示的な `DevelopmentOnly` reachability を提供しない限り `Unknown` とする。

未対応入力で `--allow-dev-licenses` を指定すること自体は command error にしない。通常 allow-list による fail-closed の結果を返す。通常出力には usage unknown の component 数を示し、全 component が usage 非対応なら stable warning を一度だけ stderr に出す。

## データモデル

`DependencyOccurrenceVariant.Value` は resolver-native な監査情報である。policy evaluator が `"dev"` や `"scope=test"` を文字列検索してはならない。variant の表記変更が policy verdict を変え、ecosystem 固有 switch が中央へ広がるためである。

入力 adapter が resolver 固有 semantics を解釈し、共通の typed data へ投影する。

```text
DependencyUsage : byte
  Unknown = 0
  Runtime = 1
  Development = 2
```

occurrence と一対一の owned `DependencyUsage[]` は採用しない。canonical report のため policy option の有無にかかわらず保持すると、通常の `scan` と `check` に occurrence 数比例の新しい allocation を常設するためである。

採用する sparse representation（occurrence-index keyed、`DependencyOccurrenceVariant` と同じ owner に併置）:

- `DependencyInventory` に nullable な 2 要素を追加する。両方 `null` のとき usage は全 occurrence `Unknown` で、**追加 allocation は 0B**。
  - `UsageDeterminedRanges` : usage を確定できた occurrence-index の連続 range 集合。range 内の occurrence は既定で `Runtime`。
  - `DevelopmentOccurrences` : `Development` の occurrence-index を昇順 sparse に保持。必ず determined range 内に位置する。
- occurrence i の usage = `DevelopmentOccurrences` に含まれれば `Development`、どれかの range 内なら `Runtime`、いずれでもなければ `Unknown`。
- capability がない input（SBOM/NuGet/Yarn 等）は両方 `null` のまま。一つの未対応 input が、対応済み input の usage を `Unknown` に落とさない。
- collection combiner は子 inventory の range を occurrence offset で rebase し、`DevelopmentOccurrences` を offset 加算して昇順連結する（子は occurrence 順に append されるため順序が保たれる）。
- policy verdict は `DependencyOccurrenceVariant.Value` の文字列解析ではなく、この typed 情報を読む。

per-component 集約は max-merge で表現できる（順序 `none < Development < Unknown < Runtime`）。`Runtime` は吸収的、`Unknown` は `Development` を吸収、component に occurrence が無ければ `Unknown`。結果が `Development` のときだけ dev allow-list を適用する。

この representation は provisional とし、`DependencyInputScannerBenchmark` / `LicensePolicyBenchmark` で確定する。`Development` が多数を占める dev-tool lockfile で sparse dev-index が dense byte array より不利なら、range ごとの default + 少数側 exception（2 bit/occurrence 以下の packed）を比較候補にする。dense array を無条件には追加しない。

この形には次の性質が必要である。

- parser の一時 reachability buffer は既存どおり pool し、owned inventory へ必要な sparse/packed 情報だけをコピーする。
- policy evaluation は occurrence 数と component 数に対して線形とし、component ごとの graph walk を行わない。
- usage 集約用 working set は component 数から容量を決め、`ArrayPool<T>` または固定長配列で一度だけ確保する。
- component loop 内で variant や purl を `string` 化しない。
- format 固有 semantics は各登録済み input handler に閉じ込め、policy evaluator に format switch を追加しない。

`Runtime` と `Development` の両方が集約された状態は、公開 enum を増やさず集約中の mixed state として扱える。最終判定は `DevelopmentOnly` ではない。

## live 評価と persisted report

live `check --input` では、reconciled `components` が `inventory.Components` と index で 1:1 に対応し、`inventory.Occurrences` の `ComponentIndex` も同じ index を指す。したがって occurrence usage を per-component へ集約し、そのまま policy へ渡せる。**初期スコープ（npm/pnpm の live `--input`）は schema 変更を要さない。**

persisted `check --report`（Slice 4 で実装）は、当初想定した `inventoryComponentIndex` による display↔inventory mapping ではなく、**per-component usage を top-level component に直接保存する**方式を採った。usage は表示 component と一緒に sort を通って永続化されるため、index mapping も Resolve の再実行も不要で最小構成になる。

- `scan --format json` は書き出し前に `DependencyUsageResolver.Resolve` で per-component usage を求め、view の sort と同じ順序で並べ替えて各 top-level component に `usage`（`development`/`runtime`、Unknown は省略）を書く。usage capability の無い inventory・非 JSON 形式では何も書かない（0B）。
- reader は `usage` を `ScanReport.ComponentUsages`（components と平行配列）へ復元する。`usage` を持たない component は `Unknown`。
- `check --report` は `ComponentUsages` をそのまま policy へ渡す。usage を保存しない旧 report は全 `Unknown` で fail-closed。
- SARIF の `FindInventoryComponentIndex`（identity 先頭一致）は usage parity と無関係な既存の別課題なので本スコープでは触れない。

## CLI と出力

`--allow-dev-licenses` は `--allow-licenses` と同じく、カンマ区切りの SPDX License Identifier だけを受け付ける。正規化、公式 casing、空要素、未知 identifier、expression を渡した場合の検証規則は共通化する。

option が指定された場合、適用件数を pass/fail にかかわらず表示する。

```text
Allowed by development policy: 3 components.
License check passed: 142 components satisfy the policy.
```

件数が 0 でも表示する。scope 情報が失われた、または想定していた例外が適用されなくなったことを CI log から確認できるためである。

violation 一覧は従来どおり禁止ライセンスを表示する。development allow-list が適用されなかった場合は、`runtime occurrence` または `usage unknown` を区別できるようにする。絶対入力 path、cache path、token は出力しない。

`--verbose` では、development policy で通過した各 component の stable identity、license expression、resolver usage を決定的な順序で列挙する。件数だけで、どの component が許可されたかを隠さない。

SARIF は text と同じ violation 集合を維持する。追加 allow-list で通過したコンポーネントを SARIF result にしない代わりに、`run.properties` の機械可読 policy allowance として component identity、license、policy source を記録する。persisted report への typed usage 保存は後続スコープとし、保存後は `--input` と `--report` が同一 verdict・同一 stdout を返す。

## baseline との境界

baseline は unresolved evidence の reviewed snapshot であり、解決済みの禁止ライセンスを許可する仕組みではない。この境界を変更しない。

- `--allow-dev-licenses` は `LicenseStatus.Matched` にだけ作用する。
- `LicenseAllowPolicy.CanAcknowledge` は通常 allow-list だけを基準にする。
- resolver 上で `DevelopmentOnly` の `conflict` や `ambiguous` を追加 allow-list で acknowledgeable にしない。
- `--update-baseline` が development policy を理由に新しい baseline entry を生成しない。

## 実施順序

schema 移行を伴わないので、live `--input` の縦スライスを最短で通し、adapter と persist を段階追加する。

### Slice 1: 共通 typed usage + policy + npm live（本スライス）

`test-first-development` に従い、実装前に次の失敗テストを追加する。

1. resolver 上の `Development` component は、通常 allow-list が MIT、development allow-list が対象ライセンスのとき通過する。
2. 同じ component に runtime occurrence が一つでもあれば失敗する。
3. usage が unknown の component は失敗する。
4. development allow-list に無いライセンスは `Development` でも失敗する。
5. `AND` / `OR` / `WITH` は通常と development の allow-list の和集合で既存 SPDX semantics を保つ。
6. unresolved status と baseline の挙動は変わらない。
7. option 省略時は既存の policy test と byte 単位で同じ stdout を返す（`Evaluate` の allocation も不変）。
8. npm parser: direct dev / transitive dev / dev と runtime の両経路から到達する同一 package / optional・peer を dev と誤認しない、を fixture で固定する。
9. combiner: 対応 input（npm）と未対応 input（SBOM 等）を含む collection で、未対応側だけ `Unknown`、対応側の usage を保持する。
10. E2E: 実際の vite dev tooling lock で、CC-BY-4.0 を `--allow-dev-licenses CC-BY-4.0` が通し、runtime component には効かない。

実装対象:

- `DependencyUsage` enum、`DependencyInventory` への `UsageDeterminedRanges`/`DevelopmentOccurrences`、combiner の rebase。
- npm parser で dev-only node を `Development` occurrence として emit（既存 `NodeFlags.Dev`）。
- per-component 集約 helper（max-merge、pooled scratch、option 指定時のみ実行）。
- `LicenseAllowPolicy` に dev allow-list（`allowed ∪ dev` の union frozen set を起動時 1 回構築）と usage を読む `Evaluate` overload。既存 overload の挙動と allocation は不変。
- `check --allow-dev-licenses` CLI、`Allowed by development policy: N components.` 出力、runtime/usage-unknown 区別。

### Slice 2: pnpm live

pnpm の `strictlyDev`（既に算出済み）を `Development` occurrence として surface する。fixture: importer 直下 dev / transitive dev / production と両経路 / workspace 複数 importer / strictly-optional を dev と誤認しない。

### Slice 3: Composer live（実装済み）

root の `require` と `require-dev` を別 owner（`-1`/`-2`）として読み、production `require` 閉包の到達可能性を単色 BFS で求める（既存 `queue` を再利用し追加 rent を避ける）。usage は次のとおり fail-closed に決める。

- `packages-dev` bucket かつ production 未到達 → `Development`。
- production 到達（bucket 問わず）→ `Runtime`。
- `packages-dev` bucket なのに production 到達 → stale/hand-merge の不整合として command error。
- `packages` bucket が graph 上 production 未到達でも `Runtime` のまま（bucket を production 側の根拠として優先）。

`packages-dev` を単独根拠にせず graph の production 到達で確認する。runtime package を bucket 移動だけで `Development` にできない（不整合 error になる）ことを negative fixture で固定した。`DependencyType`（`require`/`require-dev` 両方を depth 0 に seed）と edge projection は不変。

### Slice 4: persisted report と parity（実装済み）

report へ per-component usage を保存し、`check --report --allow-dev-licenses` を `--input` と一致させた（上記「live 評価と persisted report」参照）。`inventoryComponentIndex` は不要と判明したため採用せず、per-component 保存で最小化した。SARIF policy allowance は usage parity と独立の追加項目なので本スコープ外（backlog）。

仕様文書には確定した WHAT/WHY と実装で判明した lessons learned を残し、配列構築や lookup の詳細 HOW は移さない。cli.md / README を更新済み。

- **lessons learned**: top-level `components` は既定 sort（`ecosystem,name,version`）で並ぶため inventory 順と一致しない。usage を「表示 component と同じ配列」に載せて sort を通すことで、display↔inventory の index mapping を一切持たずに parity を得られた。usage capability の無い入力・非 JSON 形式では usage 配列を確保せず 0B を維持する。multi-component report の violation **順序** parity（live=inventory 順 vs report=表示順）は本 slice の usage とは別の既存事項。

### Slice 5a: 非 workspace Yarn（実装済み）

入力契約に汎用の optional companion 概念を追加した。`DependencyInputHandler` に `OptionalCompanionFileName` と `CompanionParser` を持たせ、検出済み single-file input の隣に companion があれば `CompanionParser` を使う。ScanCommands は primary を parse 後、handler が companion を宣言していれば同ディレクトリの sibling を直接読み（discovery の収集集合は汚さない）、companion 付きで再 scan し、source-hash にも畳み込む。companion が無ければ従来の single-file parse のまま（非破壊、single-file benchmark はゼロ増）。

Yarn usage は base parse（`ParseClassic`/`ParseBerry`）を変更せず、解決済み inventory の occurrence + edge グラフ上で post-hoc に計算する。`package.json` の `dependencies`/`optionalDependencies`/`peerDependencies` を production root、`devDependencies` を dev root として名前で occurrence に seed し、edge を辿って production/dev 到達を求め、`dev 到達 && !production 到達` を `Development` とする。`workspaces` フィールドを持つ manifest、または context が 2 つ以上（workspace lockfile）は usage 未分類（`Unknown`）にフォールバックする。

### Slice 5b: Yarn workspace（未対応）

workspace は root と各 workspace の `package.json` が別々で、あるパッケージの dev 判定はその workspace の `devDependencies` に依存する。root の `workspaces` glob から全 workspace manifest を discover して per-context に seed する必要があり、5a の単一 manifest では不十分。fail-closed で `Unknown` のまま。

## 性能検証

この変更は inventory ingestion と policy evaluation の両方へ影響する。テスト通過後、同じ code revision から変更前後を測定する。

- `DependencyInputScannerBenchmark`: typed usage の parser/graph ingestion コスト
- `LicensePolicyBenchmark`: option 省略、全 runtime、全 development、mixed usage
- `E2EBenchmark`: stage 間でコストを移していないこと

受け入れ条件は次とする（メモリは可能な限り 0B を目指す）。

- `--allow-dev-licenses` 省略時、`Evaluate` の allocation とタイミングは現状と不変（既存 overload に dev 経路を混ぜない）。
- usage を提供しない input（SBOM/NuGet/Yarn 等）の scan/check は、両 usage field が `null` で **追加 storage 0B**。
- usage を提供する input（npm/pnpm/Composer）の追加 owned memory は sparse representation の実測値で説明し、dense enum array を baseline にしない。
- development policy 指定時の per-component 集約は pooled scratch のみで owned allocation 0B、working set は component/occurrence 数から説明できる。
- component loop に LINQ、closure、regex、transient string、interface dispatch を追加しない。
- mean time または allocated bytes の説明できない regression を残さない。

## スコープ外

- dev dependency を scan または report から除外する `--exclude-dev`
- package 名や direct dependency 名から development usage を推測すること
- 最短 path だけを根拠に `DevelopmentOnly` と判定すること
- optional、peer、Maven `provided`、Cargo `build` を一律に development とみなすこと
- 特定パッケージ向け例外、wildcard、version range
- deny-list、copyleft の自動分類、法的効果の判断
- package-manager scope から production artifact 非包含を推論すること
- package manager や build tool を `ol` から起動して scope を再解決すること

## 成功条件

1. npm/pnpm の development scope だけから到達する非 permissive component（実測では CC-BY-4.0 等）を、runtime allow-list を広げずに許可できる。
2. 同じ component に runtime または unknown occurrence があれば、追加許可は適用されない。
3. scan/report には component、license evidence、occurrence、usage が残り、除外による不可視化が起きない。
4. persisted report への usage 保存後、安定した inventory index により `--input` と `--report` の verdict、stdout、SARIF violation 集合、policy allowance 集合が一致する。
5. baseline の fail-closed 境界を弱めない。
6. CLI、README、spec が resolver scope と artifact inclusion を区別し、production artifact/SBOM の通常 check を release gate として示す。
7. usage を提供しない入力の hot path を維持し、対応入力の追加 memory/time を同一 revision benchmark で説明できる。
