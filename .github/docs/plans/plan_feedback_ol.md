# 実プロジェクト 18 件による ol の使用感と検知精度の評価

## この文書の位置付け

2026-08-10 に、6 つのパッケージマネージャーそれぞれで実在の OSS リポジトリ 3 件を選び、ol を SBOM 経路・package manager 経路・混在経路の 3 通りで実行した記録である。目的は 2 つあった。**SBOM 単独あるいは PM 単独で検知するツールではなく ol を使う理由が実データで立つか**を確かめること、そして**確定できるべきなのにできないケースを見つけて直す**ことである。

評価の過程で 4 件の欠陥を修正し、1 件の仕様変更（エコシステム単位の除外）を入れた。修正はこの文書に記録した順で実施済みで、仕様は [input combination](../specs/cli.md#contract-input-combination) と [unqueryable purl](../specs/packagemanager.md#contract-unqueryable-purl) に反映してある。未実施の提案と、ol が決めるべきでないスコープ判断は「優先度順の対応事項」に残した。

計測環境: syft 1.22.0、go 1.26.4、cargo 1.97.1、maven 3.9.16、Node v24.18.0、Python 3.14.5、Temurin JDK 21.0.12。評価スクリプトと全レポートは `.references/_eval/`（gitignore 対象）にある。

## 計測の構成

| エコシステム | プロジェクト | PM 入力 |
|---|---|---|
| NuGet | Dapper、Serilog、ImageSharp | `project.assets.json` |
| npm | axios、express、chalk | `package-lock.json` |
| PyPI | requests、flask、attrs | `pip inspect` |
| Cargo | clap、serde、tokio | `cargo metadata` |
| Go | cobra、gin、logrus | `go list -m -json all` + `go mod graph` |
| Maven | gson、commons-lang、polaris-java | `dependency:tree` |

SBOM はすべて syft で生成した。ol 自身の ecosystem smoke が syft を使っているため、CI と同じ条件を再現できる。生成器を 1 つに揃えたことで、生成器の癖がエコシステム横断でどう出るかも観測できた。

比較は**行数ではなく purl identity の集合**で行った。SBOM 生成器は同一パッケージをファイル位置ごとに 1 行ずつ出すため、行数は経路間で比較できない。

## 結果: 入力を組み合わせる価値

### 母集団はほとんど重ならない

| | SBOM が知る | PM が知る | 両方あわせて |
|---|---:|---:|---:|
| パッケージ数（18 件合計） | 1,769 | 2,842 | **3,589** |
| ライセンス確定 | 1,239 | 2,669 | **2,937** |

包含性は 18 件すべてで成立した。混在が identity を落としたケースはゼロで、最良単独を下回ったケースもゼロである。

axios が最も極端だった。

```text
package-manager          rows= 648 matched= 648
sbom                     rows= 208 matched= 179
sbom+package-manager     rows=  34 matched=  34
```

**890 行のうち両方が見たのは 34 行しかない。** SBOM はドキュメントサブプロジェクトの依存（algolia 系）を拾い、lockfile はルートの dev 依存を持つ。どちらか一方だけを見るツールは 8 割方を見落とし、しかも**見落としていること自体を報告できない**。これは解決率の問題ではなく、母集団を誰も選んでいないという問題である。

### 供給元の記録が「誰の落ち度か」を切り分ける

未解決 672 件のうち **669 件が SBOM のみ由来**だった。内訳は Dapper・Serilog・ImageSharp の 3 件で 651 件を占める。

これがあるおかげで「この依存にはライセンス情報が存在しない」と「この入力がコンポーネントをでっち上げた」を区別できる。単独 scan の報告からはこの区別が原理的に取り出せない。

## 修正した欠陥

### 欠陥 1: Go のモジュールパス分割で突合が外れる（修正済み）

syft は Go のモジュールパス後半を purl の subpath に置く。ol は subpath を捨てて突合していたため、同一モジュールが 2 行に割れた。

```text
sbom-form : pkg:golang/github.com/cpuguy83/go-md2man@v2.0.6#v2   unknown
pm-form   : pkg:golang/github.com/cpuguy83/go-md2man/v2@v2.0.6   matched (MIT)
```

最初 `#vN` に限った対症療法を書いたが、`validator@v10.30.3#v10` が漏れた。`v1` で始まる文字列を `v1` と判定していたためで、メジャー番号は先頭 1 桁ではなく数値全体で見る必要がある。さらに `#codec`（`github.com/ugorji/go/codec`）や `#loader`（`github.com/bytedance/sonic/loader`）といったメジャーバージョンでないサブモジュールも同じ根本原因だと分かった。

そこで**「Go では subpath はモジュールパスの一部である」という一般規則**に置き換えた。捨てる実装は突合を逃すだけでなく、サブモジュールを親と同一視して**別モジュールのライセンスを貼り付ける**危険があった。

| | 行数 | unknown |
|---|---|---|
| cobra | 14 → **13** | 11 → **10** |
| gin | 77 → **72** | 36 → **31** |

`sonic/loader` と `ugorji/go/codec` は unknown から **matched (Apache-2.0 / MIT)** になった。

### 欠陥 2: 解決不能の理由が誤っていた（修正済み）

polaris-java の 180 件が `unsupported_package_metadata` と報告されていた。しかし Maven は対応済みエコシステムで、実際は syft がマルチモジュール pom の子から**バージョンを持たない purl** を出していただけである。「ol は Maven に対応していない」と読める説明は端的に嘘だった。

`package_metadata_unversioned_purl` を新設して分離した。2 つは同じ「問い合わせなかった」だが、読み手の次の行動が違う。前者は入力を直す、後者は ol が対応するのを待つ。リポジトリ系の理由より上位にランク付けした。バージョンが無いことが「何も引けなかった」理由であり、リポジトリ不在を報告すると探すべきでないものを探させる。

```text
auth-block-allow-list UNKNOWN package_metadata_unversioned_purl
bcpkix-jdk15to18      UNKNOWN package_metadata_unversioned_purl
```

理由の分布は 424 件の塊が 235 + 189 に正確に分割され、他の分類は動いていない。

### 欠陥 3: 新しい警告が canonical JSON に届かなかった（修正済み）

欠陥 2 の実装中に自分で作り込んだ。canonical JSON の候補側 warning 出力が語彙とは別のリストで実装されており、新しいフラグが無言で落ちた。既存のガード `WarningVocabulary_EveryFlag_RoundTripsAndFitsItsStorage` は `ToStrings` と両 `ParseWarning` しか見ていない。

**全フラグを実際にレンダリングして JSON に届くことを確認するテスト**を追加した。語彙に登録しただけで読み手に届かない状態は、これがないと再発する。

### 欠陥 4: 原因を結果が隠していた（修正済み）

欠陥 2 の修正は unversioned だけを対象にしており、一般化できていなかった。レジストリに一度も到達できなかったコンポーネントは当然リポジトリも判明しないので、`unsupported_package_metadata` を持つ行が `source_repository_unavailable` として報告されていた。詳細と修正後の出力は P1 に記載する。

一度直したつもりの規則が、隣のケースで同じ形で破れていた。ランキングの変更は「この 1 件が上か下か」ではなく「どの分類が原因側か」で考えるべきだった。

## 確定しようがないケースと ol の検出

5 類型すべてで、ol は別々の機構名を返す。混同はない。

| 類型 | 例 | ol の報告 |
|---|---|---|
| 生成器が作った幻のコンポーネント | `pkg:nuget/Dia2Lib.dll@2.0.0.0` | `package_metadata_not_found` |
| バージョンを持たない purl | `pkg:maven/com.tencent.polaris/certificate-tsf` | `package_metadata_unversioned_purl` |
| レジストリが存在しないエコシステム | `pkg:github/actions/setup-go@v6` | `unsupported_package_metadata` |
| GitHub 以外のリポジトリホスト | `pkg:golang/golang.org/x/crypto@v0.52.0` | `unsupported_source_repository` |
| リポジトリのライセンスを分類できない | `pkg:golang/github.com/pmezard/go-difflib@v1.0.0` | `license_not_recognized` |

幻のコンポーネントについて補足する。syft を .NET のビルド出力に向けるとアセンブリをカタログ化し、`pkg:nuget/CommandLine@2.9.1.0` を出す。実体は `CommandLineParser@2.9.1` で、アセンブリ名とアセンブリバージョンであってパッケージの名前とバージョンではない。nuget.org に存在しないので確定しようがなく、ol の `package_metadata_not_found` は正しい。**利用者への助言は「.NET では syft をビルド出力に向けず、`project.assets.json` を ol に直接渡す」である。** 実際 Dapper では SBOM 単独 92 件に対し混在で 147 件が確定した。

`license_not_recognized` は ol がリポジトリまで到達できている点が重要である。`go.yaml.in/yaml/v3` のような vanity import path も Go module proxy の `Origin` 経由で解決できており、報告には読むべき LICENSE の URL が付く。

```text
go.yaml.in/yaml/v3 v3.0.4 license_not_recognized https://github.com/yaml/go-yaml/blob/refs/tags/v3.0.4/LICENSE
```

## 優先度順の対応事項

### 前提: `pkg:github/*` は誰が入れたのか

先に事実を確定しておく。**GitHub Actions を持ち込んだのは syft であり、ol でも package manager 入力でもない。** SBOM の各コンポーネントが出処を記録している。

```text
purl: pkg:github/actions/cache@v4
  syft:package:foundBy = github-actions-usage-cataloger
  syft:package:type = github-action
  syft:location:0:path = \.github\workflows\test.yml
```

生成器側で止められる。cobra は 10 → 4 コンポーネントになる。

```bash
syft "dir:.references/cobra" --select-catalogers "-github-actions-usage-cataloger" -o cyclonedx-json=out.json
```

したがって「ol が GitHub Actions を解決すべきか」は欠陥ではなく**製品スコープの判断**である。この評価では決めない。以下の P1 と P3 は、その判断とは独立に成立する項目として分けてある。

### P1: 解決不能の理由を、原因側で報告する（実施済み）

スコープ判断と無関係に、報告が不正確だった。`pkg:github/*` の未解決理由が `source_repository_unavailable` になっていた。

```text
warnings on the component : source_repository_unavailable, unsupported_package_metadata
reason shown to the reader: source_repository_unavailable
```

レジストリに一度も問い合わせられなかったコンポーネントは、当然リポジトリも判明しない。**リポジトリ不在は結果であって原因ではない。** それを報告すると、読み手は最初から探されていないものを探しに行く。

これは欠陥 2 と同じクラスで、あのとき unversioned だけに限定して直したのが不十分だった。「レジストリに到達できなかった 2 つの理由は、どちらもリポジトリ系の理由より上位」と一般化した。

```text
Unresolved components
  actions/cache v4 unsupported_package_metadata
  actions/checkout v4 unsupported_package_metadata
  actions/labeler v5 unsupported_package_metadata
```

これで「ol はこのエコシステムに対応していない」という事実がそのまま出る。スコープをどう決めるにせよ、この表示が正しい。

### P2: エコシステム単位の除外を許す（実施済み）

修正前は `--exclude-packages pkg:github/` が拒否された。

```text
Invalid license policy: Package URL prefix entries must identify at least one package or namespace, such as pkg:nuget/MyCompany.: pkg:github/
```

namespace を一つずつ列挙すれば動いたが、namespace は生成器と対象リポジトリ次第で増える。

```text
--exclude-packages "pkg:github/actions/,pkg:github/golangci/,pkg:github/msys2/"
→ Excluded from evaluation: 6 components.
```

[purl prefix の規則](../specs/cli.md#contract-purl-prefix)がエコシステムのみの接頭辞を禁じていたのは、打ち間違いが広く効くのを防ぐためだった。だが**生成器が丸ごと一つのエコシステムを注入してくる**状況をこの規則は想定しておらず、意図を禁じても意図が誤りになるわけではなく、到達不能になるだけだった。未サポートのエコシステムは今後も増える。

エコシステムで止まる接頭辞を許可し、危険性は**拒否ではなく可視性**で担保する形に変えた。選択件数は常に報告され、`--verbose` は接頭辞ごとに内訳を出す。エコシステムを名指さない接頭辞（`pkg:` など）は引き続き無効である。全体を選ぶ意図はありえないため。

```text
Exclusion prefix pkg:github/ matched 6 components.
Excluded from evaluation: 6 components.
License check failed: 3 violations.
```

9 件あった違反が、実質的な 3 件（リポジトリのライセンスを分類できない Go 依存）だけになった。

### 保留中の判断: GitHub Actions を依存として扱うか

タスクではなく決定事項なので番号を振らない。18 ケース中 15 件、235 行が該当する。

**扱わない場合**の帰結は、利用者が生成器側で止める（`--select-catalogers "-github-actions-usage-cataloger"`）か、P2 で入った `--exclude-packages "pkg:github/"` で落とす。ol は `unsupported_package_metadata` と正しく報告し続ける。この道は P2 の実施で塞がりがなくなった。

**扱う場合**、`pkg:github/{namespace}/{name}` は namespace と name がそのまま GitHub リポジトリを指すので、導出は機械的である。[GoPackageMetadataProvider](../../../src/Ol.Core/PackageManagers/GoPackageMetadataProvider.cs) が既に「識別子が取得元を述べているなら識別子からリポジトリを導出する」という判断を下しており、同じ理屈がより直接的に当てはまる。ただし新エコシステムの追加なので、対応形式一覧と [verification.md](../specs/verification.md) の ecosystem smoke 契約まで波及する。

判断材料としては、`check` に効くことを挙げておく。cobra の 9 件の違反のうち 6 件が Actions で、CI に third-party のコードを実行している以上コンプライアンス上の対象だという立場も、ライブラリ依存ではないので対象外だという立場も、どちらも筋は通る。

### P3: `golang.org/x/*` 系のライセンス確定

122 件が `unsupported_source_repository`。`golang.org/x/crypto` などは Go プロジェクトのほぼすべてが依存する。Gerrit ホストであり GitHub ではないため収集できない。

`github.com/golang/<x>` へ読み替えるマッピングは、ol が証拠にない知識を持ち込むことになるので取れない。筋の良い解は **module proxy が配る zip 内の LICENSE を読む**ことで、これは [declared license reference](../specs/spdx.md#contract-declared-license-reference) が保持している「publisher が示したライセンスの所在」を実際に読みにいく能力と同じものである。両者を一つの能力として設計するべきで、P3 に置いたのは規模の理由による。

### P4: 空 purl の合成コンポーネントを報告から除く判断

syft はスキャン対象ディレクトリ自体を purl の無い root コンポーネントとして出す。

```text
D:\github\guitarrapc\ol\.references\cobra - - - root unknown sbom
```

絶対パスが report に出ている点は [report privacy](../specs/cli.md#contract-report-privacy) の「絶対ローカルパスを含めない」と衝突しうる。ol が作った値ではなく入力が持っていた値だが、report に載る以上は判断が要る。件数は各ケース 1 件と小さい。

### P5: 削除済み plan への参照が残っている

[plan_multi_source_evidence.md](plan_multi_source_evidence.md) の 2 箇所が、既に削除された `plan_nuget_license_file.md` を参照している。内容は [declared license reference](../specs/spdx.md#contract-declared-license-reference) に移っているので、リンクを付け替えれば済む。この評価の対象外だったため手を付けていない。

### P6: SBOM 単独の行数を identity で畳むか判断する

serilog の SBOM は 506 行で 72 パッケージ、Dapper は 270 行で 192 パッケージだった。syft がファイル位置ごとに 1 行出すためで、混在 scan では PM 行に畳まれる。単独 scan の行数だけが実態より大きく見える。

畳むと SBOM 単独の出力が変わり、ファイル位置という情報も失われる。現状維持でも実害は小さいが、「SBOM 単独 506 件、混在 458 件」という一見退行に見える数字を利用者が目にする。

## コマンド履歴と出力

### 入力の生成

```bash
syft "dir:.references/gin" -o cyclonedx-json=.references/_eval/inputs/gin.cdx.json -q
```

```bash
cargo metadata --format-version 1 --all-features > .references/_eval/inputs/tokio-cargo-metadata.json
```

```bash
mvn -q -B -DoutputType=json -DoutputFile=.../gson-maven-dependency-tree.json dependency:tree
```

Go は companion file が 2 つ要る。

```bash
go list -m -json all > .references/_eval/inputs/go-cobra/go-list-modules.json
go mod graph > .references/_eval/inputs/go-cobra/go-mod-graph.txt
```

### 3 経路の比較（Dapper）

```bash
dotnet ol.dll scan --input Dapper.cdx.json --format json
dotnet ol.dll scan --input .../Dapper.Tests.Performance/obj/project.assets.json --format json
dotnet ol.dll scan --input Dapper.cdx.json --input .../obj/project.assets.json --format json
```

```text
SBOM only  input=sbom/cyclonedx               components= 270 matched=  92 unknown= 178
PM only    input=package-manager/nuget-assets components= 179 matched= 129 unknown=  50
SBOM + PM  input=collection/collection        components= 357 matched= 147 unknown= 210
```

供給元ごとの内訳。

```text
package-manager          rows=  87 matched=  55
sbom                     rows= 178 matched=  18
sbom+package-manager     rows=  92 matched=  74
```

両方が見た 92 行のうち 74 行が確定している。ここが突合の効いている部分である。

### 混在 scan の人間向け出力（cobra）

```bash
dotnet ol.dll scan --input cobra.cdx.json --input .references/_eval/inputs/go-cobra --format text --quiet
```

```text
Input: collection/collection

NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED
D:\github\guitarrapc\ol\.references\cobra - - - root unknown sbom
actions/cache v4 - - unknown unknown sbom
actions/checkout v4 - - unknown unknown sbom
actions/labeler v5 - - unknown unknown sbom
actions/setup-go v6 - - unknown unknown sbom
golangci/golangci-lint-action v8.0.0 - - unknown unknown sbom
msys2/setup-msys2 v2 - - unknown unknown sbom
github.com/cpuguy83/go-md2man/v2 v2.0.6 MIT golang direct matched sbom,package-manager
github.com/inconshreveable/mousetrap v1.1.0 Apache-2.0 golang direct matched sbom,package-manager
github.com/russross/blackfriday/v2 v2.1.0 - golang transitive unknown package-manager
github.com/spf13/pflag v1.0.9 BSD-3-Clause golang direct matched sbom,package-manager
go.yaml.in/yaml/v3 v3.0.4 - golang direct unknown sbom,package-manager
gopkg.in/check.v1 v0.0.0-20161208181325-20d25e280405 - golang transitive unknown package-manager

Unresolved components
  D:\github\guitarrapc\ol\.references\cobra - source_repository_unavailable
  actions/cache v4 source_repository_unavailable
  actions/checkout v4 source_repository_unavailable
  actions/labeler v5 source_repository_unavailable
  actions/setup-go v6 source_repository_unavailable
  golangci/golangci-lint-action v8.0.0 source_repository_unavailable
  msys2/setup-msys2 v2 source_repository_unavailable
  github.com/russross/blackfriday/v2 v2.1.0 license_not_recognized https://github.com/russross/blackfriday/blob/master/LICENSE.txt
  go.yaml.in/yaml/v3 v3.0.4 license_not_recognized https://github.com/yaml/go-yaml/blob/refs/tags/v3.0.4/LICENSE
  gopkg.in/check.v1 v0.0.0-20161208181325-20d25e280405 source_repository_unavailable
```

この出力は欠陥 4 の修正前である。修正後、Actions の 6 行は原因側の理由に変わる。

```text
Unresolved components
  D:\github\guitarrapc\ol\.references\cobra - source_repository_unavailable
  actions/cache v4 unsupported_package_metadata
  actions/checkout v4 unsupported_package_metadata
  actions/labeler v5 unsupported_package_metadata
  actions/setup-go v6 unsupported_package_metadata
  golangci/golangci-lint-action v8.0.0 unsupported_package_metadata
  msys2/setup-msys2 v2 unsupported_package_metadata
  github.com/russross/blackfriday/v2 v2.1.0 license_not_recognized https://github.com/russross/blackfriday/blob/master/LICENSE.txt
  go.yaml.in/yaml/v3 v3.0.4 license_not_recognized https://github.com/yaml/go-yaml/blob/refs/tags/v3.0.4/LICENSE
  gopkg.in/check.v1 v0.0.0-20161208181325-20d25e280405 source_repository_unavailable
```

`SUPPLIED` 列が「どの入力が見たか」を述べている。`go-md2man/v2` が `sbom,package-manager` になっているのが欠陥 1 の修正結果で、修正前はここが 2 行に割れて片方が unknown だった。

### 欠陥 1 の修正前後（記録済みレポートより）

修正前（`reports/r1`）と修正後（`reports/r4`）の同一入力に対する差。

```text
case        round  components  matched  unknown
cobra      r1             14        3       11
cobra      r4             13        3       10
gin        r1             77       40       36
gin        r4             72       40       31
```

gin で消えた 5 行はすべて「SBOM 形式で unknown、PM 形式で matched」という同一モジュールの重複だった。

### 欠陥 2 の修正結果（polaris-java）

```bash
dotnet ol.dll scan --input polaris-java.cdx.json --input polaris-java-maven-dependency-tree.json --format text --quiet
```

```text
auth-block-allow-list UNKNOWN package_metadata_unversioned_purl
bcpkix-jdk15to18 UNKNOWN package_metadata_unversioned_purl
certificate-tsf UNKNOWN package_metadata_unversioned_purl
circuitbreaker-common UNKNOWN package_metadata_unversioned_purl
circuitbreaker-composite UNKNOWN package_metadata_unversioned_purl
```

180 行がこの理由に変わった。修正前はすべて `source_repository_unavailable` と `unsupported_package_metadata` だった。

### policy 評価

欠陥 4 の修正前に採取したもの。`Reason` 列は policy 側の判定なので変わらないが、`scan` の Unresolved セクションでは 6 件が `unsupported_package_metadata` と表示されるようになっている。

```bash
dotnet ol.dll check --report cobra-check.json --allow-licenses "MIT,Apache-2.0,BSD-3-Clause"
```

```text
License check failed: 9 violations.

Package	Version	Ecosystem	Purl	License/Status	Reason
actions/cache	v4	-	pkg:github/actions/cache@v4	unknown	license is unresolved
actions/checkout	v4	-	pkg:github/actions/checkout@v4	unknown	license is unresolved
actions/labeler	v5	-	pkg:github/actions/labeler@v5	unknown	license is unresolved
actions/setup-go	v6	-	pkg:github/actions/setup-go@v6	unknown	license is unresolved
golangci/golangci-lint-action	v8.0.0	-	pkg:github/golangci/golangci-lint-action@v8.0.0	unknown	license is unresolved
msys2/setup-msys2	v2	-	pkg:github/msys2/setup-msys2@v2	unknown	license is unresolved
github.com/russross/blackfriday/v2	v2.1.0	golang	pkg:golang/github.com/russross/blackfriday/v2@v2.1.0	unknown	license is unresolved
go.yaml.in/yaml/v3	v3.0.4	golang	pkg:golang/go.yaml.in/yaml/v3@v3.0.4	unknown	license is unresolved
gopkg.in/check.v1	v0.0.0-20161208181325-20d25e280405	golang	pkg:golang/gopkg.in/check.v1@v0.0.0-20161208181325-20d25e280405	unknown	license is unresolved
```

exit code は 2。Actions を除外すると 3 件まで減る。

```bash
dotnet ol.dll check --report cobra-check.json --allow-licenses "MIT,Apache-2.0,BSD-3-Clause" \
  --exclude-packages "pkg:github/actions/,pkg:github/golangci/,pkg:github/msys2/"
```

```text
Excluded from evaluation: 6 components.
License check failed: 3 violations.
```

### 未解決理由の分布（18 件合計、混在経路）

```text
package_metadata_not_found+source_repository_unavailable         658
source_repository_unavailable+unsupported_package_metadata       235
package_metadata_unversioned_purl+source_repository_unavailable  189
unsupported_source_repository                                    122
license_not_recognized                                            36
source_repository_unavailable                                     31
license_not_detected                                              19
source_repository_fetch_failed                                     2
```

修正前は 2 行目が 424 件で、3 行目が存在しなかった。

## 検証状況

全 816 テスト合格。`DependencyInventoryCombinerBenchmark`（warmup 3 / iteration 10）は 1024 → 4096 コンポーネントで 4.32 倍、線形を保っている。アロケーションは欠陥 1 の修正前後で不変（546.35 KB / 2140.32 KB）。二片構成のパスを通らない一般ケースのために fast path を入れてある。self-scan golden は無変化、`seiton` は 0 issues。

## Lessons learned

実装に効く教訓は spec へ移した。生成器の品質差と母集団の非重複は [packagemanager.md](../specs/packagemanager.md#lessons-learned)、Go のモジュールパス規則は [input combination](../specs/cli.md#contract-input-combination)、解決不能理由の分離は [unqueryable purl](../specs/packagemanager.md#contract-unqueryable-purl) にある。

この評価そのものから得た教訓を 2 つ残す。

- **合成した fixture では見つからない欠陥がある。** 欠陥 1 は「生成器が purl のどこでモジュールパスを切るか」の食い違いで、自分で書いた fixture には自分の想定しか入らない。実在の生成器を実在のリポジトリに当てるまで存在に気づけなかった。Phase 4 の 15 個の等価クラステストは全て通っていた。
- **理由の名前は解決率と同じくらい重要である。** 欠陥 2 と 4 は 1 件も解決を増やしていない。それでも直す価値があったのは、180 件が「ol は Maven に非対応」と読める文言で、235 行が「リポジトリが見つからない」という文言で報告されていたからで、どちらも利用者を誤った調査に送り出す。確定できないことより、確定できない理由を取り違えることの方が害が大きい場合がある。
- **ノイズの出処を確かめずに優先度を決めかけた。** 当初 `pkg:github/*` の解決を最優先に置いたが、それは件数だけを見た判断だった。SBOM のプロパティを読めば `github-actions-usage-cataloger` が入れたものだと即座に分かり、生成器のオプションで止められることも分かる。**ol の欠陥と、生成器の既定の選択と、製品スコープの未決定は別のものである。** 混ぜると、決めるべき人が決めていない事柄を実装で既成事実にしてしまう。
