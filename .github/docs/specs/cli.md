# OL CLI Specification

This document defines the user-facing contract of the `ol` CLI: its design basis, process I/O rules, commands, and stable report semantics. Ol reports license evidence and uncertainty; it does not claim legal certainty.

## CLI Design

### Design Basis

The CLI follows the decisions in [Ol architecture](../Architecture.md):

- Resolve the complete dependency inventory before applying view filters, so transitive dependencies and unknown relationships remain visible.
- Preserve evidence and provenance instead of selecting one source silently. Conflicting, missing, invalid, and unavailable evidence are reportable results.
- Separate factual resolution from organizational policy. `scan` collects facts; `check` evaluates a persisted report without collecting evidence again.
- Treat component-level evidence failures as best-effort results, but make an unusable invocation, input, or output an explicit command failure.
- Use canonical JSON as the persistence boundary and derive human-readable views from the same result.
- Keep persisted artifacts deterministic and free of credentials, absolute local paths, and hidden cache paths.

### Process contract

Ol uses four exit codes:

| Code | Meaning |
|---|---|
| `0` | The command completed successfully. Help and version output also use `0`. |
| `1` | Invocation, configuration, input, SPDX data, cache, network-required operation, or output failed. |
| `2` | `check` completed successfully and found one or more policy violations. |
| `3` | `check` completed successfully, but every finding is a collection failure, so the result is inconclusive. |

Exit codes `2` and `3` belong only to policy results. `scan` and `diff` do not use changes or unresolved components as an alternate failure code.

`3` exists because the three states a CI job must tell apart are "fix the pipeline", "fix the dependency", and "try again". A component whose evidence could not be collected proves nothing about its license, so reporting it as a policy violation would make a registry outage indistinguishable from a forbidden license. It is not exit `1` either: the command ran, produced a complete report, and component-level collection failures are best-effort results rather than command failures. A run is inconclusive only when **every** violation is a collection failure; one genuine finding alongside them yields `2`, because a real violation is the more actionable fact. Status `error` cannot be acknowledged by a baseline, so a baseline never converts an inconclusive run into a pass.

Primary command output is written to stdout and ends with a line feed. Successful help is also stdout. Diagnostics and human-readable scan summaries are written to stderr. An expected failure writes one concise cause to stderr, leaves stdout empty, and does not print a stack trace or partial primary result.

Successful `scan --format text|markdown` writes its report to stdout and a labeled summary to stderr. Successful JSON output contains its summary and diagnostics in the document and therefore emits no duplicate stderr summary. `--quiet` suppresses the human-readable stderr summary, never the stdout result. `--verbose` may add diagnostics to stderr and additional report fields, but does not change the result.

Unknown commands, command groups without a subcommand, and missing required command arguments exit `1`. `ol` with no arguments shows root help; explicit `--help` and `-h` show help and exit `0`. Group help lists only that group's subcommands.

<a id="contract-scan-failures"></a>

A component whose evidence is missing, invalid, conflicting, or unavailable remains in the scan result. `scan` exits `1` only when it cannot produce a trustworthy complete result, for example when an input cannot be read or recognized, the dependency inventory cannot be extracted, SPDX data cannot be loaded, options are invalid, or stdout cannot be written. View options are validated before external evidence collection begins.

### Shared report contract

<a id="contract-component-status"></a>

Every component has one status:

| Status | Meaning |
|---|---|
| `matched` | Evidence resolves to one valid SPDX expression. |
| `conflict` | Valid evidence sources disagree. |
| `unknown` | Collection completed but yielded no usable license information. |
| `ambiguous` | License text exists but cannot be normalized without guessing. |
| `invalid` | A claimed SPDX expression is invalid or names an unknown identifier. |
| `error` | Required evidence collection or processing failed and no other evidence resolved the license. |

An external-source failure is retained as warning evidence when another source still produces a single valid expression. Human output displays `-` for `unknown` and marks ambiguous or conflicting values with `(?)`; JSON retains the individual claims and their typed provenance.

<a id="contract-dependency-type"></a>

