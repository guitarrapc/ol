# 既存ライセンスチェッカーから ol へ反映すべきこと

## この文書の位置付け

[既存 OSS ライセンスチェッカーの実装分析](../references/existing_license_checkers.md)を、2026-07-28 時点の ol の実装・設計と比較し、**ol に足りず、かつ ol で実装する価値が高いもの**を優先順位順に整理する。

これは仕様や実装の commitment ではない。採用する項目は、WHAT / WHY を specs へ追加した後、個別の test-first implementation plan に分ける。既存の [backlog](../backlog.md) と重なる項目もあるが、この文書では参照実装から得た根拠、依存関係、実装しない範囲まで具体化する。

## 比較時点の ol の強み

ol は参照ツールから無差別に機能を足す必要はない。現在の ol には既に次の強い基盤がある。

- CycloneDX / SPDX と複数 package manager の **resolved dependency input** を共通 inventory にし、root / direct / transitive と graph を保持する。
- npm、NuGet、Cargo、Go、PyPI、Packagist の registry metadata と GitHub License API を、入力上の宣言とは別 evidence として収集する。
- candidate を一つに上書きせず、source / kind / raw / normalized / status / provenance とともに保持する。
- SPDX identifier と expression を active SPDX data で厳密に検証する。
- 複数 evidence の一致、conflict、unknown、ambiguous、invalid、error を明示する。
- `scan` の事実収集と `check --allow-licenses` の policy enforcement を分離し、`AND` / `OR` / `WITH` を fail-closed で評価する。
- external I/O を bounded concurrency、deduplication、versioned cache、explicit refresh で制御し、結果順序を deterministic に保つ。

根拠:

- [設計](../DESIGN.md)
- [入力 registry](../../../src/Ol.Core/DependencyInputRegistry.cs)
- [evidence data](../../../src/Ol.Core/Licensing/LicenseCandidate.cs)
- [reconciliation](../../../src/Ol.Core/Licensing/LicenseReconciler.cs)
- [allow policy](../../../src/Ol.Core/Licensing/LicenseAllowPolicy.cs)
- [source repository evidence specification](../specs/source.md)

このため、参照ツールにある次の挙動は ol へ取り込む対象にしない。

- package metadata の先頭 license だけを使う。
- SPDX expression を raw string の exact / substring comparison で判定する。
- confidence の低い heuristic 推定を確定 license として evidence へ上書きする。
- package / file ごとに無制限の task / goroutine を作る。
- installed directory だけを inventory の正とし、resolved graph を失う。
- ORT の plugin platform や rule DSL を規模ごと模倣する。

## 優先順位の基準

順位は次を総合して決めた。

1. **正しさ**: unknown や誤判定を減らしつつ、推測を確定値へ昇格させないか。
2. **監査可能性**: なぜその結果になったか、後から再現・再 review できるか。
3. **利用者価値**: CI の合否だけでなく、実際の再配布 compliance 作業を短縮するか。
4. **ol との適合**: typed evidence、完全 graph、strict SPDX、side-effect boundary を活かせるか。
5. **費用と危険**: network / disk I/O、false positive、schema compatibility、ecosystem 固有処理を制御できるか。

| Rank | Priority | 提案 | 価値 | 実装費 | 主な依存 |
|---:|---|---|---|---|---|
| 1 | P0 | package / source の legal file evidence と厳密な本文同定 | 非常に高い | 高 | evidence schema、artifact boundary |
| 2 | P0 | fingerprint 付き curation / review | 非常に高い | 中 | Rank 1 と既存 candidate |
| 3 | P1 | versioned policy file と監査可能な exception | 高い | 中 | Rank 2 の identity model |
| 4 | P1 | 保存済み scan report の再評価と evidence diff | 高い | 中 | report input schema |
| 5 | P1 | NOTICE / license bundle の生成 | 高い | 中〜高 | Rank 1、Rank 3 |
| 6 | P2 | dependency path 付き SARIF / CI annotation | 中〜高 | 中 | graph path、input location |
| 7 | P2 | ecosystem coverage の優先拡張 | 中 | ecosystem ごとに中 | registry / input adapter |
| 8 | P3 | source tree 全体の file-level scan | 条件付きで高い | 非常に高い | Rank 1〜4 |

