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

- v1 scans SBOM files through the common `--input` boundary.
- v2 adds package manager and package registry metadata as automatic hints.
- v3 adds source repository license hints.
- The `check` command adds allow-list policy checks and CI failure behavior after factual evidence resolution.

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

`scan` is the primary command. It lists components and their license status from one or more resolved dependency inputs and the available evidence sources for the current version.

The input form detects a registered format from content by default:

```bash
ol scan --input bom.json
ol scan --input bom.spdx.json
ol scan --input obj/project.assets.json
ol scan --input maven-dependency-tree.json
ol scan --input src
ol scan --input src --input tests
```

`--input` is repeatable and each value may name a file or directory. Overlapping inputs are deduplicated by resolved file path, then ordered by a non-absolute logical path before parsing and graph-index projection. A single file retains the existing single-document behavior. Multiple discovered documents must all be package-manager inputs; combining SBOM evidence documents with package-manager inventories is rejected because their license-evidence reconciliation is not a path-merging operation.

Each registered input handler owns the exact file names that directory input discovers recursively. Discovery does not follow reparse points and does not determine single-document format; registered content signatures and bundle parsers remain authoritative. `nuget-assets` registers `project.assets.json`, `npm-package-lock` registers `package-lock.json`, `pnpm-lock` registers `pnpm-lock.yaml`, both Yarn handlers register `yarn.lock`, `cargo-metadata` registers `cargo-metadata.json`, `pip-inspect` registers `pip-inspect.json`, `go-module-graph` registers the companion names `go-list-modules.json` and `go-mod-graph.txt`, `composer-lock` registers the companion names `composer.json` and `composer.lock`, `bundler-lock` registers `Gemfile.lock`, and `maven-dependency-tree` registers `maven-dependency-tree.json`. Complete companion sets in the same directory are parsed as one inventory; a missing companion is an input error. A future package-manager handler becomes part of the same directory and repeated-input collection by registering its own names and package-identity comparison. A directory containing no registered names is an input error. With explicit `--input-format`, only that handler's registered names are discovered.

`--input-format` defaults to `auto`; explicitly specifying `auto` is equivalent to omitting the option. Registered format names are matched case-insensitively. An explicit non-auto format is an assertion and must agree with the detected document format.

One or more `--input` options are required. `--input-format` asserts every discovered document.

Currently supported dependency input formats:

- `cyclonedx`: CycloneDX JSON
- `spdx`: SPDX JSON
- `nuget-assets`: NuGet `project.assets.json` version 3 or 4
- `npm-package-lock`: npm `package-lock.json` lockfile version 2 or 3
- `pnpm-lock`: pnpm `pnpm-lock.yaml` lockfile version 9.0
- `yarn-classic-lock`: Yarn Classic `yarn.lock` version 1
- `yarn-berry-lock`: Yarn Berry `yarn.lock` metadata version 8
- `cargo-metadata`: `cargo metadata --format-version 1 --locked` JSON
- `go-module-graph`: paired `go list -m -json all` and `go mod graph` output
- `pip-inspect`: `python -m pip inspect --local` JSON format version 1
- `composer-lock`: paired Composer root `composer.json` and resolved `composer.lock`
- `bundler-lock`: Bundler resolved `Gemfile.lock`
- `maven-dependency-tree`: Maven Dependency Plugin 3.7.0 or later `dependency:tree` JSON

Unsupported inputs include CycloneDX XML, SPDX tag/value, SPDX YAML, package manifests, and lockfile formats without a registered adapter. `ol` does not recursively query registries to reproduce package-manager dependency resolution; package-manager adapters consume already resolved graphs.

Auto detection uses only deterministic, format-owned content signatures; file names and extensions are not evidence for single-document formats. JSON adapters use top-level property signatures. Cargo requires format version 1 plus top-level `packages`, `workspace_members`, `resolve`, `target_directory`, and `workspace_root` with their documented JSON types. pip inspect requires string format version `1`, `pip_version`, an `installed` array, and an `environment` object. Maven dependency tree JSON requires the documented root `groupId`, `artifactId`, `version`, `type`, `scope`, `classifier`, and string-valued `optional` fields; `children` may be absent for an empty tree. pnpm requires top-level `lockfileVersion` and `importers`, Yarn Classic requires the version 1 header, Yarn Berry requires top-level `__metadata`, and Bundler requires a source section plus `PLATFORMS` and `DEPENDENCIES`. Multi-file handlers first associate their complete registered companion names within one directory, then validate every document through the format-owned bundle parser; names alone cannot make malformed content valid. Composer additionally requires the lock root to contain both `packages` and `packages-dev` arrays. Every required marker for one format must match. No match is an unsupported-input error and multiple matches are an ambiguous-input error; Ol never guesses by registration order. Known formats with unsupported versions are rejected explicitly.

