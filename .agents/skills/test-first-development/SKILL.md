---
name: test-first-development
description: Mandatory test-first workflow for changes under `src/` in Ol, the license-compliance CLI. Covers red-green testing for SBOM scanning, SPDX validation, license reconciliation, package metadata, CLI behavior, and SPDX code generation, plus regression coverage, benchmarks, and specification updates.
---

# Test-First Development

**This skill is mandatory for every task that adds or modifies code under `src/`.**

Skip this skill only when the change is limited to documentation, configuration, or generated files.

Also follow the performance-requirements skill when changing hot paths in `Ol.Core` or generated SPDX lookup code.

## Workflow

### 1. Write Failing Tests First (Red)

Before writing any production code, create tests that demonstrate the current behavior is wrong or missing.

- **New feature**: Write a test that exercises the new behavior and verify it fails (compile error or assertion failure).
- **Bug fix**: Write a test that reproduces the bug and verify it fails.
- **Modification**: Write a test that asserts the new expected behavior and verify it fails against the current code.

Run the failing test to confirm:

```shell
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/YourTestClass/YourTestMethod*
```

For a behavior-preserving performance refactor, a correctness test may not have a meaningful failing state. First confirm the relevant correctness tests pass, then capture a benchmark or allocation baseline as the red measurement. After the change, require behavioral parity and compare the same measurement.

### 2. Implement (Green)

Write the minimum production code to make the failing test pass. Then run the test again to confirm it passes.

### 3. Run Full Test Suite

After the implementation passes targeted tests, run all tests to catch regressions:

```shell
dotnet test
```

All tests must pass before proceeding.

### 4. Add Regression Tests

For bug fixes, the test written in Step 1 often doubles as the regression test. If Step 1 already covers the fix scenario, you do not need a separate test — but verify it matches the pattern below. For new features, add edge-case tests beyond the initial happy-path test from Step 1.

**When fixing domain decision logic bugs** (for example, SBOM format detection, dependency type assignment, SPDX expression handling, license reconciliation, or metadata scheduling): Step 1 writes the single failing test that reproduces the bug. After the fix passes, add the remaining equivalence-class tests here rather than widening the initial red step.

Regression test patterns by change type:

| Change type | Test pattern | Assertion |
|---|---|---|
| Valid SBOM/SPDX input was rejected | Valid-input scan or lookup test | Expected report/value is returned without exception |
| Invalid or ambiguous input was accepted | Invalid-input test | Expected exception or non-success status is produced |
| License evidence was reconciled incorrectly | Candidate/evidence combination test | Expected `LicenseStatus` and selected license |
| Package metadata behavior regressed | Cache/registry/scheduler test | Expected request count, cached value, or fallback result |

### 5. Benchmark Verification

When changing SBOM scanning, SPDX lookup, license reconciliation, or another measured hot path, run benchmarks from the repository root:

```shell
dotnet run --project src/Ol.Benchmark/Ol.Benchmark.csproj -c Release
```

Compare results against the local previous run or a baseline produced from a clean `main` checkout. `BenchmarkDotNet.Artifacts/` is ignored and is not a committed source of truth.

- **Mean**: must not increase by more than +10%
- **Allocated**: must not increase by more than +10%

Relevant benchmarks by change area:

| Changed area | Benchmark to check |
|---|---|
| `src/Ol.Core/SbomScanner.cs` and scan-domain models | `SbomScannerBenchmark.ScanCycloneDx` |
| SPDX lookup, reconciliation, or metadata hot paths | Add a focused case to the active benchmark runner before applying a numeric regression threshold if no representative benchmark exists |
| `src/Ol.Update/SpdxCodeGenerator.cs` | Regenerate output, test generated behavior, and benchmark the affected `SpdxLicenseIndex` consumption path when performance-sensitive |

The current runner invokes `SbomScannerBenchmark` directly. Add a focused method to that class, or update the runner so a new benchmark class is actually selected; merely declaring an unreferenced benchmark class is insufficient.

### 6. Update Specs

If the implementation changes observable behavior or adds new functionality, update the relevant specification:

- CLI commands, options, output, and exit behavior: `.github/docs/specs/cli.md`
- SPDX data, validation, expression, or generation behavior: `.github/docs/specs/spdx.md`
- package-manager metadata and registry behavior: `.github/docs/specs/packagemanager.md`
- license evidence/source and reconciliation behavior: `.github/docs/specs/source.md`

## Test Conventions

### Naming

- Class: `{Feature}Tests` (for example, `CycloneDxScanTests`, `SpdxStoreTests`, or `PackageMetadataTests`)
- Method: `{Action}_{Context}_{ExpectedOutcome}` (for example, `Scan_WithUtf8Bom_DetectsCycloneDx`)

### Framework

**This project uses TUnit. Always use `--treenode-filter` — do NOT use `dotnet test --filter` (that is xUnit/MSTest syntax and will not work).**

```shell
# Run all tests in a class
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/FooTests/*

# Run a single test
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/FooTests/Foo_Bar_Hoge*
```

More examples:

```shell
# Run all tests in FooTests
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/FooTests/*

# Run a single method by prefix match
dotnet test --project tests/Ol.Tests/Ol.Tests.csproj --treenode-filter /*/*/FooTests/Foo_Bar_Hoge*
```

### Assertions

Use TUnit async assertions:

```csharp
await Assert.That(report.Format).IsEqualTo(SbomFormat.CycloneDxJson);
await Assert.That(report.Components).HasCount().EqualTo(1);
```

## Test Design Guardrails

- Prefer black-box tests that verify observable behavior through the public API or a stable integration seam.
- Do not use reflection to invoke private methods or read/write private fields in tests. Those tests are brittle and usually indicate the wrong test target.
- If a behavior is important but hard to reach through the public surface, first look for a user-visible scenario that exercises it end to end.
- Only add a narrow `internal` test seam with `InternalsVisibleTo` when a black-box test is not practical and the seam itself represents a stable concept worth naming.
- Avoid writing tests whose main assertion is about a private helper method. Test the behavior that helper exists to produce.

## Classification Logic: Equivalence Class Coverage

When implementing or modifying Ol's **domain decision logic** (for example, SBOM format detection, dependency classification, SPDX validity, license reconciliation, cache freshness, or registry fallback), apply equivalence-class partitioning so accepted and rejected cases are both covered.

### Mandatory Steps

1. **Enumerate input variables** that affect the decision (for example, presence of `bomFormat`, `spdxVersion`, and the expected format value).
2. **Build a truth table** of variable combinations that make each branch true/false. Each combination is an equivalence class.
3. **Write at least one test per class**, with priority on:
   - Cases where the condition is true AND should be true (true positive)
   - Cases where the condition is true BUT should be false (false positive — **these are the most commonly missed**)
   - Cases where the condition is false AND should be false (true negative)
   - Cases where the condition is false BUT should be true (false negative)
4. **For license-status and format decisions**: include nearby valid cases as well as invalid cases. False `Unknown`, `Conflict`, or unsupported-format results erode trust in compliance reports.

### Example: Multi-Variable Condition

For SBOM format detection, let `C` mean a valid CycloneDX marker (`bomFormat: CycloneDX`) and `S` mean the presence of `spdxVersion`:

| C | S | Expected | Test case |
|---|---|---|---|
| true | false | CycloneDX | Valid CycloneDX document |
| false | true | SPDX | Valid SPDX JSON document |
| true | true | Reject as ambiguous | Document containing both markers |
| false | false | Reject as unsupported | JSON containing neither marker |

The ambiguous and unsupported rows prevent a broad marker check from silently selecting the wrong scan path.

### When to Apply

- Any `if`/`switch` with 3+ input variables affecting the decision
- Any heuristic that reconciles multiple license evidence sources or infers dependency type
- Any cache, retry, or registry-fallback decision with externally observable behavior
- **Bug fixes on classification logic**: even when fixing a single false positive/negative, build the full truth table first. This prevents the fix from introducing new false positives in adjacent equivalence classes.
