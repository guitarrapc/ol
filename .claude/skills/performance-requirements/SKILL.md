---
name: performance-requirements
description: Guidelines for high-performance and memory-efficient transitive OSS license resolution in Ol: dependency inventory and graph ingestion, evidence collection, SPDX validation, license reconciliation, caching/network enrichment, reporting, policy evaluation, and generated SPDX data. Covers allocation control, zero-copy UTF-8 data, pooled buffers, bounded concurrency, and benchmark verification.
---

# Performance Requirements

All hot paths in Ol's transitive OSS license-resolution pipeline must be implemented with **maximum attention to performance and memory efficiency**. This includes dependency inventory and graph ingestion, evidence collection, SPDX lookup and expression validation, license reconciliation, package/source enrichment, report projection, and future policy evaluation. SBOM parsing is one input stage, not the product boundary.

## Core Requirements

### 1. Verification

- Use BenchmarkDotNet to measure performance and allocations in Release builds.
- Compare against the existing baseline before accepting hot-path changes.

Hot-path verification:

1. Run tests after each implementation refactor.
2. For meaningful changes to inventory ingestion, graph resolution, SPDX lookup, reconciliation, evidence enrichment, reporting, or policy evaluation, run the relevant allocation benchmark (or a focused microbenchmark) and compare it to the previous baseline. Use disassembly diagnostics for CPU-sensitive local changes.
3. Distinguish required result allocations (for example, the returned report, component/evidence records, rendered output, and persistent cache records) from avoidable temporary allocations inside repeated dependency, candidate, and policy loops.
4. Reject unexplained regressions in mean execution time or allocated bytes.

Use disassembly diagnostics when benchmark results indicate that code generation, inlining, vectorization, bounds checks, or branch behavior may materially affect a measured hot path. Treat branch reduction and SIMD as hypotheses to measure, not unconditional goals.

### 2. Zero Allocations

- Avoid temporary arrays and collections in repeated component, dependency-edge, evidence-candidate, SPDX, and policy-evaluation loops.
- Use `Span<T>`/`ReadOnlySpan<T>` for contiguous temporary data where ownership does not escape.
- Use `stackalloc` only for small buffers with a compile-time or explicitly guarded maximum. Set the limit by total byte size and element type, not by a universal element count; the current SPDX UTF-8 normalization path uses at most 128 `char` values on the stack.
- Use `ArrayPool<T>.Shared` for larger or dynamically growing temporary buffers, and always return rented arrays in a `finally` block.
- Allocate owned arrays when they are part of the returned domain result; do not expose pooled arrays from `ScanReport`, `ScanComponent`, or license-candidate results.

Pipeline-specific additions:

- For JSON property and known-value checks, use `Utf8JsonReader.ValueTextEquals("..."u8)` or span-based UTF-8 comparison.
- In normal scan success paths, do not materialize strings for source-backed SBOM text.
- Keep source-backed component identifiers, names, versions, PURLs, and specification versions as `Utf8Slice` while their source buffer remains owned by the report.
- Decode to `string` only at an API boundary that requires ownership, for output, registry/network requests, cache keys, or exceptional error handling.
- Avoid repeated SPDX and metadata lookups by carrying resolved domain values through the scan/reconciliation flow.
- Resolve the complete dependency graph before output filtering or policy evaluation; do not trade correctness for early filtering.
- For new or changed enrichment scheduling, deduplicate equivalent package/source targets before cache or network work when the result can be safely shared. The current package metadata service does not yet coalesce duplicate targets.
- Keep external-request concurrency bounded and cancellation-aware. When modifying scheduling, also avoid creating an unbounded number of pending tasks or retaining response buffers; the current v2 implementation creates one enrichment task per component and should be improved rather than copied.

**Example with an explicit stack byte budget:**

```csharp
const int MaxStackBytes = 256;
if (byteCount <= MaxStackBytes)
{
    Span<byte> tempBuffer = stackalloc byte[MaxStackBytes];
    tempBuffer = tempBuffer[..byteCount];
    // ... repeated dependency/evidence processing ...
}
else
{
    var rentedArray = ArrayPool<byte>.Shared.Rent(byteCount);
    try
    {
        var tempBuffer = rentedArray.AsSpan(0, byteCount);
        // ... repeated dependency/evidence processing ...
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(rentedArray);
    }
}
```

### 3. Aggressive Inlining

- Consider `[MethodImpl(MethodImplOptions.AggressiveInlining)]` only for small methods on a measured hot path.
- Keep the attribute only when benchmarks and, where useful, disassembly show a benefit; forced inlining can increase code size or inhibit better JIT decisions.

### 4. Loop Optimization