## Rank 1 / P0: package / source の legal file evidence と厳密な本文同定

### 足りていないこと

現在の ol は SBOM / package input の宣言、package registry metadata、GitHub License API の検出結果を evidence にできる。一方、次を直接 evidence にできない。

- local package cache や package archive 内の `LICENSE` / `COPYING` / `NOTICE`。
- source archive / repository root の legal files。
- file content hash、path、実際の license text。
- file content と SPDX template の照合結果。

[source specification](../specs/source.md)は、GitHub API が unknown を返しても arbitrary text を推測解析しない方針である。この安全性は維持すべきだが、**監査可能な template matching** まで永続的に拒否する理由にはならない。

### 参照実装から学ぶこと

- go-licenses: package directory から module root へ legal file を探索し、同じ file を package 間で共有する。
- licensed / LicenseFinder: installed artifact の metadata と legal files を別々に保持する。
- nuget-license: `.nupkg` 内の declared file を読み、SPDX matching guideline を意識した template matcher を使う。
- ORT: declared と detected を別 fact として保持し、file path / line / provenance を失わない。

### ol での価値

- registry declaration が空の Go / NuGet / npm package を解決できる。
- GitHub でない source、private package、offline cache でも evidence を増やせる。
- package version に対応する配布 artifact そのものを確認できる。
- 後続の NOTICE / license bundle に必要な原文を得られる。
- GitHub API の一語の classification より詳しい監査証跡を残せる。

### 推奨する最小 scope

最初から source tree 全体を scan しない。次の順で範囲を制限する。

1. package manager が指定する license file
   - 例: NuGet `.nuspec` `license type=file`。
2. local package archive / cache の root legal files。
3. source archive / repository root legal files。

探索対象は exact / bounded な file name pattern に限定する。candidate には最低限次を持たせる。

- evidence kind: `package-artifact` または `source-file`。
- package / repository identity と version / ref。
- archive / file path。
- content SHA-256。
- byte length と text availability。
- matcher 名・version。
- matched SPDX expression、confidence ではなく match class。
- no-match / multiple-match / truncated / unreadable の明示状態。

本文同定は SPDX template の確定的な matcher を第一候補にする。heuristic regex や類似度だけの match は `Matched` にせず、別の review-required candidate 状態にする。

### performance / safety 制約

- inventory を完成してから artifact target を deduplicate する。
- `(ecosystem, package, version, artifact hash)` または provenance identity ごとに一度だけ読む。
- network、archive、file scan は別々の bounded concurrency とする。
- archive entry 数、展開後 byte 数、1 file byte 数、探索 depth を上限化する。
- zip slip、symlink escape、path traversal を拒否する。
- bytes を一度 hash / normalize し、同一 content は matcher result を再利用する。
- completion order ではなく component order で result を merge する。
- report への本文埋め込みは明示 option とし、既定では hash / path / result だけにして schema 膨張と source disclosure を避ける。

### 完了条件

- declaration unknown の fixture が local legal file から SPDX ID を得る。
- declared と detected が異なる fixture は conflict を保持し、どちらも消えない。
- no-match / multiple-match は確定 license に昇格しない。
- 同一 artifact を参照する複数 component で scan が一度だけ行われる。
- malicious / oversized archive を bounded failure として component evidence に残す。
- cache hit / miss と並列完了順にかかわらず report が byte-stable である。

## Rank 2 / P0: fingerprint 付き curation / review

### 足りていないこと

ol は `Concluded` という input acknowledgement を扱えるが、これは SPDX document producer が供給した fact であり、ol 利用者が project 固有に行う curation workflow ではない。現状は次の状況を安全に解決できない。

