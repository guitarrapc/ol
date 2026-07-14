# Package Metadata Hint Specification

This document defines the v2 behavior for using package manager and package registry metadata as license evidence.

Package metadata is a hint source, not an authority. It complements SBOM license information because SBOM license fields can be missing, stale, inferred, or inconsistent with package registry metadata.

## Design Basis

This specification derives from the [Ol design](../DESIGN.md), especially the decisions to [preserve evidence instead of selecting a single authoritative source](../DESIGN.md#decision-evidence-preservation), [add evidence sources through one reconciliation model](../DESIGN.md#decision-shared-reconciliation), [make component/source failures best-effort](../DESIGN.md#decision-failure-scope), [make evidence freshness explicit](../DESIGN.md#decision-cache-freshness), [version the persistent evidence format](../DESIGN.md#decision-cache-compatibility), [bound external I/O and retry only transient failures](../DESIGN.md#decision-bounded-io), and [persist evidence with explicit provenance and privacy boundaries](../DESIGN.md#decision-provenance-privacy).

Package metadata is consequently additive evidence: it never silently replaces the SBOM claim, uses the shared SPDX and reconciliation semantics, and records disagreement as `conflict`. Cache, concurrency, and retry behavior exist to make enrichment practical and repeatable without turning a registry outage into the loss of the complete dependency report.

## Version Scope

v1 does not fetch package metadata and does not maintain package/source evidence cache.

v2 adds automatic package metadata hints.

## Current Implementation Status

The implemented v2 behavior plans supported versioned purls, consumes persistent package-metadata cache entries, and fetches registry metadata for cache misses and `--refresh`. It supports npm, NuGet, Cargo, and Go, cache metrics in JSON and stderr summaries, `--refresh`, `--concurrency`, `--retry`, and `ol cache clear` categories.

Successful fetches overwrite the relevant cache entry. A cache miss or refresh failure records component-scoped `package_metadata_fetch_failed` evidence; existing valid SBOM evidence remains authoritative for the component's final status. Go module proxy metadata provides source references but no license field, so a successful Go lookup without license text contributes unknown evidence rather than a fetch error.

Registry responses that are valid JSON but do not embed the expected metadata object contribute unknown metadata rather than terminating the scan. In particular, a direct NuGet registration leaf normally exposes `catalogEntry` as a NuGet-hosted catalog URL. Ol follows that trusted URL to recover the package-version metadata; malformed or untrusted references remain unknown and must not erase usable SBOM evidence or fail the whole scan.

v3 keeps this behavior and adds source repository hints described in [source.md](source.md).

## User Experience

Users should not have to specify package manager or ecosystem manually. The CLI derives the ecosystem from component purl and other SBOM metadata where possible.

There is no required `--package-manager`, `--assume-ecosystem`, or `--hint package-manager` flag in the normal flow.

```bash
ol scan --sbom bom.json
```

The same command gains richer evidence in v2.

## Initial Ecosystem Support

Initial v2 package metadata support targets:

- npm
- NuGet
- Cargo
- Go modules

Maven and other ecosystems may be added later.

Each ecosystem is an independently registered metadata provider. A provider owns the versioned-purl acceptance rules, registry endpoint, and normalized response evidence for that ecosystem. This keeps ecosystem-specific changes local: adding or removing a provider does not change central request parsing, registry dispatch, or SBOM ecosystem detection. Provider registration is immutable for a scan so repeated component processing performs only data lookup, not runtime configuration work.

Unsupported ecosystems do not introduce a new component status. They are recorded as evidence with unsupported reason metadata. The component's final status remains based on available license evidence.

<a id="contract-package-evidence"></a>
## Evidence Model

Package metadata evidence may provide:

- raw license value
- normalized SPDX expression, if valid
- package registry URL or logical source reference
- repository commit or ref mapped to the requested package version, when the registry supplies one
- fetch timestamp
- fetch status
- warnings and errors

Package metadata is combined with SBOM evidence:

- If all usable candidates agree, status is `matched`.
- If usable candidates disagree, status is `conflict`.
- If no usable license candidate exists, status is `unknown` or `error` depending on whether sources were successfully checked.
- If metadata is present but ambiguous, status may be `ambiguous` unless another source yields a single valid expression without conflict.

External fetch failure does not automatically make a component `error`. If SBOM evidence yields a single valid license, the component remains `matched` and the fetch failure is recorded as warning evidence.

<a id="contract-package-best-effort"></a>
## Best-Effort Execution

v2 scan remains best-effort. A metadata fetch failure for one component must not stop the scan. The final summary reports fetch failures and warnings.

Whole-command failure is reserved for cases where scanning cannot proceed at all or output cannot be written.

<a id="contract-package-cache"></a>
## Cache

v2 introduces persistent package metadata cache.

Cache identity is the package schema's canonical versioned-purl key. Schema version 1 preserves the accepted input purl spelling after removing qualifiers and subpath, as defined by the shared cache-format contract. Physical entry names are opaque so package names are not exposed in directory listings, while entries retain enough logical identity and provenance for auditability.

The exact persisted properties, casing, validation rules, and schema-version behavior are defined by [package metadata cache schema version 1](cache_format.md#contract-package-cache-v1). Package metadata code must not define an independent cache shape.

Cache entries are persistent. There is no automatic TTL. `--refresh` ignores existing package metadata cache and overwrites it with newly fetched evidence.

`ol cache clear` removes evidence caches. It may accept cache categories such as `package-metadata`, `source-repository`, or `all`.

<a id="contract-package-concurrency"></a>
## Concurrency

v2 external fetches run concurrently by default.

Before external work starts, enrichment plans versioned component purls into indexed lookup data and deduplicates matching cache keys. A shared cache or registry result is then projected to every matching component in original report order. The number of active workers is bounded by `--concurrency`; the implementation must not create one pending task per component.

Default concurrency is:

```text
max(4, min(Environment.ProcessorCount, 8))
```

`--concurrency 1` means sequential execution. Values must be at least 1.

<a id="contract-package-retries"></a>
## Retries

v2 external fetches retry transient failures once by default, for two total attempts.

Retryable conditions include:

- timeout
- HTTP 429
- HTTP 5xx
- transient network errors

Non-retryable conditions include:

- HTTP 400
- HTTP 401 or 403
- HTTP 404 or package not found
- invalid URL
- unsupported ecosystem

Rate-limit responses should be recorded in evidence. The scan should continue where possible.

<a id="contract-package-privacy"></a>
## Report Privacy

Reports must not include token values or absolute cache paths. Package cache paths should be represented by logical labels and hashes when needed.
