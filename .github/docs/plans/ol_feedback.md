# DESIGN から逆算した ol の不足と実装順序

## この文書の位置付け

[ol のアーキテクチャ](../Architecture.md)が利用者へ約束している体験を起点に、**まだ果たされていない約束**を特定し、それを果たすために何を支払うかを整理する。[既存 OSS ライセンスチェッカーの実装分析](../references/existing_license_checkers.md)は、その支払いの相場を知るための参照であって、機能一覧の出典ではない。

これは仕様や実装の commitment ではない。採用する項目は WHAT / WHY を specs へ追加した後、個別の test-first implementation plan に分ける。[backlog](../backlog.md) と重なる項目（policy categories、SARIF、Maven、source archive inspection）については、この文書が参照実装から得た根拠、依存関係、支払う代償、実装しない範囲まで具体化する。

方法は次の順とする。参照ツールにある機能から出発すると、ol の配布形態や既存の強みを無視した項目が混ざるため、順序を逆にしている。

1. DESIGN が約束している体験を軸として並べる。
2. 各軸について、現在の実装が**現物として**どこまで届いているかを確認する。
3. 届いていない差分だけを不足とする。
4. 各不足について参照実装が支払っている代償を見積もり、順序を決める。

## 現在の ol（現物確認）

ranking の前提になるため、推測ではなく登録済みの実体で確認する。

| 能力 | 実体 | 根拠 |
|---|---|---|
| resolved dependency input | CycloneDX、SPDX、NuGet assets、npm、pnpm、Yarn Classic / Berry、Cargo、Go module graph、pip inspect、Composer、Bundler、Maven の 13 形式と collection | [DependencyInputRegistry.cs](../../../src/Ol.Core/DependencyInputRegistry.cs)、[DependencyInventory.cs](../../../src/Ol.Core/DependencyInventory.cs) |
| registry metadata provider | npm、NuGet、Cargo、Go、PyPI、Packagist、RubyGems、**Maven** の 8 種 | [OlDefaults.cs](../../../src/Ol.Core/OlDefaults.cs) |
| source repository evidence | GitHub License API のみ。repository / ref / path / blob SHA / http status を保持 | [GitHubLicenseApiClient.cs](../../../src/Ol.Core/GitHub/GitHubLicenseApiClient.cs)、[specs/source.md](../specs/source.md) |
| evidence 保持 | source / kind / raw / normalized / status / deprecated / warnings / typed provenance | [LicenseCandidate.cs](../../../src/Ol.Core/Licensing/LicenseCandidate.cs) |
| reconciliation | matched / conflict / unknown / ambiguous / invalid / error の 6 状態 | [LicenseReconciler.cs](../../../src/Ol.Core/Licensing/LicenseReconciler.cs) |
| SPDX | 版を固定した**識別子**データ。本文・template は持たない（生成物 22KB） | [SpdxGeneratedLicenseData.g.cs](../../../src/Ol.Core/Generated/SpdxGeneratedLicenseData.g.cs)、[specs/spdx.md](../specs/spdx.md) |
| policy | `check --allow-licenses` の SPDX 識別子 allow-list。`AND` / `OR` / `WITH` を fail-closed 評価。**承認 baseline**と**永続 report 評価**を持つ | [CheckCommands.cs](../../../src/Ol/CheckCommands.cs)、[LicenseAllowPolicy.cs](../../../src/Ol.Core/Licensing/LicenseAllowPolicy.cs)、[LicenseBaseline.cs](../../../src/Ol.Core/Licensing/LicenseBaseline.cs) |
| 出力 | `scan` が stdout に text / Markdown / JSON。`check` は text と **SARIF**。**`diff`** が text / JSON | [ScanCommands.cs](../../../src/Ol/ScanCommands.cs)、[SarifRenderer.cs](../../../src/Ol/SarifRenderer.cs)、[DiffCommands.cs](../../../src/Ol/DiffCommands.cs) |
| 永続 report の再利用 | canonical JSON を入力契約として兼用。`check --report` は parse も network も行わない | [ScanReportReader.cs](../../../src/Ol.Core/Reporting/ScanReportReader.cs) |
| cache | TTL なしの永続 cache。`--refresh` でのみ無効化 | [specs/cache_format.md](../specs/cache_format.md) |

前版のこの文書は Bundler / RubyGems を未対応として扱っていたが、実装済みである。ecosystem の不足は上表から取り直すこと。太字は本計画で追加した能力。

## 約束と充足度