- upstream metadata が typo / deprecated alias / custom string である。
- declared と detected が conflict するが、人間が正しい解釈を確認済みである。
- custom license を `LicenseRef-*` として管理したい。
- false positive を version / evidence 内容に限定して例外化したい。

単純な `package -> concluded license` override は、upstream の license 変更後も古い判断を通し続けるため危険である。

### 参照実装から学ぶこと

- licensed: review 後に normalized license text が変わると再 review を要求する。
- license-checker-rseidelsohn: package semver、license file、text range、SHA-256 checksum で clarification を固定し、unused clarification を error にできる。
- LicenseFinder: who / why / timestamp / version を decision history に残す。
- ORT: raw evidence、curation、concluded license、resolution を別 data として保持する。

### ol での価値

- conflict / ambiguous を「証拠を消さずに」運用可能な concluded result へ進められる。
- project 固有例外が command line の暗黙知にならない。
- package update、file change、registry correction 時に再 review を強制できる。
- policy exception と factual correction を区別できる。

### 推奨する data model

versioned curation file に、少なくとも次を明示する。

- component selector:
  - canonical purl を優先。
  - exact version または明示 version range。
  - 必要時のみ source ref / input format。
- action:
  - normalized claim mapping。
  - concluded SPDX expression。
  - candidate exclusion ではなく finding resolution。
- audit:
  - reason。
  - reviewer。
  - reviewed-at。
- guard:
  - 対象 candidate source / kind。
  - expected raw value hash または legal file content hash。
  - expected normalized expression / status。

適用後も original candidates と pre-curation reconciliation を report に保持し、curation を独立 evidence / decision として出す。

### 適用規則

- component selector が複数 component に曖昧 match したら command error。
- guard が一致しなければ curation を適用せず `stale-curation` として fail closed。
- 一度も使用されなかった entry は warning、strict option では error。
- concluded expression 自体も active SPDX data で厳密に validation する。
- policy exception と license conclusion を同じ action にしない。
- curation 前の `conflict` / `unknown` は raw report から消さない。

### 完了条件

- exact purl / version / evidence hash にだけ curation が適用される。
- version または evidence content が変わると自動的に stale になる。
- original、curated、effective の三つを JSON で追跡できる。
- unused / ambiguous / duplicate curation を deterministic に報告する。
- curation なしの既存 scan output と hot path に不要な allocation / I/O を追加しない。

## Rank 3 / P1: versioned policy file と監査可能な exception

### 足りていないこと

現在の `check --allow-licenses` は strict SPDX allow-list と unresolved status の fail-closed 判定として良い最小実装である。一方、実際の compliance policy で必要になる次がない。

- deny / review / classification。
- package + version に限定した exception。
- dependency scope / production / development / distribution context。
- notice、source disclosure、copyleft review 等の obligation category。
- exception の reason、owner、期限。
- repository に commit できる versioned policy file。

これは既存の [license check plan](plan_license_check.md) でも初期 scope 外と明記されている。

### 参照実装から学ぶこと

- LicenseFinder: permit / restrict と個別 package approval を分離する。
- licensed: allowed、reviewed、ignored を分け、review を version-aware にする。
- go-licenses: license ID とは別に配布上の category を持つ。
- ORT: license classification と rule violation severity を分離する。

### ol での価値

- command line の長い allow-list を repository policy にできる。
- package 例外を全 version に誤適用せず、監査情報を残せる。
- 「使ってよいか」だけでなく「NOTICE が必要か」「source 提供 review が必要か」を後続 artifact へ渡せる。
- 同じ scan fact に対して製品 / distribution profile ごとの policy を変えられる。

### 推奨する最小 scope

最初の policy file は宣言的 data に限定し、DSL / plugin / arbitrary code execution を導入しない。

