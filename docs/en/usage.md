# Ecosystem Usage

English | [日本語](../ja/usage.md)

ol reads a resolved dependency graph. Give it a CycloneDX or SPDX JSON SBOM, a supported lockfile, or supported package-manager output; do not pass an unresolved manifest such as `package.json`, `*.csproj`, or `Cargo.toml` by itself.

The common workflow is:

```bash
ol scan --input <resolved-input> --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## SBOM

For releases, audits, CI, and repositories containing several ecosystems, prefer one canonical CycloneDX or SPDX JSON SBOM produced from the build's resolved dependency graph.

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

SBOM generation remains the responsibility of ecosystem-native tools. ol enriches the supplied components with package metadata and GitHub License API evidence, then reports missing or conflicting evidence before policy evaluation.

## .NET / NuGet

Generate a CycloneDX SBOM from a restored solution:

```bash
dotnet tool restore
dotnet tool run dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

Or scan NuGet's resolved `project.assets.json` directly. A directory input recursively combines discovered assets files while preserving project, target-framework, and runtime contexts.

```bash
dotnet restore MySolution.slnx
ol scan --input src/MyProject/obj/project.assets.json
ol scan --input src --input tests --format json > ol-report.json
```

## JavaScript / Node.js

For npm, generate an SBOM with [CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm). [cdxgen](https://github.com/CycloneDX/cdxgen) can cover pnpm, Yarn, and mixed repositories.

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

Supported resolved inputs can also be scanned directly:

```bash
# npm package-lock.json version 2 or 3
ol scan --input package-lock.json

# pnpm-lock.yaml version 9
ol scan --input pnpm-lock.yaml

# Yarn Classic version 1 or Yarn Berry metadata version 8
ol scan --input yarn.lock
```

Yarn lockfiles do not record development scope. Where the root relationship cannot be proven, dependency type remains `unknown`.

## Rust / Cargo

Generate a CycloneDX SBOM:

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
```

Or capture Cargo metadata using the same locked feature and target selection as the build:

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
```

ol retains workspace contexts, dependency kinds, features, and target expressions without evaluating them against the machine running the scan.

## Go modules

Generate a CycloneDX SBOM with the same GOOS, GOARCH, CGO, and build-tag selection as the released application:

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
```

For direct input, generate both files from the same module or workspace using these exact names:

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt
ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
```

You may pass their containing directory instead. ol uses the selected module list as authoritative and does not expose local replacement paths.

## Python

Generate an SBOM from the exact environment used by the build or deployment:

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

Or activate that environment and capture `pip inspect` JSON format version 1:

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
```

ol treats the installed distribution set as authoritative. It does not resolve `requirements.txt`, `pyproject.toml`, Poetry, uv, or Pipenv declarations.

## PHP / Composer

Generate a CycloneDX SBOM from the locked project:

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
```

Or scan a same-directory `composer.json` and `composer.lock` pair. ol uses the manifest only for root identity and direct relationships; it does not run Composer or inspect `vendor/`.

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
```

## Ruby / Bundler

Generate a CycloneDX SBOM or scan `Gemfile.lock` directly:

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json

ol scan --input Gemfile.lock --format json > ol-report.json
```

Only gems resolved from RubyGems.org receive `pkg:gem` identities and registry enrichment. Private, Git, and path sources remain visible without exposing their URLs or local paths.

## Java / JVM

### Maven

Generate an aggregate CycloneDX SBOM:

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
```

Or generate dependency-tree JSON with Maven Dependency Plugin 3.7.0 or later:

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

The dependency-tree format contains no license metadata, so ol enriches versioned Maven packages with deps.dev evidence.

### Gradle

Gradle does not provide an official portable JSON contract for its resolved dependency graph. Generate a CycloneDX or SPDX JSON SBOM instead.

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

Resolve the package graph and scan `Package.resolved` schema version 2 or 3:

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
```

`Package.resolved` contains no package-to-package edges, so dependency type remains `unknown`.

### CocoaPods

Install pods and scan `Podfile.lock` directly:

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
```

ol collapses subspecs into their root pod. Only pods proven to come from public Specs sources receive `pkg:cocoapods` identities and CocoaPods CDN enrichment.
