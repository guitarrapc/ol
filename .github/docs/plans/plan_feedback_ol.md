# 実プロジェクト 18 件による ol の使用感と検知精度の評価

## この文書の位置付け

2026-08-10 に、6 つのパッケージマネージャーそれぞれで実在の OSS リポジトリ 3 件を選び、ol を SBOM 経路・package manager 経路・混在経路の 3 通りで実行した記録である。目的は 2 つあった。**SBOM 単独あるいは PM 単独で検知するツールではなく ol を使う理由が実データで立つか**を確かめること、そして**確定できるべきなのにできないケースを見つけて直す**ことである。

評価の過程で 4 件の欠陥を修正し、4 件の仕様変更（エコシステム単位の除外、Go のライセンス取得元、root の報告範囲、単独入力への同一性規則の適用）を入れた。修正はこの文書に記録した順で実施済みで、仕様は [input combination](../specs/cli.md#contract-input-combination) と [unqueryable purl](../specs/packagemanager.md#contract-unqueryable-purl) に反映してある。未実施の提案と、ol が決めるべきでないスコープ判断は「優先度順の対応事項」に残した。

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
| ライセンス確定 | 1,249 | 2,689 | **2,957** |

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
| 機械可読でない場所を publisher が宣言 | `pkg:nuget/System.Runtime@4.3.0` | `declared_license_location_not_collected` |
| リポジトリのライセンスを分類できない | `pkg:golang/github.com/pmezard/go-difflib@v1.0.0` | `license_not_recognized` |

幻のコンポーネントについて補足する。syft を .NET のビルド出力に向けるとアセンブリをカタログ化し、`pkg:nuget/CommandLine@2.9.1.0` を出す。実体は `CommandLineParser@2.9.1` で、アセンブリ名とアセンブリバージョンであってパッケージの名前とバージョンではない。nuget.org に存在しないので確定しようがなく、ol の `package_metadata_not_found` は正しい。**利用者への助言は「.NET では syft をビルド出力に向けず、`project.assets.json` を ol に直接渡す」である。** 実際 Dapper では SBOM 単独 92 件に対し混在で 147 件が確定した。

`license_not_recognized` は ol がリポジトリまで到達できている点が重要である。`go.yaml.in/yaml/v3` のような vanity import path も Go module proxy の `Origin` 経由で解決できており、報告には読むべき LICENSE の URL が付く。

```text
go.yaml.in/yaml/v3 v3.0.4 license_not_recognized https://github.com/yaml/go-yaml/blob/refs/tags/v3.0.4/LICENSE
```

## NuGet の未解決は何なのか

Go を解決した流れで NuGet も同じ手が効くか確かめた。**効かない。** そして理由が Go とは根本的に違う。

まず母数を分ける。NuGet の未解決 804 行のうち **651 行は syft の幻アセンブリ**で、SBOM のみが供給している。実在するパッケージは 130 件で、そのうち **108 件（83%）が Microsoft / System / runtime.\* 系**である。旧 .NET Core 期の細粒度パッケージ群がそのまま残っている。

deps.dev を 15 件で試した結果は一様だった。

```text
System.Runtime                              4.3.0   non-standard
System.IO                                   4.3.0   non-standard
NETStandard.Library                         1.6.1   non-standard
System.Memory                               4.5.3   non-standard
System.Security.Cryptography.ProtectedData  4.5.0   non-standard
Microsoft.DotNet.PlatformAbstractions       3.1.6   non-standard
```

`non-standard` は SPDX 識別子ではなく「標準ライセンスではない何かがある」という意味なので、この用途には使えない。Go で 65/65 に答えた同じ API が、ここでは 1 件も答えない。

原因は registry のメタデータにある。

```text
licenseExpression : ''
licenseUrl        : http://go.microsoft.com/fwlink/?LinkId=329770
projectUrl        : https://dot.net/
repository        : null
```

`licenseExpression` が無く、`licenseUrl` は fwlink のリダイレクトである。deps.dev はこれを標準ライセンスとして分類できず、ol も SPDX 式を得られない。`projectUrl` が `https://dot.net/` なのが、この一群に `unsupported_source_repository` が混ざる理由でもある。

ol はこれを正しく扱っている。`licenseUrl` を [declared license reference](../specs/spdx.md#contract-declared-license-reference) の `location` として保持し、報告では宣言先の URL とともに提示する。

```text
runtime.any.System.Runtime 4.3.0 declared_license_location_not_collected http://go.microsoft.com/fwlink/?LinkId=329770
```

**では宣言先を読めば解決するのか。読んでも解決しない。** fwlink は `https://github.com/dotnet/core/blob/main/license-information.md` に着き、その冒頭にこう書いてある。

```text
This document is provided for informative purposes only and is not itself a license.
```

本文は「.NET のソースは MIT を使う」と散文で述べ、各リポジトリの LICENSE へ誘導する。つまりこのパッケージ自身の宣言から SPDX 識別子には到達できない。到達するには散文を読解するか、別リポジトリの LICENSE へ渡り歩くことになり、どちらも「参照から SPDX ID を推測しない」という非目標に触れる。

したがって**この 108 件は原理的に確定できない部類**であり、deps.dev の `non-standard` も ol の `declared_license_location_not_collected` も同じ結論を別の言い方で述べている。ol の答えは「publisher が示した場所はここだ、読むのは人間だ」であり、これが正しい。

含意として、[P3](#p3-golangorgx-系のライセンス確定実施済み) と同じ形の解決策を NuGet に期待すべきではない。Go で効いたのは、ライセンス事実がパッケージ内容に存在していて情報源がそれを読んでいたからである。ここでは事実そのものがパッケージの外にあり、しかも機械可読な形で存在しない。

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

### P3: `golang.org/x/*` 系のライセンス確定（実施済み）

122 件が `unsupported_source_repository` だった。`golang.org/x/crypto` などは Go プロジェクトのほぼすべてが依存する。Gerrit ホストであり GitHub ではないため収集できなかった。

当初は「module proxy が配る zip 内の LICENSE を読む」を想定していたが、調べ直すと**もっと確実な道があった**。ol は Maven で既に deps.dev を使っており、deps.dev は Go にも同じ形で答える。実測した Go モジュール **65 件すべてにライセンスを返し**、ol が既に解決していた 45 件のうち 44 件と一致した（残り 1 件は ol が未解決だったものを deps.dev が埋めた）。

```text
$ curl https://api.deps.dev/v3/systems/go/packages/golang.org%2Fx%2Fcrypto/versions/v0.52.0
licenses: BSD-3-Clause
link SOURCE_REPO = https://go.googlesource.com/crypto
```

**proxy を置き換えるのではなく併用した。** proxy の `Origin.Ref` はリリースタグを指しており、これを失うと既定ブランチのライセンスで代用することになる。タグ以降に再ライセンスされたプロジェクトを誤って報告する経路を作ってしまう。また Go 全体を単一の第三者集約サービスに依存させると、それが落ちたとき今日得られている証拠まで失う。deps.dev への追随は**任意**とし、届かなければ proxy が述べたところまでで縮退する。

行レベルの結果:

| | matched | unknown |
|---|---|---|
| cobra | 3 → **5** | 10 → **7** |
| gin | 40 → **55** | 31 → **15** |
| logrus | 3 → **6** | 8 → **4** |

`golang.org/x/*` 全 9 件、`google.golang.org/protobuf`、`rsc.io/pdf`、`gopkg.in/check.v1`、`pmezard/go-difflib`、`pelletier/go-toml/v2`、`blackfriday/v2` がすべて解決した。

```text
pkg:golang/golang.org/x/crypto@v0.52.0
  status=matched license=BSD-3-Clause
    package-registry   matched    raw='BSD-3-Clause'
    source-repository  unknown    raw='https://go.googlesource.com/crypto'
```

リポジトリ側の証拠は unknown のまま保持されている。片方が答えたからといって他方を捨てない。

残る Go の未解決は 4 件で、うち 3 件は deps.dev が**関係を述べずに複数ライセンスを返す**ものである。ambiguous として報告し、勝手に `OR` で繋がない。これは Maven で既に確立していた扱いを Go が継承した形である。

```text
pkg:golang/gopkg.in/yaml.v3@v3.0.1
  status=ambiguous license=MIT; Apache-2.0 (?)
```

#### この修正で危うく取り逃がしかけたこと

最初の全体再計測で結果がまったく動かなかった。原因は**キャッシュ**で、変更前に書かれた Go エントリは「ライセンス無し」として保存済みであり、`cacheHits=57 misses=0` で全件がそこから読まれていた。

ol にはこのための機構が既にある。`ResolverVersion` は「書き込んだビルドがどの観測をできたか」を記録し、新しいリゾルバは改善できるエントリだけを再収集する。5 → 6 に上げることで、既存の一般規則（ライセンス空のエントリは再収集）がそのまま効く。Go 専用の規則は要らなかった。

**能力を足したのにキャッシュのバージョンを上げないと、改善は利用者に届かない。** エントリの形式は何も変わっていないので見落としやすい。

### P4: 空 purl の合成コンポーネント（実施済み）

syft はスキャン対象ディレクトリ自体を purl の無い root コンポーネントとして出す。

```text
D:\github\guitarrapc\ol\.references\cobra - - - root unknown sbom
```

**policy 側は既存の規則で既に片付いていた。** [cli.md](../specs/cli.md#contract-policy-checks) が「Policy evaluates all non-root, non-excluded components」と定めており、この合成コンポーネントは `dependency: root` なので `check` は評価しない。当初この節を立てたとき、私はその規則を確認していなかった。

残っていたのは 2 点で、どちらも対応した。

**Unresolved セクションから root を外した。** このセクションの目的は「読み手が次に何をするか」であり、policy が評価しない root について読み手にできることはない。policy のスコープと表示のスコープがずれていた。表には残るので、その SBOM が何を記述しているかは失われない。

```text
Unresolved components
  actions/cache v4 unsupported_package_metadata
  ...
```

以前はこの先頭に絶対パスの行が 1 本入っていた。

**report privacy 契約の文言を狭めた。** canonical JSON に絶対パスが 2 箇所あったが、内訳を見ると ol が構築した値（`metadata.input.sourceRef`）は `"2 inputs"` と無害化済みで、残るのは入力が述べた component 名だけだった。

契約は「Reports ... must not contain ... absolute local paths」と無条件に書かれており、文字通りには違反している。しかし component の名前・バージョン・識別子は入力自身の主張であり、書き換えれば report が記述対象の文書と食い違い、`sourceId` による突合も壊れる。**契約が縛るのは ol が自分について書く値であって、入力が述べたことではない**と明記する形にした。ローカルツリーから生成した report を公開する利用者は、生成器が書いた identity がそのまま載ることを前提にすべきである、という注意も併記した。

### P5: 削除済み plan への参照が残っている

[plan_multi_source_evidence.md](plan_multi_source_evidence.md) の 2 箇所が、既に削除された `plan_nuget_license_file.md` を参照している。内容は [declared license reference](../specs/spdx.md#contract-declared-license-reference) に移っているので、リンクを付け替えれば済む。この評価の対象外だったため手を付けていない。

### P6: SBOM 単独の行数を identity で畳む（実施済み）

#### 重複の実態

18 ケースの SBOM 単独 report で、purl を持つ 2,518 行が **1,769 パッケージ**だった。**749 行（30%）が重複**である。出処は 2 つある。

| 出処 | 例 | 最悪 |
|---|---|---|
| .NET のアセンブリが複数の出力先に置かれる | serilog、Dapper、ImageSharp | `Microsoft.TestPlatform.CommunicationUtilities` が **42 行** |
| GitHub Actions が複数のワークフローで使われる | axios、attrs、gson ほか | `actions/checkout` が **8 行** |

#### ol が保持している区別は何か

serilog の 42 行を並べると、**`sourceId` 以外はすべて同一**だった。

```text
sourceId : pkg:nuget/Microsoft.TestPlatform.CommunicationUtilities@17.1100.124.45402?package-id=ece062cad76e353c
sourceId : pkg:nuget/Microsoft.TestPlatform.CommunicationUtilities@17.1100.124.45402?package-id=ec42d6e4d5f2bc77
sourceId : pkg:nuget/Microsoft.TestPlatform.CommunicationUtilities@17.1100.124.45402?package-id=01fdb6f365147021
```

この `?package-id=` は syft 内部の識別子であって**ファイル位置ではない**。syft は位置を `properties` に置くが、ol はそれを読まない。つまり**畳んで失われる情報は、読み手が使えるものとしては存在しない**。当初この節に「ファイル位置という情報も失われる」と書いたが、それは誤りだった。

#### 観測できる害

`check` の違反出力が最も分かりやすい。

```text
$ ol check --report serilog-sbom.json --allow-licenses "MIT,Apache-2.0,BSD-3-Clause"
License check failed: 401 violations.
```

**401 件の違反は 31 パッケージ**で、1 パッケージが 42 件を占める。読み手は同じ事実を 13 回読まされる。`scan` の表も同じ割合で膨らむ。

一方 **baseline は無傷**である。401 件を承認しても baseline のエントリは 37 件で、既に purl 単位に畳まれている。

#### 判断の芯

registry は CycloneDX を既定の `Ordinal`（purl のみ、`SourceId` は同一性に含めない）で登録している。**つまり「SBOM の同一性は purl である」と既に宣言されている。** npm などが `OrdinalWithSourceId` を使うのは install path が意味を持つからで、その区別は既に表現されている。

食い違っているのは単独 scan の行の粒度だけで、parser が bom-ref ごとに 1 行を作っている。複数入力の combiner はこの宣言に従って畳む。**単独 scan だけが自分の registry 宣言に従っていない**、というのが問題の正体である。

#### 当初の選択肢立てが誤っていた

最初は「inventory ごと畳む」対「表示だけ畳む」で立てたが、実測すると**混在 scan では combiner が既に inventory を畳んでいた**。

```text
serilog-sbom  inventory.components= 506  displayed components= 506  distinct purls=72
serilog-both  inventory.components= 458  displayed components= 458  distinct purls=80
```

表示だけ畳むと、単独が inventory 506 / 表示 72、混在が 458 / 458 となり、**非対称が減るどころか増える**。同じ SBOM が、単独で読むか lockfile と一緒に読むかで自分の inventory 形状を変えることになる。

#### 実施した内容

単独入力も同じ combiner を通すようにした。**新しい規則を足したのではなく、規則から外れていた経路を戻した**だけである。`ScanInputs` は入力が 1 つのとき combiner を素通りしており、そこだけが自分の registry 宣言に従っていなかった。

occurrence は残るので、その文書が何件記述したかは失われない。serilog がその極端な例になる。

```text
before : rows=506  inventory=506  occurrences=506
after  : rows= 73  inventory= 73  occurrences=506
```

18 ケースの SBOM 単独 report で表示行は 2,537 → **1,802**（735 行減、29%）。`check` の違反出力は serilog で **401 → 37** になった。identity 単位の解決数は r6 と完全に一致しており、**畳んでも結論は 1 件も変わっていない**。PM 入力は `OrdinalWithSourceId` なので install path 違いは今も別行のままで、影響を受けない。

#### 性能で一度やり直した

単独入力を無条件に combiner へ通した最初の実装は、contexts・occurrences・edges を毎回コピーするため E2E がゲートを超えた。

| | 素通り（従来） | 無条件 combine | 修正後 |
|---|---|---|---|
| ScanTextWithCachedMetadata | 171.4 us / 4.64 KB | — / 5.31 KB | 168.8 us / 4.97 KB |
| ScanNuGetJsonWithCachedMetadata | 409.3 us / 10.36 KB | — / 11.38 KB | 388.6 us / 10.69 KB |

単一 inventory 用の経路を分け、**畳むものが無ければ解析済みの配列をそのまま返す**ようにした。ほとんどの入力は各パッケージを一度しか述べないので、通常はこの分岐で終わる。最終的に平均は 4 件中 3 件でむしろ低下し、アロケーション増は +3〜7% に収まった。

計測中に、最初に取った E2E のベースライン（294.6 us）自体が外れ値だったことも分かった。1 行だけ戻した同一ビルドで測り直すまで、存在しない退行を追いかけていた。

#### リスク計測

重複行が status・dependency・license・version のいずれかで食い違うグループは 18 ケースで **0 件**。name だけは 7 件食い違ったが、すべて `pkg:github/github/codeql-action` で、`#analyze` `#init` `#upload-sarif` という subpath で区別される別のアクションだった。combiner は purl 全体で比較するのでこれらは畳まれない。**食い違いに見えたのは私の集計が subpath を落としていたためである。**

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

ol が実際に選ぶ理由（[unresolved section](../specs/cli.md#contract-unresolved-section) のランキングに従って 1 つに絞ったもの）で数える。

```text
package_metadata_not_found                658
unsupported_package_metadata              235
package_metadata_unversioned_purl         189
declared_license_location_not_collected   103
license_not_recognized                     32
source_repository_unavailable              24
license_not_detected                       20
unsupported_source_repository              11
```

評価開始時は 2 行目と 3 行目が `unsupported_package_metadata` の 424 件に混ざっており、未解決の総数は 652 件だった。現在は 632 件で、減った 20 件はすべて Go である。

> **この集計は一度誤っていた。** 当初は component の `warnings` 配列だけを見ており、宣言された参照から**導出される**理由を落としていた。そのため `unsupported_source_repository` が 111 件に見えていたが、実際は 11 件で、100 件は「publisher がライセンスの所在を宣言しており、ol はまだそこを読んでいない」だった。ol の報告は最初から正しく、誤っていたのは評価側である。JSON から理由を再構成するときは、警告だけでなく `evidence.declaredLicenseReferenceKind` も見る必要がある。

## 検証状況

全 821 テスト合格。`DependencyInventoryCombinerBenchmark`（warmup 3 / iteration 10）は 1024 → 4096 コンポーネントで 4.32 倍、線形を保っている。アロケーションは欠陥 1 の修正前後で不変（546.35 KB / 2140.32 KB）。二片構成のパスを通らない一般ケースのために fast path を入れてある。self-scan golden は無変化、`seiton` は 0 issues。

## Lessons learned

実装に効く教訓は spec へ移した。生成器の品質差と母集団の非重複は [packagemanager.md](../specs/packagemanager.md#lessons-learned)、Go のモジュールパス規則は [input combination](../specs/cli.md#contract-input-combination)、解決不能理由の分離は [unqueryable purl](../specs/packagemanager.md#contract-unqueryable-purl) にある。

この評価そのものから得た教訓を 2 つ残す。

- **合成した fixture では見つからない欠陥がある。** 欠陥 1 は「生成器が purl のどこでモジュールパスを切るか」の食い違いで、自分で書いた fixture には自分の想定しか入らない。実在の生成器を実在のリポジトリに当てるまで存在に気づけなかった。Phase 4 の 15 個の等価クラステストは全て通っていた。
- **理由の名前は解決率と同じくらい重要である。** 欠陥 2 と 4 は 1 件も解決を増やしていない。それでも直す価値があったのは、180 件が「ol は Maven に非対応」と読める文言で、235 行が「リポジトリが見つからない」という文言で報告されていたからで、どちらも利用者を誤った調査に送り出す。確定できないことより、確定できない理由を取り違えることの方が害が大きい場合がある。
- **同じ手が隣のエコシステムで効くとは限らない。** deps.dev は Go の 65/65 に答え、NuGet の Microsoft 系には 1 件も答えない。違いは情報源の優劣ではなく、ライセンス事実がどこにあるかである。Go はパッケージ内容にあり、旧 .NET パッケージは fwlink の先の散文にある。[入力経路の優劣がエコシステムごとに逆転する](../specs/packagemanager.md#lessons-learned)のと同じ構造が、外部情報源にも当てはまる。
- **性能の退行を疑う前に、ベースラインが本物か確かめる。** 単独入力を combiner に通した直後、E2E が +22〜33% に見えた。1 行だけ戻した同一ビルドで測り直すと、比較対象にしていた過去の値のほうが外れ値で、実際の差は誤差の範囲だった（3/4 はむしろ低下）。それでも無条件コピーは実在するコストだったので、畳むものが無ければ配列をそのまま返す経路は残している。**測り直さなければ、存在しない退行を追いかけ続けていた。**
- **既にある規則を確認せずに課題を立てた。** root コンポーネントを「報告から除くべきか」という項目を立てたが、policy 側は最初から `non-root` で除外していた。実際に残っていたのは表示スコープと policy スコープのずれという別の話で、正しく切り分けるまで課題の粒度が合っていなかった。仕様に既存の答えがないか先に読むべきだった。
- **報告の質を、報告を読まずに評価しかけた。** 未解決の分類を JSON の `warnings` 配列だけから作ったため、宣言された参照から導出される理由を 100 件取りこぼし、`unsupported_source_repository` が実際の 10 倍に見えていた。ol は最初から正しく報告していた。ツールの出力品質を測るなら、ツールが実際に出力するものを見る。
- **「無い」ものを作る前に、「答えを持っている情報源」を探す。** `golang.org/x/*` の未解決に対して、最初は module proxy の zip から LICENSE を読む実装を想定していた。実際には ol が既に Maven で使っている deps.dev が Go にも同じ形で答え、実測 65 件すべてを埋めた。失敗の形が「このホストは GitHub ではない」であって「このパッケージにライセンスが見つからない」ではないとき、足りないのは収集能力ではなく問いに答える情報源である。
- **能力を足したらキャッシュのバージョンを上げる。** Go の変更は、最初の全体再計測でまったく効かなかった。エントリ形式は何も変わっていないので見落としやすいが、空のライセンスは古いリゾルバの性質であって事実ではない。`ResolverVersion` を上げるまで、改善は利用者に届かない。
- **ノイズの出処を確かめずに優先度を決めかけた。** 当初 `pkg:github/*` の解決を最優先に置いたが、それは件数だけを見た判断だった。SBOM のプロパティを読めば `github-actions-usage-cataloger` が入れたものだと即座に分かり、生成器のオプションで止められることも分かる。**ol の欠陥と、生成器の既定の選択と、製品スコープの未決定は別のものである。** 混ぜると、決めるべき人が決めていない事柄を実装で既成事実にしてしまう。
