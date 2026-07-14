# SPDX Data and License Semantics Specification

This document defines how `ol` uses SPDX License List data and how it interprets SPDX license identifiers and expressions.

SPDX data is foundational for all versions because license matching must be explainable and versioned. The tool should not silently depend on whatever SPDX list is current on the network at scan time.

## Data Sources

SPDX data is resolved in this order:

1. `--spdx-data <dir>`
2. User-managed data installed by `ol spdx update`
3. CLI-bundled data

The active data source must be recorded in JSON reports.

`--spdx-data <dir>` points to a directory containing:

```text
licenses.json
exceptions.json
```

These match the JSON files published by SPDX License List data. The `details/` and per-license JSON directories are not required for v1 validation.

## User Data Layout

User-managed SPDX data is stored by version:

```text
<user-data-dir>/ol/spdx/
  current.txt
  <version>/
    licenses.json
    exceptions.json
```

`current.txt` identifies the active user-managed version:

```text
3.27.0
```

The exact platform-specific user data root is not part of this spec. Reports must not emit absolute paths to it.

## Commands

### `ol spdx update`

Downloads the latest `licenses.json` and `exceptions.json` into the user-managed SPDX data store and makes that version current.

This is a user-facing command. It is distinct from any development-time tooling that refreshes CLI-bundled SPDX data.

### `ol spdx version`

Displays active, user-managed, and bundled SPDX License List versions.

Example:

```text
Active SPDX License List: 3.27.0 (user)
User SPDX License List: 3.27.0
Bundled SPDX License List: 3.26.0
```

### `ol spdx list`

Lists installed user-managed SPDX versions and identifies the active one.

### `ol spdx use <version>`

Sets an installed user-managed SPDX version as current.

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

## Candidate and Evidence Records

Each component JSON record retains the raw SBOM license values in both `licenseCandidates` and `evidence`. Each candidate includes:

- `source`, initially `sbom`
- `kind`, such as `declared`, `concluded`, `expression`, `id`, or `name`
- `raw` and normalized SPDX expression when valid
- classification `status`
- `deprecated` and candidate `warnings`

The component `warnings` array aggregates candidate warnings. This preserves unknown-like, ambiguous, invalid, and deprecated values for later evidence sources to reconcile in v2 and v3.

## SBOM Field Reconciliation

SPDX SBOMs can contain both `licenseDeclared` and `licenseConcluded`. Both are evidence.

- If valid license candidates collapse to one expression, status is `matched`.
- If valid license candidates disagree, status is `conflict`.
- Unknown-like values do not create a conflict when a valid candidate exists.

For CycloneDX, a single `expression` or a single license `id` can be `matched`. Multiple license IDs without explicit `AND`/`OR` semantics are `ambiguous`, not automatically synthesized into a license expression.
