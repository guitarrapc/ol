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
| D. 止まったときに前へ進める | 「policy が何を禁じるかを決める」 | 決められるのは SPDX 識別子の allow-list のみ | **果たされていない。最大の穴** |
| E. 検査の次へ届く | （DESIGN は約束していない） | license ID の報告まで | 拡張であって未達ではない |
| F. 小さく速いままでいる | 単一 native AOT バイナリ | 維持。renderer は 0 allocation | 新機能はここを削る方向に働く |

事実側（A・B・C）は概ね約束を果たしている。**policy 側（D）だけが約束に対して極端に薄い。** ol は「観察と policy を分離する」と宣言し、`matched` は「解決済みであって許可済みではない」と定義しているにもかかわらず、policy が表現できる意思決定は識別子の列挙一つしかない。

## 不足の一覧と順序

### Gap 1 / P0: policy が方針を表現できない

**約束**: [decision-policy-separation](../DESIGN.md) — 同じ事実に対し、組織ごとに異なる policy を適用できる。

**現状で起きること**: `check` は `--allow-licenses` を必須とし、`unknown` / `conflict` / `ambiguous` / `invalid` / `error` を無条件で違反にする。実在の依存集合には必ず解決不能な component が残るため、**利用者は最初の 1 件で恒久的に停止する**。deny も、package 単位の例外も、理由も期限も表現できない。CLI 引数だけなので、方針が repository に残らず review もできない。

fail-closed 自体は正しい。欠けているのは、閉じた後に監査可能な形で前へ進む手段である。

**参照実装の解**: LicenseFinder は permit / restrict と package approval を分離し、licensed は allowed / reviewed / ignored を version 条件付きで持つ。ORT は classification と rule violation severity を分ける。

**推奨する最小 scope**: 宣言的 data のみ。DSL、plugin、任意コード実行を導入しない。

- schema version、named profile。
- SPDX allow / deny 識別子。
- classification: `allowed` / `denied` / `review` / `notice-required` / `source-disclosure-review`。
- package exception: 正確な purl、正確な version または明示 range、action、reason、owner、expires。
- unresolved status の扱い。既定は現行どおり fail closed。

`AND` / `OR` / `WITH` は既存 evaluator を共有し、policy file 側に別 parser を作らない。

**支払うもの**: 新しい I/O は設定ファイル 1 つ。同定データもローカル実体化も不要。CLI と policy file の precedence 定義、および exit code 契約の維持が主な設計作業。

**この順位に対する反論と応答**: 「証拠が不十分なまま例外を作りやすくすると、無知を制度化する」。これは正しい懸念であり、次で抑える。exception には reason / owner / expires を必須にし、期限切れと未使用を報告する。`unknown` の承認は `allowed` と別の action として記録し、report から消さない。事実の訂正（Gap 2）と方針の例外を同じ action にしない。

**完了条件**:

- CLI allow-list と policy file の precedence / conflict が一意に定義される。
- exception は package identity と version を外れると適用されない。
- expired / unused exception を報告できる。
- policy result に matched rule / exception と reason が残る。
- unresolved の既定 fail-closed を維持する。
- policy parse failure は violation の exit 1 ではなく command error の exit 2 になる。

### Gap 2 / P0: 人間の判断を残す場所がない

**約束**: [decision-evidence-preservation](../DESIGN.md) — 証拠を消さずに保持する。判断もまた監査対象である。

**現状で起きること**: `Concluded` は SPDX document producer が供給した事実であり、ol 利用者の curation ではない。upstream の typo、deprecated alias、custom string、確認済みの conflict を、証拠を消さずに解決する手段がない。

