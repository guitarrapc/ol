# v2 Implementation Plan

This plan describes a language-agnostic implementation path for v2. v2 builds on v1 by automatically adding package manager and package registry metadata as license evidence.

## Goals

- Preserve all v1 CLI behavior and report fields.
- Automatically derive package metadata lookup targets from SBOM component purl values.
- Add metadata fetchers for npm, NuGet, Cargo, and Go modules.
- Add persistent package metadata cache.
- Add `--refresh`, `--concurrency`, `--retry`, and `ol cache clear` behavior.
- Keep scan best-effort and component-scoped for metadata failures.

## Non-Goals

- No manual `--package-manager`, `--assume-ecosystem`, or `--hint package-manager` UX.
- No source repository license fetching; that is v3.
- No Maven support in initial v2.
- No allow-list or policy enforcement.
- No cache pruning behavior.

## Preconditions

v2 depends on v1 foundations:

- internal component model
- purl and ecosystem fields
- SPDX expression validation
- evidence reconciliation
- JSON report model
- best-effort error handling
- output rendering, grouping, and sorting

## New Architecture Slices

Add these boundaries or equivalents:

- Package metadata planner.
- Package metadata fetcher interface.
- npm metadata fetcher.
- NuGet metadata fetcher.
- Cargo metadata fetcher.
- Go module metadata fetcher.
- Package metadata cache store.
- Fetch scheduler with concurrency and retry policy.
- Metadata evidence normalizer.
- Cache command handling.

## CLI Changes

### `ol scan`

The same command automatically gathers package metadata where supported:

```bash
ol scan --sbom bom.json
```

New v2 options:

- `--refresh`
- `--concurrency <n>`
- `--retry <n>`

Semantics:

- `--refresh` ignores package metadata cache and overwrites it with fresh results.
- `--concurrency 1` runs fetches sequentially.
- `--retry 0` disables retries.
- default retry count is 1 retry, meaning 2 total attempts.

### `ol cache clear`

Add evidence cache clearing:

```bash
ol cache clear
ol cache clear package-metadata
ol cache clear source-repository
ol cache clear all
```

In v2, `source-repository` may be accepted as a no-op or reserved category for v3, but it must not fail unexpectedly if documented for the shared cache command. If strict category validation is preferred, document that `source-repository` becomes active in v3.

## Package Metadata Planning

For every component:

1. Read purl.
2. Parse purl into type, namespace, name, version, and qualifiers.
3. Map purl type to supported ecosystem.
4. If supported, create a metadata lookup request.
5. If unsupported or purl is missing, add unsupported or unavailable evidence without changing status by itself.

Supported initial mappings:

- `pkg:npm/...` -> npm
- `pkg:nuget/...` -> NuGet
- `pkg:cargo/...` -> Cargo
- `pkg:golang/...` -> Go modules

Do not require user-provided ecosystem flags.

## Fetcher Contract

Each package metadata fetcher returns a normalized fetch result:

- cache key
- source type
- package identity
- fetch status
- HTTP status or equivalent, if applicable
- raw license fields
- source references, avoiding secrets
- warnings
- errors

Fetcher implementations should not decide final component status. They only produce evidence.

## Ecosystem Fetch Details

The exact endpoint choices may vary by implementation, but each fetcher must satisfy the evidence contract.

### npm

Inputs:

- package name, including scoped names
- version

Expected evidence:

- package version license field
- repository URL, if present for future v3 planning
- registry response metadata needed for audit

### NuGet

Inputs:

- package id
- version

Expected evidence:

- license expression where available
- license file/license URL metadata where available
- project/repository URL where available

### Cargo

Inputs:

- crate name
- version

Expected evidence:

- crate license field
- repository URL where available

### Go Modules

Inputs:

- module path

- version

Expected evidence:

- module metadata source references
- repository URL where available
- license field only when available from selected metadata source

Go module license metadata may be weaker than other ecosystems. Missing license evidence should become `unknown` evidence, not a fetch error.

## Cache Design

Package metadata cache path:

```text
<user-data-dir>/ol/cache/package-metadata/<sha256(cache-key)>.json
```

Cache key:

- Prefer normalized purl including version.
- Include ecosystem and package version.
- Do not include transient request parameters.

Cache entry includes:

- schema version
- cache key
- cache key SHA-256
- fetched timestamp
- fetcher/source name
- raw metadata relevant to license evidence
- normalized license candidate, if any
- warnings and errors

Cache file names are hash-based. Cache file bodies keep plain logical cache keys for debugging.

There is no TTL. `--refresh` overwrites package metadata cache for requested components.

Corrupt cache entries are component-level cache errors. The scan should attempt network fetch unless disabled by future options. If fetch also fails and no other evidence exists, component status may become `error`.

## Fetch Scheduling

Default concurrency:

```text
max(4, min(Environment.ProcessorCount, 8))
```

Validation:

- `--concurrency` must be integer >= 1.
- `--retry` must be integer >= 0.

Retry policy:

- default retry count: 1
- retry timeout, HTTP 429, HTTP 5xx, and transient network errors
- do not retry HTTP 400, 401, 403, 404, invalid URL, unsupported ecosystem, or package not found

The scheduler should preserve deterministic output order by sorting/rendering after all component evidence has been collected, not by fetch completion order.

## Evidence Integration

For each component:

1. Start with v1 SBOM evidence.
2. Add package metadata evidence if available.
3. Validate metadata license values through the active SPDX validator.
4. Re-run the common reconciliation rules.

Outcomes:

- Same valid license from SBOM and package metadata: `matched`.
- Valid SBOM license but package fetch failed: `matched` with warning.
- SBOM unknown and package metadata valid: `matched`.
- SBOM valid and package metadata valid but different: `conflict`.
- Package metadata ambiguous and no valid candidate: `ambiguous`.
- All checked sources have no license: `unknown`.
- Required evidence could not be fetched and no usable candidate exists: `error`.

## JSON Report Additions

Add package metadata evidence to component records.

Add network/cache metadata such as:

- package metadata cache mode: used, refreshed, missed, written
- concurrency value
- retry count
- fetch warning/error counts

Do not add absolute cache paths. Use logical labels and hashes where useful.

## Text and Markdown Output

The default columns remain unchanged:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS
```

Verbose columns remain:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS PURL
```

Package metadata evidence changes `LICENSE` and `STATUS` through the shared reconciliation rules, but it does not add default columns.

## Stderr Summary

Add v2 summary fields:

- package metadata supported component count
- cache hit count
- cache miss count
- refreshed count
- fetch error count
- unsupported ecosystem count
- active concurrency and retry count, if useful

## Test Plan

### Unit Tests

- purl parser and ecosystem mapper.
- unsupported ecosystem evidence.
- cache key normalization and hash file naming.
- cache read/write/corrupt entry handling.
- retry classifier.
- concurrency value validation.
- evidence reconciliation with SBOM and package metadata combinations.

### Fetcher Contract Tests

Use fixture responses for each ecosystem:

- valid SPDX license
- ambiguous license string
- missing license field
- package not found
- transient error then success
- transient error exhausted

### Integration Tests

- scan with all metadata from cache.
- scan with cache miss and network fixture fetch.
- scan with `--refresh` overwriting cache.
- scan with `--concurrency 1` yielding same output as concurrent run.
- scan with metadata conflict changing status to `conflict`.
- scan with metadata fetch failure preserving `matched` from SBOM.
- `ol cache clear package-metadata` removes package cache.

### Golden Files

Extend v1 golden outputs with package evidence in JSON while keeping text/markdown columns stable.

## Implementation Milestones

1. Add cache store abstraction for package metadata.
2. Add purl parser and metadata planning.
3. Add fetch scheduler, concurrency, and retry policy.
4. Add npm fetcher with fixture tests.
5. Add NuGet fetcher with fixture tests.
6. Add Cargo fetcher with fixture tests.
7. Add Go module fetcher with fixture tests.
8. Add metadata evidence integration and report additions.
9. Add cache clear command.
10. Add integration and golden tests.
