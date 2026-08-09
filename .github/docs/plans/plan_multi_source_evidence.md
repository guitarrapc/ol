# SBOM・package manager・repository を突き合わせる証拠モデルの回復

## この文書の位置付け

Ol の出発点は「SBOM と package manager と source repository を組み合わせて証拠を固める」ことだった。SBOM が最も整理された入力であることを前提に、SBOM だけでは見落とすもの、実際にはずれているものを他の経路で補う。5 つのエコシステムを実測した結果、この組み合わせが成立していないことが分かった。組み合わせるほど結果が悪くなる経路が存在し、片方の入力しか選べない制約が仕様として明文化されている。

測定は入力経路の優劣を決めるためのものではない。どの経路が何を落とすかを知り、補い合わせるために行った。この文書は、その測定結果と、組み合わせを機能させるための実装順序を定める。実装済み仕様ではない。

Phase 1 から Phase 3 は 2026-08-09 に、Phase 4 は 2026-08-10 に実装済み。結果は各節に記録した。残るのは Phase 5 (再測定) である。

## 背景: 測定

2026-08-09 に、5 エコシステムそれぞれの実プロジェクトを 4 条件で計測した。

| 条件 | 入力 | 外部証拠収集 |
|---|---|---|
| A | package manager の解決済み入力 | あり |
| B | CycloneDX SBOM | あり |
| C | CycloneDX SBOM | `--no-external-evidence` |
| D | package manager の解決済み入力 | `--no-external-evidence` |

SBOM は各エコシステム純正のジェネレータで生成した (`CycloneDX` .NET tool 6.2.0、`@cyclonedx/cyclonedx-npm`、`cyclonedx-py`、`cyclonedx-gomod`、`cargo-cyclonedx` 0.5.9)。

### 解決率

| エコシステム | A: PM+収集 | B: SBOM+収集 | C: SBOM 単独 | D: PM 単独 |
|---|---:|---:|---:|---:|
| NuGet (Dapper, 260) | **199** | 199 (+48 ambiguous) | 193 (+51 ambiguous) | 0 |
| npm (lock 247 / SBOM 230) | 246 (+1 conflict) | 229 (+1 conflict) | 230 | **247** |
| Python (51) | 45 | **48** | 40 | 34 |
| Go (graph 105 / SBOM 40) | 71 | **39/40** | **39/40** | 0 |
| Cargo (meta 172 / SBOM 127) | 76 (**+94 conflict**) | 59 (+68 conflict) | 127 | **164** |

### 測定から読み取れること

**入力経路の優劣はエコシステムごとに逆転する。** ライセンス事実がどこに存在するかで 3 つに分かれる。

| クラス | 事実の所在 | 該当 | 帰結 |
|---|---|---|---|
| 1 | 解決済み入力に内包 | npm、Cargo | PM 経路がオフラインで完結する |
| 2 | 解決済み入力に皆無 | NuGet、Go | 収集に全面的に依存する |
| 3 | インストール済み成果物 > registry API | Python | SBOM が優位 |

**クラス 2 でも SBOM の価値はジェネレータ次第で正反対になる。** `cyclonedx-gomod` は module cache の LICENSE を実際に読んで判定するため SBOM 単独で 39/40 に達する。`CycloneDX` .NET tool は nuspec しか見ないため、NuGet では SBOM に切り替えても解決は増えない。実際、NuGet の SBOM が解決する集合は Ol の PM 経路の真部分集合だった (SBOM のみが解決 = 0 件、Ol のみが解決 = 6 件)。

**入力によって母集団が変わる。** npm は lockfile 247 に対し SBOM 230 で、lockfile にしか存在しない 53 件がある。Go は module graph 105 に対し SBOM 40 で、graph は build に入らないモジュールまで数える。Cargo は metadata 172 に対し SBOM 127。Ol はこの差を報告しない。コンプライアンスでは「足りない」方向が危険である。