- schema version。
- named policy profile。
- SPDX allow / deny identifiers。
- license classification:
  - `allowed`
  - `denied`
  - `review`
  - `notice-required`
  - `source-disclosure-review`
- package exception:
  - exact purl。
  - exact version または明示 range。
  - action。
  - reason / owner / expires。
- unresolved status の扱い。ただし既定は現在どおり fail closed。

`AND` / `OR` / `WITH` は既存 evaluator を共有し、policy file 側で別 parser を作らない。license choice が必要な `OR` は、単に「どれか allowed だから pass」だけでなく選択した branch を result に記録できるようにする。これは後の NOTICE 生成で必要になる。

### 完了条件

- CLI allow-list と policy file の precedence / conflict が一意に定義される。
- exception は package identity と version を外れると適用されない。
- expired / unused exception を report できる。
- policy result に matched rule / exception と reason が残る。
- unresolved の既定 fail-closed を維持する。
- policy parse failure は violation exit 1 ではなく command error exit 2 になる。

## Rank 4 / P1: 保存済み scan report の再評価と evidence diff

### 足りていないこと

現在の `check` は `scan` と同じ pipeline を一度だけ実行するため、1 command 内の二重処理はない。しかし policy を変更するたびに input parsing、registry / source enrichment を再実行する。保存済み JSON report は output contract であり、policy input として明示的に versioning / validation されていない。

また、前回と今回で次が変わった package を専用に示す diff がない。

- component の追加 / 削除 / version 変更。
- evidence source / raw / normalized / hash の変更。
- reconciliation status の変更。
- curation / review の stale 化。
- policy result の変更。

### 参照実装から学ぶこと

- licensed: dependency metadata と reviewed cache を repository に保存し、status を高速に再評価する。
- LicenseFinder: report diff を独立 output にする。
- ORT: analyzer / scanner / evaluator / reporter の中間 result を保存し、工程を再利用する。

### ol での価値

- network なしで policy review と CI 再評価ができる。
- registry / source の時間変化から policy 結果を切り離せる。
- pull request で「license に関係する変化だけ」を reviewer に見せられる。
- curation guard と組み合わせ、再 review 対象を自動抽出できる。

### 推奨する実装境界

- 現在の renderer JSON をそのまま parser input にせず、`ScanResult` の永続 input contract を versioned schema として定義する。
- schema major version、SPDX data version、tool version、input identity / hash、collection settings を検証する。
- report input では enrichment を暗黙に再実行しない。
- `check --report <file>` または同等の明示的 input mode にする。
- diff は component identity の stable key と evidence fingerprint を使う pure transform とする。
- report に secret、token、absolute cache path を入れない既存 privacy boundary を維持する。

### 完了条件

- 同じ persisted result と policy から byte-stable な同一判定を得る。
- schema version / malformed / partial report を command error にする。
- report input 時に network request が発生しない。
- added / removed / updated / evidence-changed / policy-changed を区別する。
- diff の順序が component order と change kind で deterministic である。

## Rank 5 / P1: NOTICE / license bundle の生成

### 足りていないこと

ol は text / Markdown / JSON で license facts を報告できるが、製品へ同梱するための次の artifact を生成しない。

- third-party notices。
- dependency ごとの license text。
- attribution / copyright。
- selected license branch。
- source disclosure 対象一覧または source bundle manifest。

license ID の report だけでは再配布義務を履行できない。

### 参照実装から学ぶこと

- go-licenses `save`: license category に応じ、license / notice / source を bundle する。
- licensed `notices`: reviewed cache の legal contents から NOTICE を作る。
- nuget-license: package license files を download / 保存する。
- ORT reporter: 同じ resolved facts から NOTICE、SPDX、CycloneDX 等を作る。

### ol での価値

- scan / check の結果を実際の release artifact へ接続できる。
- 「検査は通ったが原文がない」という最後の手作業を減らせる。
- deterministic artifact により release 間 diff と review が容易になる。