`scan` is best-effort. Component-level problems must be recorded in the result and must not stop processing of other components. The command returns non-zero only when the scan itself cannot be performed or output cannot be written.

The command boundary parses every supported input through the registered dependency-input adapter and then consumes a normalized inventory. Multiple package-manager inventories retain their contexts, occurrences, and edges while sharing report components according to the originating handler's package-identity comparison. Enrichment, reconciliation, filtering, grouping, sorting, and rendering do not dispatch on parser types. Explicit `--input-format` validation and directory discovery use the same registry as content detection.

For a repository containing multiple package managers, auto-detected inputs with different registered formats produce one `package-manager/collection` inventory. Context and occurrence indexes are remapped into the collection without creating edges between input graphs. Component combination is format-scoped: identical canonical purls from different formats remain distinct graph evidence, while downstream package-metadata scheduling may deduplicate the same registry cache key. An explicit non-auto `--input-format` is therefore inappropriate for a mixed-format directory.

A single repository-wide SBOM and a direct package-manager collection are alternative authoritative inputs, not layers to union. The CLI rejects SBOM/package-manager mixtures and multiple SBOM documents. Per-ecosystem SBOMs must be merged by an SBOM-aware tool before Ol scans the resulting document. CI may scan a canonical merged/polyglot SBOM and direct lockfiles as separate jobs and reports.

Examples of whole-command failures:

- dependency input cannot be read.
- input format is unsupported or does not match the input content.
- input is malformed enough that components cannot be extracted.
- SPDX data cannot be loaded.
- stdout cannot be written.

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
ol scan --input bom.json --format text
ol scan --input bom.json --format json
ol scan --input bom.json --format markdown
```

Primary output is written only to stdout and ends with a line feed in every format. Persist a report with shell redirection so the same output contract remains usable in pipelines:

```bash
ol scan --input bom.json --format markdown > licenses.md
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
ol scan --input bom.json --dependency direct
ol scan --input bom.json --dependency root,direct
ol scan --input bom.json --dependency transitive
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
ol scan --input bom.json --sort status,ecosystem,name
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
ol scan --input bom.json --sort status,name --sort-order desc
```

Allowed values are `asc` and `desc`. Default is `asc`.

The comma-separated `--sort` value must contain at least one key.

## Grouping

`--group-by` switches the output view from component rows to aggregate rows. It accepts one or more comma-separated output fields:

```bash
ol scan --input bom.json --group-by license
ol scan --input bom.json --group-by ecosystem,license
ol scan --input bom.json --group-by dependency,status
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
- `format`: the registered format name, currently `cyclonedx`, `spdx`, `nuget-assets`, `npm-package-lock`, `pnpm-lock`, `yarn-classic-lock`, `yarn-berry-lock`, `cargo-metadata`, `go-module-graph`, `pip-inspect`, `composer-lock`, `bundler-lock`, or `maven-dependency-tree`; a package-manager collection containing different formats reports `collection`
- `sourceRef`: the input file or directory basename, or `{count} inputs` for repeated input, rather than an absolute local path
- `sourceSha256`: the SHA-256 of the complete file input, or a deterministic aggregate over logical discovery paths and content hashes for directory or repeated input
- `parser`: the stable parser identity
- `specificationVersion`: the source format version when present

Existing SBOM-specific fields remain additive compatibility aliases in schema v1: `sbomRef`, `sbomFormat`, `sbomSpecVersion`, and `sbomSha256`. A future non-SBOM input must not emit fabricated SBOM aliases. The SPDX metadata object records its logical data reference, License List version, and SHA-256 hashes of the active `licenses.json` and `exceptions.json` files.

