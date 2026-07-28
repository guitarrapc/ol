# DESIGN から逆算した ol の不足と実装順序

## この文書の位置付け

[ol の設計](../DESIGN.md)が利用者へ約束している体験を起点に、**まだ果たされていない約束**を特定し、それを果たすために何を支払うかを整理する。[既存 OSS ライセンスチェッカーの実装分析](../references/existing_license_checkers.md)は、その支払いの相場を知るための参照であって、機能一覧の出典ではない。

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
| resolved dependency input | CycloneDX、SPDX、NuGet assets、npm、pnpm、Yarn Classic / Berry、Cargo、Go module graph、pip inspect、Composer、**Bundler** の 12 形式と collection | [DependencyInputRegistry.cs](../../../src/Ol.Core/DependencyInputRegistry.cs)、[DependencyInventory.cs](../../../src/Ol.Core/DependencyInventory.cs) |
| registry metadata provider | npm、NuGet、Cargo、Go、PyPI、Packagist、**RubyGems** の 7 種 | [OlDefaults.cs](../../../src/Ol.Core/OlDefaults.cs) |
| source repository evidence | GitHub License API のみ。repository / ref / path / blob SHA / http status を保持 | [GitHubLicenseApiClient.cs](../../../src/Ol.Core/GitHub/GitHubLicenseApiClient.cs)、[specs/source.md](../specs/source.md) |
| evidence 保持 | source / kind / raw / normalized / status / deprecated / warnings / typed provenance | [LicenseCandidate.cs](../../../src/Ol.Core/Licensing/LicenseCandidate.cs) |
| reconciliation | matched / conflict / unknown / ambiguous / invalid / error の 6 状態 | [LicenseReconciler.cs](../../../src/Ol.Core/Licensing/LicenseReconciler.cs) |
| SPDX | 版を固定した**識別子**データ。本文・template は持たない（生成物 22KB） | [SpdxGeneratedLicenseData.g.cs](../../../src/Ol.Core/Generated/SpdxGeneratedLicenseData.g.cs)、[specs/spdx.md](../specs/spdx.md) |
| policy | `check --allow-licenses` の SPDX 識別子 allow-list のみ。CLI 引数限定、`AND` / `OR` / `WITH` を fail-closed 評価 | [CheckCommands.cs](../../../src/Ol/CheckCommands.cs)、[LicenseAllowPolicy.cs](../../../src/Ol.Core/Licensing/LicenseAllowPolicy.cs) |
| 出力 | `scan` が text / Markdown / JSON と `--out-file`。`check` は text 固定 | [ScanCommands.cs](../../../src/Ol/ScanCommands.cs) |
| cache | TTL なしの永続 cache。`--refresh` でのみ無効化 | [specs/cache_format.md](../specs/cache_format.md) |

前版のこの文書は Bundler / RubyGems を未対応として扱っていたが、実装済みである。ecosystem の不足は上表から取り直すこと。

## 約束と充足度