## 判明した欠陥

### 1. 選言を満たす観測を conflict にしている (Phase 1 で解消)

Cargo の 94 件の conflict のうち 93 件が同一パターンだった。

```text
dependency-input   = MIT OR Apache-2.0
cargo-registry     = MIT OR Apache-2.0
github-license-api = Apache-2.0
```

`Apache-2.0` は `MIT OR Apache-2.0` を充足する。矛盾ではない。repository には両方の license file が置かれており、GitHub License API はそのうち一方を報告しているだけである。

[spdx.md](../specs/spdx.md) は「If valid license candidates disagree, status is `conflict`」とだけ定めており、式どうしの関係を持たない。そのため文字列としての差がそのまま conflict になる。デュアルライセンスが慣習である Rust では、これが収集を有害化させる。オフラインで 164 件 matched だったものが、収集を足すと 76 件 matched + 94 件 conflict になる。`check` は conflict を fail closed で扱うため、健全な依存が 94 件の違反として報告される。

証拠を突き合わせるほど結論が悪化するのは、Ol の出発点そのものの否定である。

### 2. `component.evidence.licenses` を使えていない (Phase 2 で解消)

`cyclonedx-gomod` は検出したライセンスを既定で `component.evidence.licenses` に置く。「検出結果が正しい保証がないため」というのがツール側の理由であり、Ol の declaration と detection の区別と同じ考えである。測定した Go SBOM では 39/39 コンポーネントがそこに正しい SPDX ID を持っていた。Ol はそれを読まないため、`-assert-licenses` を付けずに生成された SBOM では C 条件が 0 件になる。

これは見落としではなく [spdx.md](../specs/spdx.md) に記録された意図的な保留であり、その理由は「観測ライセンスの列は AND/OR 関係を述べないため、結論済みの式と安全に比較できない」である。欠陥 1 と同じ、式の関係意味論の不在が阻害要因になっている。

### 3. ジェネレータの非回答をライセンス名として受理している (Phase 3 で参照として保持)

`CycloneDX` .NET tool は、nuspec が `licenseExpression` を持たない package に対して次を出力する。

```json
{ "license": { "name": "Unknown - See URL", "url": "https://raw.githubusercontent.com/antlr/antlrcs/master/LICENSE.txt" } }
```

Ol はこれを SBOM のライセンス名として取り込み、`license: "Unknown - See URL (?)"`、`status: ambiguous` と表示する。NuGet の SBOM 経路で 48 件がこの状態になった。ジェネレータの「わかりません」を、あたかもライセンス名であるかのように印字している。

`name` と `url` の組は、[plan_nuget_license_file.md](plan_nuget_license_file.md) が扱う NuGet の `licenseUrl`／`licenseFile` と同じもの、すなわち publisher が示したライセンスの所在であって、ライセンスそのものではない。同じ形は npm の legacy `licenses[{type,url}]`、Cargo の `license_file`、CocoaPods の `license.file`／`text`、PyPI の `license_files` にも存在する。

npm の legacy 形式については、Ol が値を読まないことによる解決漏れも確認した。`wrench@1.5.9` は registry が `licenses: [{ "type": "MIT", ... }]` を返すが、Ol は `license` (単数) しか読まないため `unknown` になる。機械的に決まる SPDX ID を落としている。

### 4. SBOM と package manager 入力を同時に scan できない

```console
$ ol scan --input pip-inspect.json --input py.cdx.json
Unable to scan input: Multiple inputs must all be package-manager inputs.
```

[cli.md](../specs/cli.md) が「A repository-wide SBOM and direct package-manager inputs are alternative authoritative sources and must be scanned separately」と規定している。

測定はこの前提を支持しない。Python は SBOM が 48、PM が 45 で互いに解決できない要素を持ち、Go は SBOM が母集団として正確で PM が過大計上し、npm は lockfile が母集団として完全で SBOM が 53 件欠落する。どちらか一方を選ばせる限り、精度はエコシステムごとに上限で頭打ちになる。

