# エコシステム別の使い方

[English](../en/usage.md) | 日本語

olは解決済みの依存グラフを読み取ります。CycloneDXまたはSPDX形式のJSON SBOM、対応するロックファイル、パッケージマネージャーの解決済み出力を渡してください。`package.json`、`*.csproj`、`Cargo.toml`のような未解決のマニフェストを単独で入力することはできません。

基本的な流れは次のとおりです。

```bash
ol scan --input <解決済み入力> --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## SBOM

リリース、監査、CI、複数エコシステムを含むリポジトリでは、ビルドの解決済み依存グラフ全体を表すCycloneDXまたはSPDX形式のJSON SBOMを1つ生成することを推奨します。

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

SBOMの生成は各エコシステム向けツールを使います。olは入力されたコンポーネントへパッケージメタデータとGitHub License APIの証拠を追加し、未解決または競合する証拠をポリシー評価前に報告します。

## .NET / NuGet

restore済みのソリューションからCycloneDX SBOMを生成します。単一のproject fileから生成する場合は、参照先プロジェクトもスキャンするため`--recursive`を指定することで、`PrivateAssets="all"`などによってルートのassets fileから除外された開発用依存関係の取りこぼしも避けられます（[cyclonedx-dotnet#1107](https://github.com/CycloneDX/cyclonedx-dotnet/issues/1107)）。

```bash
dotnet tool restore
dotnet tool run dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

NuGetが生成した`project.assets.json`も直接スキャンできます。ディレクトリを渡すと、配下のassets fileを再帰的に検出し、プロジェクト、ターゲットフレームワーク、Runtime Identifierのコンテキストを保持したまままとめます。

```bash
dotnet restore MySolution.slnx
ol scan --input src/MyProject/obj/project.assets.json
ol scan --input src --input tests --format json > ol-report.json
```

## JavaScript / Node.js

npmでは[CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm)でSBOMを生成できます。pnpm、Yarn、複数構成のリポジトリには[cdxgen](https://github.com/CycloneDX/cdxgen)を利用できます。

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

対応する解決済み入力も直接スキャンできます。

```bash
# npm package-lock.json version 2または3
ol scan --input package-lock.json

# pnpm-lock.yaml version 9
ol scan --input pnpm-lock.yaml

# Yarn Classic version 1またはYarn Berry metadata version 8
ol scan --input yarn.lock
```

Yarnのロックファイルには開発用scopeが記録されません。ルートとの関係を確定できない場合、依存関係の種類は`unknown`になります。

## Rust / Cargo

CycloneDX SBOMを生成します。

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
```

または、ビルドと同じロック済みfeatureおよびtargetの選択条件でCargo metadataを生成します。

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
```

olはworkspaceのコンテキスト、依存関係の種類、feature、target式を保持しますが、スキャンを実行したマシンに対して再評価しません。

## Go modules

リリース対象と同じGOOS、GOARCH、CGO、build tagを指定してCycloneDX SBOMを生成します。

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
```

直接入力する場合は、同じmoduleまたはworkspaceから次の正確なファイル名で2つの出力を生成します。

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt
ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
```

2ファイルを含むディレクトリも渡せます。olは選択済みmodule listを正しい情報源とし、ローカル置換のパスを公開しません。

## Python

ビルドまたはデプロイで実際に使う環境からSBOMを生成します。

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

または、対象の仮想環境を有効化し、`pip inspect` JSON format version 1を生成します。

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
```

olはインストール済みディストリビューションの集合を正しい情報源とします。`requirements.txt`、`pyproject.toml`、Poetry、uv、Pipenvの宣言は解決しません。

## PHP / Composer

ロック済みプロジェクトからCycloneDX SBOMを生成します。

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

同じディレクトリにある`composer.json`と`composer.lock`も直接スキャンできます。マニフェストから利用するのはルートパッケージと直接依存関係だけで、Composerの実行や`vendor/`の調査は行いません。

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
```

## Ruby / Bundler

CycloneDX SBOMを生成するか、`Gemfile.lock`を直接スキャンします。

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json

ol scan --input Gemfile.lock --format json > ol-report.json
```

RubyGems.orgから解決されたgemだけに`pkg:gem`識別子を付与し、レジストリから証拠を補完します。プライベート、Git、path sourceはURLやローカルパスを公開せずに保持します。

## Java / JVM

### Maven

集約したCycloneDX SBOMを生成します。

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
```

または、Maven Dependency Plugin 3.7.0以降でdependency tree JSONを生成します。

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

dependency tree形式にはライセンスメタデータがないため、olはversion付きMaven packageをdeps.devの証拠で補完します。

### Gradle

Gradleは、解決済み依存グラフの可搬JSON形式を公式には提供していません。代わりにCycloneDXまたはSPDX形式のJSON SBOMを生成します。

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

SwiftPMでCycloneDX SBOMを生成し、出力ディレクトリをスキャンします。

```bash
swift package generate-sbom --sbom-spec cyclonedx --sbom-output-dir .build/sboms
ol scan --input .build/sboms --format json > ol-report.json
```

ビルド時条件を反映した最も正確なSBOMが必要な場合は、`swift build --build-system swiftbuild`を使います。詳しくは[Software Bill of Materials (SBOM)の生成](https://docs.swift.org/swiftpm/documentation/packagemanagerdocs/generatingsboms/)を参照してください。

または、package graphを解決し、schema version 2または3の`Package.resolved`を直接スキャンします。

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
```

`Package.resolved`にはpackage間のedgeがないため、依存関係の種類は`unknown`になります。

### CocoaPods

CycloneDX CocoaPods gemをインストールし、JSON SBOMを生成してスキャンします。

```bash
gem install cyclonedx-cocoapods
pod install
cyclonedx-cocoapods --output bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

コンポーネントのメタデータやフィルターのオプションについては、[CycloneDX CocoaPods](https://github.com/CycloneDX/cyclonedx-cocoapods)を参照してください。

または、podをインストールし、`Podfile.lock`を直接スキャンします。

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
```

olはsubspecをroot podへまとめます。public Specs由来と確認できるpodだけに`pkg:cocoapods`識別子を付与し、CocoaPods CDNから証拠を補完します。