[参照文書の評価軸](../references/existing_license_checkers.md#ol-の設計目標から見た横断評価)に沿って、DESIGN の約束と現状を対応させる。

| 軸 | DESIGN の約束 | 現状 | 差分 |
|---|---|---|---|
| A. 数え落とさない | 完全な inventory と graph を先に確定し、filter は view | 12 input が root / direct / transitive と context 別 graph を保持 | **ほぼ果たされている**。残るのは ecosystem 数 |
| B. 判定の理由が残る | evidence を上書きせず provenance 付きで保持 | 3 系統の typed evidence、6 状態、警告を保持 | **果たされている**。ただし人間の判断を残す場所がない |
| C. 同じ入力なら同じ結果 | 版を固定した SPDX、TTL なし cache、決定的順序 | 識別子検証の範囲では成立 | **果たされている**。本文同定を足すと崩れる（後述） |
| D. 止まったときに前へ進める | 「policy が何を禁じるかを決める」 | 決められるのは SPDX 識別子の allow-list のみ | **最大の穴。baseline で埋める設計を確定済み**（Gap 1） |
| E. 検査の次へ届く | （DESIGN は約束していない） | license ID の報告まで | 拡張であって未達ではない |
| F. 小さく速いままでいる | 単一 native AOT バイナリ | 維持。renderer は 0 allocation | 新機能はここを削る方向に働く |

事実側（A・B・C）は概ね約束を果たしている。**policy 側（D）だけが約束に対して極端に薄い。** ol は「観察と policy を分離する」と宣言し、`matched` は「解決済みであって許可済みではない」と定義しているにもかかわらず、policy が表現できる意思決定は識別子の列挙一つしかない。

## 不足の一覧と順序

番号は識別子であって順位ではない。実施順は優先度に従い、`Gap 1 → Gap 3 → Gap 4 → Gap 2 → Gap 5` となる。Gap 2 は当初 P0 だったが、Gap 1 の設計確定により合否判定には不要と分かったため P2 へ後退した（[経緯](#gap-2--p2-事実の訂正curation)）。

### Gap 1 / P0: fail-closed の逃げ道がなく、既存製品へ導入できない — **設計確定**

**約束**: [decision-policy-separation](../DESIGN.md) — 同じ事実に対し、組織ごとに異なる policy を適用できる。

**現状で起きること**: `check` は unresolved を無条件で違反にする。実在の依存集合には必ず解決不能な component が残り、利用者に打つ手のないものが大半を占める（registry が license を書いていない、GitHub 以外、private package）。想定する二つの利用場面は、この点で形が違う。

- **OSS の PR に違反パッケージ**: baseline は既に green で差分は数個。新規パッケージが unresolved なら本当に止めるべきで、逃げ道は要らない。**今日の CLI で完結している。**
- **プロプライエタリ製品の出荷前 GPL 検出**: 初回実行で unresolved が数十件出る。allow-list に何を足しても消えず、**探し物である GPL 1 件がそこに埋もれる**。

後者だけが解決を要する。そして deny-list はこれを解決しない。deny にすれば静かになるが、知らない license を見なくなり検出力が落ちる。見逃しのコストが極大で誤警報のコストが小さい以上、fail-closed な allow-list を捨てる理由はない。**allowed に絞る判断は維持する。**

**採用した解**: 追加する概念を一つに絞り、**ol が生成する baseline** だけを入れる。policy file、profile、classification、deny-list、scope policy はいずれも採用しない。仕様は [cli.md の baseline 契約](../specs/cli.md#contract-policy-baseline)に確定済み。

```bash
ol check --input . --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

これは不変。ケース1 は今日と 1 文字も変わらない。ケース2 だけが `--baseline` と `--update-baseline` の 2 ステップになる。

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

**副産物**: `--update-baseline` を実行しても禁止ライセンスは消えない。したがって初回導入は 1 コマンドで済み、その結果 exit 1 で落ちたなら**それが探し物**である。

**この設計で消えた設計負債**: 新しい status を導入しない（承認は violation 集合から除外するだけで、component は unresolved のまま report に残る）。policy 入力が 2 つだけなので precedence の定義が不要。curation を持たないので stale-curation 状態も不要。

**残る懸念**: `error` を承認対象から外したため、到達不能な private registry の package が恒久的に詰む経路が理論上ある。実務では private package は自前 SBOM が license を持つため `error` にならないはずである。刺さった場合は「恒久的な 404 のみ承認可」のような狭い規則で対処し、この決定は覆さない。

### Gap 2 / P2: 事実の訂正（curation）

**現状で起きること**: upstream の typo、deprecated alias、custom string、確認済みの conflict について、「正しい license はこれである」と記録する手段がない。

**Gap 1 の決定による位置付けの変化**: 当初は Gap 1 と対で P0 に置いていたが、**合否判定には不要**であることが分かったため後退させる。ケース1 でもケース2 でも、利用者が必要としているのは「見た、受け入れた」であって「実際の license 値はこれだ」ではない。値が必要になるのは **NOTICE 生成（Gap 5）** であり、curation はそこで初めて必須になる。

したがって curation は Gap 5 の前提として扱い、単独では実装しない。baseline の schema version があるので、同じファイルに action を足す形で後から拡張できる。使う予定のフィールドを今から予約しない。

**着手時に引き継ぐ設計**: [参照文書 D 軸](../references/existing_license_checkers.md#d-止まったときに前へ進める)の三つの出口（事実の訂正 / 結論の確定 / 方針の例外）を混ぜないこと。guard は既存の candidate `Raw` と GitHub License API の blob SHA（`SourceRepositoryEvidence.LicenseSha`）で成立する。registry evidence 側だけ `CacheKeySha256`（cache key であって内容 hash ではない）なので content hash の追加が要る。適用後も original candidates と curation 前の reconciliation を report に残す。

### Gap 3 / P1: 「再 scan なしの再評価」が未実装

**約束**: DESIGN は明示的にこう書いている — 「同じ事実 report を、依存を再 scan したり証拠を再収集したりせずに、異なる policy で評価できる」。

**現状で起きること**: `check` は毎回 input parsing と enrichment を含む pipeline を実行する（[CheckCommands.cs](../../../src/Ol/CheckCommands.cs)）。[cli.md](../specs/cli.md) は「scan と同じ pipeline を一度だけ実行する」と正直に書いており、**DESIGN の記述だけが実装より先へ出ている**。これは新機能の提案ではなく、文書化済みの未実装である。

**過大主張しないこと**: 「network なしで再評価できる」は既に成立している。cache は TTL を持たず、`--refresh` を指定しない限りネットワークへ出ない。この Gap の実利は次に限られる。

- parse と reconciliation のコスト削減。
- cache dir を持ち回れない環境（別 job、別マシン）への可搬性。
- registry / source の時間変化から policy 結果を切り離すこと。
- **前回との差分を出せること**（これが本命）。

**推奨する実装境界**: renderer JSON をそのまま parser 入力にせず、`ScanResult` の永続入力契約を versioned schema として定義する。ただし **schema を二枚管理にしない**こと。canonical JSON に schema version と入力同一性を足して 1 枚で兼ねられるなら、そちらを優先する。二枚に割る判断をするなら、同期を検証する test を同時に定義する。

**完了条件**: 同じ永続結果と policy から同一判定を得る / schema version 不整合・破損・部分 report を command error にする / report 入力時に network request が発生しない / added / removed / updated / evidence-changed / policy-changed を区別する / diff の順序が決定的である。

### Gap 4 / P1: 証拠の最後の一歩（原文）

**約束**: Design Goal 2 — 独立に帰属可能な証拠源から結論を組み立てる。

**現状で起きること**: registry declaration が空で GitHub でもない package は unknown のまま解決できない。原文を持たないため、後続の NOTICE も作れない。

**この Gap を 4 位に置く理由**: 価値は高いが、**C 軸（同じ入力なら同じ結果）を壊す唯一の項目**であり、支払いが他より一桁重い。[参照文書 C 軸](../references/existing_license_checkers.md#c-同じ入力なら同じ結果)の実測が示すとおり、本文同定は二つの新しい依存を必ず連れてくる。

1. **同定データの版**: 現在の SPDX データは識別子のみで 22KB。SPDX template matching は本文つきデータを要求し、nuget-license はこれを版固定の外部データ package として取り込んでいる。ol では [spdx.md の data resolution 契約](../specs/spdx.md)（明示ディレクトリ → user-managed → bundled）が `licenses.json` と `exceptions.json` しか要求していないため、**user-managed SPDX を選ぶと matcher が動かないか劣化する**。これは `decision-versioned-spdx` の違反であり、matcher の追加ではなくデータ契約の変更である。[Ol.Update](../../../src/Ol.Update) の生成範囲と native AOT の配布サイズにも波及する。
2. **package のローカル実体化**: 参照実装で本文を読めるものはすべて installed / restore 済みを前提とする。ol の入力は resolved graph なので、**同じ入力ファイルから機械ごとに異なる evidence が出る**状態へ移る。ORT だけが provenance を固定して自分で download することで解決している。

前版が最優先に推していた「NuGet embedded license file」も、この観点では最小の縦切りではない。`.nupkg` は NuGet global packages folder にしか存在せず、restore 済み環境という新しい前提を持ち込む。加えて現代の NuGet は `license type="expression"` が主流で `type="file"` は少数派であり、`type="file"` を選ぶ package は独自条項であることが多い。独自条項は SPDX template と no-match になり、規則どおり no-match は確定 license に昇格しない。つまり**この縦切りが最も高い確率で生む結果は「unknown のまま」**である。設計リスクを小さく固定する題材としては良いが、利用者価値の根拠にはならない。

**推奨する順序**: Gap 3 を先に済ませ、legal file evidence の投入前後を diff として計測する。「local legal file で unknown が実際に何件減ったか」を観測してから corpus 投資を判断する。

**着手前に必ず決めること**: 上記 1 の SPDX データ契約と、2 の「取得できなかったこと」の表現（evidence なしか、明示的な未取得状態か）。後者を決めずに実装すると [verification.md](../specs/verification.md) の golden report が機械依存で壊れる。

**推奨する最小 scope**（決定後）: 探索は exact / bounded な file name pattern に限定する。candidate は evidence kind、package / repository identity と version / ref、archive / file path、content SHA-256、byte length、matcher 名と version、match class、no-match / multiple-match / truncated / unreadable の明示状態を持つ。heuristic 類似度だけの match を `Matched` にしない。

**performance / safety 制約**: inventory 確定後に artifact target を deduplicate / provenance identity ごとに一度だけ読む / network・archive・file scan を別々の bounded concurrency にする / archive entry 数・展開後 byte 数・1 file byte 数・探索 depth を上限化する / zip slip・symlink escape・path traversal を拒否する / 同一内容は matcher 結果を再利用する / completion order ではなく component order で merge する / report への本文埋め込みは明示 option とする。

**完了条件**: declaration unknown の fixture が local legal file から SPDX ID を得る / declared と detected が異なる fixture は conflict を保持する / no-match と multiple-match が確定 license に昇格しない / 同一 artifact を参照する複数 component で読み取りが一度だけ行われる / malicious・oversized archive が bounded failure として evidence に残る / **artifact を取得できる機械とできない機械の差が契約どおりに表れる**。

### Gap 5 / P2: 検査の次（NOTICE / license bundle）

DESIGN は約束していないため、これは拡張である。Gap 4 の原文と Gap 1 の classification を前提とする。原文が無い状態で SPDX template から汎用本文を補うと、package が付した追加条項や NOTICE を落とすため既定動作にしてはならない。

最初の成果物は決定的な `THIRD-PARTY-NOTICES` に限定する。component identity、version、source URL、effective expression、取得した原文と provenance、原文が無い component の明示的な incomplete list を含める。

**設計上の分岐点**: `OR` のライセンス選択は **policy 評価の副産物ではなく利用者の入力**とする。allow-list を満たした branch を「選択された license」として成果物へ書くと、ol が利用者に代わって選択を宣言することになり、DESIGN の非目標（法的判断をしない）に抵触する。ORT と同じく license choice は明示的な設定入力として受け取る。

**完了条件**: 同じ結果と policy から byte-stable な artifact ができる / name collision・同一 text の dedup・改行と encoding を決定的に扱う / 原文と生成した区切りを区別できる / missing text・custom terms・multiple license・未選択の `OR` を黙って落とさない / artifact の各項目から scan evidence へ逆引きできる。

### Gap 6 / P2: dependency path 付き SARIF

`check` は違反 component と理由を全件出すが、CI annotation として repository 上の位置や、transitive violation を導入した direct dependency path を出さない。完全な graph を持つ ol の優位を出力へ活かせる。

**scope の現実**: license-checker-php の売りは violation を `composer.json` の direct dependency 宣言行へ結び付ける点だが、**ol は manifest を読まない**。入力は lockfile と resolved graph であり、physical location を出せる入力は限られる。したがって初期実装で出せるのは大半が logical location と dependency path になる。この前提で価値を見積もること。偽の line 1 は作らない。

**完了条件**: SARIF schema validation が通る / direct・transitive・multiple-path・no-location の fixture を持つ / check text と SARIF で violation 集合が一致する / 絶対 path・cache path・token を出力しない。

### Gap 7 / P2: ecosystem coverage

現状は上表のとおり 12 input / 7 provider で、Ruby は対応済みである。参照ツール群と比べて残る主な空白は次になる。

1. **JVM: Maven / Gradle** — 利用規模が最大。Maven Central / POM に license と SCM metadata がある。multi-module、scope、dependency management、Gradle variant が難所。
2. **Apple: SwiftPM / CocoaPods** — package graph と Git provenance は取りやすいが、registry metadata より source legal files の比重が高く、Gap 4 の未決事項に依存する。
3. **Dart / Flutter: Pub** — lockfile と pub.dev metadata を使える。
4. Erlang / Elixir、Haskell、Conan 等。

**採用条件**（ecosystem 数だけを増やさない）: resolved graph と root / direct / transitive semantics、正規化 purl と source identity、scope / variant の audit data、registry provider または unsupported の明示、real fixture と golden report と重複排除 scheduling test、ecosystem 固有 parser の hot-path benchmark。これは [verification.md](../specs/verification.md) の「provider と `sandbox/ecosystems/manifest.json` は 1 対 1」という既存契約と一致する。

### Gap 8 / P3: source tree 全体の file-level scan

Gap 4 の bounded な root legal-file evidence を実運用し、解決できない component と監査要求を計測してから判断する。file 数に比例する CPU / I/O、false positive、path exclusion、scanner dataset の版による再現性、copyright / snippet を含む新しい domain model が必要になり、ol の中心価値から最も遠い。

実装する場合も core に scanner を組み込まず、provenance を固定した source archive を入力とし、外部 scanner の版付き結果を typed evidence として ingest する narrow boundary から始める。

## 実装計画より先に決める仕様課題

いずれも specs の変更であり、実装計画の前段に置く。

**解消済み**（Gap 1 を baseline に絞った結果、課題自体が消滅した）:

- ~~状態モデルの増設~~ — 承認は新しい status を作らず、violation 集合から除外するだけ。component は unresolved のまま report に残る。curation を持たないので stale-curation も不要。Gap 4 の review-required だけが将来の検討事項として残る。
- ~~policy 入力の precedence~~ — deny-list なし、暗黙の baseline 発見なし。入力は `--allow-licenses` と `--baseline` の 2 つで、後者は前者を弱められない。

**未決**:

1. **SPDX データ契約**。Gap 4 の前提。本文 / template を SPDX データの一部とするなら、[spdx.md](../specs/spdx.md) の resolution 順序、user-managed データの要件、`Ol.Update` の生成範囲、配布サイズ目標をまとめて改訂する。
2. **host 依存 evidence の契約**。artifact を取得できない機械での結果表現。`--skip-enrichment` に相当する明示的な無効化手段を持つか。golden report への影響（Gap 4）。
3. **license choice の位置**。policy 評価の出力ではなく入力とする（Gap 5）。curation（Gap 2）と同時に決める。
4. **永続入力 schema を canonical JSON と兼ねるか分けるか**（Gap 3）。baseline の fingerprint 定義は Gap 3 の evidence diff と共有できるため、先に baseline を実装して形を確かめる。

## ロードマップ

### Phase A: 既存製品へ導入できるようにする — **実装済み**

仕様は [cli.md の baseline 契約](../specs/cli.md#contract-policy-baseline)。実装は [`LicenseBaseline`](../../../src/Ol.Core/Licensing/LicenseBaseline.cs)、[`LicenseAllowPolicy.CanAcknowledge`](../../../src/Ol.Core/Licensing/LicenseAllowPolicy.cs)、[`CheckCommands`](../../../src/Ol/CheckCommands.cs)。検証は [`LicenseBaselineTests`](../../../tests/Ol.Tests/LicenseBaselineTests.cs) と [`CliCheckTests`](../../../tests/Ol.Tests/CliCheckTests.cs)。

確認できた挙動:

- 承認は `unknown` / `ambiguous` / `conflict` / `invalid` のみ。`error` と `matched` には効かない。
- 禁止ライセンスへ正規化される候補を持つ component は、書き込み時も適用時も承認されない。allow-list を狭めると過去の承認が無効になる。
- 版と証拠の変化で承認が自動的に外れる。
- 全置換が決定的で、無変更の再生成は byte 同一。timestamp を持たない。
- baseline の欠落・破損・schema 不整合は exit 2。`--update-baseline` は `--baseline` を要求する。
- exit 0 / 1 / 2 の既存契約を維持。baseline を使わない経路に allocation と I/O を追加しない。

実装で確定した設計判断は [cli.md の Lessons Learned](../specs/cli.md#lessons-learned) に記録した。特に fingerprint は候補の挿入順に依存させない。挿入順は enrichment pipeline の実装詳細である一方、fingerprint は利用者の repository に永続するため、evidence source を1つ足しただけで全 baseline が無効化されてはならない。

### Phase B: 再評価と可視化

1. 永続入力契約と `check` の report 入力 mode。
2. evidence / policy diff。
3. SARIF。

検証: offline 再評価の byte 安定性 / added・removed・updated・evidence-changed・policy-changed の区別 / check text と SARIF の violation 集合一致。

### Phase C: 証拠の最後の一歩

1. 未決の仕様課題 1（SPDX データ契約）と 2（host 依存 evidence）を決める。決まるまで着手しない。
2. legal file evidence schema と archive safety contract。
3. content hash cache と決定的 matcher。
4. Phase B の diff で unknown 減少を計測する。

### Phase D: 成果物と coverage

NOTICE / license bundle は、未決の仕様課題 3（license choice を入力とする）と Gap 2（curation）を前提とする。原文が取れていても「この conflict の結論はこれ」を記録できなければ、成果物に書く license 値が定まらない。あわせて fixture と実例に基づく ecosystem 追加を行う。file-level scan は Phase C で解決しない実例が十分に集まった場合だけ個別計画にする。

## 今回のスコープに入れないもの

- 参照ツールにあっても取り込まない挙動: package metadata の先頭 license だけを使う / SPDX expression を raw string の exact・substring 比較で判定する / confidence の低い heuristic を確定 license として evidence へ上書きする / package・file ごとに無制限の task を作る / installed directory を inventory の正とし resolved graph を失う / ORT の plugin platform と rule DSL を規模ごと模倣する。
- Phase A の期間中に着手しないもの: curation（事実の訂正）、deny-list、policy file、本文同定、NOTICE 生成、file-level scan、新規 ecosystem。
- `--allow-licenses` の入力補助（`osi-approved` のような SPDX 由来グループ）。SPDX データに無い「コピーレフトでない」という分類が本当に欲しいものであり、それは ol による法的判断になる。OSI 承認には GPL が含まれるため、SPDX 由来のグループはケース2 を解かない。
- 外部プロセス依存（package manager CLI、MSBuild、外部 scanner）の常時要求。単一 native バイナリという配布形態を崩す。

## 次に作る個別計画

**`check` の baseline**（Gap 1、Phase A）。仕様が [cli.md](../specs/cli.md#contract-policy-baseline) に確定しているため、test-first implementation plan を直接起こせる。

- ol の約束と実装の乖離が最も大きく、利用者が最初に停止する地点である。
- 新しい I/O 境界は JSON ファイル 1 つ。同定データもローカル実体化も要求せず、既存の evaluator と evidence をそのまま使う。C 軸と F 軸を毀損しない。
- 未決の仕様課題を 1 つも持ち込まない。
- fingerprint の定義が Gap 3 の evidence diff にそのまま流用できる。

この計画では curation、deny-list、report 入力、本文取得、NOTICE を同時に実装しない。baseline の schema、承認可能性の判定、fingerprint、exit code 契約を固定し、その結果を見て Gap 3 へ進む。