## 根本原因

欠陥 1 と 2 は同じ一つの能力の不在に帰着する。**Ol は SPDX 式どうしの関係を判定できない。** 判定できるのは「正規化できたか」だけで、ある式が別の式を充足するか、包含するか、両立しないかを言えない。

そのため次が同時に起きる。

- 選言を満たす観測が矛盾に見える (欠陥 1)
- 関係を述べない観測の列を安全に取り込めない (欠陥 2)

欠陥 3 と 4 は独立で、それぞれ「宣言と参照の混同」「入力を排他にする仕様」に由来する。

## 目標

1. 選言を満たす観測が conflict にならず、真の不一致だけが conflict になる。
2. `component.evidence.licenses` を detection 種別の証拠として取り込み、assert された `licenses` と区別して保持する。
3. ライセンスの所在の宣言を、エコシステム横断の一つのモデルとして表現し、ライセンス名と混同しない。
4. SBOM と package manager 入力を一度の scan で突き合わせられる。
5. 入力ごとの母集団の差が報告から分かる。
6. どの結論も、どの証拠の組み合わせから来たかを説明できる。

## 非目標

- 式の関係判定を根拠に、証拠のどれかを削除したり優先順位で勝たせたりしない。関係が分かっても候補は保持する。
- 選言のうち一つを Ol が選ばない。`MIT OR Apache-2.0` は `MIT OR Apache-2.0` のまま報告する。選択は policy の領域である。
- SPDX 式の完全な充足性判定器を作らない。実データに現れる関係 (選言の要素、同一集合、包含) に限り、判定できない組は従来どおり conflict として保持する。
- `evidence.licenses` を `licenses` と同格に扱わない。detection は declaration ではない。
- 参照 (URL、artifact 内 path、名前、埋め込み本文) から SPDX ID を推測しない。
- 入力の母集団差を Ol が補完しない。差の報告にとどめる。
- ジェネレータごとの癖を名前で特別扱いしない。`Unknown - See URL` という文字列を条件に書かない。

## 採用する境界

### 式の関係判定

正規化済みの二つの式について、**最上位の選言要素の集合**だけを見て判定する。要素の内部構造は解釈しない。

| 関係 | 例 | 扱い |
|---|---|---|
| 同一 | `MIT` と `MIT` | 一致 |
| 一方が他方の最上位選言要素 | `Apache-2.0` と `MIT OR Apache-2.0` | 充足。conflict にしない |
| 最上位選言集合が等しい | `MIT OR Apache-2.0` と `Apache-2.0 OR MIT` | 一致 |
| それ以外 | `MIT` と `Apache-2.0` | conflict |

最上位が `OR` でない式は、選言要素が一つの式として扱う。したがって最上位が `AND` の式は、その全体と一致する場合しか一致しない。`(MIT OR Apache-2.0) AND Unicode-3.0` と `Apache-2.0` は conflict のまま残る。連言を分配して要素を取り出すことはしない。

要素の内部に `WITH` や括弧があっても、その要素自体を文字列として比較するだけなので判定できる。`Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT` と `Apache-2.0` は、後者が最上位選言要素の一つなので充足する。例外の意味論を解釈しているわけではない。

実測した Cargo の 94 件の conflict のうち、この規則で解消するのは 93 件、`AND` を含むため conflict のまま残るのが 1 件である。

充足関係が成立したとき、報告する式は**より制約の強い方ではなく宣言側**とする。`MIT OR Apache-2.0` を宣言した package は、repository に Apache-2.0 の本文があっても `MIT OR Apache-2.0` である。利用者は選言のどちらでも選べる。Ol が repository の観測を根拠に選択肢を狭めるのは、証拠が言っていないことを言うことになる。

### detection 証拠