Dependency type is `root`, `direct`, `transitive`, or `unknown`. It is `unknown` whenever the input cannot prove the relationship. The value is required in canonical JSON and shown in the default human-readable columns:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS
```

Verbose output additionally includes `PURL`.

<a id="contract-output-formats"></a>
<a id="contract-json-report"></a>

`scan` supports `text`, `markdown`, and canonical `json`; the default is `text`. Canonical JSON has a top-level `schemaVersion` and contains producer, input, SPDX, cache/network metadata, the complete inventory and graph, component results or grouped results, summary, and warnings. Consumers must reject or explicitly migrate unsupported schema versions.

The complete inventory is independent of sorted, filtered, or grouped views. Occurrence indexes address `inventory.components`, never displayed component or group indexes. The report identifies inputs with logical references and content hashes. It accepts input and SPDX JSON with an optional UTF-8 BOM.

<a id="contract-report-privacy"></a>

Reports, baselines, SARIF, and diagnostics must not contain token values, absolute local paths, or hidden cache paths. Use logical identifiers, basenames, relative paths, and hashes. Authentication may be reported only as a mode, never as a credential value.

<a id="contract-purl-prefix"></a>

`--exclude-packages` and `--skip-evidence-packages` share one package URL prefix rule. Matching is ordinal, case-sensitive, and anchored at `/`, `.`, and `@` separators. A full versioned purl matches exactly; a component without a purl never matches. Empty values, non-`pkg:` values, and ecosystem-only prefixes such as `pkg:npm/` are invalid. Duplicate prefixes have no additional effect, and supplied order is retained for verbose match counts.

A namespace written the way its ecosystem spells it is accepted: an `@` that starts a segment is canonicalized to `%40`, so `pkg:npm/@acme/` and `pkg:npm/%40acme/` are the same prefix and deduplicate to one entry. The `@` that separates a version is left alone, so a full purl such as `pkg:npm/left-pad@1.3.0` still addresses one component. Verbose output reports the canonical form, because that is what was matched.

## CLI Command List

| Command | Purpose | Primary output |
|---|---|---|
| `ol scan` | Resolve license evidence from one or more dependency inputs. | Text, Markdown, or canonical JSON report. |
| `ol check` | Evaluate a canonical JSON report against an allow-list. | Deterministic pass result or complete violation list. |
| `ol diff` | Compare two canonical JSON reports for license-relevant changes. | Text or JSON diff. |
| `ol cache clear` | Clear Ol-managed evidence caches. | Cleared categories. |
| `ol spdx version` | Show the active SPDX data source. | Active version and user-data location. |
| `ol spdx list` | List installed SPDX data versions. | Installed versions with the active version marked. |
| `ol spdx update` | Download current SPDX data. | Installed version. |
| `ol spdx use` | Activate an installed SPDX data version. | Active version. |
| `ol spdx clear` | Remove user-managed SPDX data. | Confirmation. |

### `ol scan`

```text
ol scan --input <file-or-directory> [--input <file-or-directory> ...]
```

`--input` is required and repeatable. It accepts CycloneDX JSON, SPDX JSON, or supported resolved package-manager inputs: NuGet assets, npm, pnpm, Yarn Classic/Berry, Cargo metadata, Go module graph, pip inspect, Composer, Bundler, Maven dependency tree, Swift `Package.resolved`, and CocoaPods lock data. `--input-format` defaults to `auto`; an explicit format is an assertion and must match every discovered document.

Ol consumes already resolved inventories; it does not resolve manifests or version ranges. Directory discovery uses only registered resolved-input names, does not follow reparse points, and requires complete companion-file sets for Go and Composer. Content signatures, not filenames or registration order, determine a document's format. Unsupported versions, no match, ambiguous matches, malformed companion sets, SBOM/package-manager mixtures, and multiple SBOM documents are input failures.

Repeated and directory inputs are deduplicated by resolved file path and processed in deterministic logical-path order. Multiple package-manager formats form one collection while retaining their own contexts and graphs; Ol does not invent edges between inventories. A repository-wide SBOM and direct package-manager inputs are alternative authoritative sources and must be scanned separately.

`scan` collects external package and source evidence by default. `--refresh` bypasses reusable entries. `--no-external-evidence` reads neither external sources nor their caches and reports that collection was not attempted.

<a id="contract-skip-evidence-packages"></a>

`--skip-evidence-packages <prefixes>` disables registry, repository, and cache collection only for matching components. Each remains in the report and receives an `unknown` evidence candidate with `external_evidence_not_collected`; input evidence may still resolve its final status. An unresolved result fails closed in `check` unless a baseline acknowledges it. Combined with `--no-external-evidence`, the option has no additional effect.

<a id="contract-dependency-filtering"></a>

`--dependency root,direct,transitive,unknown` filters only the rendered view; analysis always uses the complete inventory. When filtering to `direct`, the stderr summary identifies excluded `unknown` relationships. `--sort` accepts `name`, `version`, `license`, `ecosystem`, `dependency`, `status`, and `purl`; default order is `ecosystem,name,version`, ascending. `--group-by` accepts all of those except `purl`, adds `COUNT`, and produces a grouped view. Empty filter, sort, or group lists are invalid.

The cache root is selected by `--cache-dir`, then `OL_CACHE_DIR`, then legacy category-specific roots, then the platform user-cache location. A supplied root is an isolation directory: Ol manages only its `package-metadata` and `source-repository` children. Cache paths never appear in reports. Cache schemas are specified in [cache_format.md](cache_format.md).

### `ol check`

<a id="contract-policy-checks"></a>
<a id="contract-policy-report-input"></a>

```text
ol check --report <scan.json> --allow-licenses <SPDX-ids>
```

`check` reads one ungrouped canonical JSON report. It performs no dependency parsing, evidence collection, cache access, or network access. Invalid, malformed, grouped, or unsupported-schema reports are command failures.

`--allow-licenses` is a required comma-separated list of SPDX License Identifiers. Whitespace and casing are normalized using active SPDX data. Empty entries, expressions, exception identifiers, natural-language names, and unknown identifiers are invalid. For a `matched` component, `AND` requires both operands, `OR` requires either operand, parentheses retain SPDX precedence, and `WITH` has the policy value of its base license.

Policy evaluates all non-root, non-excluded components. `unknown` dependency type remains in scope. `unknown`, `conflict`, `ambiguous`, `invalid`, and `error` fail closed unless the baseline rules below acknowledge the unresolved status. All violations are collected and deterministically ordered.

`--allow-dev-licenses` adds identifiers only for components proven development-only by resolver data persisted in the report. It uses the same identifier validation as `--allow-licenses`; a supplied empty value is invalid. Any runtime or usage-unknown occurrence keeps the component under the primary allow-list. Inputs without reliable development reachability therefore fail closed. When supplied, the count admitted by this policy is always printed.

`--exclude-packages` removes matching purls from policy evaluation, baseline generation, violation output, SARIF, and the passing count, but never changes the scan report. The excluded count is always printed when the option is supplied. This is a policy-scope decision; it is distinct from `scan --skip-evidence-packages`.

<a id="contract-policy-baseline"></a>

`--baseline <file>` acknowledges reviewed unresolved components so that the unresolved set cannot grow silently. `--update-baseline` requires `--baseline`, replaces it with a deterministic complete snapshot, and then evaluates against that snapshot; it does not merely append or suppress evaluation.

Only `unknown`, `ambiguous`, `conflict`, and `invalid` may be acknowledged, and only when no recognizable candidate is rejected by the active allow-list. `matched` belongs in the allow-list and `error` represents an operation to repair. Entries identify the component and fingerprint its status and evidence, so changed evidence or identity expires the acknowledgement. Applying a baseline rechecks the active allow-list. Missing, malformed, or incompatible baselines exit `1`; acknowledged counts, including zero, are reported.

<a id="contract-policy-sarif"></a>

`--sarif <file>` writes the same violations as SARIF 2.1.0 without changing stdout. Stable rule IDs are `OL0001` not allowed, `OL0002` conflict, `OL0003` unresolved, `OL0004` ambiguous, `OL0005` invalid expression, and `OL0006` evidence error. Results use logical component locations and include the deterministic shortest dependency path when available; Ol does not invent source positions. Development-policy allowances are recorded as run properties rather than findings.

Policy files, deny-lists, per-package exceptions, concluded licenses, and dependency-scope policy are outside this command's contract.

### `ol diff`

<a id="contract-diff"></a>

```text
ol diff --previous <scan.json> --current <scan.json> [--format text|json]
```

`diff` reports `added`, `removed`, `version-changed`, `status-changed`, `license-changed`, and `evidence-changed`. Each material dimension is an independent change record even when human text groups changes for one component. Output is deterministic; JSON uses schema version 1 and reports both affected-component and independent-change counts. `diff` exits `0` when comparison succeeds even when changes exist, and `1` when either report or output is unusable.

### `ol cache clear`

```text
ol cache clear [package-metadata|source-repository|all]
```

The positional category defaults to `all`. Clearing a category removes only the corresponding Ol-managed child under the selected cache root. Clearing `all` preserves the isolation root and unrelated sibling files. An existing file cannot be used as a cache root.

### `ol spdx`

```text
ol spdx version
ol spdx list
ol spdx update
ol spdx use <version>
ol spdx clear
```

The SPDX commands inspect and manage user-installed SPDX License List data. `update` is the only command in this group that requires network access. `clear` removes user-managed data and returns Ol to bundled data.

## Lessons Learned

- Detection must validate the complete document. Optional UTF-8 BOMs are common, and selecting the first recognizable marker can misclassify a document that contains conflicting format markers.
- The documented command shape is part of the contract. A positional cache category implemented as both an option and an argument made help ambiguous and left conflicting forms without a principled winner.
- Canonical artifacts must be stable under irrelevant ordering. Baseline fingerprints sort evidence claims before hashing, and report views never reuse their sorted indexes as inventory indexes.
- Acknowledgement is not a license status. It removes a policy violation while preserving the original unresolved status and evidence, which keeps factual resolution separate from organizational policy.
- Root and unknown dependency relationships are not interchangeable. The inspected subject is outside dependency policy, while an unknown relationship remains fail-closed because missing graph evidence cannot prove first-party ownership.
- Safe defaults matter across every parser: an absent license must become `unknown`, never an empty `matched` result.
- Policy exclusion and skipped collection solve different problems. Exclusion belongs to `check` and changes scope; skipped collection belongs to `scan` and preserves a visible unresolved component.
- Option names should describe observable behavior. `--skip-evidence-packages` and `--exclude-packages` make their distinct effects visible without claiming ownership or package provenance that Ol cannot verify.
- A collection failure and a policy violation must not share an exit code. Both leave a component unresolved, but only one is a fact about licensing, and a CI job that cannot tell them apart either retries genuine violations or treats registry outages as findings. The split needed no new status because `LicensePolicyViolationKind.Error` already carried the distinction to the renderer.
- A canonical identifier is not always the one a user can type. npm purls encode a scope as `%40acme` while every other tool, and the package name itself, spells it `@acme`, so requiring the encoded form made a correct-looking prefix silently match nothing. Canonicalizing a segment-initial `@` on input accepts both spellings without weakening the boundary rule, because the version separator is positionally distinguishable from a namespace marker.
- A definitive negative answer is not a collection failure. Classifying registry `404` as an error made a package published only to a private feed permanently unresolvable, because status `error` cannot be acknowledged — the exact dead end that motivated an escape-hatch option. Reclassifying it as unknown fixed every ecosystem at once, including the ones whose input cannot express where a package came from, and left `error` meaning only what a retry could change.
- A lockfile download URL is not evidence of where a package is published. Withholding the public-registry identity from npm and pnpm entries whose host was not `registry.npmjs.org` looked like the rule Cargo and Bundler already apply, but those record a registry identity while npm records a download URL. A corporate proxy serves public packages from an internal host, so the rule would have silently disabled enrichment for an entire proxied dependency tree — harming exactly the organizations it was meant to help. It was implemented and then withdrawn before release.
- Diff change kinds must remain independently filterable. A version change must not hide a simultaneous license or status change in machine-readable output.
