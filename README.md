# ol

OpenSource License checker.

## Usage

```bash
$ ol --help
Usage: [command] [-h|--help] [--version]

Commands:
  cache clear     Clears cached evidence for the specified category.
  check           Check a resolved dependency input against allowed SPDX licenses.
  scan            Scan a resolved dependency input.
  spdx clear      Clear user-managed SPDX data.
  spdx list       List installed SPDX data versions.
  spdx update     Download SPDX data into the user data directory.
  spdx use        Switch active SPDX data version.
  spdx version    Show the active SPDX data source.
```

```bash
$ ol check --help
Usage: check [options...] [-h|--help] [--version]

Check a resolved dependency input against allowed SPDX licenses.

Options:
  --input <string[]?>           Repeatable resolved dependency input files or directories. [Default: null]
  --allow-licenses <string?>    Comma-separated SPDX License Identifiers. [Default: null]
  --input-format <string?>      Input format assertion; defaults to auto detection. [Default: null]
  --spdx-data <string?>         Directory containing licenses.json and exceptions.json. [Default: null]
  --verbose                     Include input detection diagnostics.
  --refresh                     Skip package metadata cache entries.
  --cache-dir <string?>         Root directory for isolated package-metadata and source-repository caches. [Default: null]
  --skip-enrichment             Use only evidence already present in the dependency input.
  --concurrency <int>           Maximum concurrent package metadata lookups. [Default: 0]
  --retry <int>                 Reserved package metadata retry count. [Default: 1]
```

Use `check` in CI to fail when a resolved dependency has a forbidden or unresolved license:

```bash
ol check --input . --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

The command returns `0` when every component satisfies the allow-list, `1` for policy violations, and `2` when the check cannot be completed.

```bash
$ ol scan --help
Usage: scan [options...] [-h|--help] [--version]

Scan a resolved dependency input.

Options:
  --input <string[]?>            Repeatable resolved dependency input files or directories. [Default: null]
  --input-format <string?>       Input format assertion; defaults to auto detection. [Default: null]
  --format <ReportFormat>        Output format: text, json, or markdown. [Default: Text]
  --out, --out-file <string?>    Write output to this path. [Default: null]
  --verbose                      Include verbose columns and input detection diagnostics.
  --dependency <string?>         Dependency output filter: root,direct,transitive,unknown. [Default: null]
  --group-by <string?>           Group output by fields: name,version,license,ecosystem,dependency,status. [Default: null]
  --sort <string>                Sort keys: ecosystem,name,version,license,dependency,status,purl. [Default: @"ecosystem,name,version"]
  --sort-order <SortOrder>       Sort order: asc or desc. [Default: Asc]
  --spdx-data <string?>          Directory containing licenses.json and exceptions.json. [Default: null]
  --quiet                        Suppress stderr summary.
  --refresh                      Skip package metadata cache entries.
  --cache-dir <string?>          Root directory for isolated package-metadata and source-repository caches. [Default: null]
  --skip-enrichment              Use only evidence already present in the dependency input.
  --concurrency <int>            Maximum concurrent package metadata lookups. [Default: 0]
  --retry <int>                  Reserved package metadata retry count. [Default: 1]