`component.evidence.licenses` は独立した候補として保持し、declaration 由来の候補と種別で区別する。列が複数の要素を持つ場合、その関係が不明であることを保持したまま扱う。

関係が不明な列は、単独では結論を作らない。declaration と一致するか充足関係にあるとき、その declaration を補強する証拠になる。declaration が無い場合は、列が単一要素のときに限り候補となり、複数要素なら `ambiguous` とする。ここで AND/OR を勝手に補わない。

### 宣言された参照

publisher が示したライセンスの所在を、ライセンス値とは別の型で表す。型名と種別名の両方で「参照は本文ではない」ことを表す。

| 種別 | 供給元 |
|---|---|
| 場所 (URL) | NuGet `licenseUrl`、SBOM `license.url`、npm legacy `licenses[].url`、Maven POM `<url>` |
| artifact 内 path | NuGet `licenseFile`、Cargo `license_file`、CocoaPods `license.file`、PyPI `license_files`、npm `SEE LICENSE IN` |
| 名前 | npm `licenses[].type`、CocoaPods `license.type`、SBOM `license.name` |
| 埋め込み本文 | CocoaPods `license.text`、PyPI `license` 長文 |

名前は、版固定の SPDX データに対する完全一致でのみ解決する。`MIT` は解決し、`BSD` は多義なので解決しない。場所と artifact 内 path の解決は本文照合を要するため [plan_nuget_license_file.md](plan_nuget_license_file.md) の範囲であり、この文書では参照として保持することまでを扱う。埋め込み本文は存在の記録のみとし、本文を既定報告に含めない。

### 入力の組み合わせ

SBOM と package manager 入力を一つの収集として扱えるようにする。同一性は purl で突き合わせ、purl を持たない component は突き合わせない。

一方にしか存在しない component は、そのまま両方の和として報告する。Ol は欠落を補完しない。どの入力がどの component を供給したかを報告に残し、母集団の差を利用者が見られるようにする。

入力間で依存辺を発明しない。グラフはそれぞれの入力の文脈のまま保持する。これは既存の複数 package manager 入力の規則と同じである。

## 実施順序

### Phase 1-3 (実装済み)

確定した仕様は spec にある。ここには残作業の前提になる結果だけを残す。

