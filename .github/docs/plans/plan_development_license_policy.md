# 開発専用依存へ追加ライセンスを許可する `check` ポリシー

## 背景

`ol check` のライセンスポリシーは、解決済みの全コンポーネントへ一つの `--allow-licenses` を適用する。これは配布物へ含まれる依存を一律に検査する場合には明快だが、Vite のような開発ツールと、その推移的依存だけに現れるライセンスを区別できない。

組織のポリシーとして「配布物へ含まれる依存では LGPL を許可しないが、入力から開発専用と証明できる依存では LGPL を許可する」は成立し得る。現在はこの差を表現する手段がなく、利用者は次のどちらかを選ばざるを得ない。

- `--allow-licenses` へ LGPL を加え、runtime 依存にも同じ許可を広げる。
- 開発専用の違反を受け入れず、`check` を CI policy として使用しない。

dev 依存を検査対象から除外する方法は採らない。開発専用であっても依存 inventory とライセンス証拠には残し、どのポリシーによって許可されたかを可視化する。

## 結論

通常の allow-list とは別に、開発専用と証明できる依存へだけ適用する追加 allow-list を設ける。

```text
ol check --input package-lock.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --allow-dev-licenses LGPL-2.1-only,LGPL-2.1-or-later
```

`--allow-dev-licenses` は任意とする。省略時の判定、出力、性能特性は現在と変えない。指定時も `--allow-licenses` を置き換えず、開発専用コンポーネントにだけ追加する。

この plan は組織全体の scope policy を扱う。特定パッケージだけを承認する例外は [任意パッケージのライセンスポリシー例外](plan_package_license_exceptions.md) で別に扱う。

## ポリシー契約

### 評価順序

解決済みライセンスを持つ各コンポーネントを次の順序で評価する。

1. `--allow-licenses` だけで SPDX expression を評価する。
2. 通常 allow-list で許可されれば、依存 usage に関係なく通過する。
3. 通常 allow-listで許可されず、コンポーネントが `development-only` と証明できる場合だけ、通常 allow-list と `--allow-dev-licenses` の和集合で同じ SPDX expression を再評価する。
4. 追加 allow-list でも許可されなければ、従来どおり `NotAllowed` violation とする。

SPDX expression の意味は既存契約を変えない。たとえば通常 allow-list が `MIT`、development allow-list が `LGPL-2.1-only` のとき、development-only な `MIT AND LGPL-2.1-only` は通過する。`OR`、`AND`、`WITH` は既存の `SpdxExpression.TryEvaluatePolicy` と同じ意味で評価する。

`unknown`、`ambiguous`、`conflict`、`invalid`、`error` は development allow-list の対象にしない。これらは解決済みライセンスの scope policy ではなく、証拠の不確実性または収集失敗である。baseline の acknowledgeability も変更しない。

### development-only の証明

development-only はコンポーネントの名前や direct dependency の名前から推測しない。dependency input が提供した解決情報だけから判定する。

一つの report component に対応する全 occurrence を、全 resolution context にわたって集約する。

- occurrence が一つ以上存在し、すべて `Development` と証明できる場合だけ development-only とする。
- `Runtime` が一つでもあれば通常ポリシーを適用する。
- `Unknown` が一つでもあれば通常ポリシーを適用する。
- 同一 package/version が dev と runtime の両方から到達する場合、dev 側の occurrence を理由に追加許可してはならない。
- graph または occurrence usage を提供しない入力は development-only とみなさない。

「最短 dependency path が dev tool を通る」は十分な証明ではない。runtime へ至る別経路を見落とすため、全 occurrence を対象にする。

### 入力形式ごとの初期対応

初期実装は、現在の parser が development-only reachability を明示的に確定できる次の入力に限定する。

| 入力 | 初期判定 |
|---|---|
| npm `package-lock.json` | lockfile の `dev` semantics に基づく |
| pnpm `pnpm-lock.yaml` | importer から計算済みの strictly-dev reachability に基づく |
| Composer `composer.json` + `composer.lock` | `packages-dev` occurrence に基づく |

次は初期対応に含めない。

- Yarn lock 単体: root manifest の dev relationship を証明できない形式では `Unknown` とする。
- CycloneDX / SPDX: 現在の共通 inventory が development usage として正規化していないため、SBOM 固有 scope を直ちに policy へ流用しない。
- Maven: `test`、`provided`、`optional` は同じ意味ではない。development-only として認める scope を仕様化してから追加する。
- Cargo: `dev`、`build`、target condition と複数 incoming kind の意味を整理し、全到達経路を証明できるようにしてから追加する。
- NuGet、Go、pip、Bundler: 入力が明示的な development-only reachability を提供しない限り `Unknown` とする。

未対応入力で `--allow-dev-licenses` を指定すること自体は command error にしない。通常 allow-list による fail-closed の結果を返し、scope が証明できなかったことを診断可能にする。

## データモデル

`DependencyOccurrenceVariant.Value` は resolver-native な監査情報である。policy evaluator が `"dev"` や `"scope=test"` を文字列検索してはならない。variant の表記変更が policy verdict を変え、ecosystem 固有 switch が中央へ広がるためである。

入力 adapter が resolver 固有 semantics を解釈し、共通の typed data へ投影する。

```text
DependencyUsage
  Unknown
  Runtime
  Development
```

実装候補は `DependencyInventory` に occurrence index と一対一対応する `DependencyUsage[]` を所有させる形とする。配列が空なら全 occurrence を `Unknown` と解釈する。sparse variant は監査用としてそのまま残す。

この形には次の性質が必要である。

