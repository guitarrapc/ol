[![build](https://github.com/guitarrapc/ol/actions/workflows/build.yml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/build.yml)
[![Release](https://github.com/guitarrapc/ol/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/release.yaml)

# ol

[English](README.md) | 日本語

解決済み依存関係とSBOMを用いたOSSライセンスチェッカーです。

olは、アプリケーションが実際に利用する直接・推移的依存パッケージについて、ライセンス一覧を出力します。パッケージのライセンス情報は、SBOM、パッケージレジストリ、ソースリポジトリを参照して多角的に分析し精度を高めます。これにより、現在利用しているOSSライセンスを把握したり、PRで変更される依存関係でライセンス違反がないかをCIで自動判定できます。

## olでできること

olは法的助言を提供せず、ライセンスの法的な確実性は主張できません。観測できない情報を推測せず、不確実性を結果として残すことに重点を置いています。

- 現在のプロジェクトに含まれる、推移的依存関係まで含めたライセンス確認
- ライセンス証拠の欠落、曖昧さ、競合、無効なSPDX式の可視化
- 保存した2つのレポート間にある、ライセンス関連の変更比較
- SPDX License Identifierによる安定したフォーマットでのライセンス表現
- 証拠の出典を含んだJSONレポートの保存とこれに対するチェック

**olがしないこと**

ol自身は依存関係を解決しません。これは各言語の依存解決結果が最も確度が高く、解決済みの依存関係に焦点を当てるためです。このため、`package.json`、`*.csproj`、`Cargo.toml`のようなマニフェストではなく、次のいずれかを入力にします。

- CycloneDXまたはSPDX形式のJSON SBOM
- npm/Cargo/NuGetなどのパッケージマネージャーによる解決済み入力（`package-lock.json`、`cargo-metadata.json`、`project.assets.json`など）

## クイックスタート

GitHubのReleases ページから利用OS向けアセットをダウンロードし、`ol`（Windows は `ol.exe`）を任意の場所に配置します。

```sh
# Homebrew (macOS/Linux)
brew tap guitarrapc/ol https://github.com/guitarrapc/ol
brew install guitarrapc/ol/ol

# .NET global tool
dotnet tool install -g ol

# Windows (Scoop)
scoop bucket add guitarrapc https://github.com/guitarrapc/scoop-bucket
scoop install ol
```

言語を問わず解決済みの依存関係を扱うには、SBOM（例では`bom.cdx.json`）が最も便利です。olはSBOM以外にも、各言語のパッケージマネージャーが解決したロックファイルや出力を直接入力として受け付けます。

> [!TIP]
> CycloneDX JSON SBOMを生成するには、[@cyclonedx/cyclonedx-npm](https://www.npmjs.com/package/@cyclonedx/cyclonedx-npm)などのツールがあります。

```bash
# macOS/Linuxでは、必要に応じて実行権限を付与
chmod +x ./ol

# CycloneDXまたはSPDX形式のJSON SBOMをスキャン
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json

# カレントディレクトリ以下から対応するエコシステムの解決済み依存関係をスキャン
ol scan --input .

# 監査対象製品に含まれないドキュメントやPagesプロジェクトを除外
ol scan --input . --exclude-input-path src/documents --exclude-input-path Pages

# 対応するロックファイルやパッケージマネージャー出力を直接スキャン
ol scan --input package-lock.json
ol scan --input src/MyProject/obj/project.assets.json

# SBOMと、それが記述する解決済みツリーを同時にスキャン (両方あるならこれを推奨)
ol scan --input bom.cdx.json --input .

# レビュー用のMarkdownレポートを出力
ol scan --input . --format markdown > ol-report.md

# 再利用可能なJSONレポートを出力
ol scan --input . --format json > ol-report.json

# 直接依存関係だけをライセンスごとに集約して表示
ol scan --input . --dependency direct --group-by license

# 保存したレポートをSPDXライセンスの許可リストで評価
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause

# 保存した2つのレポート間でライセンス関連の変更を比較
ol diff --previous before.json --current after.json

# 入力に含まれるライセンス証拠だけを使用
ol scan --input bom.cdx.json --no-external-evidence
```

### GitHub Actions

[guitarrapc/setup-ol](https://github.com/guitarrapc/setup-ol)を使うことで、簡単にolをインストールできます。

```yaml
on:
  push:
    branches: [main]

jobs:
  license-check:
    runs-on: ubuntu-24.04
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7
      - uses: guitarrapc/setup-ol@v1.0.0
      - name: ライセンスをスキャン
        run: ol scan --input . --format json > ol-report.json
        env:
          OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      - name: ライセンス違反を検出
        run: ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

過去のレポートをコミットしておくことで、PRでパッケージ変更があったときに、ライセンスの変更や新規追加を検出できます。OSSライブラリは時にバージョン変更でライセンスが変わることがありますが、olを使うことで検知できます。

```yaml
on:
  pull_request:
    branches: [main]

jobs:
  license-check:
    runs-on: ubuntu-24.04
    permissions:
      contents: read
    steps:
      - uses: actions/checkout@v7
      - uses: guitarrapc/setup-ol@v1.0.0
      - name: ライセンスをスキャン
        run: ol scan --input . --format json > after.json
        env:
          OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      - name: ライセンス変更を比較
        run: ol diff --previous before.json --current after.json
      - name: ライセンス違反を検出
        run: ol check --report after.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## 使い方

```bash
$ ol --help
Usage: [command] [-h|--help] [--version]

Commands:
  cache clear            Clears cached evidence for the specified category.
  cache pack             Packs managed cache entries into one deterministic archive.
  cache prune            Removes managed cache entries older than the specified age.
  cache unpack           Unpacks an Ol cache archive into the managed cache directories.
  check                  Check a canonical JSON scan report against allowed SPDX licenses.
  diff                   Compare two persisted JSON scan reports and report license-relevant changes.
  scan                   Scan a resolved dependency input.
  skill export-plugin    Export a portable Agent Plugin package.
  skill install          Install the skill into the current workspace.
  spdx clear             Clear user-managed SPDX data.
  spdx list              List installed SPDX data versions.
  spdx update            Download SPDX data into the user data directory.
  spdx use               Switch active SPDX data version.
  spdx version           Show the active SPDX data source.
```

| コマンド | 役割 |
|---|---|
| `ol scan` | 解決済み依存関係からライセンス証拠を収集し、レポートを生成する。 |
| `ol check` | canonical JSONレポートを許可リストで評価する。 |
| `ol diff` | 2つのcanonical JSONレポートを比較する。 |
| `ol skill install` | 同梱されたlicense-scan Agent SkillをCodexまたはClaude向けにインストールする。 |
| `ol skill export-plugin` | SkillをポータブルなAgent Pluginとして出力する。 |
| `ol cache clear` | olが管理する証拠キャッシュを削除する。 |
| `ol cache pack` | 証拠キャッシュを決定的な`.olcache`アーカイブにまとめる。 |
| `ol cache prune` | 指定した期間より古い管理対象キャッシュを削除する。 |
| `ol cache unpack` | `.olcache`アーカイブを分離されたキャッシュディレクトリへ復元する。 |
| `ol spdx version` | 使用中のSPDXデータを表示する。 |
| `ol spdx list` | インストール済みSPDXデータを一覧表示する。 |
| `ol spdx update` | SPDXデータをダウンロードする。 |
| `ol spdx use` | 使用するSPDXデータのversionを切り替える。 |
| `ol spdx clear` | ユーザー管理のSPDXデータを削除する。 |

SBOMやロックファイルといった依存関係の情報からライセンスを収集するには`scan`を使います。解析レポートから、利用しているパッケージの一覧とライセンスを確認できます。JSON形式で出力すると、`check`や`diff`で再利用できます。

CIリポジトリ間で読み取り専用のキャッシュシードを配布する場合は、jobごとに`RUNNER_TEMP`配下のfreshなディレクトリへunpackします。scanはそこへエントリを追加できますが、ディレクトリはjobとともに破棄され、commitしたアーカイブは読み取り専用のままです。

```bash
ol cache pack cysharp.olcache --cache-dir .ol-cache --max-age 30d
ol cache unpack cysharp.olcache --cache-dir "$RUNNER_TEMP/ol-cache"
ol scan --input . --cache-dir "$RUNNER_TEMP/ol-cache"
```

信頼された更新jobも同じfreshなディレクトリを使い、対象リポジトリをrefreshしてから`--max-age`付きで次のseedをpackします。永続的なローカルキャッシュを使う場合は、`ol cache prune --cache-dir .ol-cache --max-age 30d`で古い管理対象エントリを明示的に削除できます。

アーカイブには、元のキャッシュに含まれるパッケージとリポジトリの識別情報が保存されます。private repositoryの証拠から作成したキャッシュシードは公開しないでください。

```bash
$ ol scan --help
Usage: scan [options...] [-h|--help] [--version]

Scan a resolved dependency input.

Options:
  --input <string[]>                    Repeatable resolved dependency input files or directories. [Required]
  --exclude-input-path <string[]?>      Repeatable file or directory paths excluded from directory input discovery. [Default: null]
  --input-format <string>               Input format: auto (default), cyclonedx, spdx, nuget-assets, npm-package-lock, pnpm-lock, yarn-classic-lock, yarn-berry-lock, cargo-metadata, go-module-graph, pip-inspect, composer-lock, bundler-lock, maven-dependency-tree, swift-package-resolved, or cocoapods-lock. [Default: @"auto"]
  --format <ReportFormat>               Output format: text, json, or markdown. [Default: Text]
  --verbose                             Include verbose columns and input detection diagnostics.
  --dependency <string?>                Dependency output filter: root,direct,transitive,unknown. [Default: null]
  --group-by <string?>                  Group output by fields: name,version,license,ecosystem,dependency,status. [Default: null]
  --sort <string>                       Sort keys: ecosystem,name,version,license,dependency,status,purl. [Default: @"ecosystem,name,version"]
  --sort-order <SortOrder>              Sort order: asc or desc. [Default: Asc]
  --spdx-data <string?>                 Directory containing licenses.json and exceptions.json. [Default: null]
  --quiet                               Suppress stderr summary.
  --refresh                             Ignore cached package metadata and source repository entries and fetch them again.
  --cache-dir <string?>                 Root directory for isolated package-metadata and source-repository caches. [Default: null]
  --no-external-evidence                Use only license evidence declared in the input; package registries, source repositories, and their caches are never read.
  --skip-evidence-packages <string?>    Comma-separated package URL prefixes whose external evidence is never collected. [Default: null]
  --concurrency <int>                   Maximum concurrent package metadata and source repository lookups. [Default: 0]
  --retry <int>                         Retry count for package registry and GitHub License API requests. [Default: 1]
```

`scan`で生成したライセンス解析レポートのライセンスを評価するには`check`を使います。許可ライセンスに違反したパッケージがあるかを確認できます。

```bash
$ ol check --help
Usage: check [options...] [-h|--help] [--version]

Check a canonical JSON scan report against allowed SPDX licenses.

Options:
  --report <string>                 Persisted canonical JSON scan report to evaluate. [Required]
  --allow-licenses <string>         Comma-separated SPDX License Identifiers. [Required]
  --allow-dev-licenses <string?>    Comma-separated SPDX License Identifiers additionally allowed for development-only components. [Default: null]
  --exclude-packages <string?>      Comma-separated package URL prefixes whose components are not evaluated. [Default: null]
  --spdx-data <string?>             Directory containing licenses.json and exceptions.json. [Default: null]
  --verbose                         Include persisted report diagnostics.
  --baseline <string[]?>            Repeatable baseline files acknowledging already reviewed unresolved components. A component is acknowledged when any of them states it. [Default: null]
  --update-baseline                 Rewrite the last baseline file, holding what the earlier ones do not already acknowledge.
  --sarif <string?>                 Write violations as SARIF to this file for CI code scanning. [Default: null]
```

事前に`scan`で生成したレポートと、新たに`scan`で生成したレポートを比較するには`diff`を使います。変更があったパッケージを表示します。

```bash
$ ol diff --help
Usage: diff [options...] [-h|--help] [--version]

Compare two persisted JSON scan reports and report license-relevant changes.

Options:
  --previous <string>      Previously persisted JSON scan report. [Required]
  --current <string>       Current JSON scan report. [Required]
  --format <DiffFormat>    Output format. [Default: Text]
```

SPDXデータはバンドルされています。手元の環境でSPDXデータを最新版に更新できます。

```bash
$ ol spdx --help
Usage: spdx [command] [-h|--help] [--version]

Manage SPDX data.

Commands:
  clear      Clear user-managed SPDX data.
  list       List installed SPDX data versions.
  update     Download SPDX data into the user data directory.
  use        Switch active SPDX data version.
  version    Show the active SPDX data source.
```

olは依存関係を何度も問い合わせないようキャッシュを生成します。キャッシュはユーザーが削除できます。

```bash
$ ol cache --help
Usage: cache [command] [-h|--help] [--version]

Manage locally cached scan evidence.

Commands:
  clear     Clears cached evidence for the specified category.
  pack      Packs managed cache entries into one deterministic archive.
  prune     Removes managed cache entries older than the specified age.
  unpack    Unpacks an Ol cache archive into the managed cache directories.
```

olには、解決済み入力の選択、SBOMとパッケージマネジャー証拠の併用、scan結果の解釈をcoding agentへ案内するAgent Skillが同梱されています。現在のworkspaceへインストールするか、ポータブルな[Agent Plugin](https://agent-plugins.org/)として出力できます。

```bash
ol skill install --target codex
ol skill install --target claude
ol skill export-plugin --output ./ol-plugin
ol skill export-plugin --output ./ol-plugin --with-claude
```

Codexは`.agents/skills/license-scan`、Claudeは`.claude/skills/license-scan`へインストールします。`--output`で出力先を変更できます。既存ディレクトリは`--force`を指定しない限り保持されます。`--with-claude`は同じ`skills/license-scan`を共有したままClaude Code用manifest adapterを追加します。

### 終了コード

各コマンドの終了コードは次の通りです。CIでは`check`の終了コードを利用して、許可ライセンス違反があるかを判定できます。

| 終了コード | 意味 |
| ---: | --- |
| `0` | コマンドは正常に完了しました。helpやversionの出力も`0`を返します。 |
| `1` | 引数の解析に失敗した、または無効な設定、入力、I/O、その他の実行エラーのためコマンドを完了できませんでした。 |
| `2` | `check`がポリシー評価を完了し、1つ以上の違反を検出しました。 |
| `3` | `check`は完了しましたが、何も証明できませんでした。検出されたものがすべて収集失敗であるか、レポートが「入力は解決済み依存を 1 つも宣言していない」と述べています。 |

### ライセンス結果の読み方

各コンポーネントには次のstatusが付きます。

| status | 意味 |
|---|---|
| `matched` | 証拠が1つの有効なSPDX式へ解決された。 |
| `conflict` | 複数の有効な証拠が異なるライセンスを示している。 |
| `unknown` | 収集は完了したが、利用可能なライセンス情報がなかった。 |
| `ambiguous` | ライセンス文字列はあるが、推測なしでは1つのSPDX式へ正規化できない。 |
| `invalid` | 主張されたSPDX式または識別子が無効。 |
| `error` | 証拠の収集または処理に失敗し、ほかの証拠でも解決できなかった。 |

`matched`は「解決できた」という事実であり、「組織として許可する」という意味ではありません。許可・不許可は`check`の許可リストで判断します。`unknown`、`conflict`、`ambiguous`、`invalid`、`error`は、`check`では安全側に倒して違反として扱います。

`ambiguous`のうち1つだけは例外で、判断すべきことが残っていません。deps.devのようなレジストリは見つけたライセンスを列挙するだけで相互の関係を述べません。olは各メンバーをSPDXへ突き合わせて解決し、その結果をライセンス列挙として記録します（候補の`kind: license-set`、表記は`MIT; Apache-2.0`）。これで不明なのは演算子だけになるので、**列挙されたすべてのメンバーが許可リストに含まれていれば、ANDと読んでもORと読んでも許可**になり、`check`は`Allowed on every reading of ambiguous evidence`として報告します。1つでも許可外のメンバーがあれば、olが解決できないメンバーを含めば（deps.devは識別できなかったライセンスに`non-standard`を返します）、あるいはライセンス名・URL・classifier・publisherが自由文に書いたセミコロンのように列挙ではない`ambiguous`値であれば、従来どおり違反です。どちらの場合もスキャンレポート上のstatusは`ambiguous`のままで、これが決めるのはポリシーの可否だけであり、ライセンスそのものではありません。

レジストリが`404`を返した場合はレジストリが応答しているため、`error`ではなく`unknown`（warningは`package_metadata_not_found`）になります。private feedにのみ公開されたパッケージがこの形になり、ベースラインで承認できます。`error`は応答自体が得られなかった場合、つまりタイムアウト、`429`、`5xx`に限られます。これが終了コード`3`の意味を成り立たせています。

## ライセンスの確度を高める

`scan`は、入力に含まれる証拠に加えて、対応するパッケージレジストリとGitHub License APIからライセンス情報を収集し確度を高めます。収集したパッケージのライセンス情報はローカルにキャッシュします。GitHub Actionsでレート制限を避けるには、`GITHUB_TOKEN`を`OL_GITHUB_TOKEN`へ明示的に渡します。olは`GITHUB_TOKEN`を暗黙には読みません。

```yaml
env:
  OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

入力に含まれる証拠だけを使う場合は、外部ソースとキャッシュを無効化します。

```bash
ol scan --input bom.cdx.json --no-external-evidence
```

外部証拠がないコンポーネントは未解決のまま残るため、通常のスキャンより`check`の違反が増える場合があります。

SPDX式を書かない代わりに、NuGetの旧`licenseUrl`やCycloneDXの`license.url`のようにライセンスのURLだけを書くパッケージがあります。olはそのページを取得しません。ただしSPDXライセンスリスト自身が`seeAlso`として公開しているURLは識別子のURL表記として解決します（`https://www.apache.org/licenses/LICENSE-2.0`は識別子を定義しているレコードそのものがApache-2.0として公開しています）。照合はscheme、大文字小文字、先頭の`www.`、末尾のスラッシュだけを無視します。SPDXが公開していないURL、複数ライセンスで共有されているURLは未解決の宣言のまま残り、パッケージが明示したライセンスがURLで上書きされることもありません。

同じ規則は、olが実際に読むライセンス文書（パッケージ同梱の`LICENSE`など）にも適用されます。文書はそこに再現されたSPDXライセンステキストで識別されるほか、文書自身が含んでいるSPDX公開URLでも識別されます。そのため`Licensed under the Apache License, Version 2.0`と書いて正典のApacheページを指しているだけの`LICENSE`もApache-2.0として解決します。1つの文書が2つのライセンスを名指している場合はどちらにも解決しません。xunit 2.4.1の`license.txt`はプロジェクト本体をApache-2.0として通知形式で宣言したうえで、1つのサブディレクトリに取り込んだコード向けにMITの全文を引用しています。olはテンプレートとして読めるほうを選ばず、未解決として報告します。どちらの読み方で判定したかは`spdx-template`と`spdx-license-url`としてレポートに記録されます。

> [!TIP]
> olは任意のリポジトリ内容をクロールしたり、ディレクトリ構成やライセンスファイルからライセンスを推測したりしません。

## パッケージ依存関係の解決

リリースや監査の成果物には、対象全体を表す1つのCycloneDXまたはSPDX JSON SBOMを推奨します。ローカルで素早く確認する場合や、解決済みグラフが既に存在する場合は、パッケージマネージャーのロックファイルを直接利用できます。両方が手元にある場合は同時に渡してください。[SBOMと解決済みツリーを併用する](#sbomと解決済みツリーを併用する)を参照してください。

| エコシステム | olへ渡す解決済み入力 | 準備方法 |
|---|---|---|
| 共通 | CycloneDX / SPDX JSON SBOM | エコシステム固有のツールで依存関係を解決してSBOMを生成 |
| .NET / NuGet | `project.assets.json` v3/v4 | `dotnet restore`で`obj/project.assets.json`を生成 |
| npm | `package-lock.json` v2/v3 | `npm install`で`package-lock.json`を生成 |
| pnpm | `pnpm-lock.yaml` v9 | `pnpm install`で`pnpm-lock.yaml`を生成 |
| Yarn | Classic v1またはBerry metadata v8の`yarn.lock` | `yarn install`で`yarn.lock`を生成 |
| Rust / Cargo | cargo metadataのJSON | `cargo metadata --format-version 1 --locked`でjson生成|
| Go modules | go moduleとグラフ | `go list -m -json all`と`go mod graph`を同一ディレクトリに保存 |
| Python | pipのJSON v1 | `python -m pip inspect --local`で`pip-inspect.json`を生成 |
| PHP / Composer | `composer.json`と`composer.lock` | `composer.json`と`composer.lock`を同一ディレクトリに保存 |
| Ruby / Bundler | `Gemfile.lock` | `bundle install`で`Gemfile.lock`を生成 |
| Java / Maven | Maven Dependency Plugin 3.7以降のdependency tree JSON | `mvn dependency:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json`で保存 |
| Java / Gradle | Gradleの解決済みグラフには公式の可搬JSON形式がないため、SBOM | SBOMを生成 |
| SwiftPM | `Package.resolved` v2/v3 | `swift package resolve`で`Package.resolved`を生成 |
| CocoaPods | `Podfile.lock` | `pod install`で`Podfile.lock`を生成 |

> [!TIP]
> olは入力内容から形式を自動判定します。通常は`--input-format`を指定する必要はありません。複数の入力を渡すには`--input A --input B`と繰り返し指定します。

### SBOMと解決済みツリーを併用する

SBOMはビルドが解決したパッケージを記録しますが、そのパッケージがディスク上のどこにあるかは記録しません。パッケージマネージャー入力は両方を記録します。1つのSBOMと解決済みツリーを同時に渡すと、各componentは2つの入力が提供しうる証跡の和集合で判定されます。

```bash
ol scan --input bom.cdx.json --input .
```

SBOMを公開しているプロジェクトには、この入力を推奨します。SBOM単体では得られないものが2つ戻ってきます。

- **ビルドが実際に消費したパッケージ内のライセンスファイル。** olが復元済みパッケージ内の`LICENSE`を読むのは、そのパッケージの位置を入力が教えたときだけです。これはregistryのメタデータがライセンスを書いていない発行元を解決し、実在のパッケージで`matched`と未解決を分けます。たとえば`Microsoft.DotNet.PlatformAbstractions`はNuGet registryにライセンス記載がなくGitHubも`NOASSERTION`を返しますが、パッケージには`LICENSE.TXT`が同梱されています。解決済みツリーを指す入力がない場合、スキャンのサマリーはevidence表の`Package artifacts`行の`targets`に`0`を報告します。
- **resolverが生成した依存グラフ。** SBOMが利用可能なグラフを持つかは生成ツール次第です。不完全なグラフを出力する生成ツールでは、olが分類しないcomponentが残ります。`dependency: unknown`になると`--dependency direct`が効かなくなり、`--allow-dev-licenses`の緩和も適用されません。この緩和はresolverが開発専用であることを証明できたcomponentにしか適用されないためです。

SBOM側も、SBOMだけが知っていることを提供し続けます。生成元が主張したライセンスと、olが直接読むパッケージマネージャーの外にあるcomponentです。

1つのSBOMは、任意の数のパッケージマネージャー入力と組み合わせられます。2つ目のSBOMは入力エラーになります。1つの対象を表すリポジトリ全体の文書が2つあるのは入力側の矛盾であり、olが解決できるものではないからです。

```text
Unable to scan input: A collection accepts at most one SBOM document.
```

### 監査対象からリポジトリのサブツリーを除外する

```bash
# product-a/docsだけ除外。product-b/docsは対象のまま
ol scan --input product-a --input product-b \
  --exclude-input-path product-a/docs

# 複数パスは繰り返し指定
ol scan --input product-a --input product-b \
  --exclude-input-path product-a/docs \
  --exclude-input-path product-b/docs
```

パスはカレントディレクトリ基準の、実在する正確なファイルまたはディレクトリです。globには対応しません。除外配下を `--input` で明示したディレクトリは探索せずスキップし、明示したファイルはエラーになります。olのディレクトリ探索だけに作用するため、リポジトリ全体のSBOMを生成する場合は生成側でも同じパスを除外してください。



## よく使う操作

### 表示を絞り込む

`--dependency`は表示だけを絞り込みます。解析は常に完全な依存関係を対象にします。

```bash
ol scan --input . --dependency direct
ol scan --input . --group-by license
ol scan --input . --sort status,name
```

### 開発時のみの依存関係に別の許可リストを適用する

依存関係リゾルバーが開発時のみと証明できるコンポーネントに限り、追加の許可リストを適用できます。利用区分が不明な入力には適用されません。

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --allow-dev-licenses CC-BY-4.0
```

これは「本番成果物に含まれない」ことの証明ではありません。リリース成果物は基本の許可リストで別途確認してください。

**lockfileと一緒にSBOMをスキャンしても、この緩和は取り消されません。** SBOMは開発スコープを記録しないので到達性について何も述べておらず、その観測はresolverの判定を覆さず棄権します。componentを格下げできるのは、runtimeと判定したresolverだけです。どの入力も判定しなかったcomponentはunknownのままで、このオプションが緩和することはありません。

```text
$ ol scan --input package-lock.json --input bom.cdx.json --format json > report.json
$ ol check --report report.json --allow-licenses MIT --allow-dev-licenses CC-BY-4.0
Allowed by development policy: 1 component.
License check passed: 1 component satisfies the allow-list.
```

ひとつ知っておくべき点があります。lockfileより広い範囲を含むSBOMは、同じパッケージのruntime installを抱えている可能性があり、olはpackage URLだけで照合するため、それが開発用の行にfoldされます。SBOMが解決済み入力の外まで届いたかどうかは`Supplied by`のサマリー行で確認できます。

### 既存プロジェクトへベースラインを導入する

既存プロジェクトには、olが解決できないコンポーネントが残りえます。プライベートフィードのパッケージ、ライセンス欄のないレジストリ、GitHub以外のソースなどです。これらは安全側に倒して違反になりますが、自分のコードを直しても解消できません。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

```text
License check failed: 1 violation.

Package                  Version  Ecosystem  Purl                                     License/Status  Reason                 Mechanism                   Reference  Path
@mycompany/internal-sdk  1.0.0    npm        pkg:npm/%40mycompany/internal-sdk@1.0.0  unknown         license is unresolved  package_metadata_not_found  -          -

Unresolved mechanisms
  package_metadata_not_found: 1
```

`Reason`はポリシーがなぜ拒否したかを、`Mechanism`は証拠がなぜ確定しなかったかを示します。次の行動を決めるのは後者です。この例は公開レジストリに存在しないパッケージなので、収集を繰り返しても答えは出ません。末尾の集計は行を母集団にまとめます。未解決が100件あっても実際には数種類であり、母集団ごとに一度で片が付くからです。

確認して受け入れたものを`--update-baseline`で記録します。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 1 component.
License check passed: 2 components satisfy the allow-list.
```

`ol-baseline.json`には、対象コンポーネントと、その結論を生んだ証拠、そして証拠の指紋が記録されます。このファイルをバージョン管理へ追加します。生の証拠値と、パブリッシャーが宣言したライセンス参照先が入っているため、以降の変更はPRのdiffだけで判断できます。

```json
{
  "schemaVersion": 1,
  "acknowledged": [
    {
      "ecosystem": "npm",
      "name": "@mycompany/internal-sdk",
      "version": "1.0.0",
      "purl": "pkg:npm/%40mycompany/internal-sdk@1.0.0",
      "status": "unknown",
      "evidence": [
        { "source": "package-registry", "kind": "fetch", "raw": "" },
        { "source": "source-repository", "kind": "unavailable", "raw": "" }
      ],
      "fingerprint": "eb7d5af4cdf1b2d6cff18128705d9a713c8d82d16426ba3a7d2463e4c512c41e"
    }
  ]
}
```

以降の実行では、ファイルを指定して`--update-baseline`を外します。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json
```

**新たに未解決となったコンポーネントは、引き続き違反になります。** これがベースラインの目的です。承認済みの集合は、レビューを経ずに増えません。

```text
Acknowledged by baseline: 1 component.
License check failed: 1 violation.

Package                Version  Ecosystem  Purl                                   License/Status  Reason                 Mechanism                   Reference  Path
@mycompany/reporting   2.1.0    npm        pkg:npm/%40mycompany/reporting@2.1.0   unknown         license is unresolved  package_metadata_not_found  -          -

Unresolved mechanisms
  package_metadata_not_found: 1
```

**禁止ライセンスは、再生成しても吸収されません。** 承認できるのは`unknown`、`ambiguous`、`conflict`、`invalid`だけで、しかも認識可能な候補が許可リストに拒否されない場合に限られます。解決済みのライセンスは`--allow-licenses`で扱う対象であり、`error`は修復すべき収集失敗です。許可リストがどう読んでも許可する`ambiguous`の列挙も、レビューすべき違反ではないため承認対象になりません。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 1 component.
License check failed: 1 violation.

Package       Version  Ecosystem  Purl                          License/Status  Reason                  Mechanism  Reference  Path
copyleft-lib  3.0.0    npm        pkg:npm/copyleft-lib@3.0.0    GPL-3.0-only    license is not allowed  -          -          pkg:npm/report-builder@1.4.0 > pkg:npm/copyleft-lib@3.0.0
```

承認されたコンポーネントは、レポート上では未解決のステータスと証拠をそのまま保持します。外れるのは違反という扱いだけです。バージョンが上がったり、レジストリが記載を修正したりすると指紋が一致しなくなり、そのコンポーネントは再びレビューされるまで違反に戻ります。

### 一部コンポーネントの収集または評価を除外する

2つのオプションは目的が異なります。

| オプション | 段階 | 振る舞い |
|---|---|---|
| `scan --skip-evidence-packages <purl-prefix>` | 証拠収集 | 一致するコンポーネントへの外部アクセスを行わない。コンポーネント自体はレポートと評価に残る。 |
| `check --exclude-packages <purl-prefix>` | ポリシー評価 | 一致するコンポーネントを許可リスト評価、ベースライン、違反、SARIFから除外する。スキャンレポートは変更しない。 |

どちらも大文字と小文字を区別するPackage URLの接頭辞を使います。所有者やプライベートパッケージをolが自動判定する機能ではありません。

名前空間は、そのエコシステムでの表記のまま書けます。`--skip-evidence-packages pkg:npm/@acme/`は`pkg:npm/%40acme/util@1.0.0`に一致します。バージョン区切りの`@`はそのまま扱うため、`pkg:npm/left-pad@1.3.0`は引き続きそのコンポーネント1つだけを指します。

private packageを扱うためにどちらかが必須ということはありません。レジストリの`404`はすでに`unknown`になるため、ベースラインで承認できます。成功し得ないリクエストを止めたいときに`--skip-evidence-packages`を、コンポーネント自体を検査の対象外にしたいときに`--exclude-packages`を使います。

### 2つのレポートを比較する

```bash
ol diff --previous before.json --current after.json
ol diff --previous before.json --current after.json --format json
```

追加、削除、バージョン、ステータス、ライセンス、証拠の変更だけを表示します。変更が存在しても比較に成功すれば終了コードは`0`です。ポリシー判定は`check`で行います。

変更の前に、2つのレポートが同じ母集団を記述しているとは限らない場合、それぞれのレポートが生成された境界（除外された入力パス、`--dependency`フィルター、入力カバレッジ）を表示します。読み込んだ入力が少ないレポートはコンポーネントも少なく、そのすべてが削除として現れます。境界がなければ「入力が読まれなかった」と「依存が削除された」が同じdiffになります。

### SARIFを出力する

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --sarif ol.sarif
```

stdoutの判定結果は変わらず、同じ違反集合をSARIF 2.1.0として保存します。依存グラフが利用できる場合、推移的な違反へ至る最短の依存パスも含まれます。

## よくある質問

### `package.json`、`*.csproj`、`Cargo.toml`を直接渡せますか

渡せません。これらは要求された依存関係を示すマニフェストであり、実際に解決されたバージョンや推移的依存関係を確定しません。SBOMを生成するか、対応する解決済み入力を渡してください。.NETでは`dotnet restore`を実行して`obj/project.assets.json`を指定します。Rustでは`cargo metadata --format-version 1 --locked > cargo-metadata.json`を実行して`cargo-metadata.json`を指定します。`Cargo.toml`も`Cargo.lock`も直接は指定できません。lockfileをコミットしていないライブラリでは`--locked`を外してください。

### SBOMとパッケージマネジャー入力のどちらを使うべきですか

リリース、監査、複数エコシステムを含むリポジトリでは、全体を表す1つのSBOMを推奨します。ローカルでのフィードバックや、解決済みグラフが既に生成・commitされている場合はパッケージマネジャー入力が便利です。

両方あるなら、両方渡すのがさらに良い選択です。olはpackage URLで両者を突き合わせて証跡を統合し、`SUPPLIED`列にcomponentがSBOM由来か、パッケージマネジャー入力由来か、その両方かを表示します。2つの入力が列挙するcomponentが異なる場合 (ロックファイルにはSBOMが省いたエントリが含まれることがあります)、両者の食い違いを別々にスキャンして隠すのではなく報告させたい場合、そしてビルドが消費したパッケージ内のライセンスファイルをolに読ませられるのはパッケージマネジャー入力だけであるという理由から、併用する価値があります。[SBOMと解決済みツリーを併用する](#sbomと解決済みツリーを併用する)を参照してください。

### olはネットワーク接続を必要としますか

既定では外部証拠を収集するため利用しますが、`--no-external-evidence`で完全に無効化できます。通常のSPDX検証にはbundled dataを利用できるため、事前セットアップやネットワーク接続は必須ではありません。

### 未解決の既存依存関係はどう扱いますか

まず生の証拠とステータスを確認してください。修正できない既知の未解決項目はベースラインで承認できますが、禁止ライセンスや`error`は承認できません。

### スキャンせずに別のポリシーを適用できますか

できます。保存したcanonical JSONレポートを、異なる`--allow-licenses`で`check`してください。`check`は外部アクセスを行いません。

## エコシステム別の使い方

エコシステムごとのSBOM生成方法、解決済み入力のコマンド例、入力時の注意点は[エコシステム別の使い方](docs/ja/echosystems.md)を参照してください。

.NET/NuGet、JavaScript、Rust、Go、Python、PHP/Composer、Ruby/Bundler、Java/Maven・Gradle、SwiftPM、CocoaPodsを掲載しています。

## 詳細ドキュメント

- [エコシステム別の使い方](docs/ja/echosystems.md)
- [デザイン原則](.github/docs/DESIGN.md)
- [アーキテクチャ](.github/docs/Architecture.md)
- [CLIとレポートの仕様](.github/docs/specs/cli.md)
- [SPDXの仕様](.github/docs/specs/spdx.md)
- [パッケージマネジャー証拠の仕様](.github/docs/specs/packagemanager.md)
- [ソースリポジトリ証拠の仕様](.github/docs/specs/source.md)
- [キャッシュ形式の仕様](.github/docs/specs/cache_format.md)

## Development

エコシステムCIとセルフスキャンの契約は[verification.md](.github/docs/specs/verification.md)に記載されています。

Repository sandbox

```bash
# Regenerate ol's committed SBOM and text, Markdown, and JSON report.
./sandbox/Update-SelfScan.ps1

# keep the committed SBOM as a fixed golden input and regenerate only its derived reports
./sandbox/Update-SelfScan.ps1 -ReportsOnly
```

Scan

```bash
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format markdown
```

Check

```bash
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format json > ol-report.json
dotnet run --project src/Ol -- check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

生成データ

```bash
# SPDXライセンスリストを生成/更新します
dotnet run --project src/Ol.Update -- generate
```