| Phase | 内容 | 仕様 |
|---|---|---|
| 1 | 式の関係判定。充足する観測を conflict にしない | [expression agreement](../specs/spdx.md#contract-expression-agreement) |
| 2 | `component.evidence.licenses` を detection 証拠として取り込む | [observed licenses](../specs/spdx.md#contract-observed-licenses) |
| 3 | 宣言された参照を横断モデルとして保持する | [declared license reference](../specs/spdx.md#contract-declared-license-reference)、[unresolved section](../specs/cli.md#contract-unresolved-section) |

欠陥 1 と 2 は解消した。Cargo の A 条件は 76 matched + 94 conflict から **169 matched + 1 conflict** になり、B 条件は 59 から 126 matched になった。残る 1 件は `unicode-ident` の `(MIT OR Apache-2.0) AND Unicode-3.0` で、後に第二の関係規則を追加して解消した。`-assert-licenses` 無しで生成した Go SBOM の C 条件は **0 から 39/40 matched** になった。欠陥 3 は参照として保持する形で解消し、解決率はどのエコシステムでも変わっていない (参照は結論を作らない)。

上の測定表は Phase 1-3 より前の値である。Phase 5 の再測定はこの表を更新する。

### Phase 4 (実装済み)

欠陥 4 も解消した。SBOM 1 つと package manager 入力 N 個を一度に scan でき、purl identity で突き合わせる。確定した仕様は [input combination](../specs/cli.md#contract-input-combination) と [component supply](../specs/cli.md#contract-component-supply) にある。

実装中に決めた、計画時点では未定だった点を記録する。

- **突合は purl の identity 部分で行う。** qualifier と subpath を落とし、format が既に宣言している大小文字規則で比較する。Ol 自身が maven・gem・cocoapods で qualifier 付き purl を出すため、purl 全体の一致では実測のほとんどが突合されない。
- **fan-out の向きは package manager 側が行を持つ。** SBOM が 1 つの component として述べるものを package manager は install path ごとに分けて持つ。潰すと母集団が package manager 単独より減るため、SBOM 側を吸収して宣言を全行に配る。SBOM の occurrence は入力順で最初の行に付く。SBOM が区別していない以上、これ以外に発明せずに選べる端点がない。
- **突合は SBOM の境界だけに適用する。** lockfile 同士は別々のインストールを記述しているので、同じ purl でも別の観測である。加えて、どちらが行を持つかを決める根拠が無い。
- **母集団差の表現は component の supply とした。** context に入力の情報を持たせる案は、SBOM が context を持たないため合成 context の追加を要し、SBOM 単独 scan の出力まで変えてしまう。候補の source から逆算する案は、宣言を持たない component が候補を持たないため機能しない。母集団差で問題になるのはまさにその種の component である。
- **`schemaVersion` は 1 のまま据え置いた。** reader が完全一致で判定するため、追加フィールドのために上げると利用者の保存済み report が読めなくなる。

### Phase 5: 再測定

5 エコシステムを測り直し、上の表を更新する。条件は 4 つから 5 つに増やす。

| 条件 | 入力 | 外部証拠収集 |
|---|---|---|
| A | package manager の解決済み入力 | あり |
| B | CycloneDX SBOM | あり |
| C | CycloneDX SBOM | `--no-external-evidence` |
| D | package manager の解決済み入力 | `--no-external-evidence` |
| E | SBOM + package manager の同時入力 | あり |

条件 E を直接測れるようになったので、完了条件は「A と B の和」という抽象的な言い方をやめ、**E が A も B も下回らない**という形にする。あわせて、E の母集団が A と B のいずれの母集団も包含することを確認する。

## Test matrix

Phase 1-4 の等価クラスは回帰テストとして存在する。Phase 4 の分は `MixedInputScanTests` にある。残りは Phase 5 の分で、これはテストではなく測定である。

## 仕様更新

| 文書 | 更新内容 | 状態 |
|---|---|---|
| [cli.md](../specs/cli.md) | 複数入力の組み合わせ規則。入力ごとの供給範囲の報告 | 済 |
| [Architecture.md](../Architecture.md) | 「Preserve evidence instead of selecting one source」を、入力経路の排他が否定していた事実と、その解消 | 済 |
| [verification.md](../specs/verification.md) | 5 エコシステム 4 条件の再測定を検証手順として位置付けるか判断する | 済。契約は ecosystem smoke に入れ、解決率は CI の合格条件にしない |

## 完了条件

- SBOM と package manager 入力を一度に scan でき、母集団の差が報告から分かる。**達成済み。**
- 5 エコシステムの再測定で、条件 E が条件 A も B も下回らない。**Phase 5 で確認する。**
- 全テスト、ecosystem smoke、関連ベンチマークが合格する。**Phase 4 時点で合格。**

## この計画の値打ちについて

実装前に見積もった時点で、Phase 4 が解決率に足すものはほぼ無いと分かっていた。Phase 1-3 が既に取り切っていたためである。それでも実施したのは、獲得が解決率ではなく**母集団差の可視化と結論の由来説明**にあるからで、完了条件もそう読めるように書き換えてある。Phase 5 の測定は「増えたか」ではなく「減っていないか」を確認するものである。

## Lessons learned

Phase 1-3 の lessons learned は spec へ移した。入力経路と SBOM ジェネレータの品質差は [packagemanager.md](../specs/packagemanager.md#lessons-learned)、式の関係判定と collection の結論押し下げは [spdx.md](../specs/spdx.md#lessons-learned)、warning 語彙の予算は [backlog.md](../backlog.md#warning-vocabulary-budget) にある。
