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

Download the asset for your OS from GitHub Releases, then place `ol` (or `ol.exe` on Windows) where you want.

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

An SBOM such as `bom.cdx.json` is the most convenient input when you need to cover resolved dependencies across languages. ol can also consume supported lockfiles and package-manager outputs directly.


> [!TIP]
> Tools such as [@cyclonedx/cyclonedx-npm](https://www.npmjs.com/package/@cyclonedx/cyclonedx-npm) can generate a CycloneDX JSON SBOM.

```bash
# On macOS/Linux, add execute permission if needed
chmod +x ./ol

# Scan a CycloneDX or SPDX JSON SBOM
npx @cyclonedx/cyclonedx-npm --output-format JSON --output-file bom.cdx.json
ol scan --input bom.cdx.json

# Scan supported ecosystem-resolved dependency inputs under the current directory
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
| `3` | `check` completed, but every finding is a collection failure, so the result is inconclusive. |

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

A registry that answers `404` has answered, so the component becomes `unknown` with the warning `package_metadata_not_found` rather than `error`. This is what a package published only to a private feed looks like, and a baseline can acknowledge it. `error` is reserved for questions that were never answered — timeouts, `429`, and `5xx` — which is also what makes exit code `3` meaningful.

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
> ol detects formats from content, so `--input-format` is normally unnecessary. Repeat `--input A --input B` to combine inputs. One SBOM can be combined with package-manager inputs in a single report; a second SBOM cannot.

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

An existing project can contain components ol cannot resolve: a package on a private feed, a registry with no license field, a source outside GitHub. They fail closed, and you cannot fix them by editing your own code.

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

```text
License check failed: 1 violation.

Package                  Version  Ecosystem  Purl                                     License/Status  Reason
@mycompany/internal-sdk  1.0.0    npm        pkg:npm/%40mycompany/internal-sdk@1.0.0  unknown         license is unresolved
```

Record what you reviewed and accepted with `--update-baseline`:

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 \
  --baseline ol-baseline.json --update-baseline
```

```text
Acknowledged by baseline: 1 component.
License check passed: 2 components satisfy the allow-list.
```

`ol-baseline.json` now records that component with the evidence that produced it, plus a fingerprint of that evidence. Commit the file; the raw claims are in it, so a reviewer can judge a future change from the pull request diff alone.

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

Later runs name the file and drop `--update-baseline`:

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json
```

**A newly unresolved component still fails.** This is the point of a baseline: the accepted set cannot grow without review.

```text
Acknowledged by baseline: 1 component.
License check failed: 1 violation.

Package                Version  Ecosystem  Purl                                   License/Status  Reason
@mycompany/reporting   2.1.0    npm        pkg:npm/%40mycompany/reporting@2.1.0   unknown         license is unresolved
```

**A forbidden license is never absorbed**, even when you regenerate the file. Only `unknown`, `ambiguous`, `conflict`, and `invalid` can be acknowledged, and only when no recognizable candidate is rejected by the allow-list. A resolved license belongs in `--allow-licenses`, and an `error` is a collection failure to repair.

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

An acknowledged component keeps its unresolved status and evidence in the report; only its violation is removed. When the version changes, or a registry corrects its metadata, the fingerprint stops matching and the component fails again until it is reviewed anew.

### Skip collection or exclude evaluation for selected components

These options solve different problems:

| Option | Stage | Behavior |
|---|---|---|
| `scan --skip-evidence-packages <purl-prefix>` | Evidence collection | Makes no external request for matching components. Components remain in the report and policy evaluation. |
| `check --exclude-packages <purl-prefix>` | Policy evaluation | Removes matching components from allow-list evaluation, baselines, violations, and SARIF. The scan report is unchanged. |

Both use case-sensitive Package URL prefixes. ol does not infer package ownership or whether a package is private.

Write a namespace the way its ecosystem spells it: `--skip-evidence-packages pkg:npm/@acme/` matches `pkg:npm/%40acme/util@1.0.0`. A version separator is unaffected, so `pkg:npm/left-pad@1.3.0` still selects that one component.

Neither is required to keep a private package reviewable: a registry `404` already yields `unknown`, which a baseline can acknowledge. Use `--skip-evidence-packages` when you want to stop spending a request that cannot succeed, and `--exclude-packages` when the component is outside the check.

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

You can also pass both. ol matches them on package URL and combines their evidence, and the `SUPPLIED` column shows whether a component came from the SBOM, the package-manager input, or both. This is worth doing when the two inputs enumerate different sets — a lockfile often holds entries an SBOM omits — or when you want disagreements between them reported rather than hidden by scanning separately.

### Does ol require network access?

By default, yes, for external evidence collection. `--no-external-evidence` disables all registry, repository, and evidence-cache access. Bundled SPDX data keeps ordinary validation available offline.

### How should I handle an existing unresolved dependency?

Review its raw evidence and status first. A baseline can acknowledge known unresolved evidence that cannot be fixed, but never a forbidden license or `error`.

### Can I apply another policy without scanning again?

Yes. Run `check` with a different `--allow-licenses` value against the saved canonical JSON report. `check` performs no external access.

## Ecosystem usage

For ecosystem-specific SBOM generation, resolved-input commands, and important input constraints, see the [ecosystem usage guide](docs/en/echosystems.md).

It covers .NET/NuGet, JavaScript, Rust, Go, Python, PHP/Composer, Ruby/Bundler, Java/Maven and Gradle, SwiftPM, and CocoaPods.

## Detailed documentation

- [Ecosystem usage](docs/en/echosystems.md)
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
