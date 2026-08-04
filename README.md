[![build](https://github.com/guitarrapc/ol/actions/workflows/build.yml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/build.yml)
[![Release](https://github.com/guitarrapc/ol/actions/workflows/release.yaml/badge.svg)](https://github.com/guitarrapc/ol/actions/workflows/release.yaml)

# ol

Open-source license checker for resolved dependencies and SBOMs.

ol consumes an SBOM or a supported resolved package-manager input, enriches its components with package metadata and source-repository license evidence, reconciles those claims through SPDX semantics, and produces explainable reports for review or policy checks. SBOM generation and ecosystem-specific dependency resolution remain the responsibility of ecosystem-native tools.

Source-repository enrichment intentionally uses the GitHub License API as a bounded evidence source. ol does not crawl arbitrary repository contents or guess licenses from repository layout; component-level evidence for repositories with independently licensed subtrees should be supplied by the SBOM or other dependency input.

## Why ol?

A dependency manifest tells you what a project requested, but license review needs the versions that the build actually resolved, including transitive dependencies. The result must also be useful to both a human reviewer and a CI policy check.

Use ol when you want to:

- review the licenses of the dependencies in a release, audit, or pull request;
- find missing, ambiguous, or conflicting license evidence before applying policy;
- enforce an SPDX license allow-list in CI;
- compare two saved reports and focus review on license-relevant changes.

ol deliberately starts from a resolved dependency graph: a CycloneDX or SPDX JSON SBOM, a supported lockfile, or package-manager output such as `project.assets.json` or `cargo metadata`. It does not resolve manifests or version ranges itself.

## Quick start

Download and extract the binary for your platform from [GitHub Releases](https://github.com/guitarrapc/ol/releases), then put it on `PATH`.

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

In GitHub Actions, [guitarrapc/setup-ol](https://github.com/guitarrapc/setup-ol) installs the latest ol release and adds it to `PATH`:

```yaml
steps:
  - uses: actions/checkout@v7
  - uses: guitarrapc/setup-ol@v1.0.0
  - run: ol scan --input . --format json > ol-report.json
    env:
      OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
  - run: ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

See [SBOM and ecosystem support](#sbom-and-ecosystem-support) for accepted inputs and ecosystem-specific commands.

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

```bash
$ ol scan --help
Usage: scan [options...] [-h|--help] [--version]

Scan a resolved dependency input.

Options:
  --input <string[]?>                   Repeatable resolved dependency input files or directories. [Default: null]
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
  --allow-licenses <string?>        Comma-separated SPDX License Identifiers. [Default: null]
  --allow-dev-licenses <string?>    Comma-separated SPDX License Identifiers additionally allowed for development-only components. [Default: null]
  --exclude-packages <string?>      Comma-separated package URL prefixes whose components are not evaluated. [Default: null]
  --spdx-data <string?>             Directory containing licenses.json and exceptions.json. [Default: null]
  --verbose                         Include persisted report diagnostics.
  --baseline <string?>              Baseline file acknowledging already reviewed unresolved components. [Default: null]
  --update-baseline                 Rewrite the baseline file as a complete snapshot.
  --sarif <string?>                 Write violations as SARIF to this file for CI code scanning. [Default: null]
```

```bash
$ ol diff --help
Usage: diff [options...] [-h|--help] [--version]

Compare two persisted JSON scan reports and report license-relevant changes.

Options:
  --previous <string?>          Previously persisted JSON scan report. [Default: null]
  --current <string?>           Current JSON scan report. [Default: null]
  --allow-licenses <string?>    Comma-separated SPDX License Identifiers; adds policy verdict transitions. [Default: null]
  --spdx-data <string?>         Directory containing licenses.json and exceptions.json. [Default: null]
  --format <DiffFormat>         Output format. [Default: Text]
```

`--input-format` defaults to `auto`. ol identifies the input from registered content signatures and rejects unknown or ambiguous documents. Supported assertions are:

| Language | name |
| --- | --- |
| SBOM | `cyclonedx` |
| SBOM | `spdx` |
| .NET | `nuget-assets` |
| JavaScript (npm) | `npm-package-lock` |
| JavaScript (pnpm) | `pnpm-lock` |
| JavaScript (Yarn v1) | `yarn-classic-lock` |
| JavaScript (Yarn v2+) | `yarn-berry-lock` |
| Rust | `cargo-metadata` |
| Go | `go-module-graph` |
| Python | `pip-inspect` |
| PHP | `composer-lock` |
| Ruby | `bundler-lock` |
| Java / JVM | `maven-dependency-tree` |
| Swift (SwiftPM) | `swift-package-resolved` |
| Swift / Objective-C (CocoaPods) | `cocoapods-lock` |

`--verbose` writes the detected input kind and format to stderr in addition to showing verbose report columns.

Use an isolated cache root when a build or CI job must not share the user cache:

```bash
dotnet run --project src/ol -- scan --input sandbox/sbom/cyclonedx-sample.json --cache-dir .tmp/ol-cache
```

### Check licenses

Use `check` in CI to allow only selected SPDX License Identifiers. `scan` performs input detection and license enrichment and writes a canonical JSON report; `check` evaluates every non-root component in that report and reports all violations without rescanning or using the network. An SBOM root describes the application being checked, so `scan` retains it as evidence while `check` excludes it from dependency policy.

Generate and check a report from a lockfile, package-manager output, SBOM, or directory:

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause

ol scan --input package-lock.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,ISC

ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

`--allow-licenses` is a required comma-separated list of SPDX License Identifiers. Matching is case-insensitive and normalized to official SPDX casing. Natural-language names, SPDX expressions, exception identifiers, unknown identifiers, and empty entries are rejected as configuration errors.

`--allow-dev-licenses` is an optional second allow-list, validated the same way, that applies only to components reachable exclusively through a development path — for example a license pulled in solely by dev tooling such as a Vite toolchain. Usage is taken from the resolver (npm `package-lock.json`, pnpm `pnpm-lock.yaml`, the Composer pair, Maven `test` scope, and Cargo dev-only reachability) and aggregated across all occurrences, so a single runtime or usage-unknown occurrence keeps the component on the primary allow-list; names are never used to infer development usage. For Composer, a `packages-dev` entry that a production `require` can still reach is treated as inconsistent input rather than silently allowed. Yarn leaves usage unknown, because `yarn.lock` records no development scope. The resolved usage is saved in the JSON report; a report without usage fails closed. It states an organization policy, not that the package is absent from a production build, so keep checking the production artifact with the primary allow-list alone.

```bash
ol scan --input package-lock.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause --allow-dev-licenses CC-BY-4.0
```

SPDX expressions found in dependencies are evaluated using SPDX semantics:

| Dependency expression | `--allow-licenses MIT,Apache-2.0` |
| --- | --- |
| `MIT` | Pass |
| `MIT AND Apache-2.0` | Pass |
| `MIT OR GPL-3.0-only` | Pass |
| `MIT AND GPL-3.0-only` | Violation |
| `GPL-2.0-only WITH Classpath-exception-2.0` | Violation |

`OR` passes when at least one choice is allowed, while `AND` requires every license. `WITH` uses the allow status of its base license. Components with `unknown`, `conflict`, `ambiguous`, `invalid`, or `error` license status fail closed.

Example violation output:

```text
License check failed: 2 violations.

Package      Version  Ecosystem  Purl                         License/Status  Reason
example-lib  1.2.3    npm        pkg:npm/example-lib@1.2.3    GPL-3.0-only   license is not allowed
unknown-lib  2.0.0    nuget      pkg:nuget/unknown-lib@2.0.0  unknown        license is unresolved
```

Exit codes are suitable for CI:

| Exit code | Meaning |
| ---: | --- |
| `0` | The command completed successfully; help and version output also use `0`. |
| `1` | Argument parsing failed, or the command could not be completed because of invalid configuration, input, I/O, or another execution failure. |
| `2` | `check` completed policy evaluation and found one or more violations. |

Evaluate only the license evidence declared in the input when external collection is intentionally disabled:

```bash
ol scan --input bom.cdx.json --no-external-evidence --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

`--no-external-evidence` contacts no package registry and no source repository, and reads neither of their caches. Because unresolved licenses fail closed, it can produce violations that a check with external evidence would resolve.

Skip collection for selected components instead of all of them with the `scan` option `--skip-evidence-packages`:

```bash
ol scan --input . --skip-evidence-packages pkg:nuget/MyCompany. --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

This is useful when a component cannot be resolved for reasons outside the license itself, such as a registry that requires authentication. Without it, every run spends a request that cannot succeed and the component ends as `error`, which a baseline cannot acknowledge. With it, no request is made and the component is reported as `unknown` with the warning `external_evidence_not_collected`, which a baseline can acknowledge. The component stays in the report and in the check; only the collection is skipped. Prefixes use the same matching rules as `--exclude-packages`, and `--verbose` reports how many components each prefix matched.

#### Excluding packages from the check

`--exclude-packages` removes selected components from the check. It is useful when a component cannot be resolved for reasons outside the license itself, such as a registry that requires authentication, and when a package is reviewed through a separate process.

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 --exclude-packages pkg:nuget/MyCompany.,pkg:npm/@mycompany/
```

Excluded components are absent from evaluation, the baseline, violations, SARIF, and the passing count, exactly like an SBOM root. They remain in `scan` output and in the JSON report with their evidence. `check` always prints the count, including `0`:

```text
Excluded from evaluation: 3 components.
License check passed: 812 components satisfy the allow-list.
```

Prefixes are matched against the component purl, case-sensitively, and only at purl boundaries (`/`, `.`, `@`):

| Prefix | Matches | Does not match |
| --- | --- | --- |
| `pkg:nuget/MyCompany.` | `pkg:nuget/MyCompany.Core@1.0.0` | `pkg:nuget/MyCompanyEvil@1.0.0` |
| `pkg:npm/@mycompany/` | `pkg:npm/@mycompany/util@1.0.0` | `pkg:npm/mycompany-util@1.0.0` |
| `pkg:npm/left-pad@1.3.0` | that exact component | any other version |

A value naming only an ecosystem, such as `pkg:npm/`, is rejected. A component with no purl is never excluded, and a casing mismatch leaves the component evaluated. The option changes nothing in the report itself.

#### Adopting `check` on an existing project

Failing closed is right for a pull request, where the baseline is already clean and anything newly unresolved deserves a look. It is not enough for a product that already exists: real dependency sets contain components whose license Ol cannot resolve, and most of them are not something you can fix. A baseline records the unresolved components you have reviewed and accepted, so only *newly* unresolved components fail.

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json --update-baseline
```

That one command adopts a baseline and still evaluates the result. If it exits `2`, what remains is a genuine finding — a forbidden license can never be absorbed by a baseline:

```text
Acknowledged by baseline: 2 components.
License check failed: 1 violation.

Package  Version  Ecosystem  Purl                     License/Status  Reason
poison   2.0.0    npm        pkg:npm/poison@2.0.0     GPL-3.0-only    license is not allowed
```

Commit `ol-baseline.json`, then check against it from then on:

```bash
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json
```

Only `unknown`, `ambiguous`, `conflict`, and `invalid` can be acknowledged. `error` cannot, because a collection failure is something to repair rather than to accept. `matched` cannot, because a resolved license is a policy decision that belongs in `--allow-licenses`. Above all, **a component is never acknowledgeable when any of its evidence normalizes to a license your allow-list rejects** — so `--update-baseline` cannot silence a GPL dependency, not even one hidden inside a conflict.

The file is generated; you never write it by hand. Each entry keeps the raw claims so a reviewer can judge it straight from a pull request diff:

```json
{
  "ecosystem": "npm",
  "name": "vague",
  "version": "0.1.0",
  "purl": "pkg:npm/vague@0.1.0",
  "status": "ambiguous",
  "evidence": [ { "source": "sbom", "kind": "name", "raw": "BSD" } ],
  "fingerprint": "ffb2d51436e7..."
}
```

The fingerprint makes an acknowledgement expire by itself. When a version changes, a registry corrects its metadata, or a repository's license file changes, the entry stops applying and the component fails again until it is reviewed anew. `--update-baseline` always rewrites the whole file, so a baseline is a reviewed snapshot to reduce, not a list to curate. `--baseline` must be named explicitly; Ol never picks one up by convention.

Ol cannot identify every forbidden license: an unnormalizable string such as `GPLv3` has no SPDX meaning to check against, and Ol refuses to guess. Such a claim can be acknowledged, but its raw text appears in the baseline diff, so it stays visible to review.

#### Policy evaluation from a saved report

Save a report once, then evaluate any policy against it offline. No input parsing, no registry or repository calls, no network:

```bash
ol scan --input . --format Json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

The canonical JSON report is the required input contract, so there is no second file format to keep in sync. Collection options belong to `scan` and are not accepted by `check`; `--baseline` and `--sarif` remain policy-evaluation options.

#### CI code scanning (SARIF)

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT --sarif ol.sarif
```

Text output on stdout is unchanged; SARIF carries the same violations. Because Ol reads resolved graphs rather than manifests, results use logical locations instead of invented file positions, and each one names the direct dependency that introduced a transitive violation — the part you can actually change:

```text
pkg:npm/poison@2.0.0: license is not allowed (GPL-3.0-only).
Introduced through pkg:npm/direct@1.0.0 > pkg:npm/poison@2.0.0
```

Rule IDs are stable: `OL0001` not allowed, `OL0002` evidence conflict, `OL0003` unresolved, `OL0004` ambiguous, `OL0005` invalid expression, `OL0006` evidence error.

### Compare two reports

`diff` shows only what changed about licensing between two saved reports, so a reviewer does not have to read a whole report to find it.

```bash
ol diff --previous before.json --current after.json --allow-licenses MIT
```

```text
License-relevant changes: 2 changes.

Change           Ecosystem  Name    Previous  Current
license-changed  npm        poison  MIT       GPL-3.0-only
policy-changed   npm        poison  MIT       GPL-3.0-only
```

Change kinds are `added`, `removed`, `version-changed`, `status-changed`, `license-changed`, `evidence-changed`, and `policy-changed` when `--allow-licenses` is given. `evidence-changed` means the underlying claims moved while the conclusion held — a change of fact rather than of wording. `--format Json` emits the same set as a document. `diff` reports rather than enforces: it exits `0` when it completes and `1` when a report could not be read.

## SBOM and ecosystem support

ol supports CycloneDX and SPDX JSON SBOMs across ecosystems. It can also read the following resolved package-manager inputs directly:

| Ecosystem | Direct resolved input | Direct support |
| --- | --- | --- |
| .NET / NuGet | `project.assets.json` | Supported |
| JavaScript / npm | `package-lock.json` version 2/3 | Supported |
| JavaScript / pnpm | `pnpm-lock.yaml` version 9 | Supported |
| JavaScript / Yarn | Yarn Classic v1 and Berry metadata v8 `yarn.lock` | Supported |
| Rust / Cargo | `cargo metadata --format-version 1` JSON | Supported |
| Go modules | `go list -m -json all` plus `go mod graph` | Supported |
| Python | `pip inspect` JSON format version 1 | Supported |
| PHP / Composer | Same-directory `composer.json` and `composer.lock` | Supported |
| Ruby / Bundler | `Gemfile.lock` | Supported |
| Java / Maven | Maven Dependency Plugin 3.7+ tree JSON | Supported |
| Java / Gradle | — | Use a CycloneDX or SPDX JSON SBOM |

The sections below show how to generate or select each input and include report examples. The compact direct-input examples from npm onward use the repository's deterministic samples with `--no-external-evidence`, so their license results reflect only evidence declared by each resolved input. For the exact file-discovery rules and recommended workflow by ecosystem, see [Dependency files by ecosystem](#dependency-files-by-ecosystem).

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

### Dependency files by ecosystem

ol does not resolve package manifests or version ranges itself. It consumes either a resolved graph supported by a direct input adapter or an SBOM whose generator performed the ecosystem-specific resolution. Passing a declaration such as `package.json`, `*.csproj`, `Cargo.toml`, or `pyproject.toml` directly to ol is not supported.

| Ecosystem | Typical dependency files | Resolution supplied to ol | Recommended workflow |
|---|---|---|---|
| .NET / NuGet | `*.sln`, `*.slnx`, `*.csproj`, `packages.lock.json` | Generated `project.assets.json` version 3/4 | Run `dotnet restore`, then scan the generated file or a directory containing it with `--input`. |
| JavaScript / npm | `package.json`, `package-lock.json` | `package-lock.json` version 2/3 | Scan the committed lockfile directly with `--input`; an install is not required for ol. |
| JavaScript / pnpm | `package.json`, `pnpm-lock.yaml`, workspace file | `pnpm-lock.yaml` version 9.0 | Scan the committed lockfile directly with `--input`. Importers become separate contexts. |
| JavaScript / Yarn Classic | `package.json`, `yarn.lock` | Yarn lockfile version 1 | Scan `yarn.lock` directly. The lockfile has no root manifest graph, so dependency type remains unknown where the root relationship cannot be proven. |
| JavaScript / Yarn Berry | `package.json`, `yarn.lock`, `.yarnrc.yml` | Yarn metadata version 8 lockfile | Scan `yarn.lock` directly. Workspace contexts and proven descriptor edges are retained without reconstructing install state. |
| Rust / Cargo | `Cargo.toml`, `Cargo.lock` | `cargo metadata --format-version 1 --locked` JSON | Generate `cargo-metadata.json` using the build's feature/target selection, then scan it with `--input`. ol does not resolve `Cargo.toml` or `Cargo.lock` itself. |
| Go modules | `go.mod`, `go.sum`, optional `go.work` | Paired `go list -m -json all` and `go mod graph` output | Generate `go-list-modules.json` and `go-mod-graph.txt` together, then pass both files or their directory. ol consumes Go's selected build list instead of running MVS itself. |
| Java / JVM | Maven Dependency Plugin 3.7+ `dependency:tree` JSON | `maven-dependency-tree.json` | Resolved graph input; version-specific Maven license and source hints are enriched from deps.dev. |
| Java / JVM | Gradle files and lock state, SBT files | CycloneDX/SPDX JSON SBOM | Gradle does not officially provide a machine-readable resolved-graph JSON format, so direct Gradle input is unsupported; use its CycloneDX generator or a polyglot generator. |
| Swift / SwiftPM | `Package.swift`, `Package.resolved` | `Package.resolved` schema version 2/3 | Scan the resolved file directly. Pins and source refs are retained; dependency type stays unknown because the lock file has no graph. |
| Swift / Objective-C / CocoaPods | `Podfile`, `Podfile.lock` | Resolved `Podfile.lock` | Scan the lock directly. Public pod license/source hints are enriched from the exact CocoaPods CDN podspec. |
| Python | `requirements*.txt`, `pyproject.toml`, `poetry.lock`, `Pipfile.lock`, `uv.lock` | CycloneDX/SPDX JSON SBOM, or `python -m pip inspect --local` JSON | Prefer an SBOM generated from the intended environment. Alternatively, generate `pip-inspect.json` and scan it directly; ol consumes installed distributions and does not choose markers, extras, or platform wheels. |
| PHP / Composer | `composer.json`, `composer.lock` | Paired `composer.json` and `composer.lock`, or CycloneDX/SPDX JSON SBOM | Prefer an SBOM from the locked project. Alternatively, scan the directory containing the pair with `--input-format composer-lock`; ol consumes the lockfile without invoking Composer. |
| Ruby / Bundler | `Gemfile`, `Gemfile.lock` | CycloneDX/SPDX JSON SBOM, or resolved `Gemfile.lock` | Prefer an SBOM generated from the locked project. Alternatively, scan `Gemfile.lock` directly with `--input`; ol consumes its resolved specs and root dependencies without evaluating `Gemfile`. |

For direct adapters, directory discovery recognizes only the resolved files listed above: `project.assets.json`, `package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `cargo-metadata.json`, `pip-inspect.json`, `Gemfile.lock`, `maven-dependency-tree.json`, `Package.resolved`, `Podfile.lock`, the paired Composer files `composer.json` plus `composer.lock`, and the paired Go files `go-list-modules.json` plus `go-mod-graph.txt`. For the remaining ecosystems, [cdxgen](https://github.com/cdxgen/cdxgen) supports recursive multi-language SBOM generation from common lockfiles and project metadata. Ecosystem-native CycloneDX generators are also suitable when they preserve the resolved component identities and dependency graph required by the report.

### Repositories with multiple package managers

Use one canonical dependency source per ol report. For a release or audit artifact, the preferred workflow is one repository-wide CycloneDX JSON SBOM. A polyglot generator such as [cdxgen](https://github.com/cdxgen/cdxgen) can recursively detect multiple languages and package managers:

```bash
# First run the repository's normal locked restore/install steps.
cdxgen -r -o bom.cdx.json .
ol scan --input bom.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

Check that the generated BOM contains every intended project, package ecosystem, and dependency relationship. A single file is only better when its generator has complete coverage. If separate ecosystem tools produce separate CycloneDX BOMs, merge them before scanning; [CycloneDX CLI](https://github.com/CycloneDX/cyclonedx-cli) supports hierarchical merge when every input BOM identifies its subject in `metadata.component`:

```bash
cyclonedx merge --input-files dotnet.cdx.json node.cdx.json --output-file repository.cdx.json --output-format json --hierarchical --name my-repository --version "$GIT_COMMIT"

ol scan --input repository.cdx.json --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

When a trustworthy polyglot SBOM is unavailable, scan resolved package-manager inputs directly. Restore .NET projects first so `project.assets.json` exists, then pass selected roots or the repository directory. Do not specify `--input-format` for mixed formats:

```bash
dotnet restore MyRepository.slnx
cargo metadata --format-version 1 --locked > src/rust/cargo-metadata.json
pushd src/go
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt
popd
pushd src/python
python -m pip inspect --local > pip-inspect.json
popd
ol scan --input src/backend --input src/frontend --input src/rust --input src/go --input src/python --input src/php --input src/ruby --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

ol recursively discovers `project.assets.json`, `package-lock.json`, `pnpm-lock.yaml`, both Yarn lock formats, `cargo-metadata.json`, `pip-inspect.json`, `Gemfile.lock`, and complete Composer and Go companion pairs. Different detected formats produce a `package-manager/collection` report. Every input keeps its own contexts, occurrences, and edges; ol does not invent cross-language dependency edges. Components are combined only under the originating format's identity rules, so the same npm purl resolved by npm and pnpm remains separate graph evidence while registry enrichment work is deduplicated by cache key.

ol intentionally rejects SBOM and package-manager inputs in the same report, and it does not accept multiple SBOM files as an implicit union. Combining them would double-count packages and make conflicting graph/evidence precedence ambiguous. To validate both paths in CI, produce two independent reports: a canonical SBOM report and a direct-lockfile report. The runnable mixed-manager example is under [sandbox/package-manager-inputs](sandbox/package-manager-inputs/README.md).

## FAQ

### Can I pass a manifest such as `package.json`, `*.csproj`, or `Cargo.toml`?

No. ol reads dependencies that have already been resolved, so it can review the exact versions and transitive graph used by the build. Generate an SBOM or use the [supported resolved input](#dependency-files-by-ecosystem) for the ecosystem.

### Should I use an SBOM or a package-manager input?

Prefer one canonical CycloneDX or SPDX JSON SBOM for a release or audit artifact, especially for a repository with multiple ecosystems. Direct package-manager inputs are convenient for local feedback and for ecosystems where the resolved graph is already committed or generated by the build.

### Can ol scan several ecosystems in one repository?

Yes. The preferred workflow is a repository-wide SBOM. ol can also discover several supported package-manager inputs from directories and combine them into one report. It does not mix SBOMs with package-manager inputs or implicitly merge multiple SBOM files.

### Does ol need network access?

By default, ol collects external evidence: package registry metadata and bounded GitHub License API lookups, both cached locally. Use `--no-external-evidence` to report only the license evidence declared in the input; no registry, no source repository, and no cache is read. Unresolved components remain visible in `scan` and fail closed in `check`.

### How do I avoid GitHub API rate limits in CI?

Map a token explicitly as `OL_GITHUB_TOKEN`. ol does not implicitly read `GITHUB_TOKEN`:

```yaml
env:
  OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

### What should I do with an unresolved existing dependency?

Review its raw evidence first. When adopting `check` on an existing project, a generated [baseline](#adopting-check-on-an-existing-project) can acknowledge reviewed unresolved components while continuing to reject forbidden licenses and newly changed evidence.

### Can I apply a different policy without scanning again?

Yes. Persist a JSON report and pass it to `check`:

```bash
ol scan --input . --format json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```


## Development

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).

### Repository sandbox

Regenerate ol's committed SBOM and text, Markdown, and JSON report snapshots through the generalized input API with:

```bash
./sandbox/Update-SelfScan.ps1
```

To keep the committed SBOM as a fixed golden input and regenerate only its derived reports, run:

```bash
./sandbox/Update-SelfScan.ps1 -ReportsOnly
```

CI separately generates a live self-scan of `src/Ol/Ol.csproj` with the latest .NET 10 SDK, validates its dependency inventory, and enforces the `MIT` license policy on its distributable dependencies. The live SBOM is retained as an artifact instead of being compared byte-for-byte with the golden input.

### Scan

```bash
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format markdown
```

### Check

```bash
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format json > ol-report.json
dotnet run --project src/Ol -- check --report ol-report.json --allow-licenses MIT,Apache-2.0,BSD-2-Clause,BSD-3-Clause
```

### Generated Data

SPDX License list are generated from the SPDX license list JSON. To update the license list, run:

```bash
dotnet run --project src/Ol.Update -- generate
```
