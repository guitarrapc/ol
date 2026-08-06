# エコシステム別の使い方

[English](../en/echosystems.md) | 日本語

olは解決済みの依存グラフをスキャンします。CycloneDXまたはSPDX形式のJSON SBOM、対応するロックファイル、パッケージマネージャーの解決済み出力を指定します。`package.json`、`*.csproj`、`Cargo.toml`などの未解決マニフェストは単独で指定できません。

スキャンしてからポリシーを適用します。

```bash
ol scan --input <解決済み入力> --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## SBOM

リリース、監査、CI、複数エコシステムを含むリポジトリでは、ビルドの解決済み依存グラフからCycloneDXまたはSPDX形式のJSON SBOMを1つ生成します。

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

SBOMは各エコシステム向けツールで生成します。olはパッケージメタデータとGitHub License APIの証拠を追加し、未解決または競合する証拠をポリシー評価前に報告します。

## .NET / NuGet

NuGetのSBOM生成には、[cyclonedx-dotnet](https://github.com/CycloneDX/cyclonedx-dotnet)を使います。単一のproject fileから生成する場合は、参照先プロジェクトもスキャンするため`--recursive`を指定することで、`PrivateAssets="all"`などによってルートのassets fileから除外された開発用依存関係の取りこぼしも避けられます（[cyclonedx-dotnet#1107](https://github.com/CycloneDX/cyclonedx-dotnet/issues/1107)）。

```bash
dotnet tool install -g cyclonedx-dotnet
dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、NuGetの`project.assets.json`を指定します。ディレクトリを指定すると、ファイルを再帰的に検出します。

```bash
dotnet restore MySolution.slnx
ol scan --input src/MyProject/obj/project.assets.json
ol scan --input src --input tests --format json > ol-report.json
```

## JavaScript / Node.js

npmのSBOM生成には[CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm)を、pnpm、Yarn、複数構成のリポジトリには[cdxgen](https://github.com/CycloneDX/cdxgen)を使います。

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、対応する解決済み入力を指定します。

```bash
# npm package-lock.json version 2または3
ol scan --input package-lock.json

# pnpm-lock.yaml version 9
ol scan --input pnpm-lock.yaml

# Yarn Classic version 1またはYarn Berry metadata version 8
ol scan --input yarn.lock
```

Yarnのロックファイルには開発用scopeがありません。ルートとの関係を確定できない依存関係は`unknown`になります。

## Rust / Cargo

cargo-cyclonedxでSBOMを生成します。

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、ビルドと同じロック済みfeatureとtargetを指定してCargo metadataを生成します。

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
```

olはworkspaceのコンテキスト、依存関係の種類、feature、target式を保持します。スキャン環境に合わせた再評価は行いません。

## Go modules

リリース対象と同じGOOS、GOARCH、CGO、build tagでSBOMを生成します。

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、同じmoduleまたはworkspaceから次の名前で2ファイルを生成します。

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt
ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
```

2ファイルを含むディレクトリも指定できます。olは選択済みmodule listを正とし、ローカル置換のパスを公開しません。

## Python

ビルドまたはデプロイで使う環境からSBOMを生成します。

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、対象環境を有効化して`pip inspect` JSON format version 1を生成します。

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
```

olはインストール済みディストリビューションを正とします。`requirements.txt`、`pyproject.toml`、Poetry、uv、Pipenvの宣言は解決しません。

## PHP / Composer

ロック済みプロジェクトでCycloneDXプラグインを実行し、SBOMを生成します。

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、`composer.json`と`composer.lock`を同じディレクトリに置きます。olはマニフェストからルートパッケージと直接依存関係だけを読み取ります。Composerの実行や`vendor/`の調査は行いません。

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
```

## Ruby / Bundler

CycloneDX SBOMを生成するか、SBOM抜きで`Gemfile.lock`を指定します。

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json

ol scan --input Gemfile.lock --format json > ol-report.json
```

RubyGems.orgのgemだけに`pkg:gem`識別子を付与し、レジストリの証拠を追加します。プライベート、Git、path sourceはURLやローカルパスを公開しません。

## Java / JVM

### Maven

集約CycloneDX SBOMを生成します。

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
```

SBOM抜きでスキャンするには、Maven Dependency Plugin 3.7.0以降でdependency tree JSONを生成します。

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

dependency tree JSONにはライセンスメタデータがありません。olはversion付きMaven packageにdeps.devの証拠を追加します。

### Gradle

Gradleには、解決済み依存グラフの公式な可搬JSON形式がありません。CycloneDXまたはSPDX形式のJSON SBOMを生成します。

```kotlin
plugins {
    id("org.cyclonedx.bom") version "3.2.4"
}
```

```bash
./gradlew cyclonedxBom
ol scan --input build/reports/cyclonedx/bom.json --format json > ol-report.json
```

## Swift / Objective-C

### SwiftPM

SwiftPMでCycloneDX SBOMを生成します。より正確なSBOMを生成するには`swift build --build-system swiftbuild`を使います。詳しくは[Software Bill of Materials (SBOM)の生成](https://docs.swift.org/swiftpm/documentation/packagemanagerdocs/generatingsboms/)を参照してください。

```bash
swift package generate-sbom --sbom-spec cyclonedx --sbom-output-dir .build/sboms
ol scan --input .build/sboms --format json > ol-report.json
```

SBOM抜きでスキャンするには、package graphを解決してschema version 2または3の`Package.resolved`を指定します。

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
```

`Package.resolved`にはpackage間のedgeがありません。依存関係の種類は`unknown`になります。

### CocoaPods

CycloneDX CocoaPods gemでSBOMを生成します。メタデータとフィルターのオプションは[CycloneDX CocoaPods](https://github.com/CycloneDX/cyclonedx-cocoapods)を参照してください。

```bash
gem install cyclonedx-cocoapods
pod install
cyclonedx-cocoapods --output bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

SBOM抜きでスキャンするには`Podfile.lock`を指定します。

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
```

olはsubspecをroot podへまとめます。public Specs由来のpodだけに`pkg:cocoapods`識別子を付与し、CocoaPods CDNの証拠を追加します。
