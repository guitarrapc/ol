---
name: performance-requirements
description: Ol-specific performance and memory requirements for transitive OSS license resolution: dependency inventory and graph ingestion, evidence collection, SPDX validation, license reconciliation, caching/network enrichment, reporting, policy evaluation, and generated SPDX data. Covers the data-oriented extension boundaries, Utf8Slice zero-copy rules, enrichment planning and deduplication, and which benchmarks gate a change. Builds on the general dotnet-performance-discipline skill.
---

# Performance Requirements

All hot paths in Ol's transitive OSS license-resolution pipeline must be implemented with **maximum attention to performance and memory efficiency**. This includes dependency inventory and graph ingestion, evidence collection, SPDX lookup and expression validation, license reconciliation, package/source enrichment, report projection, and future policy evaluation. SBOM parsing is one input stage, not the product boundary.

> **The project-independent C# rules live in the `dotnet-performance-discipline` skill.** Pooled-buffer ownership, `Span`/`Memory` selection, stackalloc byte budgets, transient-string avoidance, hot-path prohibitions, immutable lookup structures, bounded error paths, and allocation verification are defined there and apply here in full. Load it first. This document adds only what is specific to Ol, and records the few places where Ol is stricter or has already made a decision.

## Data-Oriented Architecture Is a Performance Requirement

Data-oriented design is mandatory, not an optional refactoring preference. Allocation rate, cache locality, bounded work, and extension locality are correctness properties of this repository.

- Model pipeline state as explicit `record struct`/`struct` data and deterministic transforms. Keep side effects at I/O, cache, and network boundaries.
- Keep per-item hot paths free of virtual/interface dispatch, closures, LINQ, transient strings, and growable collections. Registration-time polymorphism is permitted only at narrow format/provider boundaries; resolve it once before repeated work.
- An added SBOM format must be representable by one registered format handler containing its marker and parser. Its registration must not require a central format `switch`, parser-dispatch edit, or output-format edit.
- An added package ecosystem must be representable by one registered provider owning purl validation, endpoint construction, and response projection. It must not require central ecosystem switches in request parsing, registry retrieval, or scan ecosystem detection.
- Registries are immutable per operation. Construct lookup tables, delegates, and encoded marker data once at startup/registration time; never construct them per component, dependency, candidate, or request.
- Plan enrichment into indexed arrays with capacities derived from component count. Deduplicate normalized targets before cache/network work, execute only bounded workers, and project each shared result back in deterministic component order.
- Do not introduce behavior-heavy service layers, inheritance hierarchies, or broad dependency injection into parsing/reconciliation loops. A provider/parser boundary is acceptable only when it confines an independently changing concern.
- Treat a change that spreads one format or ecosystem across multiple unrelated files as a design failure. Add a registration test proving the new concern is consumed through its own registered handler/provider.

## Ol-Specific Requirements

### 1. Zero-Copy Pipeline Text

- Keep source-backed component identifiers, names, versions, PURLs, and specification versions as `Utf8Slice` while their source buffer remains owned by the report.
- In normal scan success paths, do not materialize strings for source-backed SBOM text.
- Decode to `string` only at an API boundary that requires ownership: output, registry/network requests, cache keys, or exceptional error handling.
- For JSON property and known-value checks, use `Utf8JsonReader.ValueTextEquals("..."u8)` or span-based UTF-8 comparison.
- Avoid repeated SPDX and metadata lookups by carrying resolved domain values through the scan/reconciliation flow.

### 2. Inventory and Evidence Temporary State

For component and dependency-edge accumulation during inventory ingestion, rent buffers from `ArrayPool<T>.Shared`, grow geometrically, and return replaced and final buffers. Store `Utf8Slice` offsets into an owned source byte array instead of copying each JSON string. Apply the same discipline to repeated evidence reconciliation and policy working sets. Clear returned arrays when elements contain references.

Never let pooled storage escape into the owned report: do not expose pooled arrays from `ScanReport`, `ScanComponent`, or license-candidate results. Copy out with `ToArray()` over the used range.

### 3. The Two Pooled Owners

`dotnet-performance-discipline` ranks a type that owns a rental as the last resort. This repository has exactly two, and both need their justification kept current.

**`Ol.Core.Reporting.DependencyRootPaths`** holds the three buffers one report's path resolution produces: the occurrence predecessors, the reached occurrence per component, and the identity index that locates a reported component in the inventory. The justification is that `DependencyPathResolver.BuildRootPaths` already rents and returns every buffer that stays inside it, so these three — precisely the ones that outlive the call in its return value — were the only unpooled state in the resolver.

