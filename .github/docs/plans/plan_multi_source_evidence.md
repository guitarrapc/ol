# SBOM・package manager・repository を突き合わせる証拠モデルの回復

## この文書の位置付け

Ol の出発点は「SBOM と package manager と source repository を組み合わせて証拠を固める」ことだった。SBOM が最も整理された入力であることを前提に、SBOM だけでは見落とすもの、実際にはずれているものを他の経路で補う。5 つのエコシステムを実測した結果、この組み合わせが成立していないことが分かった。組み合わせるほど結果が悪くなる経路が存在し、片方の入力しか選べない制約が仕様として明文化されている。

測定は入力経路の優劣を決めるためのものではない。どの経路が何を落とすかを知り、補い合わせるために行った。この文書は、その測定結果と、組み合わせを機能させるための実装順序を定める。実装済み仕様ではない。

Phase 1 は 2026-08-09 に実装済み。結果は当該節に記録した。

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

### 1. 選言を満たす観測を conflict にしている

Cargo の 94 件の conflict のうち 93 件が同一パターンだった。

```text
dependency-input   = MIT OR Apache-2.0
cargo-registry     = MIT OR Apache-2.0
github-license-api = Apache-2.0
```

`Apache-2.0` は `MIT OR Apache-2.0` を充足する。矛盾ではない。repository には両方の license file が置かれており、GitHub License API はそのうち一方を報告しているだけである。

[spdx.md](../specs/spdx.md) は「If valid license candidates disagree, status is `conflict`」とだけ定めており、式どうしの関係を持たない。そのため文字列としての差がそのまま conflict になる。デュアルライセンスが慣習である Rust では、これが収集を有害化させる。オフラインで 164 件 matched だったものが、収集を足すと 76 件 matched + 94 件 conflict になる。`check` は conflict を fail closed で扱うため、健全な依存が 94 件の違反として報告される。

証拠を突き合わせるほど結論が悪化するのは、Ol の出発点そのものの否定である。

### 2. `component.evidence.licenses` を使えていない

`cyclonedx-gomod` は検出したライセンスを既定で `component.evidence.licenses` に置く。「検出結果が正しい保証がないため」というのがツール側の理由であり、Ol の declaration と detection の区別と同じ考えである。測定した Go SBOM では 39/39 コンポーネントがそこに正しい SPDX ID を持っていた。Ol はそれを読まないため、`-assert-licenses` を付けずに生成された SBOM では C 条件が 0 件になる。

これは見落としではなく [spdx.md](../specs/spdx.md) に記録された意図的な保留であり、その理由は「観測ライセンスの列は AND/OR 関係を述べないため、結論済みの式と安全に比較できない」である。欠陥 1 と同じ、式の関係意味論の不在が阻害要因になっている。

### 3. ジェネレータの非回答をライセンス名として受理している

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

### Phase 1: 式の関係判定と偽 conflict の除去 (実装済み)

