# Ol

OpenSource License checker.

## Usage

```bash
$ dotnet run --project src/Ol -- --help
Usage: [command] [-h|--help] [--version]

Commands:
  cache clear     Clears cached evidence for the specified category.
  scan            Scan a resolved dependency input.
  spdx clear      Clear user-managed SPDX data.
  spdx list       List installed SPDX data versions.
  spdx update     Download SPDX data into the user data directory.
  spdx use        Switch active SPDX data version.
  spdx version    Show the active SPDX data source.
```

```bash
$ dotnet run --project src/Ol -- scan --help
Usage: scan [options...] [-h|--help] [--version]

Scan a resolved dependency input.

Options:
  --sbom <string?>               SBOM JSON path. Cannot be combined with --input. [Default: null]
  --input <string?>              Resolved dependency input file, or a directory containing project.assets.json files. [Default: null]
  --input-format <string?>       Input format: auto (default), cyclonedx, spdx, or nuget-assets. [Default: null]
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

`--input-format` defaults to `auto`. Ol identifies the input from registered content signatures and rejects unknown or ambiguous documents. Use `--input-format cyclonedx`, `spdx`, or `nuget-assets` when an explicit format assertion is useful. `--verbose` writes the detected input kind and format to stderr in addition to showing verbose report columns.

`--sbom <path>` remains available as a compatible shortcut for CycloneDX or SPDX JSON.

Use an isolated cache root when a build or CI job must not share the user cache:

```bash
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --cache-dir .tmp/ol-cache
```

## Scan dependencies

### SBOM

Ol accepts CycloneDX and SPDX JSON SBOMs. Restore the repository-pinned CycloneDX .NET tool and generate a CycloneDX JSON SBOM from the solution:

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

Ol accepts resolved dependency inputs. For one .NET project, scan NuGet's resolved `project.assets.json` directly. For a repository or solution layout, pass a directory and Ol recursively combines the `project.assets.json` files below it. NuGet resolution can differ by project, target framework, and runtime identifier, so Ol preserves each as a separate occurrence context while reporting each package/version once.

```bash
dotnet restore Ol.slnx
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json --format markdown
```

You can specify a directory containing multiple `project.assets.json` files:

```bash
dotnet run --project src/Ol -- scan --input . --format markdown
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
| Humanizer.Core | 2.14.1 | MIT | nuget | direct | matched |
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
  License results: 42 displayed components; 41 matched; 0 conflict; 1 unknown; 0 ambiguous; 0 invalid; 0 error
  Findings: 11 warnings; 0 deprecated SPDX identifiers
  Package metadata (full scan): 42 supported; 41 cache hits; 1 cache misses; 0 refreshed; 0 fetch errors; 0 unsupported ecosystems
  Source repositories (full scan): 20 targets; 1 GitHub requests; 19 cache hits; 1 cache misses; 0 fetch errors; 15 components without source license
  Run: concurrency 8; retries 1; GitHub auth none
  Input: ol; input format NuGet assets; SPDX 5e59516 (bundled)
  Input: src; input format NuGet assets; SPDX 5e59516 (bundled)

</details>


## Development

The ecosystem CI and self-scan contract is documented in [verification.md](.github/docs/specs/verification.md).

### Repository sandbox

Regenerate Ol's committed SBOM and text, Markdown, and JSON report snapshots through the generalized input API with:

```bash
./sandbox/Update-SelfScan.ps1
```

### Generated Data

SPDX License list are generated from the SPDX license list JSON. To update the license list, run:

```bash
dotnet run --project src/Ol.Update -- generate
```
