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
  --input <string?>              Resolved dependency input path. [Default: null]
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
dotnet run --project src/Ol -- scan --input sandbox/sbom/cyclonedx-sample.json --format markdown --out output.md
```

### NuGet

Ol accepts resolved dependency inputs. For a .NET project, restore it and scan NuGet's resolved `project.assets.json` directly. NuGet resolution can differ by target framework and runtime identifier. Ol preserves each target from `project.assets.json` as a separate resolution context in JSON output.

```bash
dotnet restore Ol.slnx
dotnet run --project src/Ol -- scan --input src/Ol/obj/project.assets.json
```

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
