# v1 Implementation Plan

This plan describes a language-agnostic implementation path for v1. v1 provides SBOM-only license scanning, SPDX data management, output formatting, grouping, sorting, and report metadata. It does not fetch package metadata, fetch source repository evidence, or enforce allow-lists.

## Goals

- Implement `ol scan --sbom <path>` for CycloneDX JSON and SPDX JSON.
- Implement `text`, `json`, and `markdown` output formats.
- Implement `--out`, `--verbose`, `--group-by`, `--sort`, and `--sort-order`.
- Implement SPDX data resolution and `ol spdx` user commands.
- Produce deterministic, privacy-conscious reports with hashes and without absolute local paths.
- Continue through component-level issues and return non-zero only for scan-level failures.

## Non-Goals

- No package manager or registry metadata fetch.
- No source repository license fetch.
- No evidence cache beyond user-managed SPDX data.
- No allow-list or policy enforcement.
- No CycloneDX XML, SPDX tag/value, SPDX YAML, lockfile, or manifest input.

## Architecture Slices

Build v1 around these language-neutral modules or equivalent boundaries:

- CLI argument parsing and command dispatch.
- File input, safe path reference, and SHA-256 hashing.
- SBOM format detection.
- CycloneDX JSON reader.
- SPDX JSON reader.
- Internal component model.
- SPDX data loader and expression validator.
- License evidence reconciler.
- Dependency type resolver.
- Sort and grouping engine.
- Text, Markdown, and JSON renderers.
- Stderr summary and warning renderer.
- `ol spdx` user data manager.

The implementation may combine these boundaries physically, but tests should be able to exercise them independently.

## Internal Data Contracts

Use stable internal records before rendering output.

### Scan Metadata

Fields:

- tool name and version
- scan timestamp, if included
- input SBOM logical reference
- input SBOM SHA-256
- detected SBOM format
- SBOM spec version, when available
- active SPDX source
- active SPDX license list version
- SPDX data logical reference
- SPDX `licenses.json` SHA-256
- SPDX `exceptions.json` SHA-256

Absolute paths must not be stored in report objects. The implementation may use absolute paths internally but must convert them before report creation.

### Component Model

Fields:

- stable component id
- name
- version
- purl, optional
- ecosystem, optional or `-` in rendered output
- dependency type: `root`, `direct`, `transitive`, or `unknown`
- status: `matched`, `conflict`, `unknown`, `ambiguous`, `invalid`, or `error`
- display license
- source SBOM identifier: CycloneDX `bomRef` or SPDX `spdxId`
- license candidates
- evidence records
- warnings

### License Candidate

Fields:

- evidence source, initially `sbom`
- kind, such as `declared`, `concluded`, `expression`, `id`, or `name`
- raw value
- normalized expression, if valid
- status for this candidate
- deprecated SPDX flag
- warnings

## CLI Behavior

### `ol scan`

Required:

```bash
ol scan --sbom bom.json
```

Options:

- `--format text|json|markdown`, default `text`
- `--out <path>`
- `--verbose`
- `--dependency <root|direct|transitive|unknown[,..]>`
- `--group-by <field[,field...]>`
- `--sort <field[,field...]>`, default `ecosystem,name,version`
- `--sort-order asc|desc`, default `asc`
- `--spdx-data <dir>`
- `--quiet`, reserved if implemented in v1; it must suppress stderr summary/progress only

Validation:

- Missing `--sbom` is a command error.
- Unknown format, dependency value, sort key, group key, or sort order is a command error.
- `--spdx-data` must point to a directory containing `licenses.json` and `exceptions.json`.

### `ol spdx`

Commands:

- `ol spdx update`
- `ol spdx version`
- `ol spdx list`
- `ol spdx use <version>`
- `ol spdx clear`

Behavior:

- `update` downloads `licenses.json` and `exceptions.json`, reads the license list version, stores them under the user data SPDX directory, and updates `current.txt`.
- `version` prints active, user, and bundled versions.
- `list` prints installed user-managed versions and marks the active version.
- `use` switches `current.txt` to an installed version.
- `clear` removes user-managed SPDX data and leaves bundled data as fallback.

## SPDX Data Resolution

Resolution order:

1. `--spdx-data <dir>`
2. user-managed `ol spdx update` data
3. bundled data

Load both files:

- `licenses.json`
- `exceptions.json`

Build lookup tables:

- license identifier by case-insensitive key
- exception identifier by case-insensitive key
- deprecated flags
- license list version

Failure to initialize active SPDX data is a scan-level failure.

## SBOM Detection

Detect by JSON structure:

- CycloneDX: `bomFormat == "CycloneDX"` or CycloneDX-specific `components` layout.
- SPDX: `spdxVersion` and `packages`.

