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

`3` exists because the three states a CI job must tell apart are "fix the pipeline", "fix the dependency", and "try again". A component whose evidence could not be collected proves nothing about its license, so reporting it as a policy violation would make a registry outage indistinguishable from a forbidden license. It is not exit `1` either: the command ran, produced a complete report, and component-level collection failures are best-effort results rather than command failures. A run is inconclusive only when **every** violation is a collection failure; one genuine finding alongside them yields `2`, because a real violation is the more actionable fact. Status `error` cannot be acknowledged into a baseline, so a baseline never converts an inconclusive run into a pass.

Primary command output is written to stdout and ends with a line feed. Successful help is also stdout. Diagnostics and human-readable scan summaries are written to stderr. An expected failure writes one concise cause to stderr, leaves stdout empty, and does not print a stack trace or partial primary result.

Successful `scan --format text|markdown` writes its report to stdout and a labeled summary to stderr. Successful JSON output contains its summary and diagnostics in the document and therefore emits no duplicate stderr summary. That exemption holds only while the document states everything the stderr summary states, including facts no counter implies: whether external evidence was collected at all, and what the rendered view excluded. `--quiet` suppresses the human-readable stderr summary, never the stdout result. `--verbose` may add diagnostics to stderr and additional report fields, but does not change the result.

<a id="contract-findings-split"></a>