**参照実装の解**: [参照文書 D 軸](../references/existing_license_checkers.md#d-止まったときに前へ進める)の三つの出口。事実の訂正 / 結論の確定 / 方針の例外を混ぜない。いずれも fingerprint による変更検知を伴う。

**Gap 1 との関係**: 同じ component selector と versioned file の形式を共有する。したがって Gap 1 の直後に置く。**Gap 4（原文取得）には依存しない。** guard に使う fingerprint は、既存の candidate `Raw` と、GitHub License API から取得済みの blob SHA（`SourceRepositoryEvidence.LicenseSha`）で成立する。不足しているのは registry evidence 側で、現在保持しているのは `CacheKeySha256`（cache key であって内容 hash ではない）である点だけで、これは content hash の追加 1 項目で済む。

**推奨する data model**: component selector（正規化 purl、正確な version または明示 range）、action（claim mapping / concluded expression / finding resolution）、audit（reason / reviewer / reviewed-at）、guard（対象 candidate source と kind、期待する内容 hash）。適用後も original candidates と curation 前の reconciliation を report に残す。

**完了条件**:

- 正確な purl / version / evidence hash にだけ適用される。
- version または evidence 内容が変わると自動的に stale になり、fail closed する。
- original / curated / effective の三つを JSON で追跡できる。
- unused / ambiguous / duplicate curation を決定的に報告する。
- curation を外すと元の結果が完全に復元される。
- curation なしの経路に不要な allocation / I/O を追加しない。

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

1. **状態モデルの増設**。Gap 2 の stale-curation、Gap 4 の review-required を、既存の閉じた 6 状態と `check` の fail-closed 契約、JSON report 契約のどこに載せるか。candidate の warning か、candidate status か、component status かで、破壊的変更の範囲と exit code の意味が変わる。DESIGN の「evidence source ごとの final status を導入しない」制約にも触れる。
2. **SPDX データ契約**。Gap 4 の前提。本文 / template を SPDX データの一部とするなら、[spdx.md](../specs/spdx.md) の resolution 順序、user-managed データの要件、`Ol.Update` の生成範囲、配布サイズ目標をまとめて改訂する。
3. **license choice の位置**。policy 評価の出力ではなく入力とする（Gap 5）。
4. **host 依存 evidence の契約**。artifact を取得できない機械での結果表現。`--skip-enrichment` に相当する明示的な無効化手段を持つか。golden report への影響（Gap 4）。
5. **policy 入力の precedence**。CLI 引数と policy file の関係、両方指定時の挙動、exit code の維持（Gap 1）。
6. **永続入力 schema を canonical JSON と兼ねるか分けるか**（Gap 3）。

## ロードマップ

### Phase A: policy が意思決定を表現できるようにする

1. 仕様課題 1 と 5 を決める。
2. versioned policy file（allow / deny / classification / package exception / audit）。
3. curation file と fingerprint guard、stale / unused 検出。
4. original / curated / effective の同時追跡。

検証: 例外が identity と version を外れると適用されない / 内容変化で必ず再 review になる / curation を外すと元の結果へ戻る / 既存経路に allocation と I/O を追加しない / exit 0・1・2 の契約維持。

### Phase B: 再評価と可視化

1. 永続入力契約と `check` の report 入力 mode。
2. evidence / policy diff。
3. SARIF。

検証: offline 再評価の byte 安定性 / added・removed・updated・evidence-changed・policy-changed の区別 / check text と SARIF の violation 集合一致。

### Phase C: 証拠の最後の一歩

1. 仕様課題 2 と 4 を決める。決まるまで着手しない。
2. legal file evidence schema と archive safety contract。
3. content hash cache と決定的 matcher。
4. Phase B の diff で unknown 減少を計測する。

### Phase D: 成果物と coverage

NOTICE / license bundle、および fixture と実例に基づく ecosystem 追加。file-level scan は Phase C で解決しない実例が十分に集まった場合だけ個別計画にする。

## 今回のスコープに入れないもの

- 参照ツールにあっても取り込まない挙動: package metadata の先頭 license だけを使う / SPDX expression を raw string の exact・substring 比較で判定する / confidence の低い heuristic を確定 license として evidence へ上書きする / package・file ごとに無制限の task を作る / installed directory を inventory の正とし resolved graph を失う / ORT の plugin platform と rule DSL を規模ごと模倣する。
- Phase A の期間中に着手しないもの: 本文同定、NOTICE 生成、file-level scan、新規 ecosystem。
- 外部プロセス依存（package manager CLI、MSBuild、外部 scanner）の常時要求。単一 native バイナリという配布形態を崩す。

## 次に作る個別計画

**versioned policy file と監査可能な exception**（Gap 1）を推奨する。

- ol の約束と実装の乖離が最も大きく、利用者が最初に停止する地点である。
- 新しい I/O 境界、同定データ、ローカル実体化のいずれも要求しない。既存の evaluator と evidence をそのまま使う。
- Gap 2 の curation と component selector / versioned file 形式を共有するため、次の計画へ直結する。
- 成功も失敗も既存の golden report と exit code 契約で検証でき、C 軸と F 軸を毀損しない。

この計画では curation の適用規則、report 入力、本文取得、NOTICE を同時に実装しない。policy file の schema、precedence、exception の identity と監査項目、exit code 契約を固定し、その結果を見て Gap 2 へ進む。