Top-level `inventory` is independent of the sorted or filtered report view. It contains input-order `contexts`, lightweight component identities, `occurrences`, and `edges`. Occurrence component indexes always address `inventory.components`; they never address the displayed top-level `components` or grouped rows. Multiple occurrences may address one component when the same package identity is resolved in more than one project, target framework, RID, workspace, or installed package path. An npm occurrence with input-supplied `dev`, `optional`, `devOptional`, `peer`, `os`, or `cpu` conditions has an additive `variant` string; occurrences without such conditions omit the field. An edge `fromOccurrenceIndex` of `-1` denotes the project or workspace root owned by that edge's context. Empty platform or architecture values remain empty rather than being inferred from the host.

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

## `ol check`

`check` is the policy-enforcement command. It runs the same dependency-input, enrichment, and reconciliation pipeline as `scan` exactly once, then evaluates the completed in-memory result. Policy evaluation does not rescan inputs, repeat registry or source collection, or change `scan` exit behavior.

The initial policy surface is limited to a required allow-list:

```text
ol check --input . --allow-licenses MIT,Apache-2.0,BSD-3-Clause
```

`--allow-licenses` is one comma-separated list of SPDX License Identifiers. Surrounding ASCII whitespace is ignored. Matching is case-insensitive and identifiers are normalized to the official casing from the active SPDX data. Empty entries, unknown identifiers, SPDX expressions, exception identifiers, natural-language names, and an empty list are invalid check options. Duplicate identifiers after normalization have no additional effect.

`check` accepts the scan controls needed to produce the completed result: `--input`, `--input-format`, `--spdx-data`, `--cache-dir`, `--refresh`, `--skip-enrichment`, `--concurrency`, `--retry`, and `--verbose`. It additionally accepts `--baseline` and `--update-baseline`, defined below. The initial command does not accept scan view or report controls such as `--dependency`, `--group-by`, `--sort`, or `--format`; policy always evaluates every component in the completed result and emits one deterministic text result.

For a component with status `matched`, the normalized SPDX expression is evaluated as a Boolean expression where an allowed license identifier is true and every other license identifier is false:

- `AND` requires both operands to be true.
- `OR` requires at least one operand to be true.
- Parentheses preserve SPDX precedence.
- `WITH` has the policy value of its base license. The exception remains part of the reported normalized expression but does not independently make a forbidden base license acceptable.

For example, with `--allow-licenses MIT,Apache-2.0`, `MIT`, `MIT AND Apache-2.0`, and `MIT OR GPL-3.0-only` pass; `MIT AND GPL-3.0-only` and `GPL-3.0-only WITH Classpath-exception-2.0` fail.

Statuses `unknown`, `conflict`, `ambiguous`, `invalid`, and `error` fail closed regardless of the candidates they contain, unless an unresolved component is acknowledged by a baseline as defined below. Evaluation collects every violation rather than stopping at the first one. Each violation identifies the component by name, version, ecosystem, and purl when available, includes the normalized expression or unresolved status, and gives the reason. Output ordering is deterministic and reports no absolute input or cache path.

`check` writes its pass result or complete violation list to stdout. Expected option, input, SPDX-data, whole-command evidence-pipeline, and output failures write a concise cause to stderr without a stack trace or partial policy result. A component-level registry or source failure remains evidence in the completed result and is evaluated as a policy violation when it leaves that component unresolved; it is not an exit-2 command failure. Exit codes are:

- `0`: every component satisfies the allow-list.
- `1`: one or more policy violations were found.
- `2`: the check could not be completed because its configuration, input, evidence pipeline, or output failed.

<a id="contract-policy-baseline"></a>

### Acknowledged unresolved components

Failing closed on unresolved evidence is correct, but on its own it makes the command unusable for an existing product. Any real dependency set contains components whose license cannot be resolved: registries with no license field, non-GitHub sources, private packages. A user cannot fix those, and an allow-list cannot silence them. The goal is not that the unresolved set is empty; it is that **the unresolved set cannot grow silently**.

A baseline records the unresolved components a reviewer has already seen and accepted, so that only newly unresolved components fail:

```text
ol check --input . --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json --update-baseline
ol check --input . --allow-licenses MIT,Apache-2.0 --baseline ol-baseline.json
```

`--baseline` names the file explicitly. Ol never discovers a baseline by convention, so the command line alone states what is acknowledged. `--update-baseline` rewrites the file as a complete snapshot of the currently acknowledgeable components; it is not an append, and it requires `--baseline`. Because the snapshot replaces the file, a previous baseline is not read while updating, and an unreadable prior file does not block the rewrite. Hand-removing an entry is therefore not durable, which is intentional: a baseline is a reviewed snapshot to be reduced by fixing evidence or extending the allow-list, not a curated list of decisions.

