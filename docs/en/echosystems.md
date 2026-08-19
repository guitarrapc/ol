# Ecosystem Usage

English | [日本語](../ja/echosystems.md)

ol scans resolved dependency graphs. Use a CycloneDX or SPDX JSON SBOM, supported lockfile, or package-manager output. Unresolved manifests such as `package.json`, `*.csproj`, and `Cargo.toml` are not valid inputs by themselves.

Scan, then apply a policy:

```bash
ol scan --input <resolved-input> --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## SBOM

For releases, audits, CI, and multi-ecosystem repositories, use one CycloneDX or SPDX JSON SBOM generated from the build's resolved dependency graph.

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

Generate the SBOM with an ecosystem-native tool. ol adds package metadata and GitHub License API evidence, then reports missing or conflicting evidence before policy evaluation.

## .NET / NuGet

For NuGet SBOMs, use [cyclonedx-dotnet](https://github.com/CycloneDX/cyclonedx-dotnet). For a single project file, add `--recursive` to scan referenced projects and include development dependencies excluded from the root assets file by `PrivateAssets="all"` and similar settings ([cyclonedx-dotnet#1107](https://github.com/CycloneDX/cyclonedx-dotnet/issues/1107)).

```bash
dotnet tool install -g cyclonedx-dotnet
dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, use NuGet's `project.assets.json`. Directory inputs find files recursively.

```bash
dotnet restore MySolution.slnx
ol scan --input src/MyProject/obj/project.assets.json
ol scan --input src --input tests --format json > ol-report.json
```

## JavaScript / Node.js

For npm SBOMs, use [CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm). For pnpm, Yarn, or mixed repositories, use [cdxgen](https://github.com/CycloneDX/cdxgen).

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, use a supported resolved input:

```bash
# npm package-lock.json version 2 or 3
ol scan --input package-lock.json

# pnpm-lock.yaml version 9
ol scan --input pnpm-lock.yaml

# Yarn Classic version 1 or Yarn Berry metadata version 8
ol scan --input yarn.lock
```

Yarn lockfiles do not record development scope. Dependencies without a proven root relationship have type `unknown`.

## Rust / Cargo

Generate an SBOM with cargo-cyclonedx:

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
```

To scan without an SBOM, generate Cargo metadata with the build's locked features and target selection:

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
```

Omit `--locked` when the repository does not commit `Cargo.lock`, as a published library usually does not; with the flag the command refuses to write one and fails. ol reports `Cargo.toml` as an unscanned candidate in that case, so an unresolved Rust tree does not pass silently.

ol preserves workspace context, dependency kinds, features, and target expressions. It does not reevaluate them for the scan host.

## Go modules

Generate an SBOM with the release build's GOOS, GOARCH, CGO, and build tags:

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, generate both files from the same module or workspace with these names:

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt
ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
```

You may pass the containing directory. ol treats the selected module list as authoritative and does not expose local replacement paths.

## Python

Generate an SBOM from the build or deployment environment:

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, activate that environment and generate `pip inspect` JSON format version 1:

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
```

ol uses the installed distributions as authoritative. It does not resolve declarations from `requirements.txt`, `pyproject.toml`, Poetry, uv, or Pipenv.

## PHP / Composer

Generate an SBOM from the locked project:

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, keep `composer.json` and `composer.lock` in the same directory. ol reads the manifest only for root identity and direct relationships. It does not run Composer or inspect `vendor/`.

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
```

## Ruby / Bundler

Generate a CycloneDX SBOM, or scan `Gemfile.lock` without one:

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json

ol scan --input Gemfile.lock --format json > ol-report.json
```

Only RubyGems.org packages receive `pkg:gem` identities and registry evidence. Private, Git, and path sources remain visible without exposing URLs or local paths.

## Java / JVM

### Maven

Generate an aggregate CycloneDX SBOM:

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
```

To scan without an SBOM, generate dependency-tree JSON with Maven Dependency Plugin 3.7.0 or later:

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

Dependency-tree JSON has no license metadata. ol adds deps.dev evidence for versioned Maven packages.

### Gradle

Gradle has no official portable JSON format for its resolved dependency graph. Generate a CycloneDX or SPDX JSON SBOM.

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

Generate a CycloneDX SBOM with SwiftPM. Use `swift build --build-system swiftbuild` to generate a more accurate SBOM. See [Generating Software Bill of Materials (SBOM)](https://docs.swift.org/swiftpm/documentation/packagemanagerdocs/generatingsboms/).

```bash
swift package generate-sbom --sbom-spec cyclonedx --sbom-output-dir .build/sboms
ol scan --input .build/sboms --format json > ol-report.json
```

To scan without an SBOM, resolve the package graph and use `Package.resolved` schema version 2 or 3:

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
```

`Package.resolved` has no package-to-package edges. Dependency type is therefore `unknown`.

### CocoaPods

Generate an SBOM with the CycloneDX CocoaPods gem. See [CycloneDX CocoaPods](https://github.com/CycloneDX/cyclonedx-cocoapods) for metadata and filter options.

```bash
gem install cyclonedx-cocoapods
pod install
cyclonedx-cocoapods --output bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

To scan without an SBOM, use `Podfile.lock`:

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
```

ol groups subspecs under their root pod. Only pods from verified public Specs sources receive `pkg:cocoapods` identities and CocoaPods CDN evidence.