It is a `ref struct`, and that is the point: the compiler refuses to let it reach a field, a closure, or the far side of an `await`, so the scope cannot leak by construction rather than by review. The four report paths that build it own the scope. `CheckCommands` and `SarifRenderer` use `using`; the scan report's text and Markdown unresolved sections use `try`/`finally` instead, because they build it on the first row that needs one and a report with nothing to explain must rent nothing. Access after disposal throws, and `tests/Ol.Tests/DependencyRootPathsTests.cs` proves it — in synchronous helpers, because a `ref struct` cannot live in an async method.

**`Ol.Internals.PooledStreamBufferWriter`** owns the growable byte buffer that batches one rendered report into its output stream. It has to be a class: the renderers take `IBufferWriter<byte>`, and a type reached through an interface cannot be a `ref struct`. Its rental lasts one render and is returned by `Dispose`, which flushes first and drops the field before returning, so a later write rents afresh rather than scribbling on recycled storage. All three writers create it with `using`.

**Prefer a `ref struct` for a new owner.** Fall back to a class only when the type must be reached through an interface, or when a rental genuinely has to cross an `await`. Do not add another owner without the same kind of justification, and without a regression test proving that a disposed owner does not read storage the pool has re-handed.

#### Pool per item, not per run

`Ol.Internals.PackageMetadataWorkspace` used to be the entry here, and it was deleted rather than kept. It pooled one per-component resolution array whose lifetime was the whole scan, so the pool was renting once and returning once — buying nothing, while costing an owner class, an `IDisposable` contract, a disposed-access failure mode, and the test proving that failure mode worked. The two enrichment services now pass a plain `PackageMetadataResolution?[]`.

The criterion this settled on: **pool a buffer whose lifetime is one item, one call, or one loop; allocate a buffer whose lifetime is the whole run.** A single allocation per scan is not worth an ownership contract. Do not reintroduce a pooled owner for per-run state.

### 4. SPDX Lookup and Normalization

- `SpdxLicenseIndex.TryNormalizeLicenseIdUtf8()` must keep its stack buffer bounded and fall back to `ArrayPool<char>.Shared` for longer UTF-8 input. Inventory, registry, repository, policy-file, and CLI input is user-controlled and can be arbitrarily long. The current path uses at most 128 `char` values on the stack. If generated lookup code is introduced later, it must follow the same bounded-stack rule.
- Use `FrozenDictionary<string, string>` for case-insensitive SPDX normalization, because lookup must return the canonical identifier casing.
- Use `FrozenSet<string>` for membership-only checks such as deprecated-license detection.
- Generated SPDX arrays are valid construction input for `SpdxLicenseIndex`; after construction, do not retain an additional copy solely for the same runtime lookup.
- Do not materialize another collection merely to iterate candidates on an exceptional path.

### 5. Graph Resolution Order

Resolve the complete dependency graph before output filtering or policy evaluation. Do not trade correctness for early filtering.

### 6. Enrichment, Cache, and Policy Work

- New cache and network pipelines must be planned from normalized component identity and deduplicated where semantically equivalent. For new or changed enrichment scheduling, deduplicate equivalent package/source targets before cache or network work when the result can be safely shared.
- Bound concurrent external requests and preserve deterministic report ordering independently of completion order. Keep scheduling cancellation-aware, and avoid creating an unbounded number of pending tasks or retaining response buffers.
- Stream or cap external payloads when practical; do not retain response bodies after normalized evidence has been created.
- Policy evaluation must consume the completed in-memory report. It must not rescan dependency inputs or repeat registry/source collection.
- Pre-normalize policy identifiers and lookup structures once per run rather than once per component.
- Error paths for invalid inventory, SPDX, registry, repository, or policy input may allocate for evidence, exception, or CLI output text, but must keep display limits separate from SPDX validity rules.

### 7. Which Benchmarks Gate a Change

Run tests after each implementation refactor. For meaningful changes to inventory ingestion, graph resolution, SPDX lookup, reconciliation, evidence enrichment, reporting, or policy evaluation, run the relevant benchmark in `src/Ol.Benchmark` and compare it to a baseline measured from the same code — not to a committed report, which goes stale.

| Change area | Benchmark |
|---|---|
| Inventory ingestion, parsers, graph resolution | `DependencyInputScannerBenchmark` |
| Package/source enrichment fixed cost | `EnrichmentFixedCostBenchmark` |
| Source enrichment at scale, target dedup | `SourceRepositoryEnrichmentBenchmark` |
| Sorting, view projection, duplicate purls | `ScanViewBenchmark` |
| Report rendering fixed cost | `TextReportRendererBenchmark`, `JsonReportRendererBenchmark` |
| Policy evaluation | `LicensePolicyBenchmark` |
| Anything that shifts cost between stages | `E2EBenchmark` |

Benchmark end to end when a local optimization may move cost to another pipeline stage.