Updating does not suppress evaluation. `--update-baseline` writes the snapshot and then evaluates the result against it, so the same command that adopts a baseline still fails on anything the baseline cannot absorb. Adopting Ol on an existing product is therefore one command whose exit code answers the question that matters: a non-zero result is a genuine finding rather than a backlog of unresolved evidence.

A component may be acknowledged only when both of the following hold.

1. Its status is `unknown`, `ambiguous`, `conflict`, or `invalid`. Status `error` cannot be acknowledged, because a collection failure is a condition to repair rather than a policy question, and acknowledging one would freeze a transient outage into an approval. Status `matched` cannot be acknowledged, because a resolved license is a policy decision that belongs in `--allow-licenses`.
2. No license candidate on that component normalizes to an SPDX expression the active allow-list rejects.

Rule 2 is what keeps a forbidden license from being deferred. A `conflict` between `MIT` and `GPL-3.0-only` is not acknowledgeable while `GPL-3.0-only` is disallowed, so `--update-baseline` cannot silence it and the command still exits 1. This yields a two-layer guarantee that should not be overstated: a forbidden license **that Ol can identify** cannot be acknowledged at all, while one Ol cannot identify — an unnormalizable string such as `GPLv3`, which strict normalization refuses to guess — remains visible because the baseline records the raw claim. Rule 2 is evaluated when the baseline is applied, not only when it is written, so tightening `--allow-licenses` invalidates entries that a more permissive list had accepted.

An entry identifies its component by versioned purl when one exists and by ecosystem, name, and version otherwise, and always carries the readable name, version, and ecosystem so a reviewer never needs to know which identity form was used. It records the acknowledged status, the raw claims that produced it as `(source, kind, raw)` triples, and a fingerprint over that same evidence. The fingerprint is why an acknowledgement expires by itself: when a version changes, a registry corrects its metadata, or a repository's license file changes, the entry stops applying and the component fails again until it is reviewed anew. An entry applies to every component it matches; entries carry no reason field, because the file is generated and the surrounding version control already records who added it and when.

The file is JSON with a schema version, and records the Ol version and SPDX License List version that produced it as diagnostic information rather than as a compatibility requirement — a License List update must not invalidate a whole baseline, and real evidence changes are already caught by the fingerprint. Entries are ordered by ecosystem, name, version, and purl so that reordering an input produces no diff, and no generation timestamp is written so that regenerating an unchanged baseline produces no diff. Overlong raw values are truncated in the file and marked as truncated, while the fingerprint covers the untruncated value. Report privacy applies unchanged: a baseline is committed to a repository and must not contain tokens or absolute local paths.

Whenever a baseline is supplied, `check` reports how many components it acknowledged, including zero, so a passing run never hides the existence of a baseline and a baseline that stopped applying is visible. Acknowledged components remain in the scan result with their original unresolved status; acknowledgement removes a violation, it does not alter evidence or reconciliation. A missing, malformed, or schema-incompatible baseline is a command failure with exit code 2 rather than a silently empty baseline, so a mistyped path is reported instead of changing which components fail.

<a id="contract-policy-report-input"></a>

### Evaluating a persisted report

`--report <file>` evaluates a previously written JSON scan report instead of scanning an input:

```text
ol scan --input . --format Json > ol-report.json
ol check --report ol-report.json --allow-licenses MIT,Apache-2.0
```

The canonical JSON report is the input contract; Ol does not define a second persistence schema. One document means a report a user already has is directly usable as policy input, and an output schema and an input schema cannot drift apart. The report schema version is validated on read, and an unsupported version, a malformed document, or a grouped report produced with `--group-by` is a command failure with exit code 2 rather than a partial evaluation.

Report evaluation performs no input parsing, no registry or repository collection, and no network access. Separating policy from collection this way means a policy can be re-run, or a different policy applied, without the result depending on what a registry happened to answer at that moment. `--report` therefore cannot be combined with `--input`, `--input-format`, `--refresh`, `--skip-enrichment`, or `--cache-dir`; combining them is a configuration error. `--baseline`, `--update-baseline`, `--spdx-data`, and `--sarif` still apply.

Active SPDX data still normalizes the allow-list, so a report may be evaluated under different SPDX data than produced it. The report's own recorded License List version is reported under `--verbose` rather than enforced, for the same reason a baseline records it without enforcing it: a data refresh must not invalidate an existing artifact.

