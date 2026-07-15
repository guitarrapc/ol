# OL CLI Specification

This document defines the user-facing behavior of the `ol` CLI. It is the umbrella specification for command behavior, output contracts, result statuses, report metadata, and version boundaries.

The CLI exists to make license information from SBOMs and related evidence visible, comparable, and machine-readable. It does not claim legal certainty. It reports candidates, conflicts, unknowns, and evidence so that later policy decisions can be made explicitly.

## Design Basis

This specification derives from the [Ol design](../DESIGN.md), especially these decisions:

- [resolve the complete dependency inventory before filtering](../DESIGN.md#decision-complete-inventory), because transitive OSS use and unknown relationships must not disappear from analysis merely because a view is filtered;
- [separate factual resolution from organizational policy](../DESIGN.md#decision-policy-separation), which is why `scan` reports license facts and a later policy phase decides whether they are allowed;
- [make component/source failures best-effort but command failures explicit](../DESIGN.md#decision-failure-scope), which determines exit behavior and the distinction between component evidence and whole-command failure; and
- [use canonical JSON plus human-oriented projections](../DESIGN.md#decision-report-views), which determines the stdout contract and why text, Markdown, and JSON represent the same resolved report; and
- [persist evidence with explicit provenance and privacy boundaries](../DESIGN.md#decision-provenance-privacy), which requires logical report references and prohibits secrets and private local paths.

The command and output rules below are user-facing consequences of those design decisions. They must not introduce an alternate status model or perform policy decisions implicitly.

## Version Roadmap

`ol` evolves by widening the dependency inputs and evidence sources used by `scan`.

- v1 scans SBOM files. The original `--sbom` form remains compatible while the input boundary is generalized for future resolved package-manager inputs.
- v2 adds package manager and package registry metadata as automatic hints.
- v3 adds source repository license hints.
- A later phase adds allow-list policy checks and CI failure behavior.

Each version must preserve the prior version's report fields unless a breaking version explicitly changes them. Specs under `.github/docs/specs/` should be updated as each version is implemented.

## Commands

### `ol cache clear`

v2 provides cache management for shared evidence stores:

```bash
ol cache clear
ol cache clear package-metadata
ol cache clear source-repository
ol cache clear all
```

The cache category is a positional argument. Omitting it is equivalent to `all`.

`package-metadata` clears the persistent package metadata cache. `source-repository` clears the persistent source repository evidence cache. `all` clears both persistent evidence caches.

`scan` and `cache clear` accept `--cache-dir <path>`. The supplied path is an isolation root, never a directly managed category: Ol reads and writes only its `package-metadata` and `source-repository` children. Clearing `all` removes those children but preserves the isolation root and unrelated files beside them. An existing file is rejected as a cache root.

The CLI option takes precedence over `OL_CACHE_DIR`. The unified environment root takes precedence over the legacy category-specific roots `OL_PACKAGE_METADATA_CACHE_ROOT` and `OL_SOURCE_REPOSITORY_CACHE_ROOT`. With none of these set, Ol uses its platform-specific user cache location. Absolute cache paths and cache-root values never appear in reports.

Cache entry compatibility and category-specific JSON schemas are defined by [cache_format.md](cache_format.md). Cache JSON is an Ol-managed persistence contract and is distinct from the canonical scan report JSON.

<a id="contract-scan-failures"></a>
### `ol scan`

`scan` is the primary command. It lists components and their license status from one resolved dependency input and the available evidence sources for the current version.

The compatible SBOM shortcut accepts CycloneDX or SPDX JSON and detects the format from content:

```bash
ol scan --sbom bom.json
```

The generalized input form detects a registered format from content by default:

```bash
ol scan --input bom.json
ol scan --input bom.spdx.json
ol scan --input obj/project.assets.json
```

`--input-format` defaults to `auto`; explicitly specifying `auto` is equivalent to omitting the option. Registered format names are matched case-insensitively. An explicit non-auto format is an assertion and must agree with the detected document format.

Exactly one of `--sbom` and `--input` is required. They cannot be combined. `--input-format` can only be used with `--input`.

Currently supported dependency input formats:

- `cyclonedx`: CycloneDX JSON
- `spdx`: SPDX JSON
- `nuget-assets`: NuGet `project.assets.json` version 3 or 4

Unsupported inputs include CycloneDX XML, SPDX tag/value, SPDX YAML, lockfiles, and package manifests. `ol` does not recursively query registries to reproduce package-manager dependency resolution; the NuGet adapter consumes the graph already fixed by `dotnet restore`.

Auto detection uses only deterministic, format-owned top-level JSON signatures; file names and extensions are not evidence. CycloneDX requires `bomFormat` equal to `CycloneDX`, SPDX requires a string `spdxVersion`, and NuGet assets requires a numeric `version` plus object-valued `targets`, `libraries`, and `project`. Every required marker for one format must match. No match is an unsupported-input error and multiple matches are an ambiguous-input error; Ol never guesses by registration order. The NuGet parser then accepts schema version 3 or 4 and rejects other versions explicitly.

`scan` is best-effort. Component-level problems must be recorded in the result and must not stop processing of other components. The command returns non-zero only when the scan itself cannot be performed or output cannot be written.

The command boundary parses every supported input through the registered dependency-input adapter and then consumes a normalized inventory. Enrichment, reconciliation, filtering, grouping, sorting, and rendering do not dispatch on CycloneDX or SPDX parser types. Explicit `--input-format` validation uses the same registry as content detection.

Examples of whole-command failures:

- dependency input cannot be read.
- input format is unsupported or does not match the input content.
- input is malformed enough that components cannot be extracted.
- SPDX data cannot be loaded.
- stdout or `--out` cannot be written.

Expected input, option, SPDX-data, and I/O failures return a non-zero exit code with a concise cause on stderr. They do not emit a runtime stack trace or partial primary output. View options are validated before enrichment starts so an invalid report request does not perform external evidence collection.

Examples of component-level problems:

- A component has an invalid license expression.
- Later versions cannot fetch package metadata for one component.
- Later versions cannot fetch source repository evidence for one component.

<a id="contract-output-formats"></a>
## Output Formats

`scan` supports these formats from v1:

- `text`
- `json`
- `markdown`

Default format is `text`.

```bash
ol scan --sbom bom.json --format text
ol scan --sbom bom.json --format json
ol scan --sbom bom.json --format markdown
```

`--out` writes the same format selected by `--format` to the given file. It does not suppress stdout.

```bash
ol scan --sbom bom.json --format markdown --out licenses.md
```

For human-readable `text` and `markdown` output, a labeled scan summary is separated from the report by a blank line and written to stderr. JSON already contains canonical summary, warning, cache, network, input, and SPDX metadata, so successful JSON output does not emit a duplicate stderr summary. This keeps redirected and interactive JSON output free from an unexpected second representation of the same information.

The human-readable input summary identifies the registered input format. It does not require the downstream scan pipeline to retain an SBOM-specific report type.

`--verbose` retains its verbose report columns and additionally writes `Detected input format: {kind}/{format}` to stderr after successful detection. The normal path does not construct this diagnostic text; logging work remains inside the verbose branch.

The primary `text` report starts with `Input: {kind}/{format}`. Markdown uses the same value as inline code. This header remains present with `--quiet`; quiet suppresses stderr summary output, not primary report metadata.

`--quiet` suppresses the human-readable stderr summary/progress output. It must not suppress the primary stdout result.

`--skip-enrichment` renders only evidence already present in the dependency input. Package-registry and source-repository collection are not scheduled, and their report metadata counters are zero. This mode exists for deterministic report-contract snapshots and for environments that intentionally prohibit external evidence collection; it is not equivalent to a full license-resolution run.

## Default Columns

Default `text` and `markdown` component output uses these columns:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS
```

Verbose output adds `PURL`:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS PURL
```

`NAME`, `VERSION`, and `LICENSE` are intentionally placed first because they are the primary review fields. `PURL` is omitted from default output because it can make rows too wide.

<a id="contract-component-status"></a>
## Component Status

All versions use the same status vocabulary:

- `matched`: available evidence yields a single valid license expression.
- `conflict`: multiple evidence sources or fields yield different valid license expressions.
- `unknown`: license information is absent, empty, `NOASSERTION`, `NONE`, `UNKNOWN`, or otherwise not available.
- `ambiguous`: license text exists but cannot be normalized to one SPDX expression without guessing.
- `invalid`: a claimed SPDX expression is syntactically invalid or references unknown SPDX identifiers.
- `error`: evidence needed for a component could not be collected or processed, and no other evidence yields a usable license result.

`unknown` and `error` are distinct. `unknown` means the tool successfully checked the source and found no usable license information. `error` means the tool could not complete an evidence-gathering operation.

If an external source fails in v2/v3 but another source gives a single valid license, the component remains `matched` and the fetch failure is recorded as warning evidence.

## License Display

For `matched`, the `LICENSE` field displays the normalized SPDX expression.

For `unknown`, it displays `-`.

For `ambiguous`, it displays the raw ambiguous value with `(?)`.

For `conflict`, it displays candidate licenses separated by comma and a final `(?)`, for example:

```text
MIT, Apache-2.0 (?)
```

The marker is display-only. JSON output preserves each claim in `licenseCandidates` and attaches its non-duplicated provenance as that candidate's typed `evidence` object.

<a id="contract-dependency-type"></a>
## Dependency Type

Reports distinguish component relationship when the SBOM contains enough information:

- `root`
- `direct`
- `transitive`
- `unknown`

The field is required in JSON and displayed in default `text` and `markdown` output. If the SBOM does not contain enough dependency graph information, the value is `unknown`.

<a id="contract-dependency-filtering"></a>
## Dependency Filtering

`--dependency` filters scan output by dependency type:

```bash
ol scan --sbom bom.json --dependency direct
ol scan --sbom bom.json --dependency root,direct
ol scan --sbom bom.json --dependency transitive
```

Allowed values are:

- `root`
- `direct`
- `transitive`
- `unknown`

When supplied, the comma-separated filter must contain at least one value.

`--dependency` is an output filter, not an analysis filter. The scan still reads the full SBOM and resolves dependency relationships before filtering. This preserves correct direct/transitive classification.

When `--dependency direct` excludes components whose dependency type is `unknown`, stderr summary must include the excluded `unknown` count. This avoids implying that the scan proved those components are not direct dependencies.

## Sorting

Default sort order is:

```text
ecosystem,name,version
```

`--sort` accepts comma-separated keys:

```bash
ol scan --sbom bom.json --sort status,ecosystem,name
```

Normal sort keys:

- `name`
- `version`
- `license`
- `ecosystem`
- `dependency`
- `status`
- `purl`

`--sort-order` applies one direction to all selected keys:

```bash
ol scan --sbom bom.json --sort status,name --sort-order desc
```

Allowed values are `asc` and `desc`. Default is `asc`.

The comma-separated `--sort` value must contain at least one key.

## Grouping

`--group-by` switches the output view from component rows to aggregate rows. It accepts one or more comma-separated output fields:

```bash
ol scan --sbom bom.json --group-by license
ol scan --sbom bom.json --group-by ecosystem,license
ol scan --sbom bom.json --group-by dependency,status
```

Groupable fields:

- `name`
- `version`
- `license`
- `ecosystem`
- `dependency`
- `status`

Grouped output includes `COUNT`. Grouped JSON output includes minimal component references for traceability. Group sort keys are the group-by fields plus `count`.

The comma-separated `--group-by` value must contain at least one key. Grouped JSON retains the same top-level canonical status summary as component JSON.

<a id="contract-json-report"></a>
## JSON Report

JSON output is the canonical machine-readable report. It includes:

- tool metadata
- input SBOM metadata
- SPDX data metadata
- network/cache metadata where applicable
- the complete dependency inventory
- component results or grouped results
- summary
- warnings

The canonical summary counts every component status, including `error`, so the status counts sum to the displayed component count. This applies to both component and grouped JSON views.

Top-level `schemaVersion` identifies the breaking report contract. Schema version 1 removes the duplicate component-level `evidence` array and makes candidate provenance subordinate to each `licenseCandidates` item. Consumers must reject or explicitly migrate unsupported schema versions rather than silently interpreting a newer report as an older shape.

The current schema v1 report emits `metadata.input` and `metadata.spdx` as separate objects. Generic input metadata contains:

- `kind`: the stable input family, currently `sbom` or `package-manager`
- `format`: the registered format name, currently `cyclonedx`, `spdx`, or `nuget-assets`
- `sourceRef`: the input basename rather than an absolute local path
- `sourceSha256`: the SHA-256 of the complete input
- `parser`: the stable parser identity
- `specificationVersion`: the source format version when present

Existing SBOM-specific fields remain additive compatibility aliases in schema v1: `sbomRef`, `sbomFormat`, `sbomSpecVersion`, and `sbomSha256`. A future non-SBOM input must not emit fabricated SBOM aliases. The SPDX metadata object records its logical data reference, License List version, and SHA-256 hashes of the active `licenses.json` and `exceptions.json` files.

Top-level `inventory` is independent of the sorted or filtered report view. It contains input-order `contexts`, lightweight component identities, `occurrences`, and `edges`. Occurrence component indexes always address `inventory.components`; they never address the displayed top-level `components` or grouped rows. An edge `fromOccurrenceIndex` of `-1` denotes the project root owned by that edge's context. Empty platform or architecture values remain empty rather than being inferred from the host.

Absolute project origins retained internally for graph attribution are rendered as basenames. Relative logical origins may be retained. Canonical output never exposes an absolute local project path.

SBOM files and SPDX data files encoded with a UTF-8 BOM are accepted.

File references in reports must not use absolute local paths. Use logical references or paths relative to the current working directory where possible. If a path cannot be safely relativized, use a basename or logical label.

SBOM input metadata includes a SHA-256 hash:

```json
{
  "input": {
    "kind": "sbom",
    "format": "cyclonedx",
    "sourceRef": "bom.json",
    "sourceSha256": "...",
    "parser": "cyclonedx-json",
    "specificationVersion": "1.6",
    "sbomRef": "bom.json",
    "sbomFormat": "CycloneDX",
    "sbomSpecVersion": "1.6",
    "sbomSha256": "..."
  }
}
```

SPDX metadata is defined by [spdx.md](spdx.md) and is required in every JSON report.

When v3 source repository enrichment is active, `metadata.sourceRepository` reports target, request, cache, error, and unknown counts. `targetCount` counts deduplicated repository/ref targets, while `unknownCount` counts components without source license evidence even when multiple components share one target. `metadata.network.githubAuth` reports only `ol_github_token` or `none`; it never includes a credential value.

`metadata.packageMetadata.targetCount` counts deduplicated versioned package targets scheduled for cache or registry lookup. Component-oriented hit, miss, and outcome counts can be larger because one shared target result is projected to every matching occurrence.

Each GitHub license candidate carries a typed `evidence` object in its `licenseCandidates` entry. It contains logical repository/ref, HTTP status, cache-key hash, and license path/SHA/key/name/URL. These provenance fields are metadata, not warnings, and never contain a cache path or token value.

Component entries include original SBOM identifiers when present:

- CycloneDX `bomRef`
- SPDX `spdxId`

v1 rejects a document that simultaneously presents CycloneDX and SPDX format markers rather than choosing a format by marker order.

Line numbers and JSON Pointers are not required in v1.

<a id="contract-report-privacy"></a>
## Privacy and Security

Reports must not contain:

- token values
- absolute local paths
- hidden cache file paths

Logical identifiers and hashes should be used where possible. Token presence may be reported as an auth mode, never as a value.

<a id="contract-policy-checks"></a>
## Future Policy Checks

Allow-list enforcement is outside v1-v3 scan scope. A later phase may add `check` or equivalent policy behavior. That phase should consume scan evidence and fail closed for allow-list misses, unknowns, conflicts, ambiguous values, and invalid license expressions.

## Lessons Learned

- JSON SBOM and SPDX files written by common Windows APIs can start with a UTF-8 BOM. Input handling must strip an optional BOM before structural detection or JSON parsing.
- Format detection must examine the complete document. Selecting the first format marker can silently misclassify a document containing both CycloneDX and SPDX markers.
- ConsoleAppFramework binds command method parameters as named options. Preserve the documented positional cache-category syntax by translating it before command dispatch.
- CLI integration tests must execute the already-built CLI DLL. Parallel `dotnet run` invocations race while replacing the shared apphost executable.
- Do not add an optional second parameter to shared `params string[]` CLI test helpers: it can capture `scan` rather than treating it as command input. Use a distinct helper for cache-aware invocation.
