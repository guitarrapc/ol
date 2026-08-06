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

GitHub の Releases ページから利用 OS 向けアセットをダウンロードし、`ol`（Windows は `ol.exe`）を任意の場所に配置します。

```sh
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
