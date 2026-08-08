# SPDX Data and License Semantics Specification

This document defines how `ol` uses SPDX License List data and how it interprets SPDX license identifiers and expressions.

SPDX data is foundational for all versions because license matching must be explainable and versioned. The tool should not silently depend on whatever SPDX list is current on the network at scan time.

## Design Basis

This specification derives from the [Ol architecture](../Architecture.md), especially the decisions to [normalize only against versioned SPDX data](../Architecture.md#decision-versioned-spdx), [prefer explicit SPDX selection while retaining an offline fallback](../Architecture.md#decision-spdx-resolution), [preserve evidence instead of selecting a single authoritative source](../Architecture.md#decision-evidence-preservation), and [separate factual resolution from organizational policy](../Architecture.md#decision-policy-separation).

Those decisions require normalization to be reproducible and explainable rather than heuristic. Therefore Ol records the active SPDX source and hashes, restores official casing, retains raw claims, and distinguishes `unknown`, `ambiguous`, `invalid`, `conflict`, and deprecated-but-valid identifiers. A normalized `matched` result establishes what the evidence says; it does not establish that organizational policy permits the license.

## Development-Time Bundled Data Generation

`Ol.Update` is a development-time external generator, not an `ol` runtime dependency. Running `ol-update generate` refreshes the bundled SPDX snapshot used as the offline fallback. This keeps the published CLI independent from the generator and network while retaining versioned, reproducible data.

<a id="contract-spdx-data-resolution"></a>
## Data Sources

SPDX data is resolved in this order:

1. `--spdx-data <dir>`
2. User-managed data selected by `ol spdx use`
3. CLI-bundled data

The active data source must be recorded in JSON reports.

`--spdx-data <dir>` points to a directory containing:

```text
licenses.json
exceptions.json
```

These match the JSON files published by SPDX License List data. The `details/` and per-license JSON directories are not required for v1 validation.

## User-Managed Data

User-managed SPDX data is stored by version. Installation and selection are separate operations: `ol spdx update` installs data without changing the selection, while `ol spdx use` explicitly selects an installed version. The active selection persists across commands until it is changed, switched back to bundled data, or cleared.

The exact platform-specific user data root is not part of this spec. Reports must not emit absolute paths to it.

<a id="contract-spdx-commands"></a>
## Commands

### `ol spdx update`

Downloads the latest `licenses.json` and `exceptions.json` into the user-managed SPDX data store. Installation does not change the active selection; `ol spdx use <version>` activates the installed version explicitly.

```text
installed: 3.27.0
```

This is a user-facing command. It is distinct from `ol-update generate`, the development-time tool that refreshes generated bundled SPDX data.

### `ol spdx version`

Displays the effective active version and source, the selected user-managed version, and the bundled version. It does not display the platform-specific user data path.

Example:

```text
active: 3.27.0 (user)
user-selected: 3.27.0
bundled: 3.26.0
```

When no user-managed version is selected, `user-selected` is `none` and the bundled version is active.

### `ol spdx list`

Lists the bundled version and all valid installed user-managed versions. Every entry is identified by version and source, and the effective active entry is prefixed with `*`.

Example:

```text
* 3.26.0 (bundled)
  3.27.0 (user)
```

Bundled and user-managed data with the same version remain separate entries because their sources and selection state differ.

### `ol spdx use <version>`

Sets a valid installed user-managed SPDX version as current. The argument is a version identifier, not a directory or path. A valid installation is an immediate child of the user data root whose `licenses.json` and `exceptions.json` exist and whose declared License List version matches the directory name.

`ol spdx use bundled` clears the user-managed selection without deleting installed versions and makes the bundled version active.

Successful selection reports the effective version and source, for example `active: 3.27.0 (user)` or `active: 3.26.0 (bundled)`.

### `ol spdx clear`

Removes user-managed SPDX data. After clearing, scans fall back to bundled SPDX data unless `--spdx-data` is supplied.

## Report Metadata

Every JSON scan report includes active SPDX data metadata:

```json
{
  "spdx": {
    "source": "cli-argument | user | bundled",
    "licenseListVersion": "3.27.0",
    "dataRef": "ol/spdx/3.27.0",
    "licensesSha256": "...",
    "exceptionsSha256": "..."
  }
}
```

`dataRef` is a logical reference, not an absolute path. Examples:

- `ol/spdx/<version>` for user-managed data
- `bundled/spdx/<version>` for bundled data
- `cli-argument` for `--spdx-data`

<a id="contract-spdx-normalization"></a>
## License Identifiers and Expressions

`ol` validates SPDX License Identifiers, SPDX License Exception Identifiers, and SPDX License Expressions using the active SPDX data.

Valid examples:

```text
MIT
Apache-2.0
BSD-3-Clause
MIT OR Apache-2.0
GPL-2.0-only WITH Classpath-exception-2.0
```

SPDX identifier and exception matching is case-insensitive, but normalized output uses official SPDX casing.

Examples:

```text
mit -> MIT
apache-2.0 -> Apache-2.0
classpath-exception-2.0 -> Classpath-exception-2.0
```

This is not alias guessing. Natural language names and loose aliases are not normalized automatically.

<a id="contract-strict-normalization"></a>
## Strict Normalization

v1 normalization is intentionally strict.

Valid SPDX identifiers or expressions become `matched` and are normalized to official casing.

Examples that remain ambiguous:

```text
Apache License
BSD
GPL
LGPL
MIT/Apache
Dual licensed
SEE LICENSE IN LICENSE
Custom
Commercial
Freeware
```

The tool must not guess that these mean a specific SPDX expression. Later evidence from package metadata or source repository hints may improve confidence, but the original ambiguous evidence remains recorded.

## Unknown Values

These values are treated as `unknown`:

- empty or missing license fields
- `NOASSERTION`
- `NONE`
- `UNKNOWN`

`NONE` is not treated as safe or matched. It is grouped with `unknown` because its policy meaning is difficult and should not be silently accepted.

The raw value should remain in JSON evidence.

## Deprecated Identifiers

If an SPDX identifier exists in the active SPDX data but is deprecated, the component may still be `matched`, but the report records a warning.

Example candidate warning:

```json
{
  "raw": "GPL-2.0",
  "normalized": "GPL-2.0",
  "deprecated": true,
  "warnings": ["deprecated_spdx_identifier"]
}
```

stderr summary should include deprecated identifier warning counts.

<a id="contract-candidate-evidence"></a>
## Candidate and Evidence Records

Each component JSON record retains every license claim once in `licenseCandidates`. Each candidate includes:

- `source`
- `kind`, such as `declared`, `concluded`, `expression`, `id`, or `name`
- `raw` and normalized SPDX expression when valid
- classification `status`
- `deprecated` and candidate `warnings`
- one typed `evidence` object describing the provenance that is not already represented by the candidate

The former component-level `evidence` array duplicated `licenseCandidates` and is removed by JSON report schema version 1. Evidence is now subordinate to the claim it substantiates:

Audit evidence is a traceable observation, not an independent license conclusion. It identifies the input field or collected record from which the candidate was derived closely enough for a reviewer to locate and re-check it. Candidate fields hold the observed claim and Ol's interpretation; nested evidence holds only non-duplicated provenance needed to audit that claim.

- SBOM evidence records the exact source field. CycloneDX license `acknowledgement` is retained only when explicitly present; SPDX `licenseDeclared` and `licenseConcluded` are identified by field and are not relabeled as CycloneDX acknowledgements.
- Package registry evidence records the opaque cache-key hash and collection timestamp when known.
- Source repository evidence records the logical repository/ref, collection status, opaque cache-key hash, and detected license-file metadata when known.

`acknowledgement: declared|concluded` records the producer's assertion semantics. It is not a verified attestation. CycloneDX `declarations.attestations`, BOM signatures, SPDX annotations, and package verification codes have broader document, conformance, or identity semantics and must not be projected onto a license candidate without an explicit relationship and a recorded verification result. Ol does not emit an inferred `attested` boolean.

CycloneDX observed license collections under `component.evidence.licenses` are not flattened into independent reconciled candidates yet. A list of observed licenses does not state the AND/OR relationship needed to compare it safely with a concluded expression. Preserving that group relationship is required before Ol can use it without manufacturing false conflicts or conclusions. [Expression agreement](#contract-expression-agreement) supplies the comparison this needs for a single expression; what remains is deciding what an unordered list without a stated relationship may claim.

The component `warnings` array aggregates candidate warnings. This preserves unknown-like, ambiguous, invalid, and deprecated values for later evidence sources to reconcile in v2 and v3.

## SBOM Field Reconciliation

SPDX SBOMs can contain both `licenseDeclared` and `licenseConcluded`. Both are evidence.

- If valid license candidates collapse to one expression, status is `matched`.
- If valid license candidates disagree, status is `conflict`.
- Unknown-like values do not create a conflict when a valid candidate exists.

<a id="contract-expression-agreement"></a>

Two valid expressions collapse to one when neither offers an option the other withdraws. Ol decides that by comparing their **top-level disjunct sets**: the exact normalized text between top-level `OR` operators. When one set contains the other, the two agree and the wider offer is the reconciled expression. Otherwise they disagree and the status is `conflict`.

A disjunction states a choice the publisher offers, not a claim that every option applies at once. Repository license detection answers with the one file it found at the repository root, so it names one option out of several by construction. Reading that as disagreement makes every dual-licensed package a conflict, which is the ordinary case in some ecosystems, and leaves a scan that collected more evidence worse off than one that collected none. That outcome contradicts the reason Ol collects from several sources at all.

The reconciled value keeps the wider offer rather than the narrower observation, because nothing withdrew the other options. It is also the safe direction for policy: `OR` is permissive in an allow-list, so an allow-list permitting only the unobserved option still passes, whereas narrowing to the observed option would reject it.

The comparison is deliberately shallow and does not interpret anything inside a disjunct. A conjunction, a parenthesized group, and a `WITH` exception are each compared whole. Consequently an expression whose top level is `AND` has exactly one disjunct, itself: `(MIT OR Apache-2.0) AND Unicode-3.0` is not satisfied by `Apache-2.0`, because distributing the conjunction would drop a required term. Relations Ol cannot decide this way remain `conflict` rather than becoming a guess. Equal sets written in a different order agree, and the reconciled value is the spelling of the first candidate in evidence order, which keeps the result deterministic.

For CycloneDX, a single `expression` or a single license `id` can be `matched`. Multiple license IDs without explicit `AND`/`OR` semantics are `ambiguous`, not automatically synthesized into a license expression.

## Lessons Learned

- Comparing normalized expressions as text made collecting more evidence produce a worse result. Measured against a Rust project, adding registry and repository evidence to a lockfile scan turned 164 resolved components into 76 resolved and 94 conflicting, because `MIT OR Apache-2.0` and an observed `Apache-2.0` differ as strings while agreeing as offers. Reconciliation across sources needs a relation between expressions, not equality.
- The shallow relation was enough. Of those 94 conflicts, 93 were one expression appearing as a top-level disjunct of the other, and the single remaining one had a top-level `AND` that must not be distributed. Interpreting disjunct internals would have added risk without adding coverage.
- `Ol.Update` remains a development-time generator. The Native AOT CLI consumes generated SPDX lookup data through `Ol.Core` and must not acquire a runtime dependency on the generator.
- Installing and selecting user-managed SPDX data are separate operations. Explicit selection prevents a network refresh from silently changing the version used by later scans.
- A version argument must resolve through the validated installation inventory rather than through path combination. Treating an arbitrary directory name as a version can escape the managed data root and can make command output disagree with the data a scan actually resolves.