If both or neither match, fail with an unsupported or ambiguous SBOM format error. Do not guess from file extension alone.

## CycloneDX JSON Reader

Extract:

- BOM spec version
- root component from `metadata.component`, if present
- components from `components`
- component `bom-ref`
- name
- version
- purl
- type, if useful for root classification
- licenses array
- dependency graph from `dependencies`, if present

License extraction:

- A single `expression` is a raw SPDX expression candidate.
- A single `license.id` is an SPDX identifier candidate.
- A single `license.name` without ID is ambiguous unless it is a valid SPDX identifier after strict validation.
- Multiple license IDs without explicit expression semantics become `ambiguous`; do not synthesize `AND` or `OR`.
- Missing or empty license data becomes `unknown` evidence.

Dependency extraction:

- Mark `metadata.component` as `root` when identifiable.
- If dependency graph links root to a component directly, mark `direct`.
- If reachable from root but not direct, mark `transitive`.
- If graph is absent or insufficient, mark `unknown`.

## SPDX JSON Reader

Extract:

- SPDX document version
- packages
- package SPDX ID
- package name
- package version
- package download location, external refs, and purl external refs where present
- `licenseDeclared`
- `licenseConcluded`
- relationships for dependency type inference

License extraction:

- Treat both `licenseDeclared` and `licenseConcluded` as evidence.
- `NOASSERTION`, `NONE`, `UNKNOWN`, empty, and missing values are unknown-like evidence.
- Unknown-like evidence does not conflict with valid evidence.
- Valid candidates that differ create `conflict`.

Dependency extraction:

- Use `DESCRIBES`, `DEPENDS_ON`, and `DEPENDENCY_OF` relationships when present.
- Mark described project/package as `root` when clear.
- Derive `direct` and `transitive` from relationship graph when possible.
- Otherwise use `unknown`.

## Ecosystem Detection

Derive ecosystem primarily from purl type:

- `npm`
- `nuget`
- `cargo`
- `golang`
- `maven`
- other purl types as raw type values when recognized

If purl is missing or unparseable, use `unknown` internally and `-` in text/markdown rendering.

## SPDX Expression Validation

Implement a parser/validator that recognizes:

- SPDX license identifiers
- SPDX exception identifiers
- `AND`
- `OR`
- `WITH`
- parentheses

Identifier matching is case-insensitive, but normalized output uses official casing.

Classify values:

- valid expression: `matched`
- unknown-like values: `unknown`
- natural language or loose aliases: `ambiguous`
- syntactically malformed SPDX expression or unknown SPDX ID in expression context: `invalid`

Deprecated valid identifiers remain matched but add warning evidence.

Do not implement alias mapping such as `Apache License` to `Apache-2.0` in v1.

## Evidence Reconciliation

For each component:

1. Collect raw SBOM evidence.
2. Classify each candidate.
3. Remove unknown-like candidates from conflict consideration, but keep them in evidence.
4. If there is exactly one unique valid normalized expression, status is `matched`.
5. If there are multiple unique valid normalized expressions, status is `conflict`.
6. If there are invalid candidates and no valid candidates, status is `invalid`.
7. If there are ambiguous candidates and no valid candidates, status is `ambiguous`.
8. If there are only unknown-like candidates or no candidates, status is `unknown`.

Rendered license:

- `matched`: normalized expression
- `conflict`: valid candidate expressions joined by `, ` plus ` (?)`
- `ambiguous`: raw value plus ` (?)`
- `invalid`: raw value plus ` (?)` or `-` if no safe display value exists
- `unknown`: `-`
- `error`: `-` unless a partial value is useful

## Sorting and Grouping

Dependency filtering:

- Accept one or more values from `root`, `direct`, `transitive`, and `unknown`.
- Apply the filter after SBOM parsing, license reconciliation, and dependency type inference.
- Do not use `--dependency` to skip dependency graph analysis.
- If filtering excludes `unknown` dependency components, record the excluded count for stderr summary.
- Keep JSON, text, and markdown outputs consistent: filtered component output contains only selected dependency values, and grouped output groups only the filtered result set.

Apply component sorting before rendering component views.

Default sort:

```text
ecosystem,name,version
```

Sort keys:

- `name`
- `version`
- `license`
- `ecosystem`
- `dependency`
- `status`
- `purl`

Sort order applies to all keys.

Grouping:

- Accept one or more group keys from `name`, `version`, `license`, `ecosystem`, `dependency`, and `status`.
- Produce group rows with key fields and `COUNT`.
- For JSON grouped output, include minimal component refs: name, version, ecosystem, and purl.
- Group sort keys are group fields plus `count`.

## Renderers

### Text

Default columns:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS
```

Verbose columns:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS PURL
```

