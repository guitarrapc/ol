# SBOM・package manager・repository を突き合わせる証拠モデルの回復

## この文書の位置付け

Ol の出発点は「SBOM と package manager と source repository を組み合わせて証拠を固める」ことだった。SBOM が最も整理された入力であることを前提に、SBOM だけでは見落とすもの、実際にはずれているものを他の経路で補う。5 つのエコシステムを実測した結果、この組み合わせが成立していないことが分かった。組み合わせるほど結果が悪くなる経路が存在し、片方の入力しか選べない制約が仕様として明文化されている。

測定は入力経路の優劣を決めるためのものではない。どの経路が何を落とすかを知り、補い合わせるために行った。この文書は、その測定結果と、組み合わせを機能させるための実装順序を定める。実装済み仕様ではない。

Phase 1 から Phase 3 は 2026-08-09 に実装済み。結果は各節に記録した。残るのは Phase 4 (入力の組み合わせ) と Phase 5 (再測定) である。

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

### Phase 1: 式の関係判定と偽 conflict の除去 (実装済み)

1. 上の表の関係を判定する failing tests を追加した。判定しない組が conflict のままであることも含む。
2. reconciler が充足関係を conflict にしないようにした。報告する式は広い方 (選択肢を残す方) とする。
3. Cargo の実測形 (`MIT OR Apache-2.0` 宣言 + `Apache-2.0` 観測) を回帰テストに含めた。
4. 真の不一致が conflict のまま残ることを、npm の実測例 (`@webassemblyjs/leb128@1.13.2`: 宣言 `Apache-2.0` / repository `MIT`) で確認した。
5. [spdx.md](../specs/spdx.md) に [expression agreement](../specs/spdx.md#contract-expression-agreement) を追加した。

実測結果。Cargo の A 条件は **76 matched + 94 conflict から 169 matched + 1 conflict** になった (予測どおり解消 93 件)。残る 1 件は `unicode-ident@1.0.24` の `(MIT OR Apache-2.0) AND Unicode-3.0` で、最上位が `AND` のため判定しない設計どおりの結果である。Cargo の B 条件は 59 から 126 matched になった。NuGet、npm、Python、Go は変化なく、npm の真の conflict も保持された。

allocation は変更前後でビット単位で一致した (`DependencyInputScannerBenchmark` 全 29 行と `E2EBenchmark` 全 4 行で ±0)。mean は 1 iteration 設定のため分解能が足りず、同一コード状態の再実行でも最大 19.1% 振れる。閾値を超えた行はいずれもその範囲内で、かつ新しい経路 (matched 候補が 2 件以上のときのみ実行) を通らない benchmark だった。

### Phase 2: detection 証拠の取り込み (実装済み)

1. `component.evidence.licenses` を読む failing tests を追加した。declaration 側 (無し・解決・解決不能・不正) と observed 側 (無し・単一・複数・不正) の組み合わせを等価クラスとして網羅した。
2. `SbomLicenseField.CycloneDxEvidenceLicenses` を追加し、detection の出所を typed evidence に残した。
3. Go の実測 SBOM で、`-assert-licenses` の有無にかかわらず同じ結果になることを確認した。
4. [spdx.md](../specs/spdx.md) の保留記述を [observed licenses](../specs/spdx.md#contract-observed-licenses) に置き換えた。

実測結果。`-assert-licenses` 無しで生成した Go SBOM の C 条件が **0 から 39/40 matched** になり、`-assert-licenses` 版と一致した。NuGet、npm、Python、Cargo の B/C 条件はいずれも Phase 1 時点から変化なし。

実装中に、計画になかった欠陥が 1 つ見つかった。複数 ID の列は collection 全体としては `ambiguous` と報告されるのに、その各 entry は個別に解決済みのまま残っていた。そのため後から evidence source が 1 つ加わると、reconciler が「解決済みの主張が 2 つ食い違っている」と読んで、どの source も述べていない conflict を作った。これは `component.licenses` にも同じように存在した既存の欠陥で、collection の結論を entry へ押し下げることで両方を修正した。detection 用に別の reconciliation 規則は必要なかった。

allocation は変更前後で全行不変。mean は `ScanCycloneDx` で +1.7%、閾値超えは無し。最初の測定では package manager 系の行が一斉に +75〜85% と出たが、同一コードの再測定が baseline と一致したため測定順による環境ドリフトだった。変更が触る CycloneDX の行は 3 回の測定を通じて平坦だった。

### Phase 3: 宣言された参照のモデル化 (実装済み)

実装した範囲。

1. `DeclaredLicenseReference` と種別 (`Location` / `ArtifactPath`) を追加し、`LicenseEvidence` と報告に通した。
2. SBOM (`license.url`) を供給元として実装した。
3. npm の legacy 宣言形 (`license` object、`licenses` object、`licenses` 単一要素配列) を読むようにした。`wrench@1.5.9` が `unknown` から MIT に解決する。
4. 報告に出した。JSON evidence に `declaredLicenseReferenceKind` と `declaredLicenseReference` を追加し、text/Markdown の未解決セクションでは宣言された場所が他のどの参照よりも優先される。
5. [spdx.md](../specs/spdx.md#contract-declared-license-reference)、[cli.md](../specs/cli.md#contract-unresolved-section)、[packagemanager.md](../specs/packagemanager.md) を更新した。

実測結果。NuGet の SBOM 経路で 51 件の未解決コンポーネントが、宣言された URL 付きで未解決セクションに並ぶようになった。`https://www.devexpress.com/Support/EULAs` や `http://go.microsoft.com/fwlink/?LinkID=320539` を含む。**これらは registration endpoint が書き換えて消してしまう値であり、SBOM 経路だけが供給できる**。SBOM と package manager を重ねる意味がそのまま現れた例になった。解決率はどのエコシステムでも変化していない。参照は結論を作らないという境界どおりである。

allocation は `LicenseEvidence` に nullable 参照を 1 つ足した分だけ増えた (+8 byte/candidate、`DependencyInputScannerBenchmark` で最大 +3.0%、`E2EBenchmark` で最大 +1.0%)。最初は値型で実装して +24 byte (最大 +9.1%) になったため、他の provenance 形と同じ nullable class に変えて 3 分の 1 に落とした。mean はこの benchmark 設定では分解能が足りない (同一コードの再測定で `ScanNuGetJsonWithCachedMetadata` が 486.9 から 372.0 μs に振れる)。

後半も実装した。

6. NuGet (`licenseFile` / `licenseUrl`)、Cargo (`license_file`)、PyPI (`license_files`)、CocoaPods (`license.file` / `text`) を供給元にした。`PackageMetadataResponse`、`PackageMetadataRecord`、package metadata cache、`PackageMetadataCacheEntry`、候補生成まで参照を通した。cache は任意プロパティ 2 つの追加で済み、schema version は据え置いた。`InlineText` 種別を追加し、埋め込み本文は存在の記録のみで内容を保持しない。
7. resolver capability version を 4 に上げ、再収集の対象を全エコシステムへ広げた。どの provider も参照を述べられるようになった以上、license が空の観測はひとつのエコシステムだけでなくどこでも古い。

実測結果。NuGet の package manager 経路で、宣言された場所が未解決セクションに並ぶようになった。`https://www.devexpress.com/Support/EULAs`、`http://go.microsoft.com/fwlink/?LinkID=320539` のような location と、`LICENSE.txt`、`MIT-LICENSE.txt` のような artifact path の両方である。これは以前「registration endpoint が消すため package manager 経路では出せない」と記録した値で、SBOM 経路と package manager 経路の双方から同じ形で得られるようになった。

allocation は cache 経路で変化しなかった (`CacheReadBenchmark.PackageCacheHit` が 880 byte のまま)。cache entry の参照値を owned string ではなく `Utf8Slice` にしたため、参照を持たない大多数の entry が読み取りで何も追加で確保しない。`E2EBenchmark` は参照が実際に流れる NuGet の行だけ +0.3%、`DependencyInputScannerBenchmark` は不変。mean が閾値を超えた行は無い。

### 判断: warning 語彙は改名しない

当初 8 番目の項目として「NuGet 固有の warning 語彙を横断語彙へ移行し、旧識別子の互換方針と報告 `schemaVersion` を決める」を置いていた。これは行わないと決めた。

横断表現は typed evidence の `DeclaredLicenseReference` が担うようになった。この時点で `nuget_license_url_unsupported` を `declared_license_location_unresolved` へ改名しても、新しい事実は 1 つも運ばれない。識別子は consumer が照合する安定 ID なので、改名は名前の好み以上の理由なしに互換を壊す。

他のエコシステムに同等の warning を追加することもしない。参照を持つ未解決コンポーネントは、warning が無くても未解決セクションに status と宣言先を伴って現れる。人間が次に取る行動はそこで決まり、機械可読な事実は typed evidence にある。エコシステムごとに warning 語彙を増やすのは、この文書が避けようとした「1 エコシステムにつき 1 語彙」そのものである。

**後日の結果（追記）**: 改名しないという判断も、他エコシステムへ warning を増やさないという判断も維持された。誤っていたのは「warning が無くても status が現れるから十分」という前提のほうだった。status は機構を名指さないので、同じ事実が NuGet では `nuget_license_file_unresolved`、CocoaPods では `ambiguous` と表示され、`InlineText` は空の参照として出ていた。結論は改名でも追加でもなく、3 つの NuGet warning を**削除**して reason を `DeclaredLicenseReferenceKind` から導出することだった。詳細は [cli.md](../specs/cli.md#contract-unresolved-section) を参照。導出はこの節が守ろうとした性質をそのまま満たし、加えて 16 bit の warning 語彙から 3 bit を返した。

したがって報告 `schemaVersion` は据え置く。追加した `declaredLicenseReferenceKind` と `declaredLicenseReference` は加算的なプロパティで、既存の consumer が読む値をどれも変えない。

計画からの逸脱を 1 つ記録する。当初の項目に「`Unknown - See URL` 相当がライセンス名ではなく参照として扱われ、`ambiguous` にならないこと」があったが、これは実装しなかった。CycloneDX の `license.name` は「正規化できないライセンス名」と「ライセンス名の形をした非回答」を構造的に区別できず、区別する唯一の方法はジェネレータが書く文字列を条件に書くことである。それはこの文書自身が非目標として禁じている。`ambiguous` は「ライセンス表記はあるが推測なしには正規化できない」という定義どおりの状態であり、Ol は入力に書かれたことを忠実に報告している。人間が次に取る行動は、隣に並ぶ宣言された URL が与える。

種別についても計画から減らした。当初表に挙げた `Name` 種別は、どの供給元でも候補の `Raw` と重複するため定義していない。`InlineText` は供給元を実装する時点で追加する。

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