<a id="contract-policy-sarif"></a>

### SARIF output

`--sarif <file>` additionally writes violations as SARIF 2.1.0 for CI code scanning. The text result on stdout is unchanged, and both carry the same violation set: SARIF is another projection of one evaluation, never a filter. Acknowledged components are absent from both.

Each violation kind has a stable rule ID so annotations remain comparable across runs: `OL0001` not allowed, `OL0002` evidence conflict, `OL0003` unresolved, `OL0004` ambiguous, `OL0005` invalid expression, `OL0006` evidence error.

Ol reads resolved graphs rather than manifests, so a violation has no trustworthy file position and Ol does not invent one. Results carry a logical location plus the component's purl, ecosystem, status, license, and dependency classification. When the graph is available, a result also carries the deterministic shortest root-to-component dependency path, and the message names it. That path is the actionable part of a transitive violation: it identifies the direct dependency to upgrade or remove, which is the only thing the user can change. A canonical persisted report carries the complete inventory and graph, so evaluating it preserves the same dependency path without re-reading the original dependency input.

Policy files, deny-lists, per-package policy exceptions, license curation and concluded licenses, and dependency-scope policy remain outside the `check` scope.

<a id="contract-diff"></a>

## `ol diff`

`diff` compares two persisted JSON scan reports and reports only license-relevant change:

```text
ol diff --previous before.json --current after.json --allow-licenses MIT
```

A full report is too large to review by hand on every change, and most of what changes between two runs is not license-relevant. The diff exists so a reviewer can see what actually changed about licensing, and so a genuine policy transition is separated from ordinary registry or repository drift.

Components are identified by ecosystem and name so that a version bump reads as a change rather than as an unrelated removal and addition. Change kinds are `added`, `removed`, `version-changed`, `status-changed`, `license-changed`, `evidence-changed`, and, when `--allow-licenses` is supplied, `policy-changed`. `evidence-changed` reports that the underlying claims moved while the conclusion held, which is what distinguishes a real change of fact from a change of wording; it is derived from the same evidence fingerprint the baseline uses.

Output is `--format Text` or `--format Json`, ordered by component name and change kind so identical inputs produce identical output. `diff` reports rather than enforces, so it exits `0` whenever both reports could be read and `2` when either could not. Policy enforcement stays in `check` so an exit code keeps one meaning.

## Lessons Learned

- JSON SBOM and SPDX files written by common Windows APIs can start with a UTF-8 BOM. Input handling must strip an optional BOM before structural detection or JSON parsing.
- Format detection must examine the complete document. Selecting the first format marker can silently misclassify a document containing both CycloneDX and SPDX markers.
- ConsoleAppFramework binds command method parameters as named options. Preserve the documented positional cache-category syntax by translating it before command dispatch.
- A baseline fingerprint must not depend on the order in which license candidates were appended. Enrichment appends candidates in pipeline order, which is an implementation detail, while the fingerprint is persisted in user repositories. Sorting the claims before hashing keeps a future evidence source or a reordered enrichment phase from silently invalidating every existing baseline.
- Acknowledgement needed no new component status. Removing a violation while leaving the component's unresolved status and evidence untouched preserves the evidence contract and keeps the `check` exit-code surface unchanged, which is what made the whole feature fit behind two options.
- `LicenseStatus.Unknown` must be the zero value of the enum. Every input parser builds components itself, and several derive a component's status from a candidate that is `default` when the input declares no license. While `Matched` was the zero value, those packages were reported as resolved with an empty license expression: a silent false negative in exactly the case a compliance tool exists to catch, and one that `check` could then neither explain nor let a user acknowledge. Explicit enum values pin the safe default, and a cross-parser test asserts no component is ever `Matched` without a license.
- Inventory component indexes and top-level report component indexes are different contracts. The inventory remains in input order while the report view may be sorted or filtered, so persisted-report consumers must restore the graph and resolve a displayed component back to its inventory identity before following occurrences or edges. Reusing a report-view index as an inventory index can silently attribute a violation to the wrong dependency path.
- CLI integration tests must execute the already-built CLI DLL. Parallel `dotnet run` invocations race while replacing the shared apphost executable.
- Do not add an optional second parameter to shared `params string[]` CLI test helpers: it can capture `scan` rather than treating it as command input. Use a distinct helper for cache-aware invocation.