Use stable column alignment. Avoid wrapping assumptions in tests; verify field order and values.

### Markdown

Render a Markdown table with the same columns as text. Escape pipe characters in values if any occur. The license conflict marker uses comma and `(?)`, not `|`.

### JSON

Render canonical report objects with metadata, components or groups, summary, and warnings. Do not include stderr summary text inside JSON output.

## Stderr Summary

Write summary to stderr after primary stdout output.

Include:

- component count
- count by status
- warning count
- deprecated SPDX identifier count
- dependency-filtered component count, when `--dependency` is used
- excluded `unknown` dependency count, when applicable
- SBOM logical reference and format
- active SPDX version and source
- output file notice, if `--out` is used

`--quiet`, if implemented, suppresses this summary but not stdout.

## Error Handling and Exit Codes

Exit `0` when scan completes and output is written, even if components have `unknown`, `ambiguous`, `invalid`, `conflict`, or component-level `error` statuses.

Exit non-zero for scan-level failures:

- invalid CLI arguments
- missing or unreadable SBOM
- unsupported or malformed SBOM preventing component extraction
- SPDX data initialization failure
- output write failure

## Test Plan

### Unit Tests

- SPDX data loader resolution order.
- SPDX expression parser: valid identifiers, case normalization, exceptions, `AND`, `OR`, `WITH`, parentheses.
- Unknown-like values: empty, missing, `NOASSERTION`, `NONE`, `UNKNOWN`.
- Ambiguous values: `BSD`, `Apache License`, `MIT/Apache`.
- Deprecated identifier warning.
- CycloneDX license extraction variants.
- SPDX `licenseDeclared` and `licenseConcluded` reconciliation.
- Dependency type inference with and without graphs.
- Dependency filtering after dependency type inference, including excluded `unknown` warning counts.
- Sort and group behavior.
- Path reference redaction and SHA-256 generation.

### Integration Tests

- `ol scan --sbom cyclonedx.json --format text`.
- `ol scan --sbom cyclonedx.json --format markdown`.
- `ol scan --sbom spdx.json --format json`.
- `ol scan --sbom bom.json --out report.json` writes file and stdout.
- `ol scan --sbom bom.json --group-by ecosystem,license --format json`.
- `ol scan --sbom bom.json --dependency direct` filters output and reports excluded unknown dependency count.
- `ol scan --sbom bom.json --sort status,name --sort-order desc`.
- `ol spdx update`, `version`, `list`, `use`, and `clear` using a controlled test data directory.

### Golden Files

Maintain small SBOM fixtures for:

- matched license
- unknown license
- ambiguous license
- invalid expression
- conflict between SPDX declared and concluded
- CycloneDX multiple license IDs ambiguous case
- dependency graph direct/transitive/unknown cases

Golden outputs should avoid absolute paths and use stable timestamps or normalized placeholders.

## Implementation Milestones

1. CLI skeleton and `scan` argument validation.
2. SPDX bundled data loading and validator.
3. `ol spdx` user data commands.
4. SBOM format detection.
5. CycloneDX JSON reader.
6. SPDX JSON reader.
7. Internal component model and reconciliation.
8. Dependency inference.
9. Sort, group, and renderers.
10. Report metadata and privacy checks.
11. Integration tests and golden outputs.

## Implementation Status

**Functional status: complete (2026-07-14).** The v1 scan flow supports CycloneDX JSON and SPDX JSON, the documented output formats and scan options, SPDX source resolution, structured component evidence, dependency classification, grouping, sorting, privacy-safe JSON metadata, and the SPDX user commands.

The current implementation uses `current.txt` as the active user-managed SPDX version pointer. This is a deliberate simplification of the originally proposed `current.json` shape because the current runtime only needs the selected version; reports retain the version and logical data reference separately.

The following verification debt remains outside the functional v1 implementation and should be addressed when expanding test assets:

- Golden SBOM fixtures and golden rendered reports are not yet maintained as checked-in files.
- SPDX update/version/list/use/clear controlled integration tests are not yet present.
- The plan's full test matrix is represented primarily by focused scanner and CLI integration tests rather than a one-fixture-per-row suite.

## Lessons Learned

- UTF-8 BOMs occur in SBOM and SPDX JSON written by common Windows APIs. Parsers must strip an optional BOM before structural detection or JSON loading.
- SBOM format detection must scan the complete document. Selecting the first marker silently misclassifies documents that contain both CycloneDX and SPDX markers.
- Raw unknown-like values and deprecated identifiers must be retained as evidence even when they do not determine the rendered license. This stable candidate/evidence boundary is required for v2 metadata and v3 source evidence.
- CLI integration tests must execute the already-built CLI DLL. Parallel `dotnet run` invocations race while regenerating the shared apphost executable.