```

`--input-format` defaults to `auto`. ol identifies the input from registered content signatures and rejects unknown or ambiguous documents. Supported assertions are:

| Language | name |
| --- | --- |
| SBOM | `cyclonedx` |
| SBOM | `spdx` |
| .NET | `nuget-assets` |
| JavaScript | `npm-package-lock` |
| JavaScript | `pnpm-lock` |
| JavaScript | `yarn-classic-lock` |
| JavaScript | `yarn-berry-lock` |
| Rust | `cargo-metadata` |
| Go | `go-module-graph` |
| Python | `pip-inspect` |

`--verbose` writes the detected input kind and format to stderr in addition to showing verbose report columns.

Use an isolated cache root when a build or CI job must not share the user cache:

```bash
dotnet run --project src/ol -- scan --input sandbox/sbom/cyclonedx-sample.json --cache-dir .tmp/ol-cache
```

## Scan dependencies

### SBOM

ol accepts CycloneDX and SPDX JSON SBOMs. Restore the repository-pinned CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

```bash
dotnet tool restore
dotnet tool run dotnet-CycloneDX Ol.slnx --output sandbox/sbom --output-format Json --filename cyclonedx-sample.json
```

Scan the generated SBOM with the generalized input API:

```bash
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --format markdown
```

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
  Output file: output.md

</details>

### NuGet

ol accepts resolved dependency inputs. For one .NET project, scan NuGet's resolved `project.assets.json` directly. For a repository or solution layout, pass a directory and ol recursively combines the `project.assets.json` files below it. NuGet resolution can differ by project, target framework, and runtime identifier, so ol preserves each as a separate occurrence context while reporting each package/version once.

```bash
dotnet restore Ol.slnx
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format markdown
```

You can specify a directory containing multiple `project.assets.json` files:

```bash
dotnet run --project src/Ol -- scan --input src/ --input tests/ --format markdown
```

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

### Node.js lockfiles

ol scans resolved npm `package-lock.json` version 2/3, pnpm `pnpm-lock.yaml` version 9, Yarn Classic `yarn.lock` version 1, and Yarn Berry `yarn.lock` metadata version 8. Pass the lockfile or a directory; workspace/importer contexts and proven dependency edges are retained without running the package manager or evaluating platform conditions against the current host.

### Cargo metadata

ol scans Cargo's resolved metadata JSON format version 1. Generate it from the same locked feature and target selection used by the build, then scan the generated file:

```bash
cargo metadata --format-version 1 --locked > cargo-metadata.json
dotnet run --project src/Ol -- scan --input cargo-metadata.json
```

Each workspace member becomes a resolution context. Workspace and path nodes participate in reachability without being mislabeled as crates.io packages. Resolved features, dependency kinds, and target expressions are retained as variants; ol does not evaluate them against the current host. Cargo metadata does not record the `--filter-platform` argument itself, so ol does not infer a target triple from the machine running the scan.

### Go module graph

Go does not persist its MVS build list in a lockfile. Generate both the selected module list and its requirement edges from the same module or workspace, using these exact output names:

```bash
go list -m -json all > go-list-modules.json
go mod graph > go-mod-graph.txt

dotnet run --project src/Ol -- scan \
  --input go-list-modules.json \
  --input go-mod-graph.txt