1. 上の表の関係を判定する failing tests を追加した。判定しない組が conflict のままであることも含む。
2. reconciler が充足関係を conflict にしないようにした。報告する式は広い方 (選択肢を残す方) とする。
3. Cargo の実測形 (`MIT OR Apache-2.0` 宣言 + `Apache-2.0` 観測) を回帰テストに含めた。
4. 真の不一致が conflict のまま残ることを、npm の実測例 (`@webassemblyjs/leb128@1.13.2`: 宣言 `Apache-2.0` / repository `MIT`) で確認した。
5. [spdx.md](../specs/spdx.md) に [expression agreement](../specs/spdx.md#contract-expression-agreement) を追加した。

実測結果。Cargo の A 条件は **76 matched + 94 conflict から 169 matched + 1 conflict** になった (予測どおり解消 93 件)。残る 1 件は `unicode-ident@1.0.24` の `(MIT OR Apache-2.0) AND Unicode-3.0` で、最上位が `AND` のため判定しない設計どおりの結果である。Cargo の B 条件は 59 から 126 matched になった。NuGet、npm、Python、Go は変化なく、npm の真の conflict も保持された。

allocation は変更前後でビット単位で一致した (`DependencyInputScannerBenchmark` 全 29 行と `E2EBenchmark` 全 4 行で ±0)。mean は 1 iteration 設定のため分解能が足りず、同一コード状態の再実行でも最大 19.1% 振れる。閾値を超えた行はいずれもその範囲内で、かつ新しい経路 (matched 候補が 2 件以上のときのみ実行) を通らない benchmark だった。

### Phase 2: detection 証拠の取り込み

1. `component.evidence.licenses` を読む failing tests を追加する。単一要素、複数要素、declaration との一致・充足・不一致を網羅する。
2. 候補の種別に detection を追加し、報告と cache に保持する。
3. Go の実測 SBOM を fixture とし、`-assert-licenses` の有無で結果が同じになることを確認する。
4. [spdx.md](../specs/spdx.md) の当該保留記述を、実装した内容に置き換える。

### Phase 3: 宣言された参照のモデル化

1. 参照の型と種別を追加し、`PackageMetadataResponse`、cache、evidence、報告に通す。
2. NuGet (`licenseUrl`/`licenseFile`)、SBOM (`license.name` + `url`)、npm legacy 配列、Cargo `license_file` を供給元として実装する。
3. 名前の完全一致解決を実装する。`wrench@1.5.9` が MIT に解決することを確認する。
4. `Unknown - See URL` 相当がライセンス名ではなく参照として扱われ、`ambiguous` にならないことを確認する。
5. NuGet 固有の warning 語彙を横断語彙へ移行する。旧識別子の互換方針と報告 `schemaVersion` の扱いをここで決める。
6. [packagemanager.md](../specs/packagemanager.md)、[cache_format.md](../specs/cache_format.md)、[cli.md](../specs/cli.md) を更新する。

### Phase 4: 入力の組み合わせ

1. SBOM と package manager 入力の同時 scan を許す failing tests を追加する。
2. purl による突き合わせと、片方にしかない component の保持を実装する。
3. 入力ごとの供給範囲を報告に追加する。
4. [cli.md](../specs/cli.md) の「must be scanned separately」を改める。

### Phase 5: 再測定

Phase 1 から 4 の後、この文書の 5 エコシステム 4 条件を同じ手順で測り直し、表を更新する。条件 A と B の和が、それぞれ単独を下回らないことを確認する。

## Test matrix

| 宣言 | detection / repository 観測 | 期待 |
|---|---|---|
| `MIT` | `MIT` | matched `MIT` |
| `MIT OR Apache-2.0` | `Apache-2.0` | matched `MIT OR Apache-2.0`、conflict にしない |
| `Apache-2.0 OR MIT` | `MIT OR Apache-2.0` | matched、集合として一致 |
| `MIT` | `Apache-2.0` | conflict |
| `(MIT OR Apache-2.0) AND Unicode-3.0` | `Apache-2.0` | conflict。連言を分配しない |
| `Apache-2.0 WITH LLVM-exception OR Apache-2.0 OR MIT` | `Apache-2.0` | matched。最上位選言要素なので充足する |
| `Apache-2.0 WITH LLVM-exception OR MIT` | `Apache-2.0` | conflict。例外付きの要素とは一致しない |
| なし | detection 単一要素 | matched |
| なし | detection 複数要素 | ambiguous、AND/OR を補わない |
| `MIT` | detection 複数要素に `MIT` を含む | matched `MIT`、補強として保持 |
| 参照のみ (名前 `MIT`) | なし | matched `MIT` |
| 参照のみ (名前 `BSD`) | なし | unknown、参照を保持 |
| 参照のみ (名前 `Unknown - See URL` + URL) | なし | unknown、参照として保持、ambiguous にしない |
| SBOM と PM の両方に同一 purl | 双方が宣言 | 双方を候補として保持 |
| SBOM のみに存在する purl | — | 報告に残り、供給入力が分かる |
| PM のみに存在する purl | — | 報告に残り、供給入力が分かる |

## 仕様更新

| 文書 | 更新内容 |
|---|---|
| [spdx.md](../specs/spdx.md) | 式の関係判定の範囲と、判定しない組の扱い。conflict の定義。detection 証拠の意味論と、現在の保留記述の置き換え |
| [cli.md](../specs/cli.md) | 複数入力の組み合わせ規則。入力ごとの供給範囲の報告。参照に由来する warning の利用者向け意味 |
| [packagemanager.md](../specs/packagemanager.md) | 宣言された参照の供給元と横断語彙。npm legacy `licenses` 配列 |
| [cache_format.md](../specs/cache_format.md) | 参照と detection 証拠の永続化、互換方針 |
| [Architecture.md](../Architecture.md) | 「Preserve evidence instead of selecting one source」を、入力経路の排他が否定していた事実と、その解消 |
| [verification.md](../specs/verification.md) | 5 エコシステム 4 条件の再測定を検証手順として位置付けるか判断する |

## 完了条件

- 選言を満たす観測が conflict にならず、真の不一致は conflict のまま残る。
- 判定できない関係を判定したことにしていない。
- `evidence.licenses` が detection として取り込まれ、declaration と区別されて報告される。
- 参照がライセンス名として報告されない。
- SBOM と package manager 入力を一度に scan でき、母集団の差が報告から分かる。
- 5 エコシステムの再測定で、条件 A と B の和が単独条件を下回らない。
- 全テスト、ecosystem smoke、関連ベンチマークが合格する。

## Lessons learned

- **入力経路の優劣は一般化できない。** 「SBOM を使えばよい」も「package manager が確実」も、エコシステム単位では両方とも反例がある。決めているのは経路ではなく、そのエコシステムでライセンス事実がどこに存在するかである。だからどれか一つを選ぶのではなく、それぞれが何を落とすかを知ったうえで重ねる必要がある。
- **SBOM ジェネレータの品質差は経路の差より大きい。** 同じ CycloneDX でも、artifact の本文を読むもの (`cyclonedx-gomod`) とメタデータしか見ないもの (`CycloneDX` .NET tool) で結果が正反対になる。SBOM を入力にすることは、解決をジェネレータに委譲することであって、解決が保証されることではない。
- **証拠を増やすと悪化する経路があった。** Cargo で収集を足すと matched が 164 から 76 に落ちた。複数ソースの突き合わせは、比較の意味論を伴わなければ精度を下げる。
- **意図的な保留が別の欠陥の原因になっていた。** `evidence.licenses` の見送りは「AND/OR 関係を保持できないから」という正しい理由で記録されていたが、その同じ不足が Cargo の偽 conflict を生んでいた。保留の理由が他の症状として現れていないかを見る価値がある。
- **ジェネレータの非回答は入力として渡ってくる。** `Unknown - See URL` のような文字列は、ライセンス名の形をした「わかりません」である。入力の値をそのまま信じると、Ol は他人の推測を自分の結論として報告してしまう。
