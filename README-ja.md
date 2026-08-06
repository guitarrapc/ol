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
- npm/Cargo/NuGetなどのパッケージマネージャーが解決したロックファイル（`package-lock.json`、`Cargo.lock`、`project.assets.json`など）

## クイックスタート

言語を問わず解決済みの依存関係を扱うには、SBOM（例では`bom.cdx.json`）が最も便利です。olはSBOM以外にも、各言語のパッケージマネージャーが解決したロックファイルや出力を直接入力として受け付けます。

> [!TIP]
> CycloneDX JSON SBOMを生成するには、[@cyclonedx/cyclonedx-npm](https://www.npmjs.com/package/@cyclonedx/cyclonedx-npm)などのツールがあります。

```bash
# macOS/Linuxでは、必要に応じて実行権限を付与
chmod +x ./ol

# SBOMを生成
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json

# CycloneDXまたはSPDX形式のJSON SBOMをスキャン
ol scan --input bom.cdx.json

# カレントディレクトリ以下から対応する解決済み依存関係をスキャン
ol scan --input .

# 対応するロックファイルやパッケージマネージャー出力を直接スキャン
ol scan --input package-lock.json
ol scan --input src/MyProject/obj/project.assets.json

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
  cache clear     Clears cached evidence for the specified category.
  check           Check a canonical JSON scan report against allowed SPDX licenses.
  diff            Compare two persisted JSON scan reports and report license-relevant changes.
  scan            Scan a resolved dependency input.
  spdx clear      Clear user-managed SPDX data.
  spdx list       List installed SPDX data versions.
  spdx update     Download SPDX data into the user data directory.
  spdx use        Switch active SPDX data version.
  spdx version    Show the active SPDX data source.
```

| コマンド | 役割 |
|---|---|
| `ol scan` | 解決済み依存関係からライセンス証拠を収集し、レポートを生成する。 |
| `ol check` | canonical JSONレポートを許可リストで評価する。 |
| `ol diff` | 2つのcanonical JSONレポートを比較する。 |
| `ol cache clear` | olが管理する証拠キャッシュを削除する。 |
| `ol spdx version` | 使用中のSPDXデータを表示する。 |
| `ol spdx list` | インストール済みSPDXデータを一覧表示する。 |
| `ol spdx update` | SPDXデータをダウンロードする。 |
| `ol spdx use` | 使用するSPDXデータのversionを切り替える。 |
| `ol spdx clear` | ユーザー管理のSPDXデータを削除する。 |

SBOMやロックファイルといった依存関係の情報からライセンスを収集するには`scan`を使います。解析レポートから、利用しているパッケージの一覧とライセンスを確認できます。JSON形式で出力すると、`check`や`diff`で再利用できます。

```bash
$ ol scan --help
Usage: scan [options...] [-h|--help] [--version]

Scan a resolved dependency input.

Options:
  --input <string[]>                    Repeatable resolved dependency input files or directories. [Required]
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
  --baseline <string?>              Baseline file acknowledging already reviewed unresolved components. [Default: null]
  --update-baseline                 Rewrite the baseline file as a complete snapshot.
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
  clear    Clears cached evidence for the specified category.
```

### 終了コード

各コマンドの終了コードは次の通りです。CIでは`check`の終了コードを利用して、許可ライセンス違反があるかを判定できます。

| 終了コード | 意味 |
| ---: | --- |
| `0` | コマンドは正常に完了しました。helpやversionの出力も`0`を返します。 |
| `1` | 引数の解析に失敗した、または無効な設定、入力、I/O、その他の実行エラーのためコマンドを完了できませんでした。 |
| `2` | `check`がポリシー評価を完了し、1つ以上の違反を検出しました。 |
| `3` | `check`は完了しましたが、検出されたものがすべて収集失敗であり、判定を確定できませんでした。 |

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

> [!TIP]
> olは任意のリポジトリ内容をクロールしたり、ディレクトリ構成やライセンスファイルからライセンスを推測したりしません。

## パッケージ依存関係の解決

リリースや監査の成果物には、対象全体を表す1つのCycloneDXまたはSPDX JSON SBOMを推奨します。ローカルで素早く確認する場合や、解決済みグラフが既に存在する場合は、パッケージマネージャーのロックファイルを直接利用できます。

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
> olは入力内容から形式を自動判定します。通常は`--input-format`を指定する必要はありません。複数のパッケージマネジャーを指定するには、`--input A --input B`と繰り返し指定します。ただし、SBOMとパッケージマネジャー入力は混在できません。リポジトリ全体のSBOMを使うか、解決済みパッケージマネジャー入力をまとめるか、どちらかを選択してください。



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

### 既存プロジェクトへベースラインを導入する

既存プロジェクトには、olが解決できないコンポーネントが残りえます。プライベートフィードのパッケージ、ライセンス欄のないレジストリ、GitHub以外のソースなどです。これらは安全側に倒して違反になりますが、自分のコードを直しても解消できません。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

```text
License check failed: 1 violation.

Package                  Version  Ecosystem  Purl                                     License/Status  Reason
@mycompany/internal-sdk  1.0.0    npm        pkg:npm/%40mycompany/internal-sdk@1.0.0  unknown         license is unresolved
```

確認して受け入れたものを`--update-baseline`で記録します。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 1 component.
License check passed: 2 components satisfy the allow-list.
```

`ol-baseline.json`には、対象コンポーネントと、その結論を生んだ証拠、そして証拠の指紋が記録されます。このファイルをバージョン管理へ追加します。生の証拠値が入っているため、以降の変更はPRのdiffだけで判断できます。

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

Package                Version  Ecosystem  Purl                                   License/Status  Reason
@mycompany/reporting   2.1.0    npm        pkg:npm/%40mycompany/reporting@2.1.0   unknown         license is unresolved
```

**禁止ライセンスは、再生成しても吸収されません。** 承認できるのは`unknown`、`ambiguous`、`conflict`、`invalid`だけで、しかも認識可能な候補が許可リストに拒否されない場合に限られます。解決済みのライセンスは`--allow-licenses`で扱う対象であり、`error`は修復すべき収集失敗です。

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 1 component.
License check failed: 1 violation.

Package       Version  Ecosystem  Purl                          License/Status  Reason
copyleft-lib  3.0.0    npm        pkg:npm/copyleft-lib@3.0.0    GPL-3.0-only    license is not allowed
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

### SARIFを出力する

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --sarif ol.sarif
```

stdoutの判定結果は変わらず、同じ違反集合をSARIF 2.1.0として保存します。依存グラフが利用できる場合、推移的な違反へ至る最短の依存パスも含まれます。

## よくある質問

### `package.json`、`*.csproj`、`Cargo.toml`を直接渡せますか

渡せません。これらは要求された依存関係を示すマニフェストであり、実際に解決されたバージョンや推移的依存関係を確定しません。SBOMを生成するか、対応する解決済み入力を渡してください。

### SBOMとパッケージマネジャー入力のどちらを使うべきですか

リリース、監査、複数エコシステムを含むリポジトリでは、全体を表す1つのSBOMを推奨します。ローカルでのフィードバックや、解決済みグラフが既に生成・commitされている場合はパッケージマネジャー入力が便利です。

### olはネットワーク接続を必要としますか

既定では外部証拠を収集するため利用しますが、`--no-external-evidence`で完全に無効化できます。通常のSPDX検証にはbundled dataを利用できるため、事前セットアップやネットワーク接続は必須ではありません。

### 未解決の既存依存関係はどう扱いますか

まず生の証拠とステータスを確認してください。修正できない既知の未解決項目はベースラインで承認できますが、禁止ライセンスや`error`は承認できません。

### スキャンせずに別のポリシーを適用できますか

できます。保存したcanonical JSONレポートを、異なる`--allow-licenses`で`check`してください。`check`は外部アクセスを行いません。

## scan例

### SBOM

olはCycloneDXとSPDX形式のJSON SBOMを受け付けます。リリース、監査、CIの成果物には、各エコシステム向けの生成ツールで依存グラフを解決し、基準となるSBOMを1つ生成してください。`scan`で照合済みのライセンス証拠を確認し、同じ入力から生成したレポートを`check`でSPDXライセンスの許可リストに照らして評価します。

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

<details><summary>出力例（Markdown）</summary>

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| Ol | 0.0.0 | - | - | root | unknown |
| BenchmarkDotNet | 0.15.8 | MIT | nuget | direct | matched |
| BenchmarkDotNet.Annotations | 0.15.8 | MIT | nuget | transitive | matched |
| CommandLineParser | 2.9.1 | MIT | nuget | transitive | matched |
| ConsoleAppFramework | 5.7.13 | MIT | nuget | direct | matched |
| EnumerableAsyncProcessor | 3.8.4 | MIT | nuget | transitive | matched |
| Gee.External.Capstone | 2.3.0 | MIT | nuget | transitive | matched |
| Iced | 1.21.0 | MIT | nuget | transitive | matched |
| Microsoft.ApplicationInsights | 2.23.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.Common | 4.14.0 | MIT | nuget | transitive | matched |
| Microsoft.DiaSymReader | 2.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | MIT | nuget | transitive | matched |
| Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | nuget | direct | matched |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | - | nuget | transitive | unknown |
| Microsoft.Extensions.DependencyInjection | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.DependencyInjection.Abstractions | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.DependencyModel | 6.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Logging | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Logging.Abstractions | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Options | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Primitives | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.NET.ILLink.Tasks | 10.0.9 | MIT | nuget | direct | matched |
| Microsoft.Testing.Extensions.CodeCoverage | 18.3.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.Telemetry | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.TrxReport | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Platform | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Platform.MSBuild | 2.0.2 | MIT | nuget | transitive | matched |
| Perfolizer | 0.6.1 | MIT | nuget | transitive | matched |
| Pragmastat | 3.2.4 | MIT | nuget | transitive | matched |
| System.CodeDom | 9.0.5 | MIT | nuget | transitive | matched |
| System.Management | 9.0.5 | MIT | nuget | transitive | matched |
| System.Reflection.TypeExtensions | 4.7.0 | MIT | nuget | transitive | matched |
| TUnit | 1.12.111 | MIT | nuget | direct | matched |
| TUnit.Assertions | 1.12.111 | MIT | nuget | transitive | matched |
| TUnit.Core | 1.12.111 | MIT | nuget | transitive | matched |
| TUnit.Engine | 1.12.111 | MIT | nuget | transitive | matched |
| runtime.win-x64.Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | nuget | unknown | matched |

Scan summary
  License results: 42 displayed components; 40 matched; 0 conflict; 2 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 14 warnings; 0 deprecated SPDX identifiers
  Package metadata (full scan): 41 supported; 41 cache hits; 0 cache misses; 0 refreshed; 0 fetch errors; 0 unsupported ecosystems
  Source repositories (full scan): 20 targets; 0 GitHub requests; 20 cache hits; 0 cache misses; 0 fetch errors; 14 components without source license
  Run: concurrency 8; retries 1; GitHub auth none
  Input: cyclonedx-sample.json; input format CycloneDX; SPDX 5e59516 (bundled)

</details>

### NuGet

**SBOM:** リストア済みのソリューションから[CycloneDX for .NET](https://github.com/CycloneDX/cyclonedx-dotnet)でCycloneDX JSONを生成し、その成果物を`scan`と`check`へ渡します。

```bash
dotnet tool restore
dotnet tool run dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**解決済みNuGet入力:** NuGetが生成した`project.assets.json`を直接スキャンします。リポジトリやソリューション全体を対象にする場合はディレクトリを渡すと、その配下の`project.assets.json`をolが再帰的に検出してまとめます。

```bash
dotnet restore MySolution.slnx
ol scan --input src/Ol/obj/project.assets.json --format markdown
ol scan --input src/Ol/obj/project.assets.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

複数の`project.assets.json`を含むディレクトリを指定できます。

```bash
ol scan --input src/ --input tests/ --format markdown
ol scan --input src/ --input tests/ --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

NuGetの解決結果は、プロジェクト、ターゲットフレームワーク、Runtime Identifierごとに異なる場合があります。olはそれぞれを個別の出現コンテキストとして保持しながら、同じパッケージとバージョンはレポート上で1件にまとめます。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/nuget-assets`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| BenchmarkDotNet | 0.15.8 | MIT | nuget | direct | matched |
| BenchmarkDotNet.Annotations | 0.15.8 | MIT | nuget | transitive | matched |
| CommandLineParser | 2.9.1 | MIT | nuget | transitive | matched |
| ConsoleAppFramework | 5.7.13 | MIT | nuget | direct | matched |
| EnumerableAsyncProcessor | 3.8.4 | MIT | nuget | transitive | matched |
| Gee.External.Capstone | 2.3.0 | MIT | nuget | transitive | matched |
| Iced | 1.21.0 | MIT | nuget | transitive | matched |
| Microsoft.ApplicationInsights | 2.23.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.Analyzers | 3.11.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.CSharp | 4.14.0 | MIT | nuget | transitive | matched |
| Microsoft.CodeAnalysis.Common | 4.14.0 | MIT | nuget | transitive | matched |
| Microsoft.DiaSymReader | 2.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.NETCore.Client | 0.2.510501 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.Runtime | 3.1.512801 | MIT | nuget | transitive | matched |
| Microsoft.Diagnostics.Tracing.TraceEvent | 3.1.21 | MIT | nuget | transitive | matched |
| Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | nuget | direct | matched |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | - | nuget | transitive | unknown |
| Microsoft.Extensions.DependencyInjection | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.DependencyInjection.Abstractions | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.DependencyModel | 6.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Logging | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Logging.Abstractions | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Options | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.Extensions.Primitives | 6.0.0 | MIT | nuget | transitive | matched |
| Microsoft.NET.ILLink.Tasks | 10.0.9 | MIT | nuget | direct | matched |
| Microsoft.Testing.Extensions.CodeCoverage | 18.3.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.Telemetry | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.TrxReport | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Platform | 2.0.2 | MIT | nuget | transitive | matched |
| Microsoft.Testing.Platform.MSBuild | 2.0.2 | MIT | nuget | transitive | matched |
| Perfolizer | 0.6.1 | MIT | nuget | transitive | matched |
| Pragmastat | 3.2.4 | MIT | nuget | transitive | matched |
| System.CodeDom | 9.0.5 | MIT | nuget | transitive | matched |
| System.Management | 9.0.5 | MIT | nuget | transitive | matched |
| System.Reflection.TypeExtensions | 4.7.0 | MIT | nuget | transitive | matched |
| TUnit | 1.12.111 | MIT | nuget | direct | matched |
| TUnit.Assertions | 1.12.111 | MIT | nuget | transitive | matched |
| TUnit.Core | 1.12.111 | MIT | nuget | transitive | matched |
| TUnit.Engine | 1.12.111 | MIT | nuget | transitive | matched |
| runtime.win-x64.Microsoft.DotNet.ILCompiler | 10.0.9 | MIT | nuget | transitive | matched |

Scan summary
  License results: 41 displayed components; 40 matched; 0 conflict; 1 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 11 warnings; 0 deprecated SPDX identifiers
  Package metadata (full scan): 41 supported; 41 cache hits; 0 cache misses; 0 refreshed; 0 fetch errors; 0 unsupported ecosystems
  Source repositories (full scan): 19 targets; 0 GitHub requests; 19 cache hits; 0 cache misses; 0 fetch errors; 14 components without source license
  Run: concurrency 8; retries 1; GitHub auth none
  Input: 2 inputs; input format NuGet assets; SPDX 5e59516 (bundled)

</details>

### JavaScript/Node.js

**SBOM:** npmでは[CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm)を使ってCycloneDX JSONを生成します。pnpm、Yarn、または複数構成が混在するJavaScriptリポジトリでは、[cdxgen](https://github.com/CycloneDX/cdxgen)のような多言語対応の生成ツールを利用できます。

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

**解決済みパッケージマネージャー入力:** 対応するロックファイルまたはディレクトリを直接渡します。

#### npm

olは`package-lock.json` v2/v3をスキャンします。

```bash
ol scan --input package-lock.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/npm-package-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | MIT | npm | direct | matched |
| shared-package | 2.0.0 | Apache-2.0 | npm | transitive | matched |

</details>

#### pnpm

olは`pnpm-lock.yaml` v9をスキャンします。

```bash
ol scan --input pnpm-lock.yaml --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/pnpm-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | direct | unknown |
| shared-package | 2.0.0 | - | npm | transitive | unknown |

</details>

#### Yarn Classic

olは`yarn.lock` v1をスキャンします。

```bash
ol scan --input yarn.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/yarn-classic-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | unknown | unknown |
| shared-package | 2.0.0 | - | npm | unknown | unknown |

</details>

#### Yarn Berry

olはmetadata v8の`yarn.lock`をスキャンします。

```bash
ol scan --input yarn.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/yarn-berry-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | direct | unknown |
| shared-package | 2.0.0 | - | npm | transitive | unknown |

</details>

パッケージマネージャーを実行したり、現在のホスト環境に対してプラットフォーム条件を評価したりすることなく、workspaceとimporterのコンテキスト、および確定できる依存関係エッジを保持します。

### Rust

**SBOM:** [CycloneDX for Rust Cargo](https://github.com/CycloneDX/cyclonedx-rust-cargo)を使い、CargoプロジェクトからCycloneDX JSONを生成します。

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

複数のBOMを生成するworkspaceでは、olへ渡す前に基準となる1つのSBOMへ統合してください。

**解決済みCargo入力:** ビルドと同じロック済みfeatureおよびtargetの選択条件でCargo metadataを生成し、そのファイルをスキャンします。

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

各workspace memberは個別の解決コンテキストになります。workspace nodeとpath nodeは到達可能性の判定に使いますが、crates.ioのパッケージとして扱うことはありません。解決済みfeature、依存関係の種類、target式はvariantとして保持し、現在のホスト環境に対して再評価しません。Cargo metadataには`--filter-platform`引数そのものが記録されないため、olはスキャンを実行したマシンからtarget tripleを推測しません。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/cargo-metadata`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| itoa | 1.0.0 | MIT OR Apache-2.0 | cargo | transitive | matched |
| serde | 1.0.0 | MIT OR Apache-2.0 | cargo | direct | matched |

</details>

### Go

**SBOM:** [CycloneDX for Go modules](https://github.com/CycloneDX/cyclonedx-gomod)を使い、moduleからCycloneDX JSONを生成します。リリース対象のアプリケーションと同じGOOS、GOARCH、CGO、build tagを指定してください。

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**解決済みGo入力:** GoはMVSで選択したbuild listをロックファイルへ保存しません。同じmoduleまたはworkspaceから、選択済みmodule listと依存関係エッジの両方を、次の正確なファイル名で生成します。

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt

ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

代わりに、2ファイルを含むディレクトリを渡すこともできます。olはこの組み合わせを1つの`go-module-graph`入力として扱います。

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

`go-list-modules.json`を、選択済みbuild listと置換情報の正しい情報源として扱います。`go-mod-graph.txt`からは、両端がそのbuild listに存在するエッジだけを採用するため、置き換え前のmodule versionやGoの`go@...`/`toolchain@...` graph nodeはコンポーネントになりません。ローカルへの置き換えにはproxy purlを付与せず、ファイルシステム上のパスもレポートしません。version付きmoduleへの置き換えでは、元の要求を`sourceId`として保持しながら、置き換え先のmodule/versionを証拠の補完に利用します。list JSONに`Retracted`が含まれる場合は、出現情報へ`retracted` variantを保持します。どちらの出力からも確定できないGOOS、GOARCH、build tagは未指定のままにします。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/go-module-graph`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| github.com/google/uuid | v1.6.0 | - | golang | direct | unknown |

</details>

### Python

**SBOM:** [CycloneDX Python SBOM generator](https://github.com/CycloneDX/cyclonedx-python)を使い、ビルドまたはデプロイで実際に使用するPython環境からCycloneDX JSONを生成します。

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

この生成ツールはPoetry、Pipenv、pip requirementsも入力にできます。インストール済み環境から生成すると、ビルドで実際に選択されたパッケージを最も確実に把握できます。

**解決済みPython入力:** olは`pip inspect`が生成する安定版JSON形式v1をスキャンします。対象の仮想環境を有効化し、インストール済みディストリビューションと環境情報を出力します。

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

インストール済みディストリビューションの集合を正しい情報源とし、olは`requirements.txt`、`pyproject.toml`、Poetry、uv、Pipenvの宣言を解決しません。`requested=true`のディストリビューションは直接依存関係としてroot edgeを持ちます。`requested=false`から推移的依存関係と確定できるのは`installer`が`pip`の場合だけで、ほかのインストーラーや`requested`フィールドがない場合は`unknown`になります。条件のない`requires_dist`は、正規化した対象がインストール済みであればパッケージ間のエッジを生成します。environment markerやextraを含む項目は、どのextraが有効だったかを`pip inspect`が記録しないためエッジを生成しません。レポートのコンテキストには、入力に記録されたPythonのバージョン、実装、`sys_platform`、マシンアーキテクチャ、pipのバージョンを保持します。

ディストリビューション名はPyPAの規則で正規化し、識別と`pkg:pypi`による証拠補完に利用します。`direct_url`を持つディストリビューションにはPyPI purlを付与せず、`source=direct`だけを保持します。ローカルパスとURLはレポートしません。入力由来のライセンス証拠には、従来の`license`メタデータより`license_expression`を優先します。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/pip-inspect`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| Local_Package | 1.0.0 | - | pypi | direct | unknown |
| PySocks | 1.7.1 | - | pypi | transitive | unknown |
| Requests | 2.32.4 | Apache-2.0 | pypi | direct | matched |
| charset_normalizer | 3.4.2 | MIT | pypi | transitive | matched |
| urllib3 | 2.5.0 | MIT | pypi | transitive | matched |

</details>

### PHP / Composer

**SBOM:** [CycloneDX PHP Composer plugin](https://github.com/CycloneDX/cyclonedx-php-composer)を使い、ロック済みのComposerプロジェクトからCycloneDX JSONを生成します。

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**解決済みComposer入力:** olは同じディレクトリにある`composer.json`と`composer.lock`の組み合わせを直接スキャンします。

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

ロックファイルから、解決済みの本番用と開発用のパッケージ集合を取得します。マニフェストから利用するのはルートパッケージの識別情報と、直接の`require`/`require-dev`関係だけです。olがComposerを実行したり、バージョン制約を解決したり、`vendor/`を調べたりすることはありません。利用可能な場合はPackagistからパッケージメタデータを補完し、そこに含まれるリポジトリURLをGitHub License APIのソース証拠へ利用することがあります。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/composer-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| example/container | 1.1.0 | Apache-2.0 | composer | direct | matched |
| monolog/monolog | 3.9.0 | MIT | composer | direct | matched |
| phpunit/phpunit | 11.5.0 | BSD-3-Clause | composer | direct | matched |
| psr/log | 3.0.2 | MIT | composer | transitive | matched |
| sebastian/version | 5.0.2 | BSD-3-Clause | composer | transitive | matched |

</details>

### Ruby / Bundler

**SBOM:** [CycloneDX Ruby Gem](https://github.com/CycloneDX/cyclonedx-ruby-gem)を使い、ロック済みのBundlerプロジェクトからCycloneDX JSONを生成します。

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**解決済みBundler入力:** olは`Gemfile`、Bundler、RubyGemsを実行せず、`Gemfile.lock`を直接スキャンします。

```bash
ol scan --input Gemfile.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

ロックファイルの`DEPENDENCIES`セクションから直接依存関係を識別し、解決済みspecの依存関係から推移的エッジを構築します。記録されたplatformはそれぞれ個別の解決コンテキストになります。`https://rubygems.org/`から解決されたgemだけに`pkg:gem`識別子を付与し、RubyGems.orgのメタデータで証拠を補完します。プライベートレジストリ、Git、path sourceは、リモートパスやローカルパスを公開せずにソース分類だけを保持します。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/bundler-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| local-gem | 0.1.0 | - | - | direct | unknown |
| private-gem | 2.0.0 | - | - | direct | unknown |
| concurrent-ruby | 1.3.5 | - | gem | transitive | unknown |
| i18n | 1.14.7 | - | gem | direct | unknown |
| nokogiri | 1.18.0 | - | gem | direct | unknown |
| rack | 3.1.8 | - | gem | transitive | unknown |
| rack-protection | 4.1.1 | - | gem | direct | unknown |

</details>

### Java / JVM

#### Maven

**SBOM:** [CycloneDX Maven plugin](https://github.com/CycloneDX/cyclonedx-maven-plugin)を使い、解決済みMaven reactorから集約したCycloneDX JSONを生成します。

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**解決済みMaven入力:** olはMaven Dependency Plugin 3.7.0以降が生成したJSONをスキャンします。

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

ルート成果物は1つの解決コンテキストになります。ルート直下を直接依存関係、それより深いノードを推移的依存関係とし、各ノードの有効なscope、optionalフラグ、type、classifier、流入エッジを保持します。同じcoordinatesが複数回現れる場合は、グラフ上の出現を分けたまま、レポートでは1つのコンポーネントとして共有します。dependency tree JSONにはライセンスメタデータがないため、olはcanonical Maven purlを使い、deps.devからバージョン固有のライセンスとソースリポジトリ情報を補完します。deps.devがAND/OR関係のない複数ライセンスを返した場合は、SPDX式を作り上げず、曖昧な証拠として保持します。ビルドで有効なPOMメタデータとリポジトリコンテキストを入力成果物自体に含めたい場合は、CycloneDXを推奨します。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/maven-dependency-tree`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct | 2.0.0 | - | maven | direct | unknown |
| provided | 4.0.0 | - | maven | direct | unknown |
| transitive | 3.0.0 | - | maven | transitive | unknown |

</details>

#### Gradle

**SBOM:** ルートプロジェクトへ[CycloneDX Gradle plugin](https://github.com/CycloneDX/cyclonedx-gradle-plugin)を適用します。

```kotlin
plugins {
    id("org.cyclonedx.bom") version "3.2.4"
}
```

集約したJSON SBOMを生成してスキャンします。

```bash
./gradlew cyclonedxBom
ol scan --input build/reports/cyclonedx/bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

プロジェクト単位で出力するには`cyclonedxDirectBom`を使います。対象configurationはプラグインの`includeConfigs`と`skipConfigs`で選択できます。

**解決済みGradle入力:**

olはGradleの解決済み依存関係を直接入力としてサポートしていません。代わりにCycloneDXまたはSPDX形式のJSON SBOMを生成してください。

Gradleは、解決済み依存グラフの機械可読なJSON形式を公式には定義・提供していません。組み込みの`dependencies`と`dependencyInsight`レポートは人向けの出力であり、可搬性のある入力形式ではありません。

### Swift / Objective-C

#### SwiftPM

**解決済みSwiftPM入力:** パッケージグラフを解決し、`Package.resolved`を直接スキャンします。

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

olは`Package.swift`を評価せずに、`Package.resolved`スキーマv2とv3を読み取ります。各pinについて、解決済みバージョンまたはsource revision、source kind、v3のorigin hashを保持します。ロックファイルにはパッケージ間のエッジが含まれないため、依存関係の種類は`unknown`のままです。認証情報を含まないHTTP(S)のsource control locationだけにcanonicalな`pkg:swift`識別子とrepository hintを付与します。registry、local、認証情報を含むlocationは、リモートパッケージの識別情報として公開しません。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/swift-package-resolved`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| internal-kit | main | - | swift | unknown | unknown |
| swift-log | 1.6.2 | - | swift | unknown | unknown |

</details>

#### CocoaPods

**解決済みCocoaPods入力:** podをインストールし、`Podfile.lock`を直接スキャンします。

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

ロックファイルの`DEPENDENCIES`セクションから直接依存するpodを識別し、解決済みpodの依存関係から推移的エッジを構築します。subspecはroot podへまとめ、パッケージ識別子とライセンスを評価します。`SPEC REPOS`によってpublic trunk、CDN、またはSpecs repository由来と確認できるpodだけに`pkg:cocoapods`識別子を付与し、CocoaPods CDNからバージョン固有のライセンスとソース情報を補完します。private specやexternal sourceのpodは、リポジトリURLやローカルパスを公開せずにソース分類を保持します。

<details><summary>出力例（Markdown）</summary>

Input: `package-manager/cocoapods-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| Alamofire | 5.10.2 | - | cocoapods | transitive | unknown |
| Moya | 15.0.0 | - | cocoapods | direct | unknown |

</details>

## 詳細ドキュメント

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