```

Alternatively, pass their containing directory. ol binds the two companion files as one `go-module-graph` input:

```bash
dotnet run --project src/Ol -- scan --input .
```

`go-list-modules.json` is authoritative for the selected build list and replacement metadata. `go-mod-graph.txt` contributes only edges whose endpoints are in that selected list, so superseded module versions and Go's `go@...`/`toolchain@...` graph nodes do not become components. Local replacements receive no proxy purl and their filesystem paths are not reported. Versioned module replacements use the replacement module/version for enrichment while retaining the original requirement as `sourceId`. If the list JSON contains `Retracted` data, ol retains a `retracted` occurrence variant. GOOS, GOARCH, and build tags remain unspecified because neither output proves them.

### Python environment

ol scans the stable JSON format version 1 produced by `pip inspect`. Activate the exact virtual environment used by the build or deployment, then capture its installed distributions and environment:

```bash
python -m pip inspect --local > pip-inspect.json
dotnet run --project src/Ol -- scan --input pip-inspect.json
```

The installed distribution set is authoritative; ol does not resolve `requirements.txt`, `pyproject.toml`, Poetry, uv, or Pipenv declarations. `requested=true` distributions are direct dependencies and receive root edges. `requested=false` proves transitive classification only when `installer` is `pip`; other installers and a missing `requested` field remain unknown. Unconditional `requires_dist` entries produce package edges when the normalized target is installed. Entries with environment markers or extras do not produce edges because `pip inspect` does not record which extras activated them. The report context retains the Python version, implementation, `sys_platform`, machine architecture, and pip version supplied by the report.

Distribution names use PyPA normalization for identity and `pkg:pypi` enrichment. A distribution with `direct_url` receives no PyPI purl and retains only `source=direct`; local paths and URLs are not reported. `license_expression` is preferred over legacy `license` metadata as input-supplied license evidence.

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
| Java / JVM | `pom.xml`, Gradle files and lock state, SBT files | CycloneDX/SPDX JSON SBOM | Run the ecosystem build/resolution and use its CycloneDX generator or a polyglot generator. |
| Python | `requirements*.txt`, `pyproject.toml`, `poetry.lock`, `Pipfile.lock`, `uv.lock` | `python -m pip inspect --local` JSON | Install or sync the intended environment, generate `pip-inspect.json`, then scan it directly. ol consumes installed distributions and does not choose markers, extras, or platform wheels. |
| PHP / Composer | `composer.json`, `composer.lock` | CycloneDX/SPDX JSON SBOM | Generate an SBOM from the locked project, then scan it with `--input`. |
| Ruby / Bundler | `Gemfile`, `Gemfile.lock` | CycloneDX/SPDX JSON SBOM | Generate an SBOM from the locked project, then scan it with `--input`. |

For direct adapters, directory discovery recognizes only the resolved files listed above: `project.assets.json`, `package-lock.json`, `pnpm-lock.yaml`, `yarn.lock`, `cargo-metadata.json`, `pip-inspect.json`, and the paired Go files `go-list-modules.json` plus `go-mod-graph.txt`. For the remaining ecosystems, [cdxgen](https://github.com/cdxgen/cdxgen) supports recursive multi-language SBOM generation from common lockfiles and project metadata. Ecosystem-native CycloneDX generators are also suitable when they preserve the resolved component identities and dependency graph required by the report.

### Repositories with multiple package managers

Use one canonical dependency source per ol report. For a release or audit artifact, the preferred workflow is one repository-wide CycloneDX JSON SBOM. A polyglot generator such as [cdxgen](https://github.com/cdxgen/cdxgen) can recursively detect multiple languages and package managers:

```bash
# First run the repository's normal locked restore/install steps.
cdxgen -r -o bom.cdx.json .
dotnet run --project src/Ol -- scan --input bom.cdx.json
```

Check that the generated BOM contains every intended project, package ecosystem, and dependency relationship. A single file is only better when its generator has complete coverage. If separate ecosystem tools produce separate CycloneDX BOMs, merge them before scanning; [CycloneDX CLI](https://github.com/CycloneDX/cyclonedx-cli) supports hierarchical merge when every input BOM identifies its subject in `metadata.component`:

```bash
cyclonedx merge --input-files dotnet.cdx.json node.cdx.json --output-file repository.cdx.json --output-format json --hierarchical --name my-repository --version "$GIT_COMMIT"

dotnet run --project src/Ol -- scan --input repository.cdx.json
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
dotnet run --project src/Ol -- scan --input src/backend --input src/frontend --input src/rust --input src/go --input src/python --format json
```

ol recursively discovers `project.assets.json`, `package-lock.json`, `pnpm-lock.yaml`, both Yarn lock formats, `cargo-metadata.json`, `pip-inspect.json`, and complete Go companion pairs. Different detected formats produce a `package-manager/collection` report. Every input keeps its own contexts, occurrences, and edges; ol does not invent cross-language dependency edges. Components are combined only under the originating format's identity rules, so the same npm purl resolved by npm and pnpm remains separate graph evidence while registry enrichment work is deduplicated by cache key.

ol intentionally rejects SBOM and package-manager inputs in the same report, and it does not accept multiple SBOM files as an implicit union. Combining them would double-count packages and make conflicting graph/evidence precedence ambiguous. To validate both paths in CI, produce two independent reports: a canonical SBOM report and a direct-lockfile report. The runnable mixed-manager example is under [sandbox/package-manager-inputs](sandbox/package-manager-inputs/README.md).


## Development

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).

### Repository sandbox

Regenerate ol's committed SBOM and text, Markdown, and JSON report snapshots through the generalized input API with:

```bash
./sandbox/Update-SelfScan.ps1
```

### Generated Data

SPDX License list are generated from the SPDX license list JSON. To update the license list, run:

```bash
dotnet run --project src/Ol.Update -- generate
```
