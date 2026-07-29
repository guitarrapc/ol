# development scope の依存へ追加ライセンスを許可する `check` ポリシー

## 背景

`ol check` のライセンスポリシーは、解決済みの全コンポーネントへ一つの `--allow-licenses` を適用する。これは全 dependency scope を一律に検査する場合には明快だが、Vite のような開発ツールと、その推移的依存だけに現れるライセンスを区別できない。

組織のポリシーとして「resolver が development scope だけから到達すると記録した依存では LGPL を許可する」は成立し得る。現在はこの差を表現する手段がなく、利用者は次のどちらかを選ばざるを得ない。

- `--allow-licenses` へ LGPL を加え、runtime 依存にも同じ許可を広げる。
- development scope の違反を受け入れず、`check` を CI policy として使用しない。

dev scope の依存を検査対象から除外する方法は採らない。依存 inventory とライセンス証拠には残し、どのポリシーによって許可されたかを可視化する。

## 結論

通常の allow-list とは別に、入力が development scope だけからの到達を明示できる依存へ適用する追加 allow-list を設ける。

```text
ol check --input package-lock.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --allow-dev-licenses LGPL-2.1-only,LGPL-2.1-or-later
```

`--allow-dev-licenses` は任意とする。省略時の `check` verdict、text、SARIF violation 集合は現在と変えない。指定時も `--allow-licenses` を置き換えず、resolver 上で development scope に限定されたコンポーネントにだけ追加する。安定した component mapping と usage persistence のため、canonical JSON report schema は後述のとおり version 2 へ変更する。

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

次は初期対応に含めない。

- Yarn lock 単体: root manifest の dev relationship を証明できない形式では `Unknown` とする。
- CycloneDX / SPDX: 現在の共通 inventory が development usage として正規化していないため、SBOM 固有 scope を直ちに policy へ流用しない。
- Maven: `test`、`provided`、`optional` は同じ意味ではない。`DevelopmentOnly` として認める scope を仕様化してから追加する。
- Cargo: `dev`、`build`、target condition と複数 incoming kind の意味を整理し、全到達経路を証明できるようにしてから追加する。
- NuGet、Go、pip、Bundler: 入力が明示的な `DevelopmentOnly` reachability を提供しない限り `Unknown` とする。

未対応入力で `--allow-dev-licenses` を指定すること自体は command error にしない。通常 allow-list による fail-closed の結果を返す。通常出力には usage unknown の component 数を示し、全 component が usage 非対応なら stable warning を一度だけ stderr に出す。

## データモデル

`DependencyOccurrenceVariant.Value` は resolver-native な監査情報である。policy evaluator が `"dev"` や `"scope=test"` を文字列検索してはならない。variant の表記変更が policy verdict を変え、ecosystem 固有 switch が中央へ広がるためである。

入力 adapter が resolver 固有 semantics を解釈し、共通の typed data へ投影する。

```text
DependencyUsage
  Unknown
  Runtime
  Development
```

occurrence と一対一の owned `DependencyUsage[]` は採用しない。canonical report のため policy option の有無にかかわらず保持すると、通常の `scan` と `check` に occurrence 数比例の新しい allocation を常設するためである。

初期実装では次の sparse representation を第一候補とし、実装前 benchmark で確定する。

- input または occurrence range ごとに、development usage を完全に判定できるかを表す typed capability を持つ。
- capability がある range では、通常 occurrence を `Runtime` とし、`Development` だけを sparse typed flag として保持する。
- capability がない range はすべて `Unknown` とする。
- typed flag は、可能なら既存の sparse `DependencyOccurrenceVariant` と同じ owned entry に同居させる。policy verdict は `Value` の文字列解析ではなく flag を読む。
- collection inventory は子 inventory の capability range と sparse flag を occurrence offset に合わせて結合する。一つの未対応 input によって、対応済み input の usage を全て `Unknown` に落としてはならない。

実測で sparse range が dense/packed representation より遅い場合だけ、2 bit/occurrence 以下の packed storage を比較候補にする。byte または既定 enum の dense array を無条件に追加しない。

この形には次の性質が必要である。