- Cache frequently accessed values outside loops when measurement or inspection shows repeated work.
- Select `for`, `foreach`, or span iteration based on generated code and clarity; `foreach` over spans and concrete collection types can be allocation-free.
- Minimize redundant comparisons and repeated property or method calls on measured hot paths.

### 5. Hot Path Prohibitions

- No LINQ in inventory token loops, dependency traversal, SPDX candidate loops, reconciliation, or per-component policy hot paths.
- No regex for JSON property matching, SPDX identifier lookup, package identity extraction, or policy matching in hot paths unless a benchmark proves it is the appropriate implementation.
- Do not grow dictionaries or collections once per input token, dependency edge, component, or candidate when capacity can be estimated or pooled storage can be reused.

### 6. Avoid string

Avoid creating transient `string` values in hot paths. Use `ReadOnlySpan<byte>` or `Utf8Slice` for source-backed inventory text, and span-based lookup where available. A `string` is appropriate when an owned value is required by the public report/evidence model, SPDX data store, package or source cache/network client, policy configuration, CLI output, or an exception message; avoid repeated decoding or normalization of the same value.

### 7. Inventory and Evidence Temporary State

For component and dependency-edge accumulation during inventory ingestion, rent buffers from `ArrayPool<T>.Shared`, grow geometrically, and return replaced and final buffers. Store `Utf8Slice` offsets into an owned source byte array instead of copying each JSON string. Apply the same discipline to repeated evidence reconciliation and policy working sets. Clear returned arrays when elements contain references, and never let pooled storage escape into the owned report.

### 8. Bounded UTF-8 Normalization Buffers

`SpdxLicenseIndex.TryNormalizeLicenseIdUtf8()` must keep its stack buffer bounded and use `ArrayPool<char>.Shared` for longer UTF-8 input. Inventory, registry, repository, policy-file, and CLI input is user-controlled and can be arbitrarily long; never size a stack allocation directly from that input. If generated lookup code is introduced later, it must follow the same bounded-stack rule.

```csharp
// ❌ Unbounded stackalloc — stack overflow on long input
Span<char> chars = stackalloc char[utf8Id.Length];

// ✅ Fixed stack budget with a pool fallback for larger input
const int MaxStackChars = 128;
char[]? rented = null;
Span<char> chars = utf8Id.Length <= MaxStackChars
    ? stackalloc char[MaxStackChars]
    : (rented = ArrayPool<char>.Shared.Rent(utf8Id.Length));
try
{
    var charCount = Encoding.UTF8.GetChars(utf8Id, chars);
    // Perform the span-based SPDX lookup.
}
finally
{
    if (rented is not null)
        ArrayPool<char>.Shared.Return(rented);
}
```

### 9. Immutable SPDX Lookup Structures

Choose an immutable lookup structure that matches the domain operation:

- Use `FrozenDictionary<string, string>` for case-insensitive SPDX normalization because lookup must return the canonical identifier casing.
- Use `FrozenSet<string>` for membership-only checks such as deprecated-license detection.
- Generated SPDX arrays are valid construction input for `SpdxLicenseIndex`; after construction, do not retain an additional copy solely for the same runtime lookup.
- Do not materialize another collection merely to iterate candidates on an exceptional path.

### 10. Error-Path CPU Budget

Invalid inventory, SPDX, registry, repository, or policy input paths may allocate for evidence, exception, or CLI output text, but work must remain bounded for pathological input. Before adding suggestions or other approximate matching:

- Bound input length and candidate count before expensive work.
- Decode only the UTF-8 prefix that will be displayed rather than an entire pathological value.
- Keep display limits separate from SPDX validity rules.
- Short-circuit on byte length before decoding or searching candidates.

```csharp
// ✅ Pattern for bounded display of pathological UTF-8 input
if (span.Length > MaxDisplayLength)
{
    display = string.Concat(Encoding.UTF8.GetString(span[..MaxDisplayLength]), "...");
}
else
{
    display = Encoding.UTF8.GetString(span);
}
```

### 11. I/O, Cache, and Policy Work

- New cache and network pipelines must be planned from normalized component identity and deduplicated where semantically equivalent; existing package enrichment requires this optimization when its scheduler is revised.
- Bound concurrent external requests and preserve deterministic report ordering independently of completion order.
- Stream or cap external payloads when practical; do not retain response bodies after normalized evidence has been created.
- Policy evaluation must consume the completed in-memory report. It must not rescan dependency inputs or repeat registry/source collection.
- Pre-normalize policy identifiers and lookup structures once per run rather than once per component.
- Benchmark end-to-end changes when a local optimization shifts cost to another pipeline stage.
