---
name: performance-requirements
description: Guidelines for writing high-performance and memory-efficient code in `src/Ol.Core/` (Parsing and Linting) and generated code emitted by `src/Ol.Update/Generators/`. Covers zero allocations, per-run caching, zero-copy string design, bounded stackalloc in generated code, error-path CPU budget, and verification practices.
---

# Performance Requirements

All parser and linting code must be implemented with **maximum attention to performance and memory efficiency**.

## Core Requirements

### 1. Verification

- Test with BenchmarkDotNet to measure performance
- Verify zero allocations in Release builds

Parser verification:

1. Run tests after each implementation refactor.
3. For meaningful parser changes, run allocation benchmarks (or a focused micro benchmark) with disassembly attribute and compare to previous baseline.
4. Reject changes that regress allocation behavior without explicit justification in PR description.

Disassembly verification is super important to check CPU-level behaviour. Branchless makes a huge difference in performance for hot paths. Not only SIMD but also branchless code is important for performance. However without disassembly verification, it is super hard to identify is your code really branchless or not. So please always check disassembly for hot paths, and microbenchmarks for performance ready code.

### 2. Zero Allocations

- Never allocate arrays or collections during parser execution/ast processing.
- Use `Span<T>` for all array operations
- Use `stackalloc` for small temporary buffers (≤ 128 elements)
- Use `ArrayPool<T>.Shared` for large temporary buffers (> 128 elements)
- **NEVER** use `new T[]` or `new List<T>` for internal buffers

Parser-specific additions:

- For parser key checks, use `ReadOnlySpan<byte>` + `SequenceEqual("..."u8)`.
- In normal parse success paths, do not materialize strings.
- `GetScalarString()` is allowed only for diagnostics or exceptional fallback handling.
- Keep dynamic text as `Utf8Slice` and decode only when reporting diagnostics.
- Repeated lookups must be avoided by carrying resolved metadata through parse steps.

**Example:**

```csharp
// ✅ For small buffers - use stackalloc (no heap allocation)
if (span.Length <= 128)
{
    Span<T> tempBuffer = stackalloc T[span.Length];
    // ... parser logic using tempBuffer ...
}
// ✅ For large buffers - use ArrayPool (reusable, no GC pressure)
else
{
    var rentedArray = ArrayPool<T>.Shared.Rent(span.Length);
    try
    {
        var tempBuffer = rentedArray.AsSpan(0, span.Length);
        // ... parser logic using tempBuffer ...
    }
    finally
    {
        ArrayPool<T>.Shared.Return(rentedArray);
    }
}
```

### 3. Aggressive Inlining

- Mark hot-path methods with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`
- Especially for methods called frequently in loops (comparisons, swaps, etc.)

### 4. Loop Optimization

- Cache frequently accessed values outside loops
- Use `for` loops with indices instead of `foreach`
- Minimize redundant comparisons
- Avoid repeated property access or method calls

### 5. Hot Path Prohibitions

- No LINQ in parsing loops.
- No regex in parser implementation.
- No dictionary/collection growth in per-node parse paths.

### 6. Avoid string

Avoid `string` in hot paths. Use `ReadOnlySpan<byte>` or `Utf8Slice` for all internal text handling. Only convert to `string` for diagnostics or exceptional cases. Keep in mind that `string` always allocates and MUST avoid in internal.

### 7. HereDoc / Temporary State Zero-Alloc

For small temporary state arrays (e.g. heredoc tracking during script analysis), use `stackalloc` with a counter instead of `new List<T>()`. Store offsets into the source array instead of copying byte slices.

### 8. Bounded stackalloc in Generated Code

If generated code uses `stackalloc` with a size derived from input (e.g. UTF-8 span length), the generator **must** emit a compile-time max-length constant and guard before the stackalloc. User-controlled input can be arbitrarily long — unbounded stackalloc causes stack overflow (process-terminating).

```csharp
// ❌ Unbounded stackalloc — stack overflow on long input
Span<char> chars = stackalloc char[utf8Id.Length];

// ✅ Generator computes max from data, emits constant + early-return guard
private const int MaxIdByteLength = 32; // computed by generator

internal static bool IsKnown(ReadOnlySpan<byte> utf8Id)
{
    if (utf8Id.Length > MaxIdByteLength)
        return false;

    Span<char> chars = stackalloc char[MaxIdByteLength];
    var charCount = Encoding.UTF8.GetChars(utf8Id, chars);
    // AlternateLookup = KnownIds.GetAlternateLookup<ReadOnlySpan<char>>()
    // Allows span-based lookup into FrozenSet without allocating a string
    return AlternateLookup.Contains(chars[..charCount]);
}
```

### 9. FrozenSet as Primary, No Redundant Arrays

`FrozenSet<string>` is the correct primary store for immutable lookup sets. Do **not** keep a parallel `string[]` field:

- `FrozenSet` already provides O(1) `Contains` and struct-enumerator `foreach` (zero-allocation iteration when called directly).
- If suggestion logic needs to iterate candidates, accept `IReadOnlyCollection<string>` (which `FrozenSet` implements) rather than materializing an array. Note: interface dispatch via `IReadOnlyCollection` boxes the enumerator (one small allocation), but this is acceptable since suggestion runs only on error paths.
- Suggestion tie-break order (when multiple candidates have equal Levenshtein distance) is non-deterministic from `FrozenSet` enumeration. This is acceptable — exact tie-break order is not a correctness concern.

```csharp
// ❌ Redundant static array alongside FrozenSet
private static readonly string[] IdsArray = [.. KnownIds]; // extra alloc at type-init

// ✅ FrozenSet is primary; iterate directly via IReadOnlyCollection
internal static string? FindSuggestion(string input)
    => SuggestionHelper.FindClosest(input, KnownIds); // FrozenSet implements IReadOnlyCollection
```

### 10. Error-Path CPU Budget

Error paths are allowed to allocate, but must still be bounded for pathological inputs:

- **Skip expensive computation for long inputs**: If input length exceeds the max valid value length (`MaxIdByteLength`), there is no plausible match — skip Levenshtein search entirely.
- **Decode only what you display**: If displaying a truncated prefix, decode only that prefix from UTF-8 — don't decode the full multi-KB string. Use a separate display-cap constant (`MaxDisplayLength`, e.g. 40 bytes) distinct from the validation constant (`MaxIdByteLength`, e.g. 32 bytes).
- **Short-circuit pattern**: Check `span.Length` (bytes) before decode, decode before suggestion.

Two constants serve different purposes:
- `MaxIdByteLength`: Maximum UTF-8 byte length of any known valid value (computed by generator from data). Used to guard `stackalloc` and `IsKnown`.
- `MaxDisplayLength`: Maximum bytes to decode for diagnostic display (e.g. 40 bytes). Defined in the rule class. Applied to `span[..MaxDisplayLength]` then decoded to string with "..." appended.

```csharp
// ✅ Bounded error path — skip expensive work for pathological inputs
if (span.Length > MaxDisplayLength)
{
    // Too long to be valid — decode prefix only, skip suggestion
    display = string.Concat(Encoding.UTF8.GetString(span[..MaxDisplayLength]), "...");
}
else
{
    var decoded = Decode(slice);
    display = decoded;
    suggestion = FindSuggestion(decoded); // Levenshtein over ~600 candidates
}
```