### 推奨する前提と scope

Rank 1 の legal file text / hash と Rank 3 の policy classification / license choice を前提にする。原文が取れていない状態で SPDX template から汎用本文を補うと、package が付した追加条項や NOTICE を落とすため、既定動作にしてはならない。

最初の成果物は次に限定する。

- deterministic `THIRD-PARTY-NOTICES`。
- component identity、version、source URL、effective expression。
- 取得した original license / notice text と provenance。
- text がない component の明示的 incomplete list。
- policy が選択した `OR` branch。

source code 自体の再配布 bundle は license obligation と build provenance の設計が必要なため、後続 phase とする。

### 完了条件

- 同じ scan result / policy から byte-stable な artifact ができる。
- package name collision、同一 text dedup、line ending / encoding を deterministic に扱う。
- original text と generated separator を区別できる。
- missing text、custom terms、multiple license、unselected `OR` を黙って落とさない。
- artifact entry から scan evidence へ逆引きできる。

## Rank 6 / P2: dependency path 付き SARIF / CI annotation

### 足りていないこと

現在の `check` text は違反 component と reason を全件出すが、repository 上の direct declaration location や、transitive violation を導入した root / direct path を CI annotation として出さない。

### 参照実装から学ぶこと

license-checker-php は transitive violating package を top-level Composer dependency へ逆引きし、SARIF location を `composer.json` の direct dependency 行へ置く。違反そのものが transitive でも、利用者が修正可能な場所を示す点が有用である。

### ol での価値

- GitHub code scanning / pull request UI に policy violation を載せられる。
- transitive package 名だけでなく、upgrade / remove すべき direct dependency path を示せる。
- 完全 graph を既に持つ ol の優位を output へ活かせる。

### 推奨する scope

- SARIF rule は violation kind ごとに stable ID を持つ。
- result には component purl、license status / expression、policy reason を入れる。
- dependency graph から shortest root-to-component path を deterministic に選ぶ。
- input parser が manifest line / JSON pointer を確実に保持できる場合だけ physical location を付ける。
- 位置がない場合は偽の line 1 を作らず、logical location と dependency path を出す。
- 同じ transitive component に複数 root path がある場合は、代表 path と path count を出すか全 path の上限を決める。

### 完了条件

- SARIF schema validation が通る。
- direct / transitive / multiple-path / no-location fixture を持つ。
- check text と SARIF で violation 集合が一致する。
- absolute input path、cache path、token を出力しない。

## Rank 7 / P2: ecosystem coverage の優先拡張

### 足りていないこと

現在の resolved input は CycloneDX、SPDX、NuGet、npm、pnpm、Yarn、Cargo、Go、pip、Composer を中心とする。package metadata provider も npm、NuGet、Cargo、Go、PyPI、Packagist に限られる。

参照ツール群と比較すると、特に次が不足する。

- JVM: Maven / Gradle。
- Ruby: Bundler。
- Apple: SwiftPM / CocoaPods。
- Dart / Flutter: Pub。
- Erlang / Elixir、Haskell 等。

### 優先案

1. **Maven / Gradle**
   - 利用規模が大きく、Maven Central / POM に license と SCM metadata がある。
   - multi-module、scope、dependency management、Gradle variant が難所。
2. **Bundler**
   - `Gemfile.lock` と gemspec / installed gem の license metadata が比較的明瞭。
3. **SwiftPM**
   - package graph と Git repository provenance を取りやすい一方、registry metadata より source legal files が重要。
4. **Pub**
   - lockfile と pub.dev metadata を利用できる。

### 採用条件

ecosystem 数だけを増やさない。各 ecosystem は次を一組で提供する。

- resolved graph と root / direct / transitive semantics。
- canonical purl と source identity。
- dependency scope / variant の audit data。
- registry metadata provider または unsupported を明示する evidence。
- real fixture、golden report、deduplicated provider scheduling test。
- ecosystem 固有 parser の hot-path benchmark。