- parser の一時 reachability buffer は既存どおり pool し、owned inventory へ使用範囲だけをコピーする。
- policy evaluation は occurrence 数と component 数に対して線形とし、component ごとの graph walk を行わない。
- usage 集約用 working set は component 数から容量を決め、`ArrayPool<T>` または固定長配列で一度だけ確保する。
- component loop 内で variant や purl を `string` 化しない。
- format 固有 semantics は各登録済み input handler に閉じ込め、policy evaluator に format switch を追加しない。

`Runtime` と `Development` の両方が集約された状態は、公開 enum を増やさず集約中の mixed state として扱える。最終判定は development-only ではない。

## report component と inventory component の対応

canonical JSON report の top-level `components` は表示順であり、`inventory.components` は input order である。policy violation の index を inventory index として使用してはならない。

現在 `SarifRenderer` が持つ identity comparison と同じ契約を共有 helper へ抽出し、policy evaluation の前に report component と inventory component の対応を一度だけ構築する。

- identity は ecosystem、name、version、purl、source ID の既存組み合わせを維持する。
- component ごとの線形検索による O(component²) は避ける。
- live scan と persisted `--report` の両方で同じ対応結果を得る。
- identity を lookup するための一時 `string` を component ごとに生成しない。

## CLI と出力

`--allow-dev-licenses` は `--allow-licenses` と同じく、カンマ区切りの SPDX License Identifier だけを受け付ける。正規化、公式 casing、空要素、未知 identifier、expression を渡した場合の検証規則は共通化する。

option が指定された場合、適用件数を pass/fail にかかわらず表示する。

```text
Allowed by development policy: 3 components.
License check passed: 142 components satisfy the policy.
```

件数が 0 でも表示する。scope 情報が失われた、または想定していた例外が適用されなくなったことを CI log から確認できるためである。

violation 一覧は従来どおり禁止ライセンスを表示する。development allow-list が適用されなかった場合は、少なくとも verbose 診断で `runtime occurrence` または `usage unknown` を区別できるようにする。絶対入力 path、cache path、token は出力しない。

SARIF は text と同じ violation 集合を維持する。追加 allow-list で通過したコンポーネントを SARIF result にしない。persisted report は既存 inventory と新しい typed usage を保存し、`--input` と `--report` が同一 verdict と同一 stdout を返す。

## baseline との境界

baseline は unresolved evidence の reviewed snapshot であり、解決済みの禁止ライセンスを許可する仕組みではない。この境界を変更しない。

- `--allow-dev-licenses` は `LicenseStatus.Matched` にだけ作用する。
- `LicenseAllowPolicy.CanAcknowledge` は通常 allow-list だけを基準にする。
- development-only の `conflict` や `ambiguous` を追加 allow-list で acknowledgeable にしない。
- `--update-baseline` が development policy を理由に新しい baseline entry を生成しない。

## 実施順序

### Phase 1: policy 契約をテストで固定する

`test-first-development` に従い、実装前に次の失敗テストを追加する。

1. development-only LGPL は、通常 allow-list が MIT、development allow-list が LGPL のとき通過する。
2. 同じ LGPL component に runtime occurrence が一つでもあれば失敗する。
3. usage が unknown の LGPL component は失敗する。
4. development allow-list に無い GPL は development-only でも失敗する。
5. `AND` / `OR` / `WITH` は通常と development の allow-list の和集合で既存 SPDX semantics を保つ。
6. unresolved status と baseline の挙動は変わらない。
7. option 省略時は既存の policy test と byte 単位で同じ stdout を返す。

### Phase 2: typed usage を inventory へ追加する

共通 enum と occurrence-indexed storage を追加する。reader/writer の schema versioning、配列長、occurrence index の妥当性を検証する。

npm、pnpm、Composer adapter を一つずつ red-green で対応する。各 adapter では次を fixture で固定する。

- direct dev dependency
- dev dependency の transitive dependency
- production と development の両経路から到達する同一 package/version
- workspace または複数 context
- dev 以外の optional/peer 条件を development と誤認しない

### Phase 3: policy evaluation と CLI を接続する

policy input は run ごとに一度だけ SPDX casing へ正規化し、immutable lookup として保持する。inventory usage を component 単位へ一度だけ集約し、既存 `LicenseAllowPolicy` の expression evaluation を再利用する。

evaluation result は violation 配列に加えて development policy の適用件数を返せる explicit data とする。renderer が再評価して件数を推測してはならない。

### Phase 4: persisted report、SARIF、文書を同期する

canonical JSON round-trip、`check --report`、SARIF の violation equivalence を追加する。実装後に次を更新する。

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

- option 省略時に component 数へ比例する新しい allocation を追加しない。
- development policy 指定時の working allocation を component/occurrence 数から説明できる。
- component loop に LINQ、closure、regex、transient string、interface dispatch を追加しない。
- mean time または allocated bytes の説明できない regression を残さない。

## スコープ外

- dev dependency を scan または report から除外する `--exclude-dev`
- package 名や direct dependency 名から development usage を推測すること
- 最短 path だけを根拠に development-only と判定すること
- optional、peer、Maven `provided`、Cargo `build` を一律に development とみなすこと
- 特定パッケージ向け例外、wildcard、version range
- deny-list、copyleft の自動分類、法的効果の判断
- package manager や build tool を `ol` から起動して scope を再解決すること

## 成功条件

1. Vite のような npm/pnpm の開発ツールからだけ到達する LGPL component を、runtime allow-list を広げずに許可できる。
2. 同じ component に runtime または unknown occurrence があれば、追加許可は適用されない。
3. scan/report には component、license evidence、occurrence、usage が残り、除外による不可視化が起きない。
4. `--input` と canonical `--report` の verdict、stdout、SARIF violation 集合が一致する。
5. baseline の fail-closed 境界を弱めない。
6. option 省略時の既存 CLI 契約と hot-path 性能を維持する。