The summary's `Findings` line counts warnings on unresolved components separately from warnings on components that resolved, because the two ask for different responses. Failing to read an additional evidence source is routine, and when the component reached one license from other evidence the warning changed no outcome; a component with no settled license is where a warning describes the result. Both counts are always shown, so nothing becomes invisible by being reclassified as ordinary. The partition is by the status of the component a warning is attached to and asserts no causation: a warning on an unresolved component is not thereby the reason it is unresolved, which the [unresolved section](#contract-unresolved-section) states separately.

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
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS SUPPLIED
```

Verbose output additionally includes `PURL`.

<a id="contract-component-supply"></a>

`SUPPLIED` names the input kinds that supplied a component, as `sbom`, `package-manager`, or both. Canonical JSON carries the same tokens in a `suppliedBy` array, so one vocabulary describes both views and a collection needs no combined value such as "both".

The value is present in every report, including a scan of one input where it is constant. A field that appears only when a collection mixed kinds would force a reader to determine the input composition before it could interpret the field's absence, and would leave "this scan had one input" indistinguishable from "an older Ol wrote this report".

Supply is what makes a combined scan readable. Two inputs rarely enumerate the same set: a lockfile can hold entries an SBOM omits, and a module graph can count modules that never enter a build. Ol reports the union and never fills a gap in one input from the other, so the difference between the inputs is only visible if each component states which of them saw it.

<a id="contract-empty-inventory"></a>

An input Ol recognized but that contributes no components produces a `No components` statement in every `scan` view and an `input_declares_no_components` entry in the canonical JSON report's top-level warnings. The statement belongs to the primary result, so `--quiet` does not suppress it.

Silence would be the one false negative a policy gate cannot recover from: every count is zero, `check` finds no violation, and the run is indistinguishable from a project whose dependencies are all allowed. The ordinary causes are not exotic — an unrestored project, an `obj` directory left from a different build, an SBOM generated before install. It is not a command failure: the input was read and the report is complete, and only the reader knows whether "no dependencies" is the expected answer for that input. The condition is the resolved inventory, not the displayed view, so a `--dependency` filter that excludes every component is explained by the filter line rather than reported as an empty input.

<a id="contract-unresolved-section"></a>

A status alone does not tell a reviewer what to do next, and the mechanism that left a license unresolved decides that: wait for Ol to gain a capability, open a document, or ask the publisher. So `text` and `markdown` component views follow the table with an `Unresolved components` section listing each non-`matched` component as `NAME VERSION REASON [REFERENCE] [via PATH]`; `markdown` renders the same fields as columns and states an absent `PATH` as `-`.

`REASON` is the one mechanism that best explains the component, selected from the most specific to the most general so an unread license file is not described merely as an unusable repository. A component with no mechanism is omitted rather than listed with its status again, and the whole section is omitted when it would be empty; a report where nothing is unresolved is unchanged.

A root component is omitted too. It is the subject of the scan rather than a dependency of it, and [policy evaluates all non-root components](#contract-policy-checks), so listing one asks a reviewer for work no check will ever require. It stays in the table, because the report must not stop saying what the input described. This also keeps the section free of the directory path a generator uses to name the root of a directory scan.

Most reasons are the warning identifier the JSON report already uses, so one vocabulary describes both. Three are derived instead, one per [declared license reference](spdx.md#contract-declared-license-reference) kind, because an unread declaration is not a collection failure and no source records it as one:

| Declared kind | `REASON` | What the reviewer does |
|---|---|---|
| artifact path | `declared_license_file_not_collected` | Open that path inside the published package. |
| inline text | `declared_license_text_not_collected` | Read the license text the registry metadata itself carries. |
| location | `declared_license_location_not_collected` | Follow the URL the publisher named. |

These are ecosystem-neutral by construction: what a reviewer does next follows from the kind of place named and not from which registry answered. A named file or embedded text outranks a repository outcome because it is a document that certainly answers the question, while a URL ranks below one because it may lead anywhere. When several sources declare different kinds for one component, the strongest kind present decides.

A fourth reason is derived the same way. `license_classifier_not_specific` says the value is a [PyPI license classifier that names a license family](spdx.md#contract-license-family-classifier) rather than a license, so it can never resolve however much evidence is collected. It ranks below every reason above because it names no document: it is worth stating only when nothing points somewhere a reviewer could read, which is why `sortedcontainers` reports it while `python-dateutil`, whose repository holds a license file GitHub could not classify, still reports `license_not_recognized`.

<a id="contract-dependency-path"></a>

`PATH` is the deterministic shortest root-to-component dependency path, hops joined by ` > `, and it is the field that names something a reviewer can change: a transitive component is fixed by moving the direct dependency that pulled it in, not by editing the component the row names. It is stated only when it names more than the row already does. A direct dependency is its own introducer, and a component the input never linked to a root has no proven introducer at all, so both are reported as an absent path rather than as a one-hop path — inferring an introducer from the direct/transitive classification would assert a relationship the input never described. The same path appears in the `check` violation table and as the SARIF `dependencyPath`, so all three projections of one run state one fact.

`REFERENCE` is a location Ol observed but did not read. A declared license reference supplies it whenever one names a place, because the place a publisher named outranks any place Ol chose to look. Inline text names no place and is retained with an empty value, so it is skipped rather than printed as a blank reference or allowed to hide a location another source stated. Otherwise a reference is present only for the two mechanisms whose subject is a document Ol did not read: `license_not_recognized` supplies the repository license file GitHub could not identify, and `unsupported_source_repository` supplies the repository URL Ol cannot collect from. Those two are tied to the selected reason, because a project homepage printed beside an unread license file would read as the place that file can be found. Ol never constructs a URL evidence did not supply.

The section is part of the primary result, so `--quiet` does not suppress it. Grouped views do not carry it, because they display groups rather than components. Canonical JSON is unchanged: it already retains every warning and its typed provenance.

<a id="contract-output-formats"></a>
<a id="contract-json-report"></a>

`scan` supports `text`, `markdown`, and canonical `json`; the default is `text`. Canonical JSON has a top-level `schemaVersion` and contains producer, input, SPDX, cache/network metadata, collection mode, view scope, the complete inventory and graph, component results or grouped results, summary, and warnings. `metadata.collection.externalEvidence` is `collected` or `not-collected`; a run with `--no-external-evidence` and a run that collected and had nothing to fetch are otherwise indistinguishable, because both leave every collection counter at zero. `metadata.view` records the applied `dependencyFilter` and the `excludedCount` and `excludedUnknownCount` it removed, so a filtered report cannot be read as a complete one. Both are present in component and grouped reports. Consumers must reject or explicitly migrate unsupported schema versions.

The complete inventory is independent of sorted, filtered, or grouped views. Occurrence indexes address `inventory.components`, never displayed component or group indexes. The report identifies inputs with logical references and content hashes. It accepts input and SPDX JSON with an optional UTF-8 BOM.

<a id="contract-report-privacy"></a>

No value Ol constructs for a report, baseline, SARIF document, or diagnostic may contain a token value, an absolute local path, or a hidden cache path. Use logical identifiers, basenames, relative paths, and hashes. Authentication may be reported only as a mode, never as a credential value.

The rule binds what Ol writes about itself, not what an input said. A component's name, version, and identifiers are the input's own statement, and rewriting them would make the report disagree with the document it describes and break correlation with the source identifier a reader uses to find the component again. A generator that scans a directory names its root component after that directory, so an absolute path can reach a report that way; the path is then a fact about the input rather than something Ol disclosed about the machine it ran on. Anyone publishing a report generated from a local tree should expect it to carry whatever identity the generator wrote.

<a id="contract-purl-prefix"></a>

`--exclude-packages` and `--skip-evidence-packages` share one package URL prefix rule. Matching is ordinal, case-sensitive, and anchored at `/`, `.`, and `@` separators. A full versioned purl matches exactly; a component without a purl never matches. Empty values, non-`pkg:` values, and a prefix naming no ecosystem are invalid. Duplicate prefixes have no additional effect, and supplied order is retained for verbose match counts.

A prefix may stop at the ecosystem, so `pkg:github/` selects every component of that type. This was once rejected, on the reasoning that no single entry should be able to select a whole ecosystem by mistake. What that reasoning missed is that an SBOM generator can catalogue an entire ecosystem the project never depended on — GitHub Actions read out of workflow files are the case that prompted the change — and the only remaining way to drop them was to enumerate every namespace the generator happened to emit, which varies with the generator and the repository. Refusing the intent did not make it wrong, only unreachable, and the list of unsupported ecosystems will keep growing.

Breadth is answered by visibility rather than refusal. The count of selected components is always reported and verbose output attributes it per prefix, so an over-broad entry states its own effect rather than acting silently. A prefix naming no ecosystem stays invalid, because it selects everything and cannot be a considered choice.

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

Ol consumes already resolved inventories; it does not resolve manifests or version ranges. Directory discovery uses only registered resolved-input names, does not follow reparse points, and requires complete companion-file sets for Go and Composer. Content signatures, not filenames or registration order, determine a document's format. Unsupported versions, no match, ambiguous matches, malformed companion sets, and more than one SBOM document are input failures.

Repeated and directory inputs are deduplicated by resolved file path and processed in deterministic logical-path order. Multiple package-manager formats form one collection while retaining their own contexts and graphs; Ol does not invent edges between inventories.

<a id="contract-format-identity"></a>

Each registered format declares what makes two observations the same package, and that declaration applies whether one input was scanned or several. A resolved input that tracks distinct installations counts a package once per installation, because two copies at different paths are two things it resolved. An SBOM declares identity to be the package URL, so a document that lists one purl under several component entries describes one component; the entries remain as occurrences, so the graph and the count of what the document stated are both kept. Applying the rule only when inputs were combined made one document report a different shape depending on whether a lockfile happened to be scanned beside it.

<a id="contract-input-combination"></a>

One SBOM document may be scanned together with any number of package-manager inputs. The two describe one resolution at two granularities, so combining them lets evidence from both reach the same component; a second repository-wide document would be a contradiction in the input rather than something Ol can resolve, which is why only one is accepted.

Components are matched across that boundary on package URL identity: the part of the purl before any qualifier or subpath, compared with the case rule the package-manager format already declares. Whole-purl comparison would miss matches, because Ol and SBOM generators disagree about which qualifiers to emit for the same artifact, and because ecosystems differ on whether casing is significant.

Go is the exception to dropping the subpath, because for Go the subpath is part of the module path rather than something beside it. Generators split a module path at different points: `github.com/ugorji/go/codec` is written both as that name and as `github.com/ugorji/go` with subpath `codec`, and `github.com/cpuguy83/go-md2man/v2` likewise carries its major version in either place. Ol therefore matches a Go purl on name and subpath joined. Dropping it instead is worse than missing a match: it leaves a submodule looking like its parent, which would attach one module's license to another.

Package-manager inputs own the resulting rows and the SBOM folds into them. A package manager distinguishes installed copies that an SBOM states once, so a single SBOM component answers for every copy and its declaration reaches all of them. Collapsing them instead would report fewer components than the package-manager input alone, and Ol does not shrink a population. The SBOM's own occurrence attaches to the first matching row in input order; that is the only endpoint its graph can name without inventing a distinction the SBOM never made. A purl no package-manager input supplies keeps its own row, and a component without a purl is never matched.

Matching only spans the SBOM boundary. Two lockfiles describe two installations, so a purl they share is two observations rather than one, and package-manager inputs never fold into each other. Each input keeps its own contexts and graph, and folding adds evidence without replacing anything the receiving row already states.

Because the package-manager row is the one that survives, a folded component is reported with that input's purl spelling and qualifiers. The same component can therefore be printed as `pkg:nuget/Direct.Package@1.0.0` by a collection and as `pkg:nuget/direct.package@1.0.0` by a scan of the SBOM alone. Nothing is lost, but a consumer comparing two reports must compare purl identity rather than the printed string, exactly as the matching rule does.

Combining inputs can produce a `conflict` that neither input produces alone, because it introduces a comparison the inputs never had: what one declares against what the other declares. That is the point of scanning them together rather than a cost of it. Report identity follows the mixture: the input kind is `collection`, and the SBOM-specific identity fields are omitted because the collection's reference and hash describe every input rather than the SBOM.

`scan` collects external package and source evidence by default. `--refresh` bypasses reusable entries. `--no-external-evidence` reads neither external sources nor their caches and reports that collection was not attempted.

When a GitHub rate limit stops source collection, `scan` writes a stderr notice naming the limit kind, the reset instant when one was supplied, and the one change that would let the next run succeed: `OL_GITHUB_TOKEN` for an unauthenticated primary limit, waiting for the reset or narrowing the scan for an authenticated one, and a lower `--concurrency` for a secondary limit. The notice is not part of the summary and is written even under `--quiet`, because it explains missing evidence that the suppressed counters would otherwise be the only sign of. It states that the affected components were not cached, so a later run collects them normally. Ol does not wait out a rate limit; the behavior it reports is specified in [source.md](source.md#contract-source-rate-limit).

<a id="contract-skip-evidence-packages"></a>

`--skip-evidence-packages <prefixes>` disables registry, repository, and cache collection only for matching components. Each remains in the report and receives an `unknown` evidence candidate with `external_evidence_not_collected`; input evidence may still resolve its final status. An unresolved result fails closed in `check` unless a baseline acknowledges that component. Combined with `--no-external-evidence`, the option has no additional effect.

<a id="contract-dependency-filtering"></a>

`--dependency root,direct,transitive,unknown` filters only the rendered view; analysis always uses the complete inventory. When filtering to `direct`, the stderr summary identifies excluded `unknown` relationships, and canonical JSON records the same counts in `metadata.view`. `--sort` accepts `name`, `version`, `license`, `ecosystem`, `dependency`, `status`, and `purl`; default order is `ecosystem,name,version`, ascending. `--group-by` accepts all of those except `purl`, adds `COUNT`, and produces a grouped view. Empty filter, sort, or group lists are invalid.

The cache root is selected by `--cache-dir`, then `OL_CACHE_DIR`, then legacy category-specific roots, then the platform user-cache location. A supplied root is an isolation directory: Ol manages only its `package-metadata` and `source-repository` children. Cache paths never appear in reports. Cache schemas are specified in [cache_format.md](cache_format.md).

### `ol check`

<a id="contract-policy-checks"></a>
<a id="contract-policy-report-input"></a>

```text
ol check --report <scan.json> --allow-licenses <SPDX-ids>
```

`check` reads one ungrouped canonical JSON report. It performs no dependency parsing, evidence collection, cache access, or network access. Invalid, malformed, grouped, or unsupported-schema reports are command failures.

`--allow-licenses` is a required comma-separated list of SPDX License Identifiers. Whitespace and casing are normalized using active SPDX data. Empty entries, expressions, exception identifiers, natural-language names, and unknown identifiers are invalid. For a `matched` component, `AND` requires both operands, `OR` requires either operand, parentheses retain SPDX precedence, and `WITH` has the policy value of its base license.

Policy evaluates all non-root, non-excluded components. `unknown` dependency type remains in scope. `unknown`, `conflict`, `ambiguous`, `invalid`, and `error` fail closed unless a baseline acknowledges that component under the rules below. All violations are collected and deterministically ordered.

Violations are printed as `Package Version Ecosystem Purl License/Status Reason Path`, tab-separated. `Path` is the [dependency path](#contract-dependency-path) the scan report's inventory proves, stated as `-` when the report names no introducer for that component.

`--allow-dev-licenses` adds identifiers only for components proven development-only by resolver data persisted in the report. It uses the same identifier validation as `--allow-licenses`; a supplied empty value is invalid. Any runtime or usage-unknown occurrence keeps the component under the primary allow-list. Inputs without reliable development reachability therefore fail closed. When supplied, the count admitted by this policy is always printed.

`--exclude-packages` removes matching purls from policy evaluation, baseline generation, violation output, SARIF, and the passing count, but never changes the scan report. The excluded count is always printed when the option is supplied. This is a policy-scope decision; it is distinct from `scan --skip-evidence-packages`.

<a id="contract-policy-baseline"></a>

`--baseline <file>` supplies a baseline of acknowledged unresolved components: the components a reviewer has already seen and accepted because their evidence cannot be resolved. Acknowledging them removes their violations, so that the unresolved set cannot grow silently rather than so that it becomes empty. `--update-baseline` requires `--baseline`, replaces it with a deterministic complete snapshot, and then evaluates against that snapshot; it does not merely append or suppress evaluation.

Only `unknown`, `ambiguous`, `conflict`, and `invalid` may be acknowledged into a baseline, and only when no recognizable candidate is rejected by the active allow-list. `matched` belongs in the allow-list and `error` represents an operation to repair. Entries identify the component and fingerprint its status and evidence, so changed evidence or identity expires that acknowledgement. Applying a baseline rechecks the active allow-list. Missing, malformed, or incompatible baselines exit `1`; acknowledged counts, including zero, are reported.

<a id="contract-policy-sarif"></a>

`--sarif <file>` writes the same violations as SARIF 2.1.0 without changing stdout, declaring `$schema` as `https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json`. Stable rule IDs are `OL0001` not allowed, `OL0002` conflict, `OL0003` unresolved, `OL0004` ambiguous, `OL0005` invalid expression, and `OL0006` evidence error. Results use logical component locations and include the deterministic shortest dependency path when available; Ol does not invent source positions. Development-policy allowances are recorded as run properties rather than findings.

Policy files, deny-lists, per-package exceptions, and concluded licenses are outside this command's contract. Dependency-scope policy is limited to `--allow-dev-licenses` above; no other scope distinction is evaluated.

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

- "This output is self-describing, so it needs no summary" is a contract to be checked, not a property to be asserted. JSON was exempted from the stderr summary on that basis and then drifted from it in the two places where nothing else could recover the fact: a `--no-external-evidence` run and a run that collected and found nothing to fetch emitted byte-identical metadata, and a `--dependency`-filtered report looked exactly like a complete one. Both are the same failure — the absence of work and the absence of a subject read alike once only counters survive — and both are recoverable only if the producer states what it did rather than what it counted. Whenever one view is excused from repeating another, the excuse is a claim about content that a test has to hold in place.
- Detection must validate the complete document. Optional UTF-8 BOMs are common, and selecting the first recognizable marker can misclassify a document that contains conflicting format markers.
- The documented command shape is part of the contract. A positional cache category implemented as both an option and an argument made help ambiguous and left conflicting forms without a principled winner.
- Canonical artifacts must be stable under irrelevant ordering. Baseline fingerprints sort evidence claims before hashing, and report views never reuse their sorted indexes as inventory indexes.
- A baseline acknowledgement is not a license status. It removes a policy violation while preserving the original unresolved status and evidence, which keeps factual resolution separate from organizational policy.
- Root and unknown dependency relationships are not interchangeable. The inspected subject is outside dependency policy, while an unknown relationship remains fail-closed because missing graph evidence cannot prove first-party ownership.
- Safe defaults matter across every parser: an absent license must become `unknown`, never an empty `matched` result.
- Policy exclusion and skipped collection solve different problems. Exclusion belongs to `check` and changes scope; skipped collection belongs to `scan` and preserves a visible unresolved component.
- Option names should describe observable behavior. `--skip-evidence-packages` and `--exclude-packages` make their distinct effects visible without claiming ownership or package provenance that Ol cannot verify.
- A collection failure and a policy violation must not share an exit code. Both leave a component unresolved, but only one is a fact about licensing, and a CI job that cannot tell them apart either retries genuine violations or treats registry outages as findings. The split needed no new status because `LicensePolicyViolationKind.Error` already carried the distinction to the renderer.
- A canonical identifier is not always the one a user can type. npm purls encode a scope as `%40acme` while every other tool, and the package name itself, spells it `@acme`, so requiring the encoded form made a correct-looking prefix silently match nothing. Canonicalizing a segment-initial `@` on input accepts both spellings without weakening the boundary rule, because the version separator is positionally distinguishable from a namespace marker.
- A definitive negative answer is not a collection failure. Classifying registry `404` as an error made a package published only to a private feed permanently unresolvable, because status `error` cannot be acknowledged — the exact dead end that motivated an escape-hatch option. Reclassifying it as unknown fixed every ecosystem at once, including the ones whose input cannot express where a package came from, and left `error` meaning only what a retry could change.
- A lockfile download URL is not evidence of where a package is published. Withholding the public-registry identity from npm and pnpm entries whose host was not `registry.npmjs.org` looked like the rule Cargo and Bundler already apply, but those record a registry identity while npm records a download URL. A corporate proxy serves public packages from an internal host, so the rule would have silently disabled enrichment for an entire proxied dependency tree — harming exactly the organizations it was meant to help. It was implemented and then withdrawn before release.
- Diff change kinds must remain independently filterable. A version change must not hide a simultaneous license or status change in machine-readable output.
- The license that motivates a scope policy is not the license that turns up. `--allow-dev-licenses` was designed for LGPL reaching a project through dev tooling, but a measured Vite/TypeScript/ESLint/Vitest toolchain (255 entries, 250 development) contained no LGPL at all — what failed a permissive allow-list was `CC-BY-4.0` and a Python-2.0 package, both development-only. Keeping the mechanism license-agnostic, accepting any SPDX identifier rather than a curated copyleft set, is why the measurement did not invalidate the design.
- A count that mixes two populations reports the larger one. `Findings: 63 warnings` on a ZLinq scan where every component resolved read as sixty-three things to investigate; sixty-two were collection attempts against repositories outside GitHub, on components that already had a license. Across nine Cysharp repositories 397 of 737 warnings sat on resolved components, and one repository was 0 against 15. The fix was not to suppress the routine ones — that would hide the run's real behavior — but to stop summing populations that ask for different responses.
- A fact that only the machine-readable projection carries is a fact the human never gets. The dependency path existed in SARIF from the start, so `check` and the unresolved section printed the offending package without ever naming the direct dependency that introduced it — the one thing a reviewer can act on. Measured against nine Cysharp repositories, 238 of 331 unresolved components traced back to four ageing test and benchmark packages, which the text reports could not show. Whenever one output answers "what do I change" and another does not, the gap is a defect in the one that does not.
- A per-finding graph search costs the graph times the findings. Resolving the shortest root path independently per violation rescanned every edge each time, which stayed invisible while only SARIF asked and became the dominant cost once every unresolved row did: one breadth-first pass over an adjacency index answered 1292 findings roughly four times faster than the per-finding search answered them alone. Traversals belong to the report, not to the row.
- A derived per-component value should ride on the array that already gets sorted. Development usage was expected to need a display-to-inventory index mapping, because top-level `components` are sorted by `ecosystem,name,version` while occurrences are in input order. Persisting usage on the display components instead lets it pass through the same sort, so `check` re-resolves nothing and no index mapping exists to go stale. Inputs that determine no usage store nothing.