LicenseFinder / licensed の breadth は参考になるが、package manager ごとに license fidelity が違う点も同時に学ぶべきである。

## Rank 8 / P3: source tree 全体の file-level scan

### 足りていないこと

ORT のような source scanner は root license だけでなく、subdirectory、個別 source file header、vendored code、snippet の license finding を検出できる。ol はこの層を持たない。

### 価値がある条件

- repository root license と vendored / generated / submodule code の license が異なる。
- package metadata が repository 全体の license を表せない。
- compliance review で file-level location が必須。
- source archive を release provenance として固定できる。

### なぜ P3 か

- file 数に比例する CPU / I/O と巨大な result が発生する。
- false positive、generated file、test / example、vendored source、path exclusion の設計が必要になる。
- scanner engine / dataset の更新が結果再現性へ影響する。
- copyright、snippet、license text location を含む新しい domain model が必要になる。
- ol の「高速な transitive dependency license resolution」という中心価値から大きく広がる。

### 推奨する進め方

Rank 1 の bounded root legal-file scan を実運用し、解決できない component と監査要求を計測してから判断する。実装する場合も scanner を core に組み込まず、次の narrow boundary から始める。

- provenance-fixed source archive を input とする。
- external scanner の versioned result を typed evidence として ingest する。
- raw source scanning と reconciliation / policy を分離する。
- path exclusion / finding curation を data として定義する。
- scanner process、CPU、memory、result size を上限化する。

## 推奨ロードマップ

### Phase A: Evidence completion

1. legal file evidence schema と archive safety contract を仕様化する。
2. NuGet の declared embedded license file で end-to-end の最小縦切りを作る。
3. content hash cache と SPDX template matcher を追加する。
4. package root / source root へ bounded に拡張する。

検証:

- declaration-only / file-only / agree / conflict / no-match / malicious archive。
- deduplicated I/O、bounded concurrency、deterministic output。
- scan benchmark と allocation regression。

### Phase B: Human decision without evidence loss

1. curation file schema と component / evidence identity を仕様化する。
2. concluded license、claim mapping、resolution を別 action として実装する。
3. hash guard、stale / unused detection、audit report を追加する。

検証:

- version / content change で必ず再 review になる。
- curation を外すと元 result が完全に復元される。
- raw evidence と effective result が JSON で同時に見える。

### Phase C: Reproducible policy and deliverables

1. versioned declarative policy file。
2. persisted scan result input と diff。
3. effective license choice。
4. NOTICE / license bundle。
5. SARIF。

検証:

- offline policy re-evaluation。
- policy / evidence / artifact の traceability。
- CLI exit 0 / 1 / 2 の既存契約維持。
- golden report / SARIF / NOTICE の deterministic diff。

### Phase D: Coverage based on evidence

利用者需要と unknown / unsupported telemetry ではなく、fixture と issue の集計に基づいて Maven / Gradle 等を順次追加する。file-level source scan は root legal-file evidence で解決しない実例が十分に集まった場合だけ個別計画にする。

## 最優先で作るべき個別計画

次に一つだけ implementation plan を作るなら、**「NuGet embedded license file を package-artifact evidence として取り込み、SPDX template で同定する」**を推奨する。

理由は次のとおりである。

- current ol に NuGet resolved graph、registry provider、cache、candidate reconciliation が既にある。
- `.nuspec license type=file` は探索 heuristic を必要とせず、package author が対象 file を明示している。
- nuget-license という具体的な参照実装がある。
- archive safety、content hash、text provenance、template match、conflict preservation を小さい ecosystem scope で一通り設計できる。
- この縦切りが Rank 2 の review guard と Rank 5 の NOTICE にそのままつながる。

この最小縦切りでは、source repository 全体、generic recursive file discovery、policy file、NOTICE generation を同時に実装しない。まず evidence type と safety / determinism を固定し、その結果を見て次の計画へ進む。
