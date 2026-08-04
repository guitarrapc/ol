[![build](https://github.com/guitarrapc/ol/actions/workflows/build.yml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/build.yml)
[![Release](https://github.com/guitarrapc/ol/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/release.yaml)

# ol

English | [日本語](README-ja.md)

Open-source license checker for resolved dependencies and SBOMs.

ol lists the licenses of the direct and transitive dependencies an application actually uses. It improves accuracy by combining evidence from SBOMs, package registries, and source repositories. This lets you understand the OSS licenses in use and automatically detect license-policy violations when a pull request changes dependencies.

## What ol does

ol does not provide legal advice or claim legal certainty. It does not guess unobservable facts; uncertainty remains visible in the result.

- Reviews licenses across the current project, including transitive dependencies.
- Makes missing, ambiguous, conflicting, and invalid SPDX evidence visible.
- Compares license-relevant changes between two saved reports.
- Represents licenses consistently with SPDX License Identifiers.
- Saves JSON reports with evidence provenance and evaluates them later.

**What ol does not do**

ol does not resolve dependencies. Ecosystem-native resolution is the most reliable source of the versions a build selected, so ol focuses on resolved inputs. Instead of manifests such as `package.json`, `*.csproj`, or `Cargo.toml`, give ol one of the following:

- a CycloneDX or SPDX JSON SBOM; or
- a lockfile or resolved package-manager output from npm, Cargo, NuGet, or another supported ecosystem, such as `package-lock.json`, `cargo-metadata.json`, or `project.assets.json`.

## Quick start

An SBOM such as `bom.cdx.json` is the most convenient input when you need to cover resolved dependencies across languages. ol can also consume supported lockfiles and package-manager outputs directly.

> [!TIP]
> Tools such as [@cyclonedx/cyclonedx-npm](https://www.npmjs.com/package/@cyclonedx/cyclonedx-npm) can generate a CycloneDX JSON SBOM.

```bash
# On macOS/Linux, add execute permission if needed
chmod +x ./ol

# generate sbom
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json

# Scan a CycloneDX or SPDX JSON SBOM
ol scan --input bom.cdx.json

# Scan supported resolved dependency inputs under the current directory
ol scan --input .

# Scan a supported lockfile or package-manager output directly
ol scan --input package-lock.json
ol scan --input src/MyProject/obj/project.assets.json

# Write a reviewable Markdown report
ol scan --input . --format markdown > ol-report.md

# Write a reusable JSON report
ol scan --input . --format json > ol-report.json

# Show only direct dependencies and group them by license
ol scan --input . --dependency direct --group-by license

# Check the saved report against an SPDX license allow-list
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause

# Compare license-relevant changes between two saved reports
ol diff --previous before.json --current after.json

# Use only license evidence already present in the input
ol scan --input bom.cdx.json --no-external-evidence
```

### GitHub Actions

[guitarrapc/setup-ol](https://github.com/guitarrapc/setup-ol) provides a simple way to install ol.

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
      - name: Scan licenses
        run: ol scan --input . --format json > ol-report.json
        env:
          OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      - name: Detect license violations
        run: ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

Commit a previous report to detect added packages and license changes when a pull request updates dependencies. OSS libraries sometimes change licenses between versions; `diff` makes those changes visible.

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
      - name: Scan licenses
        run: ol scan --input . --format json > after.json
        env:
          OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
      - name: Compare license changes
        run: ol diff --previous before.json --current after.json
      - name: Detect license violations
        run: ol check --report after.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

## Usage

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

| Command | Purpose |
|---|---|
| `ol scan` | Collect license evidence from resolved dependencies and produce a report. |
| `ol check` | Evaluate a canonical JSON report against an allow-list. |
| `ol diff` | Compare two canonical JSON reports. |
| `ol cache clear` | Clear evidence caches managed by ol. |
| `ol spdx version` | Show the active SPDX data source. |
| `ol spdx list` | List installed SPDX data versions. |
| `ol spdx update` | Download SPDX data. |
| `ol spdx use` | Select the SPDX data version to use. |
| `ol spdx clear` | Remove user-managed SPDX data. |

Use `scan` to collect licenses from an SBOM, lockfile, or other resolved dependency input. JSON reports can be reused by `check` and `diff`.

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

Use `check` to evaluate a JSON report produced by `scan` and find packages whose licenses violate the configured allow-list.

```bash
$ ol diff --help
Usage: diff [options...] [-h|--help] [--version]

Compare two persisted JSON scan reports and report license-relevant changes.

Options:
  --previous <string>      Previously persisted JSON scan report. [Required]
  --current <string>       Current JSON scan report. [Required]
  --format <DiffFormat>    Output format. [Default: Text]
```

SPDX data is bundled with ol. You can download newer SPDX data and select the active version locally.

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

ol caches collected license evidence to avoid repeating the same requests. Users can clear these caches explicitly.

```bash
$ ol cache --help
Usage: cache [command] [-h|--help] [--version]

Manage locally cached scan evidence.

Commands:
  clear    Clears cached evidence for the specified category.
```

### Exit codes

Each command uses the following exit codes. CI can use the `check` result to distinguish policy violations from command failures.

| Exit code | Meaning |
|---:|---|
| `0` | The command completed successfully. Help and version output also use `0`. |
| `1` | Argument parsing, configuration, input, I/O, or another execution failure prevented completion. |
| `2` | `check` completed policy evaluation and found one or more violations. |

### Reading license results

Every component has one status:

| Status | Meaning |
|---|---|
| `matched` | Evidence resolved to one valid SPDX expression. |
| `conflict` | Valid evidence sources disagree. |
| `unknown` | Collection completed but yielded no usable license information. |
| `ambiguous` | License text exists but cannot be normalized without guessing. |
| `invalid` | A claimed SPDX expression or identifier is invalid. |
| `error` | Evidence collection or processing failed and no other evidence resolved the license. |

`matched` means resolved, not allowed. `check` applies the organization's allow-list. `unknown`, `conflict`, `ambiguous`, `invalid`, and `error` fail closed.

## Improving license confidence

In addition to evidence in the input, `scan` collects license information from supported package registries and the GitHub License API, then caches it locally. To avoid GitHub API rate limits in GitHub Actions, map `GITHUB_TOKEN` explicitly to `OL_GITHUB_TOKEN`; ol never reads `GITHUB_TOKEN` implicitly.

```yaml
env:
  OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

To use only evidence already present in the input, disable external sources and their caches:

```bash
ol scan --input bom.cdx.json --no-external-evidence
```

Without external evidence, more components may remain unresolved and therefore fail `check`.

> [!TIP]
> ol does not crawl arbitrary repository contents or guess a license from repository layout or license files.

## Resolving package dependencies

For release and audit artifacts, prefer one CycloneDX or SPDX JSON SBOM covering the complete subject. For quick local feedback, use a supported lockfile or package-manager output directly.

| Ecosystem | Resolved input for ol | How to prepare it |
|---|---|---|
| Any | CycloneDX / SPDX JSON SBOM | Resolve dependencies with an ecosystem-native tool and generate an SBOM. |
| .NET / NuGet | `project.assets.json` v3/v4 | Run `dotnet restore`. |
| npm | `package-lock.json` v2/v3 | Run `npm install`. |
| pnpm | `pnpm-lock.yaml` v9 | Run `pnpm install`. |
| Yarn | Classic v1 or Berry metadata v8 `yarn.lock` | Run `yarn install`. |
| Rust / Cargo | Cargo metadata JSON | Run `cargo metadata --format-version 1 --locked`. |
| Go modules | module list and graph | Save `go list -m -json all` and `go mod graph` in one directory. |
| Python | pip JSON v1 | Run `python -m pip inspect --local`. |
| PHP / Composer | `composer.json` and `composer.lock` | Keep both files in one directory. |
| Ruby / Bundler | `Gemfile.lock` | Run `bundle install`. |
| Java / Maven | Maven Dependency Plugin 3.7+ tree JSON | Run `mvn dependency:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json`. |
| Java / Gradle | SBOM | Generate an SBOM; Gradle has no official portable JSON format for its resolved graph. |
| SwiftPM | `Package.resolved` v2/v3 | Run `swift package resolve`. |
| CocoaPods | `Podfile.lock` | Run `pod install`. |

> [!TIP]
> ol detects formats from content, so `--input-format` is normally unnecessary. Repeat `--input A --input B` to combine package-manager inputs. SBOM and package-manager inputs cannot be mixed in one report.

## Common operations

### Filter the view

`--dependency` filters only the rendered view; analysis still uses the complete inventory.

```bash
ol scan --input . --dependency direct
ol scan --input . --group-by license
ol scan --input . --sort status,name
```

### Apply a separate allow-list to development-only dependencies

An additional allow-list applies only when resolver data proves a component is development-only. It never relaxes components whose usage is unknown.

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0,BSD-3-Clause \
  --allow-dev-licenses CC-BY-4.0
```

This does not prove that the package is absent from a production artifact. Check the release artifact separately with the primary allow-list.

### Adopt a baseline for an existing project

A baseline records reviewed unresolved components so subsequent runs detect only new or changed unresolved evidence.

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json \
  --update-baseline
```

Commit `ol-baseline.json`, then name it explicitly in later evaluations:

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json
```

A baseline cannot acknowledge a recognizable license rejected by the allow-list or an `error` caused by collection failure. Evidence or version changes automatically expire the corresponding acknowledgement.

### Skip collection or exclude evaluation for selected components

These options solve different problems:

| Option | Stage | Behavior |
|---|---|---|
| `scan --skip-evidence-packages <purl-prefix>` | Evidence collection | Makes no external request for matching components. Components remain in the report and policy evaluation. |
| `check --exclude-packages <purl-prefix>` | Policy evaluation | Removes matching components from allow-list evaluation, baselines, violations, and SARIF. The scan report is unchanged. |

Both use case-sensitive Package URL prefixes. ol does not infer package ownership or whether a package is private.

### Compare two reports

```bash
ol diff --previous before.json --current after.json
ol diff --previous before.json --current after.json --format json
```

`diff` reports additions, removals, and version, status, license, or evidence changes. It exits `0` when comparison succeeds even when changes exist; policy enforcement belongs to `check`.

### Write SARIF

```bash
ol check --report ol-report.json \
  --allow-licenses MIT,Apache-2.0 \
  --sarif ol.sarif
```

The stdout verdict remains unchanged. SARIF 2.1.0 contains the same violations and, when graph data is available, the shortest dependency path to a transitive violation.

## Frequently asked questions

### Can I pass `package.json`, `*.csproj`, or `Cargo.toml` directly?

No. These manifests describe requested dependencies, not the exact versions and transitive graph selected by the build. Generate an SBOM or use a supported resolved input.

### Should I use an SBOM or package-manager input?

Prefer one SBOM for releases, audits, and repositories containing several ecosystems. Direct package-manager inputs are convenient for local feedback or when a resolved graph is already generated or committed.

### Does ol require network access?

By default, yes, for external evidence collection. `--no-external-evidence` disables all registry, repository, and evidence-cache access. Bundled SPDX data keeps ordinary validation available offline.

### How should I handle an existing unresolved dependency?

Review its raw evidence and status first. A baseline can acknowledge known unresolved evidence that cannot be fixed, but never a forbidden license or `error`.

### Can I apply another policy without scanning again?

Yes. Run `check` with a different `--allow-licenses` value against the saved canonical JSON report. `check` performs no external access.

## Scan examples

### SBOM

ol accepts CycloneDX and SPDX JSON SBOMs. For release, audit, and CI artifacts, an ecosystem-native generator should resolve the dependency graph and produce one canonical SBOM. Use `scan` to review its reconciled license evidence, then use `check` to apply an SPDX allow-list to the same input:

```bash
ol scan --input bom.cdx.json --format markdown
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

SBOM generation and ecosystem-specific resolution remain outside ol. ol enriches the supplied components with package metadata and GitHub License API source evidence, reconciles the resulting claims, and reports unresolved or conflicting evidence before policy evaluation.

<details><summary>Output sample (Markdown)</summary>

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

**SBOM:** Generate CycloneDX JSON from the restored solution with [CycloneDX for .NET](https://github.com/CycloneDX/cyclonedx-dotnet), then run both commands against the generated artifact:

```bash
dotnet tool restore
dotnet tool run dotnet-CycloneDX MySolution.slnx --output . --output-format Json --filename bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**Resolved NuGet input:** For one .NET project, scan NuGet's generated `project.assets.json` directly. For a repository or solution layout, pass a directory and ol recursively combines the `project.assets.json` files below it:

```bash
dotnet restore MySolution.slnx
ol scan --input src/Ol/obj/project.assets.json --format markdown
ol scan --input src/Ol/obj/project.assets.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

You can specify a directory containing multiple `project.assets.json` files:

```bash
ol scan --input src/ --input tests/ --format markdown
ol scan --input src/ --input tests/ --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

NuGet resolution can differ by project, target framework, and runtime identifier, so ol preserves each as a separate occurrence context while reporting each package/version once.

<details><summary>Output sample (Markdown)</summary>

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

**SBOM:** For npm, generate CycloneDX JSON with [CycloneDX for npm](https://github.com/CycloneDX/cyclonedx-node-npm). A polyglot generator such as [cdxgen](https://github.com/CycloneDX/cdxgen) can be used for pnpm, Yarn, or mixed JavaScript repositories:

```bash
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

**Resolved package-manager input:** Pass a supported lockfile or directory directly.

#### npm

ol scans `package-lock.json` version 2/3:

```bash
ol scan --input package-lock.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/npm-package-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | MIT | npm | direct | matched |
| shared-package | 2.0.0 | Apache-2.0 | npm | transitive | matched |

</details>

#### pnpm

ol scans `pnpm-lock.yaml` version 9:

```bash
ol scan --input pnpm-lock.yaml --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/pnpm-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | direct | unknown |
| shared-package | 2.0.0 | - | npm | transitive | unknown |

</details>

#### Yarn Classic

ol scans `yarn.lock` version 1:

```bash
ol scan --input yarn.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/yarn-classic-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | unknown | unknown |
| shared-package | 2.0.0 | - | npm | unknown | unknown |

</details>

#### Yarn Berry

ol scans `yarn.lock` metadata version 8:

```bash
ol scan --input yarn.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause,ISC
```

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/yarn-berry-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct-package | 1.0.0 | - | npm | direct | unknown |
| shared-package | 2.0.0 | - | npm | transitive | unknown |

</details>

Workspace/importer contexts and proven dependency edges are retained without running the package manager or evaluating platform conditions against the current host.

### Rust

**SBOM:** Generate CycloneDX JSON from the Cargo project with [CycloneDX for Rust Cargo](https://github.com/CycloneDX/cyclonedx-rust-cargo):

```bash
cargo cyclonedx -f json
ol scan --input bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

For a workspace that produces multiple BOMs, merge them into one canonical SBOM before passing it to ol.

**Resolved Cargo input:** Generate Cargo metadata from the same locked feature and target selection used by the build, then scan the generated file:

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
ol scan --input cargo-metadata.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

Each workspace member becomes a resolution context. Workspace and path nodes participate in reachability without being mislabeled as crates.io packages. Resolved features, dependency kinds, and target expressions are retained as variants; ol does not evaluate them against the current host. Cargo metadata does not record the `--filter-platform` argument itself, so ol does not infer a target triple from the machine running the scan.

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/cargo-metadata`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| itoa | 1.0.0 | MIT OR Apache-2.0 | cargo | transitive | matched |
| serde | 1.0.0 | MIT OR Apache-2.0 | cargo | direct | matched |

</details>

### Go

**SBOM:** Generate CycloneDX JSON from the module with [CycloneDX for Go modules](https://github.com/CycloneDX/cyclonedx-gomod). Use the same GOOS, GOARCH, CGO, and build-tag selection as the released application:

```bash
cyclonedx-gomod mod -json -output bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**Resolved Go input:** Go does not persist its MVS build list in a lockfile. Generate both the selected module list and its requirement edges from the same module or workspace, using these exact output names:

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt

ol scan --input go-list-modules.json --input go-mod-graph.txt --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

Alternatively, pass their containing directory. ol binds the two companion files as one `go-module-graph` input:

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

`go-list-modules.json` is authoritative for the selected build list and replacement metadata. `go-mod-graph.txt` contributes only edges whose endpoints are in that selected list, so superseded module versions and Go's `go@...`/`toolchain@...` graph nodes do not become components. Local replacements receive no proxy purl and their filesystem paths are not reported. Versioned module replacements use the replacement module/version for enrichment while retaining the original requirement as `sourceId`. If the list JSON contains `Retracted` data, ol retains a `retracted` occurrence variant. GOOS, GOARCH, and build tags remain unspecified because neither output proves them.

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/go-module-graph`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| github.com/google/uuid | v1.6.0 | - | golang | direct | unknown |

</details>

### Python

**SBOM:** Generate CycloneDX JSON from the exact Python environment used by the build or deployment with the [CycloneDX Python SBOM generator](https://github.com/CycloneDX/cyclonedx-python):

```bash
cyclonedx-py environment .venv --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The generator also supports Poetry, Pipenv, and pip requirements inputs. Using the installed environment provides the strongest inventory of the packages actually selected for the build.

**Resolved Python input:** ol scans the stable JSON format version 1 produced by `pip inspect`. Activate the exact virtual environment, then capture its installed distributions and environment:

```bash
python -m pip inspect --local > pip-inspect.json
ol scan --input pip-inspect.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The installed distribution set is authoritative; ol does not resolve `requirements.txt`, `pyproject.toml`, Poetry, uv, or Pipenv declarations. `requested=true` distributions are direct dependencies and receive root edges. `requested=false` proves transitive classification only when `installer` is `pip`; other installers and a missing `requested` field remain unknown. Unconditional `requires_dist` entries produce package edges when the normalized target is installed. Entries with environment markers or extras do not produce edges because `pip inspect` does not record which extras activated them. The report context retains the Python version, implementation, `sys_platform`, machine architecture, and pip version supplied by the report.

Distribution names use PyPA normalization for identity and `pkg:pypi` enrichment. A distribution with `direct_url` receives no PyPI purl and retains only `source=direct`; local paths and URLs are not reported. `license_expression` is preferred over legacy `license` metadata as input-supplied license evidence.

<details><summary>Output sample (Markdown)</summary>

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

**SBOM:** Generate CycloneDX JSON from the locked Composer project with the [CycloneDX PHP Composer plugin](https://github.com/CycloneDX/cyclonedx-php-composer):

```bash
composer CycloneDX:make-sbom --output-format=JSON --output-file=bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**Resolved Composer input:** ol scans a same-directory `composer.json` and `composer.lock` pair directly:

```bash
ol scan --input . --input-format composer-lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The lockfile supplies the resolved production and development package sets. The manifest supplies only the root package identity and direct `require`/`require-dev` relationships; ol does not invoke Composer, resolve version constraints, or inspect `vendor/`. Package metadata is enriched from Packagist when available, and repository URLs from package metadata can lead to GitHub License API source evidence.

<details><summary>Output sample (Markdown)</summary>

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

**SBOM:** Generate CycloneDX JSON from the locked Bundler project with the [CycloneDX Ruby Gem](https://github.com/CycloneDX/cyclonedx-ruby-gem):

```bash
cyclonedx-ruby -p . -f json -o bom.cdx.json
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**Resolved Bundler input:** ol scans `Gemfile.lock` directly without executing `Gemfile`, Bundler, or RubyGems:

```bash
ol scan --input Gemfile.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The lockfile `DEPENDENCIES` section identifies direct dependencies, and resolved spec dependencies provide transitive edges. Each recorded platform becomes a separate resolution context. Only gems resolved from `https://rubygems.org/` receive `pkg:gem` identities and RubyGems.org metadata enrichment; private registry, Git, and path sources are retained without exposing their remote or local paths.

<details><summary>Output sample (Markdown)</summary>

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

**SBOM:** Generate aggregate CycloneDX JSON from the resolved Maven reactor with the [CycloneDX Maven plugin](https://github.com/CycloneDX/cyclonedx-maven-plugin):

```bash
mvn org.cyclonedx:cyclonedx-maven-plugin:2.9.2:makeAggregateBom -DoutputFormat=json
ol scan --input target/bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

**Resolved Maven input:** ol scans JSON produced by Maven Dependency Plugin 3.7.0 or later:

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
ol scan --input maven-dependency-tree.json
```

The root artifact becomes one resolution context. Root children are direct dependencies, deeper nodes are transitive, and each node retains its effective scope, optional flag, type, classifier, and incoming edge. Repeated coordinates share one report component while remaining distinct graph occurrences. The dependency-tree JSON contains no license metadata, so ol enriches its canonical Maven purls with version-specific license and source-repository hints from deps.dev. When deps.dev reports multiple licenses without an AND/OR relationship, ol preserves them as ambiguous evidence instead of inventing an SPDX expression. CycloneDX remains preferable when the build's effective POM metadata and repository context must be captured in the input artifact itself.

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/maven-dependency-tree`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| direct | 2.0.0 | - | maven | direct | unknown |
| provided | 4.0.0 | - | maven | direct | unknown |
| transitive | 3.0.0 | - | maven | transitive | unknown |

</details>

#### Gradle

**SBOM:** Apply the [CycloneDX Gradle plugin](https://github.com/CycloneDX/cyclonedx-gradle-plugin) to the root project:

```kotlin
plugins {
    id("org.cyclonedx.bom") version "3.2.4"
}
```

Generate and scan the aggregate JSON SBOM:

```bash
./gradlew cyclonedxBom
ol scan --input build/reports/cyclonedx/bom.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

For per-project output, use `cyclonedxDirectBom`; configurations can be selected with the plugin's `includeConfigs` and `skipConfigs` settings.

**Resolved Gradle input:**

Gradle resolved dependency input is not supported directly by ol; generate a CycloneDX or SPDX JSON SBOM instead.

Gradle does not officially define or provide a machine-readable JSON format for its resolved dependency graph. Its built-in `dependencies` and `dependencyInsight` reports are human-readable output, not a portable input contract.

### Swift / Objective-C

#### SwiftPM

**Resolved SwiftPM input:** Resolve the package graph, then scan `Package.resolved` directly:

```bash
swift package resolve
ol scan --input Package.resolved --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

ol supports `Package.resolved` schema versions 2 and 3 without evaluating `Package.swift`. Each pin retains its resolved version or source revision, source kind, and version 3 origin hash. Because the lockfile does not contain package-to-package edges, dependency type remains unknown. Only credential-free HTTP(S) source-control locations receive canonical `pkg:swift` identities and repository hints; registry, local, and credential-bearing locations are not exposed as remote package identities.

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/swift-package-resolved`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| internal-kit | main | - | swift | unknown | unknown |
| swift-log | 1.6.2 | - | swift | unknown | unknown |

</details>

#### CocoaPods

**Resolved CocoaPods input:** Install the pods, then scan `Podfile.lock` directly:

```bash
pod install
ol scan --input Podfile.lock --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The lockfile `DEPENDENCIES` section identifies direct pods, and resolved pod dependencies provide transitive edges. Subspecs are collapsed into their root pod for package identity and license evaluation. Only pods proven to come from the public trunk, CDN, or Specs repository by `SPEC REPOS` receive `pkg:cocoapods` identities and version-specific license and source enrichment from the CocoaPods CDN. Private-spec and external-source pods retain their source classification without exposing repository URLs or local paths.

<details><summary>Output sample (Markdown)</summary>

Input: `package-manager/cocoapods-lock`

| NAME | VERSION | LICENSE | ECOSYSTEM | DEPENDENCY | STATUS |
|---|---|---|---|---|---|
| Alamofire | 5.10.2 | - | cocoapods | transitive | unknown |
| Moya | 15.0.0 | - | cocoapods | direct | unknown |

</details>

## Detailed documentation

- [Design principles](.github/docs/DESIGN.md)
- [Architecture](.github/docs/Architecture.md)
- [CLI and report specification](.github/docs/specs/cli.md)
- [SPDX specification](.github/docs/specs/spdx.md)
- [Package-manager evidence specification](.github/docs/specs/packagemanager.md)
- [Source-repository evidence specification](.github/docs/specs/source.md)
- [Cache-format specification](.github/docs/specs/cache_format.md)

## Development

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).

Repository sandbox

```bash
# Regenerate ol's committed SBOM and text, Markdown, and JSON reports.
./sandbox/Update-SelfScan.ps1

# Keep the committed SBOM as a fixed golden input and regenerate only its derived reports.
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

Generated data

```bash
# Generate the SPDX License List
dotnet run --project src/Ol.Update -- generate
```