- parser の一時 reachability buffer は既存どおり pool し、owned inventory へ必要な sparse/packed 情報だけをコピーする。
- policy evaluation は occurrence 数と component 数に対して線形とし、component ごとの graph walk を行わない。
- usage 集約用 working set は component 数から容量を決め、`ArrayPool<T>` または固定長配列で一度だけ確保する。
- component loop 内で variant や purl を `string` 化しない。
- format 固有 semantics は各登録済み input handler に閉じ込め、policy evaluator に format switch を追加しない。

`Runtime` と `Development` の両方が集約された状態は、公開 enum を増やさず集約中の mixed state として扱える。最終判定は `DevelopmentOnly` ではない。

## report component と inventory component の対応

canonical JSON report の top-level `components` は表示順であり、`inventory.components` は input order である。policy violation の index を inventory index として使用してはならない。

name、version、purl、source ID、ecosystem から identity を再探索する方法は採用しない。collection combiner の identity は input format と handler 固有 comparison を含み、top-level component の表示 field だけでは一意に復元できないためである。

- canonical JSON の各 top-level component に `inventoryComponentIndex` を記録する。
- scan view の sort/filter 前に original inventory index を component と対にし、表示順を変えても index を保持する。
- reader は index の範囲、重複、component identity との整合を検証し、欠落または矛盾を partial mapping として受け入れない。
- live `check --input` は original inventory order の対応を直接使用し、persisted `check --report` は保存済み index を使用する。
- SARIF dependency path も同じ index を使用し、現在の先頭一致 helper を policy の正しさへ持ち込まない。

この変更で canonical report schema を version 2 に上げる。version 1 report は通常の `--allow-licenses` と baseline による再評価を引き続き許可するが、usage capability と安定 index を持たないため `--allow-dev-licenses` との組み合わせは exit 2 にする。version 1 を暗黙に `Unknown` として live input と異なる verdict を返してはならない。

## CLI と出力

`--allow-dev-licenses` は `--allow-licenses` と同じく、カンマ区切りの SPDX License Identifier だけを受け付ける。正規化、公式 casing、空要素、未知 identifier、expression を渡した場合の検証規則は共通化する。

option が指定された場合、適用件数を pass/fail にかかわらず表示する。

```text
Allowed by development policy: 3 components.
License check passed: 142 components satisfy the policy.
```

件数が 0 でも表示する。scope 情報が失われた、または想定していた例外が適用されなくなったことを CI log から確認できるためである。

violation 一覧は従来どおり禁止ライセンスを表示する。development allow-list が適用されなかった場合は、`runtime occurrence` または `usage unknown` を区別できるようにする。絶対入力 path、cache path、token は出力しない。

`--verbose` では、development policy で通過した各 component の stable identity、license expression、resolver usage を決定的な順序で列挙する。件数だけで、どの LGPL component が許可されたかを隠さない。

SARIF は text と同じ violation 集合を維持する。追加 allow-list で通過したコンポーネントを SARIF result にしない代わりに、`run.properties` の機械可読 policy allowance として component identity、license、policy source を記録する。persisted report は既存 inventory、新しい typed usage、安定した component index を保存し、同じ schema version の `--input` と `--report` が同一 verdict と同一 stdout を返す。

## baseline との境界

baseline は unresolved evidence の reviewed snapshot であり、解決済みの禁止ライセンスを許可する仕組みではない。この境界を変更しない。

- `--allow-dev-licenses` は `LicenseStatus.Matched` にだけ作用する。
- `LicenseAllowPolicy.CanAcknowledge` は通常 allow-list だけを基準にする。
- resolver 上で `DevelopmentOnly` の `conflict` や `ambiguous` を追加 allow-list で acknowledgeable にしない。
- `--update-baseline` が development policy を理由に新しい baseline entry を生成しない。

## 実施順序

### Phase 1: policy 契約をテストで固定する

`test-first-development` に従い、実装前に次の失敗テストを追加する。

