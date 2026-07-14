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

`ol` evolves by widening the evidence sources used by `scan`.

- v1 scans SBOM files only.
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

`package-metadata` and `all` clear the persistent package metadata cache. `source-repository` is accepted as a reserved no-op until v3 activates that cache.

<a id="contract-scan-failures"></a>
### `ol scan`

`scan` is the primary command. It lists components and their license status from the available evidence sources for the current version.

v1 accepts SBOM input only:

```bash
ol scan --sbom bom.json
```

Supported v1 SBOM formats:

- CycloneDX JSON
- SPDX JSON

Unsupported v1 inputs include CycloneDX XML, SPDX tag/value, SPDX YAML, lockfiles, and package manifests. Input format is detected from the file content rather than requiring a format flag.

`scan` is best-effort. Component-level problems must be recorded in the result and must not stop processing of other components. The command returns non-zero only when the scan itself cannot be performed or output cannot be written.

Examples of whole-command failures:

- SBOM file cannot be read.
- SBOM format is unsupported.
- SBOM is malformed enough that components cannot be extracted.
- SPDX data cannot be loaded.
- stdout or `--out` cannot be written.

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

Summary, warnings, progress, and output notices are written to stderr, not stdout. This keeps stdout valid for the selected format, especially JSON.

`--quiet` is reserved for suppressing stderr summary/progress output. It must not suppress the primary stdout result.

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

The marker is display-only. JSON output must preserve candidates and evidence separately.

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

<a id="contract-json-report"></a>
## JSON Report

JSON output is the canonical machine-readable report. It includes:

- tool metadata
- input SBOM metadata
- SPDX data metadata
- network/cache metadata where applicable
- component results or grouped results
- summary
- warnings

The current v1 report emits `metadata.input` and `metadata.spdx` as separate objects. `metadata.input.sbomRef` is the input basename, rather than an absolute local path. The SPDX object records its logical data reference, License List version, and SHA-256 hashes of the active `licenses.json` and `exceptions.json` files.

SBOM files and SPDX data files encoded with a UTF-8 BOM are accepted.

File references in reports must not use absolute local paths. Use logical references or paths relative to the current working directory where possible. If a path cannot be safely relativized, use a basename or logical label.

SBOM input metadata includes a SHA-256 hash:

```json
{
  "input": {
    "sbomRef": "bom.json",
    "sbomFormat": "CycloneDX",
    "sbomSpecVersion": "1.6",
    "sbomSha256": "..."
  }
}
```

SPDX metadata is defined by [spdx.md](spdx.md) and is required in every JSON report.

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