[参照文書の評価軸](../references/existing_license_checkers.md#ol-の設計目標から見た横断評価)に沿って、DESIGN の約束と現状を対応させる。

| 軸 | DESIGN の約束 | 現状 | 差分 |
|---|---|---|---|
| A. 数え落とさない | 完全な inventory と graph を先に確定し、filter は view | 13 input が root / direct / transitive と context 別 graph を保持。SARIF が root からの経路を出す | **ほぼ果たされている**。残るのは ecosystem 数（Gap 7） |
| B. 判定の理由が残る | evidence を上書きせず provenance 付きで保持 | 3 系統の typed evidence、6 状態、警告を保持。承認は evidence を消さず violation だけを除く | **果たされている**。事実の訂正（curation）は未実装だが合否には不要（Gap 2） |
| C. 同じ入力なら同じ結果 | 版を固定した SPDX、TTL なし cache、決定的順序 | 識別子検証の範囲で成立。永続 report の再評価も byte 一致 | **果たされている**。本文同定を足すと崩れる（Gap 4） |
| D. 止まったときに前へ進める | 「policy が何を禁じるかを決める」 | allow-list に加え、証拠指紋つきの承認 baseline を持つ | **埋まった**（Gap 1） |
| E. 検査の次へ届く | **非目標**。DESIGN が再配布成果物の生成を明示的に除外している | license ID の報告、SARIF、report diff | 差分ではない。この軸は参照実装の観察であって ol の約束ではない |
| F. 小さく速いままでいる | 単一 native AOT バイナリ | 維持。renderer は 0 allocation、baseline 未使用経路に追加コストなし | Gap 4 がここを削る方向に働く |

当初この表で最大の穴だった policy 側（D）は Gap 1 で埋まり、Gap 3 と Gap 6 が B を補強した。**残る本質的な差分は C 軸と F 軸を代償に要求する Gap 4 だけ**であり、だからこそ仕様決定を先に置いている。

## 不足の一覧と順序

番号は識別子であって順位ではない。実施順は優先度に従い、`Gap 1 → Gap 3 → Gap 6 → Gap 4 → Gap 2` となる。Gap 2 は当初 P0 だったが、Gap 1 の設計確定により合否判定には不要と分かったため P2 へ後退した（[経緯](#gap-2--p2-事実の訂正curation)）。

**進捗**: Gap 1・Gap 3・Gap 6 は実装済み。残る Gap 4 は未決の仕様課題2件が解決するまで着手しない（下記）。Gap 2 は Gap 4 と独立に、判定精度だけを根拠に評価する。Gap 7 と Gap 8 は方針どおり据え置き。

再配布成果物（`THIRD-PARTY-NOTICES`、license bundle）は Gap ではない。[Architecture の非目標](../Architecture.md#non-goals)であり、この文書の対象から外れる。番号は識別子なので Gap 5 は欠番のままとし、既存の参照を書き換えない。

### Gap 1 / P0: fail-closed の逃げ道がなく、既存製品へ導入できない — **設計確定**

**約束**: [decision-policy-separation](../Architecture.md#decision-policy-separation) — 同じ事実に対し、組織ごとに異なる policy を適用できる。

**現状で起きること**: `check` は unresolved を無条件で違反にする。実在の依存集合には必ず解決不能な component が残り、利用者に打つ手のないものが大半を占める（registry が license を書いていない、GitHub 以外、private package）。想定する二つの利用場面は、この点で形が違う。

- **OSS の PR に違反パッケージ**: baseline は既に green で差分は数個。新規パッケージが unresolved なら本当に止めるべきで、逃げ道は要らない。**今日の CLI で完結している。**
- **プロプライエタリ製品の出荷前 GPL 検出**: 初回実行で unresolved が数十件出る。allow-list に何を足しても消えず、**探し物である GPL 1 件がそこに埋もれる**。

後者だけが解決を要する。そして deny-list はこれを解決しない。deny にすれば静かになるが、知らない license を見なくなり検出力が落ちる。見逃しのコストが極大で誤警報のコストが小さい以上、fail-closed な allow-list を捨てる理由はない。**allowed に絞る判断は維持する。**

**採用した解**: 追加する概念を一つに絞り、**ol が生成する baseline** だけを入れる。policy file、profile、classification、deny-list、scope policy はいずれも採用しない。仕様は [cli.md の baseline 契約](../specs/cli.md#contract-policy-baseline)に確定済み。

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

通常の評価と baseline 更新は、どちらも同じ canonical report を入力にする。ケース2 だけが `--baseline` と `--update-baseline` を追加する。

**確定した規則と、その理由**:

| 決定 | 理由 |
|---|---|
| 承認できるのは `unknown` / `ambiguous` / `conflict` / `invalid` のみ | `matched` は policy の話なので allow-list へ。`error` は修理すべき状態で、承認すると一時的な障害が承認として固定される |
| 候補のどれかが allow-list 外へ正規化されるなら承認不可 | GPL を conflict 経由で先送りできなくする。**適用時にも判定する**ので allow-list を狭めれば過去の承認も無効になる |
| `--update-baseline` は全置換 | 追記式は stale が溜まり、掃除のための unused 検出と削除フラグが要る。全置換なら「その時点のスナップショット」の一文で説明が済む |
| 明示指定のみ、暗黙の発見をしない | CI のコマンド行だけで承認の有無が分かる |
| entry は raw 値を持つ | PR の diff だけで承認の是非を判断できる。これが無いと結局 `ol scan` を回して突き合わせることになる |
| fingerprint は status と `(source, kind, raw)` から取る | 版が上がる・registry が記述を変える・repository の license file が変わると承認が自動的に外れる |
| timestamp を書かない | 無変更の再生成で diff が出ない。誰がいつ承認したかは git が持っている |
| reason 欄を持たない | 生成物なので手書き欄を作らない。全置換で消える欄は最初から作らない |

**この設計が保証すること / しないこと**: 正規化できる禁止ライセンスは構造的に承認できない。正規化できない表記（`GPLv3` 等）は strict normalization が推測を拒む以上判定材料がなく、raw が diff に出ることで可視化されるに留まる。**「GPL は絶対に通らない」ではなく「ol が識別できる GPL は通らない、識別できないものは PR で必ず目に入る」**が正確な表現である。

**副産物**: `--update-baseline` を実行しても禁止ライセンスは消えない。したがって初回導入は 1 コマンドで済み、その結果 exit 2 で落ちたなら**それが探し物**である。

**この設計で消えた設計負債**: 新しい status を導入しない（承認は violation 集合から除外するだけで、component は unresolved のまま report に残る）。policy 入力が 2 つだけなので precedence の定義が不要。curation を持たないので stale-curation 状態も不要。

**残る懸念**: `error` を承認対象から外したため、到達不能な private registry の package が恒久的に詰む経路が理論上ある。実務では private package は自前 SBOM が license を持つため `error` にならないはずである。刺さった場合は「恒久的な 404 のみ承認可」のような狭い規則で対処し、この決定は覆さない。

### Gap 2 / P2: 事実の訂正（curation）

**現状で起きること**: upstream の typo、deprecated alias、custom string、確認済みの conflict について、「正しい license はこれである」と記録する手段がない。

**Gap 1 の決定による位置付けの変化**: 当初は Gap 1 と対で P0 に置いていたが、**未解決の証拠については合否判定に不要**であることが分かったため後退させた。ケース1 でもケース2 でも、利用者が必要としているのは「見た、受け入れた」であって「実際の license 値はこれだ」ではない。

**再配布成果物を非目標にしたことによる再評価**: 以前はこの Gap を NOTICE 生成の前提として保持していたが、その下流成果物が消えたため、curation は**判定精度だけを根拠に評価する**。そして判定精度の観点では、baseline が吸収できない経路が一つ残る。upstream が事実と異なる license を宣言し、それが allow-list 外へ正規化される場合である。status は `matched` になるので baseline は承認できず（`matched` は承認対象外）、allow-list へ足せば本当に禁止したい license まで通る。**利用者に打つ手がない偽陽性**であり、「curation は合否に不要」という当初の結論はこの一点だけ成立しない。

したがって curation は「未解決の証拠を前へ進める手段」ではなく「upstream が誤っているときの唯一の出口」として位置付ける。実装は、この経路が実例として観測されてから着手する。baseline の schema version があるので、同じファイルに action を足す形で後から拡張できる。使う予定のフィールドを今から予約しない。

**着手時に引き継ぐ設計**: [参照文書 D 軸](../references/existing_license_checkers.md#d-止まったときに前へ進める)の三つの出口（事実の訂正 / 結論の確定 / 方針の例外）を混ぜないこと。guard は既存の candidate `Raw` と GitHub License API の blob SHA（`SourceRepositoryEvidence.LicenseSha`）で成立する。registry evidence 側だけ `CacheKeySha256`（cache key であって内容 hash ではない）なので content hash の追加が要る。適用後も original candidates と curation 前の reconciliation を report に残す。

### Gap 3 / P1: 「再 scan なしの再評価」 — **実装済み**

**約束**: DESIGN は「同じ事実 report を、依存を再 scan したり証拠を再収集したりせずに、異なる policy で評価できる」と書いていたが、実装が追いついていなかった。

**確定した設計判断**: **canonical JSON をそのまま入力契約にした。**（未決だった「schema 一枚か二枚か」の結論）。report JSON は既に `schemaVersion` と `metadata.input` を持っており、二枚に割る理由がなかった。利点は二つある。利用者が既に持っている report をそのまま policy 入力にできること、そして出力 schema と入力 schema が乖離し得ないこと。`check` はこの report を必須入力とし、収集経路を持たない。scan が生成した report を check が評価できることは [`CliCheckTests`](../../../tests/Ol.Tests/CliCheckTests.cs) で CLI レベルに検証する。

**過大主張しなかったこと**: 「network なしで再評価できる」は cache により既に成立していた。実際の利得は parse コスト、cache dir を持ち回れない環境での可搬性、registry の時間変化からの隔離、そして **diff**（本命）である。

実装: [`ScanReportReader`](../../../src/Ol.Core/Reporting/ScanReportReader.cs)、[`ScanReportDiff`](../../../src/Ol.Core/Reporting/ScanReportDiff.cs)、[`DiffCommands`](../../../src/Ol/DiffCommands.cs)。仕様は [cli.md](../specs/cli.md#contract-policy-report-input) と [cli.md の diff](../specs/cli.md#contract-diff)。

**確認できた挙動**: scan が生成した永続結果を異なる policy で再評価できる / schema version 不整合・破損・grouped report は exit 1 / check は input parsing も network request も行わない / diff は added・removed・version-changed・status-changed・license-changed・evidence-changed を区別する / diff の順序が決定的で JSON も byte 安定。policy evaluation は check に限定し、diff は SPDX data に依存しない。

`evidence-changed` は baseline と同じ fingerprint から導出しており、「結論は同じだが証拠が動いた」を検出する。

### Gap 4 / P1: 証拠の最後の一歩（原文）

**約束**: Design Goal 2 — 独立に帰属可能な証拠源から結論を組み立てる。

**現状で起きること**: registry declaration が空で GitHub でもない package は unknown のまま解決できない。

**この Gap を 4 位に置く理由**: 価値は高いが、**C 軸（同じ入力なら同じ結果）を壊す唯一の項目**であり、支払いが他より一桁重い。再配布成果物を非目標にしたことで、この Gap の正当化は「unknown が実際に何件減るか」だけになった。原文そのものを保持する価値は無く、**SPDX ID へ同定できないなら取得する理由もない**。取得と同定は分離せず、一体で判断する。[参照文書 C 軸](../references/existing_license_checkers.md#c-同じ入力なら同じ結果)の実測が示すとおり、本文同定は二つの新しい依存を必ず連れてくる。

1. **同定データの版**: 現在の SPDX データは識別子のみで 22KB。SPDX template matching は本文つきデータを要求し、nuget-license はこれを版固定の外部データ package として取り込んでいる。ol では [spdx.md の data resolution 契約](../specs/spdx.md)（明示ディレクトリ → user-managed → bundled）が `licenses.json` と `exceptions.json` しか要求していないため、**user-managed SPDX を選ぶと matcher が動かないか劣化する**。これは `decision-versioned-spdx` の違反であり、matcher の追加ではなくデータ契約の変更である。[Ol.Update](../../../src/Ol.Update) の生成範囲と native AOT の配布サイズにも波及する。
2. **package のローカル実体化**: 参照実装で本文を読めるものはすべて installed / restore 済みを前提とする。ol の入力は resolved graph なので、**同じ入力ファイルから機械ごとに異なる evidence が出る**状態へ移る。ORT だけが provenance を固定して自分で download することで解決している。

前版が最優先に推していた「NuGet embedded license file」も、この観点では最小の縦切りではない。`.nupkg` は NuGet global packages folder にしか存在せず、restore 済み環境という新しい前提を持ち込む。加えて現代の NuGet は `license type="expression"` が主流で `type="file"` は少数派であり、`type="file"` を選ぶ package は独自条項であることが多い。独自条項は SPDX template と no-match になり、規則どおり no-match は確定 license に昇格しない。つまり**この縦切りが最も高い確率で生む結果は「unknown のまま」**である。設計リスクを小さく固定する題材としては良いが、利用者価値の根拠にはならない。

**推奨する順序**: Gap 3 を先に済ませ、legal file evidence の投入前後を diff として計測する。「local legal file で unknown が実際に何件減ったか」を観測してから corpus 投資を判断する。

**着手前に必ず決めること**: 上記 1 の SPDX データ契約と、2 の「取得できなかったこと」の表現（evidence なしか、明示的な未取得状態か）。後者を決めずに実装すると [verification.md](../specs/verification.md) の golden report が機械依存で壊れる。

**推奨する最小 scope**（決定後）: 探索は exact / bounded な file name pattern に限定する。candidate は evidence kind、package / repository identity と version / ref、archive / file path、content SHA-256、byte length、matcher 名と version、match class、no-match / multiple-match / truncated / unreadable の明示状態を持つ。heuristic 類似度だけの match を `Matched` にしない。

**performance / safety 制約**: inventory 確定後に artifact target を deduplicate / provenance identity ごとに一度だけ読む / network・archive・file scan を別々の bounded concurrency にする / archive entry 数・展開後 byte 数・1 file byte 数・探索 depth を上限化する / zip slip・symlink escape・path traversal を拒否する / 同一内容は matcher 結果を再利用する / completion order ではなく component order で merge する / report への本文埋め込みは明示 option とする。

**完了条件**: declaration unknown の fixture が local legal file から SPDX ID を得る / declared と detected が異なる fixture は conflict を保持する / no-match と multiple-match が確定 license に昇格しない / 同一 artifact を参照する複数 component で読み取りが一度だけ行われる / malicious・oversized archive が bounded failure として evidence に残る / **artifact を取得できる機械とできない機械の差が契約どおりに表れる**。

### Gap 6 / P2: dependency path 付き SARIF — **実装済み**

`check --sarif <file>` で SARIF 2.1.0 を出力する。実装は [`SarifRenderer`](../../../src/Ol/SarifRenderer.cs) と [`DependencyPathResolver`](../../../src/Ol.Core/Reporting/DependencyPathResolver.cs)、仕様は [cli.md](../specs/cli.md#contract-policy-sarif)。

**scope の現実は事前の見立てどおりだった**: ol は manifest を読まないため physical location を出せない。偽の line 1 は作らず、logical location と dependency path を出す。ただし**「どの direct dependency が持ち込んだか」は完全な graph から復元できる**ため、参照実装（license-checker-php）の主眼である「利用者が修正できる場所を示す」は満たせた。

```text
pkg:npm/poison@2.0.0: license is not allowed (GPL-3.0-only).
Introduced through pkg:npm/direct@1.0.0 > pkg:npm/poison@2.0.0
```

**確認できた挙動**: violation kind ごとに安定した rule ID（OL0001〜OL0006） / direct・transitive・違反なし・承認済みの fixture を持つ / check text と SARIF で violation 集合が一致する / 絶対 path・cache path・token を出力しない / canonical JSON の完全な inventory / graph を復元するため、永続 report 経由でも dependency path を維持する。

### Gap 7 / P2: ecosystem coverage

現状は上表のとおり 13 input / 8 provider で、RubyとMaven resolved inputおよびMaven metadata enrichmentは対応済みである。参照ツール群と比べて残る主な空白は次になる。

1. **Gradle** — resolved inputは未対応。multi-module、dependency management、Gradle variantが公式から解決提供されておらず、サポート対象にすべきではないため。
2. **Apple: SwiftPM / CocoaPods** — package graph と Git provenance は取りやすいが、registry metadata より source legal files の比重が高く、Gap 4 の未決事項に依存する。
3. **Dart / Flutter: Pub** — lockfile と pub.dev metadata を使える。
4. Erlang / Elixir、Haskell、Conan 等。

**採用条件**（ecosystem 数だけを増やさない）: resolved graph と root / direct / transitive semantics、正規化 purl と source identity、scope / variant の audit data、registry provider または unsupported の明示、real fixture と golden report と重複排除 scheduling test、ecosystem 固有 parser の hot-path benchmark。これは [verification.md](../specs/verification.md) の「provider と `sandbox/ecosystems/manifest.json` は 1 対 1」という既存契約と一致する。

### Gap 8 / P3: source tree 全体の file-level scan

Gap 4 の bounded な root legal-file evidence を実運用し、解決できない component と監査要求を計測してから判断する。file 数に比例する CPU / I/O、false positive、path exclusion、scanner dataset の版による再現性、copyright / snippet を含む新しい domain model が必要になり、ol の中心価値から最も遠い。

実装する場合も core に scanner を組み込まず、provenance を固定した source archive を入力とし、外部 scanner の版付き結果を typed evidence として ingest する narrow boundary から始める。

## 実測: 5 ecosystem × 3 package のコーパス評価（2026-08-09）

Gap 4 が「着手前に必ず測る」としていた「unknown が実際に何件減るか」を測った。対象は NuGet（Dapper / ImageSharp / Serilog）、npm（axios / chalk / express）、PyPI（attrs / Flask / requests）、Cargo（clap / serde / tokio）、Go（cobra / gin / logrus）の 15 repository。各 repository を **SBOM 経路**（CycloneDX JSON）と **ecosystem-native 経路**（`project.assets.json` / `package-lock.json` / `pip inspect` / `cargo metadata` / `go list -m` + `go mod graph`）の両方で scan し、`--no-external-evidence`（= 入力の宣言だけを読む tool の上限）と通常 scan を比較した。合計 4,155 component。

| ecosystem | 経路 | component | 宣言のみ | ol |
|---|---|---:|---:|---:|
| cargo | native / sbom | 355 / 44 | 355 / 44 | 355 / 44 |
| npm | native / sbom | 1606 / 1446 | 1600 / 1440 | 1605 / 1445 |
| pypi | native / sbom | 111 / 111 | 83 / 90 | 99 / 102 |
| nuget | native / sbom | 39 / 335 | 0 / 252 | 30 / 265 |
| golang | native / sbom | 70 / 38 | 0 / 0 | 47 / 28 |

読み取れる事実は三つある。

**入力が license を持つかどうかは ecosystem が決めており、利用者は選べない。** `cargo metadata` は 100% を宣言し、`package-lock.json` はほぼ宣言する。一方 `project.assets.json` は **license 欄そのものを持たない**ため native 経路の宣言は 0 件であり、Go の resolved 出力（`go list -m` / `go mod graph`）も同じく 0 件である。この 2 つでは外部 evidence が「精度向上」ではなく**唯一の情報源**になる。

**SBOM は入力形式ではなく生成器の性能である。** 同じ repository でも SBOM 経路と native 経路で component 数と宣言率が食い違う（Go の SBOM は 38 component すべて license 宣言なし、NuGet の SBOM は 335 component 中 252 件を宣言）。SBOM が正しいのではなく、SBOM を作った tool が registry を引いたかどうかが表れている。ol は両方を同じ evidence model へ落とすので、入力の質に結果が引きずられない。

**未解決の残りは 4 種類の機構に収束する。** 135 件（重複除去 92 件）の内訳は `unsupported_source_repository` 65 / `license_not_recognized` 34 / `license_not_detected` 23 / `source_repository_unavailable` 13。**このうち Gap 4（本文同定）が解くのは `license_not_recognized` の 34 件だけ**である。`github.com/pmezard/go-difflib`、`github.com/pelletier/go-toml`、`russross/blackfriday`、`ServiceStack.*`、`xunit.abstractions` はいずれも標準 license に前文や条項差があるため GitHub が `other` と判定したもので、原文を読めば同定できる。残る 101 件は本文同定では解けない: `golang.org/x/*` と `google.golang.org/protobuf` は canonical repository が `go.googlesource.com` にあり（proxy の `Origin.URL` が実際にそう答える）、`gopkg.in/*` と `rsc.io/pdf` は proxy が `Origin` を持たない。**Gap 4 の投資対効果は、当初想定していたより小さい。**

### package manager ごとの結果と、期待との一致

「期待」は、その ecosystem の resolved 出力と registry が実際に何を述べているかから導いた事前予測である。**4 つは期待どおり、Go だけが期待を下回った。**

#### Cargo — 期待どおり。ol の外部照会は裏取りにしかならない

native 355 / SBOM 44 component、**宣言のみで 100%**、ol でも 100%。`cargo metadata` の `license` は SPDX expression そのものであり、`(MIT OR Apache-2.0) AND Unicode-3.0`、`Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT`、`Unlicense OR MIT` といった複合式もそのまま保持された。未解決 0 件。

含意は「Cargo では ol が要らない」ではなく、**Cargo では ol の価値が resolution ではなく policy 側（`check` / `diff` / baseline）に寄る**ということ。実測では 392 component が GitHub の default ref から候補を受け取っているが、**結論を 1 件も変えていない**。ここは将来 request を削れる余地がある（[版に紐づかない ref](#finding-version-agnostic-ref) の選択肢 B を全面適用すると、この 392 件が最も高くつく側になる）。

#### npm — 期待どおり。残る 1 件は真に確定不能

native 1606 / SBOM 1446 component、宣言のみ 1600 / 1440、ol 1605 / 1445。`package-lock.json` v3 は `license` を持つため宣言だけでほぼ埋まり、ol は残差を埋める。

未解決は `matcha 0.7.0` の 1 件のみ。npm registry に license 欄が無く、`logicalparadox/matcha` にも LICENSE ファイルが無い（`gitHead` の commit は到達可能なので 404 は「ref が無い」ではなく「license ファイルが無い」）。**publisher が述べていないものを ol が答えないのは正しい。**

npm は package metadata が `gitHead` を返すため source 候補が commit 固定になり（native 経路で pinned 1,182 / default 72）、[版に紐づかない ref](#finding-version-agnostic-ref) の問題をほぼ受けない。default ref が結論を決めたのは 1 件だけである。

#### PyPI — 期待どおり。未解決 4 件はすべて「仕様上解決しない」側

native 111 / SBOM 111 component、宣言のみ 83 / 90、ol 99 / 102。未解決は `defusedxml`（`PSFL`）、`jsonpointer`（`Modified BSD License`）、`python-dateutil`（`Dual License`）、`sortedcontainers`（`Apache 2.0`）の 4 package のみで、**いずれも [spdx.md](../specs/spdx.md#contract-spdx-license-name) が明示的に「alias 推測をしない」と決めた値**である。ol は raw を保持して `(?)` 付きで ambiguous と報告し、`sortedcontainers` は `license_classifier_not_specific` で「これは永久に解決しない」と述べた。設計どおりに動いている。

native で宣言のみ 83 → ol 99 の差 16 件は、PyPI の自由記述 `License` 欄が空/家族名のときに repository から埋めたもの。**この埋め戻しがそのまま [版に紐づかない ref](#finding-version-agnostic-ref) の risk 面でもある**。PyPI metadata は ref を返さないため、両経路合計で 28 component が default ref の答えだけで確定しており、全 ecosystem 中で最多である。**PyPI は ol の利得が最も大きく、同時に最も脆い ecosystem である。**

#### NuGet — 期待どおり、かつ ol の存在価値が最も明確

native 39 component の宣言は **0 件**。`project.assets.json` は license 欄そのものを持たないので、`ol scan --input obj/project.assets.json` 相当のことを registry 抜きでやる tool は原理的に 0% になる。ol は 30 / 39 を確定した。SBOM 経路は生成器が registry を引いているため 252 / 335 が宣言済みで、ol は 265 / 335。

残る未解決 64 package は 3 つに割れ、**いずれも報告として正しい**。

- `licenseUrl` が `go.microsoft.com/fwlink/?LinkId=329770`（.NET Library EULA）を指す旧 `System.*` / `runtime.*` — SPDX に対応する license が無い。`declared_license_location_not_collected` + URL を出す。
- `dotnet/corefx` の `blob/master/LICENSE.TXT` を指すもの（`System.Buffers 4.5.1` など）— repository は archive され、default branch から LICENSE.TXT 自体が消えている。`license_not_detected` + publisher が示した URL を出す。
- `repository` ではなく project homepage しか持たないもの（`https://dot.net/`、`http://linq2db.com/` など）— `unsupported_source_repository` + その URL。

ラウンド 1 で修正した [ref fallback](../specs/source.md#contract-source-ref-fallback) は、この ecosystem で効いた（`NETStandard.Library 2.0.3`、`Microsoft.NETFramework.ReferenceAssemblies` 系 4 つが `unknown` → `MIT`）。旧 NuGet package の `licenseUrl` が消えた branch を指しているのは例外ではなく普通なので、この修正の適用範囲は corpus より広いと見てよい。

#### Go — **期待を下回った**。実測で最も解決率が低い

native 70 component 中 47（67%）、SBOM 38 中 28（74%）。宣言は両経路とも **0 件**なので ol 以外の選択肢が無いのは NuGet と同じだが、**確定率は NuGet より低い**。事前の期待は「GitHub 上の module が大半なので npm 並みに埋まる」だったが、そうならなかった。

内訳は 20 package で、機構は 3 つ。

- **`unsupported_source_repository` 18 件** — `golang.org/x/*`（arch / crypto / mod / net / sync / sys / term / text / tools）と `google.golang.org/protobuf`。module proxy の `Origin.URL` は実際に `https://go.googlesource.com/sys` を返しており、ol は正しく非 GitHub と報告している。Gerrit を読む機構が無い限り解決しない。`?go-get=1` の meta tag も確認したが、`golang.org/x/*` は `go-source` を出さないので GitHub mirror へ辿る観測可能な経路は無い（`google.golang.org/protobuf` だけは `go-source` が `protocolbuffers/protobuf-go` を指す）。
- **`source_repository_unavailable` 4 件** — `gopkg.in/yaml.v3`、`gopkg.in/check.v1`、`rsc.io/pdf`。proxy が `Origin` を持たない古い版で、[`GoPackageMetadataProvider`](../../../src/Ol.Core/PackageManagers/GoPackageMetadataProvider.cs) は module path から repository URL を発明しない方針のため未解決になる。`?go-get=1` を辿れば `gopkg.in/*` は `go-source` で `go-yaml/yaml` / `go-check/check` を、`rsc.io/pdf` は `go-import` で `rsc/pdf` を指す。ただし**辿った先の GitHub 判定は `go-yaml/yaml` も `go-check/check` も `other` であり、この経路を実装して回収できるのは `rsc.io/pdf`（BSD-3-Clause）1 件だけ**である。新しい HTTP source class（任意ホストへの GET と HTML 解析）を足す価値は無い。
- **`license_not_recognized` 5 件** — `go-difflib`、`pelletier/go-toml`、`russross/blackfriday`、`klauspost/compress`、`go.yaml.in/yaml/v3`。GitHub が `other` と判定したもので、Gap 4 の対象。

**Go だけ低い理由は ol の欠陥ではなく Go の配布モデルにある。** module proxy は license を返さず（`.info` は version と origin のみ）、canonical repository が Gerrit に置かれ、vanity import path が redirect 前提である。**この 3 つが重なるのは Go だけ**で、Cargo は registry が答え、npm は registry が commit まで返し、NuGet は registry が declaration を返す。Go の残り 20 件のうち Gap 4 で解けるのは 5 件だけであり、**Go の解決率を上げるには「本文同定」ではなく「Gerrit / module zip から本文を取る」という別の機構が要る**。Gap 4 の scope を決めるときにこれを混ぜないこと。

#### SBOM 経路と native 経路の食い違い（全 PM 共通）

同じ repository でも 2 経路で inventory が一致しない。Dapper は SBOM 261 / native 19、ImageSharp は SBOM 2 / native 5、cobra は両方 6。**どちらかが正しいのではなく、SBOM 生成器が何を対象にしたか（solution 全体か 1 project か、dev 依存を含むか）が表れている。** ol は入力を resolve しないのでこれは仕様どおりだが、利用者が 2 経路の数字を比べたときに驚く点ではある。`ol diff` は component 集合の差として正しく表現する。

なお ImageSharp の native 経路では、**stale な `src/ImageSharp/obj/project.assets.json`（libraries 0 件）を渡すと component 0 件・exit 0 で通り、`ol check` が "License check passed" と述べた**。ここから [empty inventory の契約](../specs/cli.md#contract-empty-inventory)を追加した。

<a id="finding-version-agnostic-ref"></a>

### 実測で見つかった false positive: 版に紐づかない ref

`chardet 5.2.0` を **`0BSD` として `matched` で報告した。正解は `LGPL-2.1` である。**

`chardet` は版をまたいで 2 回 relicense しており、この 1 package が問題の三態をすべて含む。以下は 2026-08-09 に PyPI と GitHub License API から実測した値である。

| version | 実際 | PyPI `license` / `license_expression` | GitHub `?ref=<version>` | GitHub default ref | ol の結果 |
|---|---|---|---|---|---|
| 5.2.0 | LGPL-2.1 | `LGPL`（ambiguous） | LGPL-2.1 | 0BSD | **`matched 0BSD` — 誤り** |
| 6.0.0 | LGPL-2.1-or-later | `LGPL-2.1-or-later`（有効な expression） | LGPL-2.1 | 0BSD | `conflict LGPL-2.1-or-later, 0BSD` — 検出できている |
| 7.5.0 | 0BSD | `license_expression = 0BSD`（PEP 639） | 0BSD | 0BSD | `matched 0BSD` — 正しい |

この表が問題の境界を正確に示している。**ol の reconciliation は壊れていない。** 6.0.0 では版固有の主張が有効な expression だったので、default ref の答えと食い違ったことを `conflict` として正しく検出した。**失敗するのは、版固有の主張が ambiguous または不在で、default ref の答えだけが有効な expression になったときだけ**である。5.2.0 の `LGPL` は [spdx.md の strict normalization](../specs/spdx.md#contract-strict-normalization) が明示的に「推測しない」と決めた値なので ambiguous に留まり、その結果 0BSD が単独で結論になった。

機構は明確である。package metadata が ref を与えない ecosystem（PyPI、および repository URL しか持たない NuGet package）では ref が `default` になり、**default branch の license は「その version の license」ではなく「今の HEAD の license」である**。3 version を 1 回の scan に入れると source lookup は 1 件しか発生しない（repository + `default` で dedupe されるため）。1 つの repository 単位の答えが、3 つの異なる版の component に配られている。

実測では **928 component** が default ref の答えを候補として受け取っている（repository 単位に dedupe されるので実 request は 331 件）。うち他の source が解決できず **default ref の答えだけで license が確定したものが 64 件**（PyPI 28 / Go 20 / NuGet 14 / npm 2、34 package）ある。誤りが観測されたのは chardet 1 件だけだが、残る 63 件はたまたま relicense が起きていないだけで、同じ機構の上にある。

<a id="decision-version-agnostic-ref-options"></a>

#### 選択肢

いずれも [source.md の evidence semantics](../specs/source.md#contract-source-evidence) の改訂を伴う。数字は上記コーパス（4,155 component / matched 4,020 = 96.8%）での実測値。

**A. 現状維持。** 誤りは残る。`licenseCandidates[].evidence.ref` に `default` が残っているので事実は report に存在するが、human view にも `check` にも表れないため、読み手が JSON を開かない限り気づけない。コスト 0。

**B. component の version から ref を組み立てて先に引く。** `?ref=5.2.0` → 404 なら[既に実装した default fallback](../specs/source.md#contract-source-ref-fallback) に落ちる。実装は小さい（fallback の機構は既にある）。chardet 5.2.0 は正しくなる。

- 反対理由 1: **evidence が述べていない ref を ol が発明する。** [`GoPackageMetadataProvider`](../../../src/Ol.Core/PackageManagers/GoPackageMetadataProvider.cs) が vanity import path について「module path は repository URL ではないので発明しない」と明示的に決めた判断と正面から矛盾する。同じ原則を ref にだけ適用しないのは説明できない。
- 反対理由 2: **tag の綴りが規約であって事実でない。** `5.2.0` と `v5.2.0` の両方を試せば request がさらに増え、monorepo では別 package の tag を引く。
- 反対理由 3: **コスト。** ref を version 込みにすると dedupe の単位が repository から (repository, version) に変わる。実測では default ref の 331 request が **443 の (repository, version) 組**になり、外れるたびに fallback がもう 1 回入るので**最大 886 request（約 2.7 倍）**。Cargo の 392 component は結論を 1 件も変えないのに、最も高い代金を払う側になる。
- **部分適用**（他の source が解決できなかった component にだけ probe する）なら、実測で probe は **39 件**に収まり総 request は 331 → 最大 409 で済む。ただし「package metadata 収集の結果によって送る source request が変わる」構造になり、2 つの収集段階の独立性が失われる。dedupe と concurrency の計画にも影響する。

**C. 版に紐づかない source evidence を単独では確定 license にしない。** default ref の答えは candidate として残すが、それだけでは `matched` に昇格させない（`ambiguous` / `unknown` のまま）。ネットワークコスト 0、原則とも整合する。

- コスト: **現在 default ref の答えだけで確定している 64 component（34 package）が解決不能に戻る**（PyPI 28 / Go 20 / NuGet 14 / npm 2）。`requests` → `Apache-2.0`、`Jinja2` → `BSD-3-Clause`、`NETStandard.Library` → `MIT` などの正しい結果も巻き添えになる。matched 4,020 → 3,956、96.8% → 95.2%。
- **誤り 1 件を消すために正しい 63 件を捨てる交換**であり、単独では割に合わない。

**D. 版に紐づかないことを可視化する（status は変えない）。** default ref の答えが単独でその component の license を決めたときにだけ warning を付ける（例: `source_license_not_version_specific`）。実測 64 件、30 scan で 1 scan あたり約 2 件なので noise にならない。

- 誤りは消えないが、「この結論は repository の HEAD に基づく」と report と `check` が述べる。baseline で承認する対象にもできる。
- コスト 0。warning bit を 1 つ消費する（現在 14/16 使用）。
- **A / B / C のどれを選んでも D は残せる。** B を採っても tag が存在しない package では default に落ちるし、C を採っても「なぜ昇格しなかったか」を述べる必要がある。

**E. B + D。** version ref が当たった component は版固有の evidence として扱い、外れて default に落ちた component にだけ D の warning を付ける。最も正確だが、B の原則上の問題とコストをそのまま引き受ける。

#### 判断に必要な問い

順に決めれば選択肢は絞れる。

1. **version から tag 名を組み立てることは「発明」か「観測」か。** GitHub に問い合わせて 404 が返れば「その ref は無い」という観測が得られる、と読むなら B は原則違反ではない。module path から repository URL を組み立てるのは検証手段が無いので発明だが、ref は問い合わせで検証できる、という非対称性を認めるかどうか。**ここが決まらないと B と E は選べない。**
2. **coverage と正確性のどちらを優先するか。** C は「確定できないなら黙る」を徹底する立場で、DESIGN の思想には最も近いが、実測で 1.6 ポイント（64 component）の coverage を失う。
3. **1 と 2 のどちらに転んでも D は要るか。** 要ると判断するなら、D は他の決定を待たずに先行して入れられる。

現時点の推奨は **D を先に入れ、1 の答えが出てから B または C を決める**こと。D は他のどの選択肢とも排他でなく、コストが 0 で、少なくとも「ol が黙って版違いの license を報告する」状態を終わらせる。

## 実装計画より先に決める仕様課題

いずれも specs の変更であり、実装計画の前段に置く。

**解消済み**（Gap 1 を baseline に絞った結果、課題自体が消滅した）:

- ~~状態モデルの増設~~ — 承認は新しい status を作らず、violation 集合から除外するだけ。component は unresolved のまま report に残る。curation を持たないので stale-curation も不要。Gap 4 の review-required だけが将来の検討事項として残る。
- ~~policy 入力の precedence~~ — deny-list なし、暗黙の baseline 発見なし。入力は `--allow-licenses` と `--baseline` の 2 つで、後者は前者を弱められない。

- ~~永続入力 schema を canonical JSON と兼ねるか分けるか~~ — **兼ねる**と決定（Gap 3）。canonical JSON は既に `schemaVersion` と `metadata.input` を持ち、二枚に割る理由がなかった。writer と reader が別 assembly にあるため、CLI レベルの round-trip test で同期を保証する。

**解消済み**（再配布成果物を DESIGN の非目標にした結果、課題自体が消滅した）:

- ~~license choice の位置~~ — `OR` のどの branch を選ぶかは ol が答えを持つべき問いではない。成果物を作らない以上、選択を記録する場所も必要ない。

**未決（いずれも Gap 4 系。着手前に決める）**:

1. **SPDX データ契約**。Gap 4 の前提。本文 / template を SPDX データの一部とするなら、[spdx.md](../specs/spdx.md) の resolution 順序、user-managed データの要件、`Ol.Update` の生成範囲、配布サイズ目標をまとめて改訂する。現在の生成データは識別子のみで 22KB、参照実装（nuget-license）は SPDX list version に固定した本文つき外部データ package を要求する。
2. **host 依存 evidence の契約**。artifact を取得できない機械での結果表現。`--skip-enrichment` に相当する明示的な無効化手段を持つか。golden report への影響。
3. **版に紐づかない ref の扱い**（[実測の false positive](#finding-version-agnostic-ref)）。package metadata が ref を述べない ecosystem で、repository の default ref の license を component の license として採用してよいか。`chardet` は 5.2.0 が LGPL-2.1、7.5.0 が 0BSD で、ol は 5.2.0 を 0BSD と報告した。[選択肢 A–E と判断に必要な問い](#decision-version-agnostic-ref-options)を整理済み。**「version から tag 名を組み立てるのは発明か観測か」が決まらないと選べない。**

## ロードマップ

### Phase A: 既存製品へ導入できるようにする — **実装済み**

仕様は [cli.md の baseline 契約](../specs/cli.md#contract-policy-baseline)。実装は [`LicenseBaseline`](../../../src/Ol.Core/Licensing/LicenseBaseline.cs)、[`LicenseAllowPolicy.CanAcknowledge`](../../../src/Ol.Core/Licensing/LicenseAllowPolicy.cs)、[`CheckCommands`](../../../src/Ol/CheckCommands.cs)。検証は [`LicenseBaselineTests`](../../../tests/Ol.Tests/LicenseBaselineTests.cs) と [`CliCheckTests`](../../../tests/Ol.Tests/CliCheckTests.cs)。

確認できた挙動:

- 承認は `unknown` / `ambiguous` / `conflict` / `invalid` のみ。`error` と `matched` には効かない。
- 禁止ライセンスへ正規化される候補を持つ component は、書き込み時も適用時も承認されない。allow-list を狭めると過去の承認が無効になる。
- 版と証拠の変化で承認が自動的に外れる。
- 全置換が決定的で、無変更の再生成は byte 同一。timestamp を持たない。
- baseline の欠落・破損・schema 不整合は exit 1。`--update-baseline` は `--baseline` を要求する。
- exit 0（成功）/ 1（実行失敗）/ 2（policy violation）の共通契約を維持。baseline を使わない経路に allocation と I/O を追加しない。

実装で確定した設計判断は [cli.md の Lessons Learned](../specs/cli.md#lessons-learned) に記録した。特に fingerprint は候補の挿入順に依存させない。挿入順は enrichment pipeline の実装詳細である一方、fingerprint は利用者の repository に永続するため、evidence source を1つ足しただけで全 baseline が無効化されてはならない。

### Phase B: 再評価と可視化 — **実装済み**

1. 永続入力契約と `check --report`（canonical JSON を兼用）。
2. factual report diff（`ol diff`）。
3. SARIF（`check --sarif`）。

検証済み: offline policy 再評価 / added・removed・version-changed・status-changed・license-changed・evidence-changed の区別 / diff JSON の byte 安定 / check text と SARIF の violation 集合一致 / SARIF に絶対 path と token を出さない。

**この Phase で発見して修正したバグ**: `LicenseStatus.Matched` が enum の 0 値だったため、license 宣言を持たない package が `matched` かつ license 空として報告されていた（npm / Cargo / Composer / pip の各 parser が既定 candidate の status を分岐に使うため）。コンプライアンスツールとして最悪の false negative であり、`check` では「license is not allowed」という説明不能な理由になり、baseline でも承認できない袋小路を生んでいた。`Unknown = 0` を明示値で固定し、全 parser 横断の不変条件テスト（`Matched` なら license を必ず持つ）を追加した。詳細は [cli.md の Lessons Learned](../specs/cli.md#lessons-learned)。

### Phase C: 証拠の最後の一歩

1. 未決の仕様課題 1（SPDX データ契約）と 2（host 依存 evidence）を決める。決まるまで着手しない。
2. legal file evidence schema と archive safety contract。
3. content hash cache と決定的 matcher。
4. Phase B の diff で unknown 減少を計測する。

### Phase D: 判定精度と coverage

curation（Gap 2）は、upstream の誤りが baseline で吸収できない実例が観測されてから着手する。あわせて fixture と実例に基づく ecosystem 追加を行う。file-level scan は Phase C で解決しない実例が十分に集まった場合だけ個別計画にする。

## 今回のスコープに入れないもの

- 参照ツールにあっても取り込まない挙動: package metadata の先頭 license だけを使う / SPDX expression を raw string の exact・substring 比較で判定する / confidence の低い heuristic を確定 license として evidence へ上書きする / package・file ごとに無制限の task を作る / installed directory を inventory の正とし resolved graph を失う / ORT の plugin platform と rule DSL を規模ごと模倣する。
- 再配布成果物の生成（`THIRD-PARTY-NOTICES`、attribution file、license bundle）。据え置きではなく[非目標](../Architecture.md#non-goals)である。成果物を作るには `OR` の選択、観測していない本文の代替、網羅性の主張が要り、いずれも観測から導けない。
- Phase A / B の期間中に着手しないもの（据え置き継続）: curation（事実の訂正）、deny-list、policy file、本文同定、file-level scan、新規 ecosystem。
- `--allow-licenses` の入力補助（`osi-approved` のような SPDX 由来グループ）。SPDX データに無い「コピーレフトでない」という分類が本当に欲しいものであり、それは ol による法的判断になる。OSI 承認には GPL が含まれるため、SPDX 由来のグループはケース2 を解かない。
- 外部プロセス依存（package manager CLI、MSBuild、外部 scanner）の常時要求。単一 native バイナリという配布形態を崩す。

## 次に作る個別計画

Phase A・B が完了したため、**次は実装計画ではなく仕様決定**である。残る Gap 4 は未決の仕様課題に依存しており、決めないまま着手すると再現性（C 軸）と配布サイズ（F 軸）を毀損する。Gap 2 は仕様課題に依存しないが、実例が出るまで着手しない。

**決めるべき順序**:

1. **SPDX データ契約**（未決 1）。本文 / template を SPDX データの一部にするかどうか。ここが「する」に決まらない限り、Gap 4 の matcher は成立しない。決めるべきは、bundled に本文を持つのか、外部データを版固定で参照するのか、そして user-managed SPDX を選んだときの matcher の挙動をどう定義するか。現在の 22KB という配布実績に対して桁が変わるため、単一 native バイナリという方針との折り合いを先に付ける。
2. **host 依存 evidence の契約**（未決 2）。artifact をローカルに持たない機械での結果表現。これが決まると Gap 4 の完了条件が書ける。

**先に測るべきこと**: Gap 4 に着手する前に、`ol diff` で「本文取得を足したら unknown が実際に何件減るか」を計測できる状態になった。投資判断はこの実測に基づいて行う。順序を Gap 3 → Gap 4 にしたのはこのためである。

なお Gap 7（ecosystem 追加）は仕様課題に依存しないため、上記と独立に着手できる。Mavenは[verification.md](../specs/verification.md) の provider と fixture の 1 対 1 契約まで実装済みであり、次はGradleを検討する。