1. resolver 上の `DevelopmentOnly` LGPL は、通常 allow-list が MIT、development allow-list が LGPL のとき通過する。
2. 同じ LGPL component に runtime occurrence が一つでもあれば失敗する。
3. usage が unknown の LGPL component は失敗する。
4. development allow-list に無い GPL は `DevelopmentOnly` でも失敗する。
5. `AND` / `OR` / `WITH` は通常と development の allow-list の和集合で既存 SPDX semantics を保つ。
6. unresolved status と baseline の挙動は変わらない。
7. option 省略時は既存の policy test と byte 単位で同じ stdout を返す。
8. resolver 上は dev でも production source へ bundle され得るため、CLI help と README が artifact 非包含を保証しない。

### Phase 2: typed usage を inventory へ追加する

共通 enum、usage capability range、sparse typed flag を追加する。collection combiner、reader/writer の schema versioning、range と occurrence index の妥当性を検証する。dense、sparse、packed の代表 graph を同一 benchmark で比較し、owned memory と走査時間を記録して representation を確定する。

npm、pnpm、Composer adapter を一つずつ red-green で対応する。各 adapter では次を fixture で固定する。

- direct dev dependency
- dev dependency の transitive dependency
- production と development の両経路から到達する同一 package/version
- workspace または複数 context
- 対応済み input と未対応 input を含む collection
- dev 以外の optional/peer 条件を development と誤認しない
- Composer の `require` と `require-dev` の区別
- Composer manifest の production requirement と lock `packages-dev` が矛盾する入力

### Phase 3: policy evaluation と CLI を接続する

policy input は run ごとに一度だけ SPDX casing へ正規化し、immutable lookup として保持する。inventory usage を component 単位へ一度だけ集約し、既存 `LicenseAllowPolicy` の expression evaluation を再利用する。

evaluation result は violation 配列に加えて development policy を適用した component index と件数を返せる explicit data とする。renderer が再評価して件数を推測してはならない。

### Phase 4: persisted report、SARIF、文書を同期する

canonical JSON version 2 round-trip、version 1 の base-policy compatibility、version 1 と development policy の明示的な exit 2、cross-format identity collision、`check --report`、SARIF の violation equivalence と policy allowance properties を追加する。実装後に次を更新する。

- `specs/cli.md`: `check` の scope policy、出力、baseline との境界
- `specs/packagemanager.md`: 各 adapter が保証する typed usage semantics
- `README.md`: CLI 例と対応入力
- `backlog.md`: dependency-scope policy を initial scope 外とする記述と残課題

仕様文書には確定した WHAT/WHY と実装で判明した lessons learned を残し、配列構築や lookup の詳細 HOW は移さない。

## 性能検証

この変更は inventory ingestion と policy evaluation の両方へ影響する。テスト通過後、同じ code revision から変更前後を測定する。

- `DependencyInputScannerBenchmark`: typed usage の parser/graph ingestion コスト
- `LicensePolicyBenchmark`: option 省略、全 runtime、全 development、mixed usage
- `E2EBenchmark`: stage 間でコストを移していないこと

受け入れ条件は次とする。

- option 省略時にも canonical report の再評価に必要な usage facts は保持するため、ゼロ増加を無条件には約束しない。追加する owned memory は sparse/packed representation の実測値で説明し、dense enum array を baseline にしない。
- usage を提供しない input の scan/check に occurrence 数比例の storage を追加しない。
- development policy 指定時の working allocation を component/occurrence 数から説明できる。
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

1. Vite のような npm/pnpm の development scope だけから到達する LGPL component を、runtime allow-list を広げずに許可できる。
2. 同じ component に runtime または unknown occurrence があれば、追加許可は適用されない。
3. scan/report には component、license evidence、occurrence、usage が残り、除外による不可視化が起きない。
4. canonical report version 2 の安定した inventory index により、`--input` と `--report` の verdict、stdout、SARIF violation 集合、policy allowance 集合が一致する。
5. baseline の fail-closed 境界を弱めない。
6. CLI、README、spec が resolver scope と artifact inclusion を区別し、production artifact/SBOM の通常 check を release gate として示す。
7. usage を提供しない入力の hot path を維持し、対応入力の追加 memory/time を同一 revision benchmark で説明できる。
