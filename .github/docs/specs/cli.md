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
| `3` | `check` completed successfully but proved nothing, because every finding is a collection failure or the report states its input declared no resolved dependencies. |

Exit codes `2` and `3` belong only to policy results. `scan` and `diff` do not use changes or unresolved components as an alternate failure code.

`3` exists because the three states a CI job must tell apart are "fix the pipeline", "fix the dependency", and "try again". A component whose evidence could not be collected proves nothing about its license, so reporting it as a policy violation would make a registry outage indistinguishable from a forbidden license. It is not exit `1` either: the command ran, produced a complete report, and component-level collection failures are best-effort results rather than command failures. A run is inconclusive only when **every** violation is a collection failure; one genuine finding alongside them yields `2`, because a real violation is the more actionable fact. Status `error` cannot be acknowledged into a baseline, so a baseline never converts an inconclusive run into a pass.

<a id="contract-inconclusive-empty-report"></a>

An [empty inventory](#contract-empty-inventory) reaches `3` by the same reasoning through a different route. There are no findings to classify, so the letter of the rule above does not reach it, but the state it describes is identical: the command ran, the report is complete, and it proves nothing about any license. A pass would be the worse answer rather than the neutral one, because zero violations over zero components is exactly what a project whose dependencies are all allowed produces, so the two become indistinguishable at the only place a CI job looks. Nor can a baseline convert it: a baseline acknowledges components, and this report has none.

The boundary that makes this safe without an opt-out was measured rather than assumed. A project that legitimately resolves no dependencies still declares a root, so its inventory is not empty and it stays at `0`; and a scan that found nothing to read at all already fails at `1` before a report exists. What remains for `3` is only the case the warning was written for: Ol read a real input and that input declared nothing.

Primary command output is written to stdout and ends with a line feed. Successful help is also stdout. Diagnostics and human-readable scan summaries are written to stderr. An expected failure writes one concise cause to stderr, leaves stdout empty, and does not print a stack trace or partial primary result.

Successful `scan --format text|markdown` writes its report to stdout and a labeled summary to stderr. Successful JSON output contains its summary and diagnostics in the document and therefore emits no duplicate stderr summary. That exemption holds only while the document states everything the stderr summary states, including facts no counter implies: whether external evidence was collected at all, and what the rendered view excluded. `--quiet` suppresses the human-readable stderr summary, never the stdout result. `--verbose` may add diagnostics to stderr and additional report fields, but does not change the result.

<a id="contract-findings-split"></a>

The summary's `Findings` line counts warnings on unresolved components separately from warnings on components that resolved, because the two ask for different responses. Failing to read an additional evidence source is routine, and when the component reached one license from other evidence the warning changed no outcome; a component with no settled license is where a warning describes the result. Both counts are always shown, so nothing becomes invisible by being reclassified as ordinary. The partition is by the status of the component a warning is attached to and asserts no causation: a warning on an unresolved component is not thereby the reason it is unresolved, which the [unresolved section](#contract-unresolved-section) states separately.

Unknown commands, command groups without a subcommand, and missing required command arguments exit `1`. `ol` with no arguments shows root help; explicit `--help` and `-h` show help and exit `0`. Group help lists only that group's subcommands.

<a id="contract-repeated-option"></a>

An option supplied more than once exits `1`, naming the option. The exceptions are the options documented as repeatable, which accumulate. Ol has no layer a later value could override — no configuration file, no environment defaults, and policy files are outside the `check` contract — so no invocation means "replace what I said before", and a repeat is either an accident or an intent to accumulate. Resolving it by position served neither: it changed the policy a run enforced while reporting nothing, and the same two flags in the other order gave a different answer. Measured on one report, `--allow-licenses` written twice yielded three violations or seven depending on order, and a second `--exclude-packages` silently discarded a prefix that matched thirty-six components. Everything after a `--` escape is a value, so a repeat there is not an invocation error.

An option that accumulates is stated as such in its own contract; an option that does not is not made repeatable to serve a caller that wants to combine values, because the value syntax already does that where combining is meaningful. `--input`, `--exclude-input-path`, and [`--baseline`](#contract-policy-baseline) accumulate; every other option is single-use.

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

The summary totals the same fact: the stderr `Supplied by` line and the canonical JSON `summary.supply` object report how many components each input kind supplied alone and how many both supplied. Per component the field answers "which input saw this one"; only the totals answer whether an input earned its place in the collection, which is the question a combined scan is configured to ask and the one nothing in the report reached without walking every component. The totals follow the same always-present rule as the field, and describe the displayed population the rest of the summary describes.

`--verbose` follows the totals with one line per ecosystem, in ordinal order, counting the same three populations; an empty ecosystem is stated as `-`, as the component table states it, so the lines sum to the totals. The totals say whether a second input earned its place and this says where: one ecosystem supplied by both inputs beside another supplied by one is the ordinary shape of a polyglot scan rather than a defect, because a source-tree SBOM generator reads some ecosystems' lockfiles and not other ecosystems' resolver output. It is a verbose diagnostic rather than a summary fact, because the report already carries `ecosystem` and `suppliedBy` per component and a consumer of the canonical JSON can compute exactly this; the canonical JSON is therefore unchanged.

Ol prints those counts and draws no conclusion from them. A threshold that called a one-sided ecosystem a scope mismatch was designed and rejected: measured across eight polyglot repositories it would have fired on all eight, every one correctly configured, because the NuGet population is package-manager-only in all of them. A hint that always fires is one readers learn to skip, which costs more than the missed hint — the same reasoning that limits the detected-candidate report to ecosystems the scan produced nothing from. Stating the split lets a reader recognize the case; naming it would be a guess about intent that the inputs do not carry.

<a id="contract-empty-inventory"></a>

An input Ol recognized but that contributes no components produces a `No components` statement in every `scan` view and an `input_declares_no_components` entry in the canonical JSON report's top-level warnings. The statement belongs to the primary result, so `--quiet` does not suppress it.

Silence would be the one false negative a policy gate cannot recover from: every count is zero, `check` finds no violation, and the run is indistinguishable from a project whose dependencies are all allowed. The ordinary causes are not exotic — an unrestored project, an `obj` directory left from a different build, an SBOM generated before install. It is not a command failure: the input was read and the report is complete, and only the reader knows whether "no dependencies" is the expected answer for that input. The condition is the resolved inventory, not the displayed view, so a `--dependency` filter that excludes every component is explained by the filter line rather than reported as an empty input.

The warning reaches the gate. `check` reads the report's top-level warnings, states `License check incomplete: the report states its input declared no resolved dependencies.` instead of an allow-list result, and [exits `3`](#contract-inconclusive-empty-report). Stating it only in `scan`'s views was the same defect one step later: `scan` warned in every projection it wrote, the reader dropped the array at the persistence boundary, and the command whose whole product is an exit code reported a pass. A fact only one projection carries does not reach the reader of the other.

`check` restores every top-level warning and acts on the identifiers it knows, rather than restoring the one identifier it acts on. An identifier a later Ol adds is then carried by an older reader instead of rejected by it, which is the additive case the schema version is not the guard for; `deprecated_spdx_identifier` is restored the same way and gates nothing, because it describes an identifier the report already carries per component and changes nothing about what the run proved. A report written before top-level warnings existed is read as having stated none, which is what it was.

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

Two more reasons are derived the same way. [`package_metadata_no_purl`](packagemanager.md#contract-unqueryable-purl) says the component carries no package identity, so no source could ever be asked about it; it is derived from the empty purl the report already carries, and therefore stated whether or not the run collected external evidence.

`license_classifier_not_specific` says the value is a [PyPI license classifier that names a license family](spdx.md#contract-license-family-classifier) rather than a license, so it can never resolve however much evidence is collected. It ranks below every reason above because it names no document: it is worth stating only when nothing points somewhere a reviewer could read, which is why `sortedcontainers` reports it while `python-dateutil`, whose repository holds a license file GitHub could not classify, still reports `license_not_recognized`.

<a id="contract-dependency-path"></a>

`PATH` is the deterministic shortest root-to-component dependency path, hops joined by ` > `, and it is the field that names something a reviewer can change: a transitive component is fixed by moving the direct dependency that pulled it in, not by editing the component the row names. It is stated only when it names more than the row already does. A direct dependency is its own introducer, and a component the input never linked to a root has no proven introducer at all, so both are reported as an absent path rather than as a one-hop path — inferring an introducer from the direct/transitive classification would assert a relationship the input never described. The same path appears in the `check` violation table and as the SARIF `dependencyPath`, so all three projections of one run state one fact.

`REFERENCE` is a location Ol observed but did not read. A declared license reference supplies it whenever one names a place, because the place a publisher named outranks any place Ol chose to look. Inline text names no place and is retained with an empty value, so it is skipped rather than printed as a blank reference or allowed to hide a location another source stated. Otherwise a reference is present only for the two mechanisms whose subject is a document Ol did not read: `license_not_recognized` supplies the repository license file GitHub could not identify, and `unsupported_source_repository` supplies the repository URL Ol cannot collect from. Those two are tied to the selected reason, because a project homepage printed beside an unread license file would read as the place that file can be found. Ol never constructs a URL evidence did not supply.

The section is part of the primary result, so `--quiet` does not suppress it. Grouped views do not carry it, because they display groups rather than components. Canonical JSON is unchanged: it already retains every warning and its typed provenance.

<a id="contract-output-formats"></a>
<a id="contract-json-report"></a>

`scan` supports `text`, `markdown`, and canonical `json`; the default is `text`. Canonical JSON has a top-level `schemaVersion` and contains producer, input, SPDX, cache/network metadata, collection mode, view scope, the complete inventory and graph, component results or grouped results, summary, and warnings. `metadata.packageArtifacts` records restored-artifact targets, documents, and SPDX matches. `metadata.declaredGitHubFiles` records exact declared-file targets, GitHub requests, cache hits and misses, documents, SPDX matches, and fetch errors. Text and Markdown expose the same full-scan counters in their stderr summary. `metadata.collection.externalEvidence` is `collected` or `not-collected`; a run with `--no-external-evidence` and a run that collected and had nothing to fetch are otherwise indistinguishable, because both leave every collection counter at zero. `metadata.view` records the applied `dependencyFilter` and the `excludedCount` and `excludedUnknownCount` it removed, so a filtered report cannot be read as a complete one. `metadata.inputDiscovery` records `detectedFileCount`, `ignoredCandidateCount`, `ignoredCandidates`, and `incompleteInputSetCount`. All are present in component and grouped reports. Consumers must reject or explicitly migrate unsupported schema versions.

<a id="contract-input-discovery-metadata"></a>

`metadata.inputDiscovery` exists because the JSON exemption from the stderr summary is conditional, and this was the part the document did not hold up. An ignored candidate and a skipped companion set both make the report smaller without leaving any trace in the components it does contain, so no counter elsewhere implies them; and `--format json` writes no stderr summary at all, which made the recommended CI path the one path that could not tell a complete scan from one that silently skipped an ecosystem.

`ignoredCandidates` names the directory patterns Ol declares — `Cargo.lock`, `Cargo.toml`, `*.csproj` — rather than the paths where they were found. That is a closed vocabulary Ol owns, so the field satisfies the [report-privacy contract](#contract-report-privacy) by construction, is identical on any machine that discovered the same candidates, and can be compared as a set. Paths the invocation excluded stay in `metadata.inputScope`, which is a different fact: one is what the caller ruled out, the other is what discovery found and could not use.

Every field is written unconditionally and with every count, for the reason `inputScope` is: a field that appeared only when it had something to say would leave "discovery ignored nothing" indistinguishable from "an older Ol wrote this report", and would force a reader to determine the document's shape before reading it. Adding the object does not change `schemaVersion`, because a consumer reading a key it already knows is unaffected by one beside it; a reader that wants the fact must therefore treat an absent object as unstated rather than as zeros, the same distinction `metadata.view` draws between a count Ol supplied and a count Ol defaulted.

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
| `ol skill install` | Install the bundled license-scan Agent Skill into a workspace. | Written file location. |
| `ol skill export-plugin` | Export the skill as a portable Agent Plugin package. | Written plugin location. |
| `ol cache clear` | Clear Ol-managed evidence caches. | Cleared categories. |
| `ol spdx version` | Show the active SPDX data source. | Active version and user-data location. |
| `ol spdx list` | List installed SPDX data versions. | Installed versions with the active version marked. |
| `ol spdx update` | Download current SPDX data. | Installed version. |
| `ol spdx use` | Activate an installed SPDX data version. | Active version. |
| `ol spdx clear` | Remove user-managed SPDX data. | Confirmation. |

### `ol scan`

```text
ol scan --input <file-or-directory> [--input <file-or-directory> ...] [--exclude-input-path <path> ...]
```

`--input` is required and repeatable. It accepts CycloneDX JSON, SPDX JSON, or supported resolved package-manager inputs: NuGet assets, npm, pnpm, Yarn Classic/Berry, Cargo metadata, Go module graph, pip inspect, Composer, Bundler, Maven dependency tree, Swift `Package.resolved`, and CocoaPods lock data. `--input-format` defaults to `auto`; an explicit format is an assertion and must match every discovered document.

`--exclude-input-path` is repeatable and removes existing, exact file or directory paths from directory-input discovery. A relative exclusion is resolved once from the current working directory, using the same path semantics as `--input`; it is not expanded independently beneath each directory input. The resolved exclusion must be a strict descendant of at least one directory input, unless it contains an explicitly named directory input, in which case that input is skipped. Missing paths and paths outside every input are invalid. When exclusions are supplied, inputs and exclusions are resolved together from their common parent using file-system entry casing, then matched ordinally at path-segment boundaries. Glob syntax is not supported. An explicitly named file beneath an exclusion remains an input failure, while an explicitly named directory beneath an exclusion is skipped rather than traversed. This makes repeated `--input` declarations deterministic: excluded inputs contribute no discovered files, and the scan continues with the remaining inputs (or reports that no registered inputs remain).

Excluded directories are pruned before recursive enumeration, so inaccessible or expensive content below them is not visited. The exclusion applies equally to registered resolved inputs, known unsupported candidates, and incomplete companion-set discovery. It does not remove dependencies already represented by an included root lockfile or SBOM; generators such as Syft must receive the corresponding scope exclusion before producing such an input. Canonical JSON records the normalized logical paths in `metadata.inputScope.excludedPaths`, and the human-readable input-discovery summary reports their count and values.

Persisted input scope remains visible through the report lifecycle. `check` prints excluded input paths before its policy result. `diff` prints both boundaries when either report excludes a path, and its JSON output records `inputScope.previous`, `inputScope.current`, and whether the path sets changed. Reports produced before `inputScope` existed are read as having no excluded input paths.

Ol consumes already resolved inventories; it does not resolve manifests or version ranges. Directory discovery uses only registered resolved-input names and does not follow reparse points. Content signatures, not filenames or registration order, determine a document's format. Unsupported versions, no match, ambiguous matches, and more than one SBOM document are input failures. A failure on a discovered file names that file, because the user never named it and cannot otherwise tell which of the discovered inputs failed.

Ol also reports resolved inputs it could have consumed but did not. Automatic directory discovery detects `Cargo.lock`, `Cargo.toml`, and `*.csproj`, which are not themselves supported inputs, and names the supported input each one requires: `cargo metadata --format-version 1 --locked > cargo-metadata.json`, the same command without `--locked`, and `dotnet restore` producing `obj/project.assets.json`. A Cargo library does not commit its lockfile, so detecting only `Cargo.lock` left the whole ecosystem silently unscanned in exactly the repositories that publish crates; the manifest is the file every Cargo project has. A project carrying a lockfile always carries a manifest too, so `Cargo.lock` supersedes `Cargo.toml` whenever both are found: one unscanned ecosystem is reported once, and the reported advice is the one that reproduces the recorded resolution. `--locked` is offered only for the lockfile, because it cannot succeed without one. A detected candidate is reported only when the scan produced nothing from its ecosystem, so a repository that also supplies `cargo-metadata.json`, `project.assets.json`, or an SBOM carrying that ecosystem stays silent. That check is scan-wide rather than per directory, so one covered project silences the candidate for every project in the same scan; a hint that fires on a repository already scanning the ecosystem would train readers to ignore it, which costs more than the missed hint. The report is a warning that survives `--quiet`, because a silently unscanned ecosystem is the failure the hint exists to prevent, and it degrades to an input failure when discovery found no registered input at all. An explicit `--input-format` suppresses detection entirely: the format is an assertion about what to scan, so the ecosystems it excludes are a decision rather than an oversight. When unmatched content was named directly as `Cargo.lock` or a `*.csproj`, the input failure carries the same guidance instead of only the generic no-signature message.

Go and Composer need a complete companion-file set, and who named the file decides what an incomplete one costs. A file the user named is an assertion that Ol should read it, so a missing companion there is an input failure, as it is under an explicit `--input-format`. Directory discovery only proposes candidates, so an incomplete set it found is skipped, reported as a warning that survives `--quiet`, counted in the scan summary, and the remaining inputs are still reported. `composer.json` is the only discovered name that is an ordinary hand-written manifest rather than a generated artifact or a self-contained lock, so it turns up without `composer.lock` in vendored trees and fixtures that have nothing to do with the repository being scanned; letting one of those decide whether every other input is reported made `--input .` unusable on ordinary repositories. Skipping every discovered candidate remains an input failure, because an empty report reads as a repository without dependencies rather than one Ol could not read.

The human-readable scan summary reports the number of detected physical input files, the known candidates discovery ignored, the companion sets it skipped, and the distinct component ecosystems in ordinal order; a scan with no identified component ecosystem reports `none`. Canonical JSON states the same three counts in [`metadata.inputDiscovery`](#contract-input-discovery-metadata); the ecosystems are omitted there because the report's components already carry them.

Repeated and directory inputs are deduplicated by resolved file path and processed in deterministic logical-path order. Multiple package-manager formats form one collection while retaining their own contexts and graphs; Ol does not invent edges between inventories.

<a id="contract-format-identity"></a>

Each registered format declares what makes two observations the same package, and that declaration applies whether one input was scanned or several. A resolved input that tracks distinct installations counts a package once per installation, because two copies at different paths are two things it resolved. An SBOM declares identity to be the package URL, so a document that lists one purl under several component entries describes one component; the entries remain as occurrences, so the graph and the count of what the document stated are both kept. Applying the rule only when inputs were combined made one document report a different shape depending on whether a lockfile happened to be scanned beside it.

<a id="contract-input-combination"></a>

One SBOM document may be scanned together with any number of package-manager inputs. The two describe one resolution at two granularities, so combining them lets evidence from both reach the same component; a second repository-wide document would be a contradiction in the input rather than something Ol can resolve, which is why only one is accepted.

Components are matched across that boundary on package URL identity: the part of the purl before any qualifier or subpath, compared with the case rule the package-manager format already declares. Whole-purl comparison would miss matches, because Ol and SBOM generators disagree about which qualifiers to emit for the same artifact, and because ecosystems differ on whether casing is significant.

Go is the exception to dropping the subpath, because for Go the subpath is part of the module path rather than something beside it. Generators split a module path at different points: `github.com/ugorji/go/codec` is written both as that name and as `github.com/ugorji/go` with subpath `codec`, and `github.com/cpuguy83/go-md2man/v2` likewise carries its major version in either place. Ol therefore matches a Go purl on name and subpath joined. Dropping it instead is worse than missing a match: it leaves a submodule looking like its parent, which would attach one module's license to another.

Package-manager inputs own the resulting rows and the SBOM folds into them. A package manager distinguishes installed copies that an SBOM states once, so a single SBOM component answers for every copy and its declaration reaches all of them. Collapsing them instead would report fewer components than the package-manager input alone, and Ol does not shrink a population. The SBOM's own occurrence attaches to the first matching row in input order; that is the only endpoint its graph can name without inventing a distinction the SBOM never made. A purl no package-manager input supplies keeps its own row, and a component without a purl is never matched.

Matching only spans the SBOM boundary. Two lockfiles describe two installations, so a purl they share is two observations rather than one, and package-manager inputs never fold into each other. Each input keeps its own contexts and graph, and folding adds evidence without replacing anything the receiving row already states.

<a id="contract-folded-relationship"></a>

A folded row reports the relationship its resolver determined. The SBOM supplies one only where no resolver did, and never supplies `root`.

Relationship is graph-relative, so two inputs stating different values are usually describing different graphs rather than disagreeing about one, and the row belongs to the graph the scan is about. Reporting whichever value sat closest to a root made relationship the last field where a fold could overwrite what the receiving row already stated, while identity, purl spelling, qualifiers, source identifier, installation granularity, and repository URL all follow the receiving row. Nothing is lost by preferring the resolver: each input's occurrences and edges are preserved separately, so an SBOM's own relationship for a component stays derivable from the report. What changes is only which relationship the component-level value summarizes.

`root` is excluded from the fill-in as well as from the merge. Only an SBOM states it, and a package-manager input listing a component is itself the determination that the component is a dependency of the scanned resolution, so a folded root describes the receiving row no better than silence does. The distinction is not cosmetic: [policy skips a root](#contract-policy-checks), so admitting the value would let a second input withdraw a resolved dependency from the gate — the same failure that [an abstaining usage occurrence](#contract-development-usage-and-sbom) was ruled out for, in the field beside it. An SBOM root that no package-manager input answers for is unaffected, because no fold happens: it keeps its own row and stays a root, exactly as when the SBOM is scanned alone.

The gate direction also runs one way. A [`--dependency`-filtered report](#contract-dependency-filtering) narrows what `check` evaluates, so a resolved `transitive` restated as `direct` leaves a component the resolver placed in scope out of a `transitive` gate, while the reverse case reports `direct` under either rule. Preferring the resolver has no symmetric cost.

Because the package-manager row is the one that survives, a folded component is reported with that input's purl spelling and qualifiers. The same component can therefore be printed as `pkg:nuget/Direct.Package@1.0.0` by a collection and as `pkg:nuget/direct.package@1.0.0` by a scan of the SBOM alone. Nothing is lost, but a consumer comparing two reports must compare purl identity rather than the printed string, exactly as the matching rule does.

Combining inputs can produce a `conflict` that neither input produces alone, because it introduces a comparison the inputs never had: what one declares against what the other declares. That is the point of scanning them together rather than a cost of it. Report identity follows the mixture: the input kind is `collection`, and the SBOM-specific identity fields are omitted because the collection's reference and hash describe every input rather than the SBOM.

`scan` collects external package and source evidence by default. `--refresh` bypasses reusable entries. `--no-external-evidence` reads neither external sources nor their caches and reports that collection was not attempted.

When a GitHub rate limit stops source collection, `scan` writes a stderr notice naming the limit kind, the reset instant when one was supplied, and the one change that would let the next run succeed: `OL_GITHUB_TOKEN` for an unauthenticated primary limit, waiting for the reset or narrowing the scan for an authenticated one, and a lower `--concurrency` for a secondary limit. The notice is not part of the summary and is written even under `--quiet`, because it explains missing evidence that the suppressed counters would otherwise be the only sign of. It states that the affected components were not cached, so a later run collects them normally. Ol does not wait out a rate limit; the behavior it reports is specified in [source.md](source.md#contract-source-rate-limit).

<a id="contract-skip-evidence-packages"></a>

`--skip-evidence-packages <prefixes>` disables registry, repository, and cache collection only for matching components. Each remains in the report and receives an `unknown` evidence candidate with `external_evidence_not_collected`; input evidence may still resolve its final status. An unresolved result fails closed in `check` unless a baseline acknowledges that component. Combined with `--no-external-evidence`, the option has no additional effect.

<a id="contract-dependency-filtering"></a>

`--dependency root,direct,transitive,unknown` filters only the rendered view; analysis always uses the complete inventory. When filtering to `direct`, the stderr summary identifies excluded `unknown` relationships, and canonical JSON records the same counts in `metadata.view`. `--sort` accepts `name`, `version`, `license`, `ecosystem`, `dependency`, `status`, and `purl`; default order is `ecosystem,name,version`, ascending. `--group-by` accepts all of those except `purl`, adds `COUNT`, and produces a grouped view. Empty filter, sort, or group lists are invalid.

The cache root is selected by `--cache-dir`, then `OL_CACHE_DIR`, then legacy category-specific roots, then the platform user-cache location. A supplied root is an isolation directory: Ol manages only its `package-metadata`, `source-repository`, and `github-file` children. Cache paths never appear in reports. Cache schemas are specified in [cache_format.md](cache_format.md).

### `ol check`

<a id="contract-policy-checks"></a>
<a id="contract-policy-report-input"></a>

```text
ol check --report <scan.json> --allow-licenses <SPDX-ids>
```

`check` reads one ungrouped canonical JSON report. It performs no dependency parsing, evidence collection, cache access, or network access. Invalid, malformed, grouped, or unsupported-schema reports are command failures. A report [whose input declared no resolved dependencies](#contract-empty-inventory) is readable and complete, and is reported as inconclusive rather than evaluated.

<a id="contract-policy-filtered-report"></a>

A report the producing scan narrowed with [`--dependency`](#contract-dependency-filtering) is a valid policy input, and `check` states the filter and the count it removed before the result. A grouped report is refused because its rows are aggregates that no policy can evaluate; a filtered report is evaluable and merely smaller, and which components a gate covers is the reader's decision to make rather than Ol's. It is not a decision the reader can make unknowingly: the report carries the filter in `metadata.view`, and a gate that consumed that fact without repeating it would leave a partial evaluation reading exactly like a complete one. Components the filter removed because no input determined their relationship are counted separately, because those are the ones policy otherwise keeps fail-closed, so dropping them is the part of the exclusion that changes what the run can prove.

A `metadata.view` that cannot be read is a command failure, and the failure names the field that failed. A view states all three of `dependencyFilter`, `excludedCount`, and `excludedUnknownCount`: a view stating no filter is not the same document as one stating nothing, and a count Ol supplied is not the same claim as a count Ol defaulted. `dependencyFilter` holds `null` when the scan applied none; both counts are non-negative integers, `excludedUnknownCount` never exceeds `excludedCount`, and a view claiming no filter reports no exclusions. A report predating the field entirely is read as unfiltered, which is what it was.

Each rejected shape is the same confusion in a different form: a view `check` cannot read, or can read but must disbelieve, leaves it unable to tell a complete report from a narrowed one. A document claiming no filter while reporting exclusions is the narrowed-report-read-as-complete case restated, and an unknown-relationship count above the total describes a subset larger than its set. Ol's own writer produces none of them, but the canonical report is the input contract rather than a private format, so the reader states the contract instead of assuming its own producer wrote the document.

`--allow-licenses` is a required comma-separated list of SPDX License Identifiers. Whitespace and casing are normalized using active SPDX data. Empty entries, expressions, exception identifiers, natural-language names, and unknown identifiers are invalid. For a `matched` component, `AND` requires both operands, `OR` requires either operand, parentheses retain SPDX precedence, and `WITH` has the policy value of its base license.

Policy evaluates all non-root, non-excluded components. `unknown` dependency type remains in scope. `unknown`, `conflict`, `ambiguous`, `invalid`, and `error` fail closed unless a baseline acknowledges that component under the rules below. All violations are collected and deterministically ordered.

Violations are printed as `Package Version Ecosystem Purl License/Status Reason Mechanism Reference Path`, tab-separated. `Path` is the [dependency path](#contract-dependency-path) the scan report's inventory proves, stated as `-` when the report names no introducer for that component.

`Reason` states why policy rejected the component; `Mechanism` and `Reference` state why its evidence never settled, using the identifiers and the selection order the scan report's [unresolved section](#contract-unresolved-section) defines, so one vocabulary describes both projections. Both are `-` for a component whose license resolved, because the allow-list already explains it, and both are `-` when no mechanism applies. The violation table then ends with an `Unresolved mechanisms` tally counting the violations each mechanism explains, ordered by count and then by identifier, and omitted when no violation carries one. Components with no mechanism are tallied as `no mechanism reported` rather than merged into a named one.

This exists because `check` is the projection a policy gate reads, and a status alone names no action: a hundred rows reading `license is unresolved` are usually a handful of populations, and which population a component belongs to decides what is done about all of them at once. `check` re-derives nothing — the mechanism follows from the evidence the persisted report already carries, and evaluating a report never collects again.

`--allow-dev-licenses` adds identifiers only for components proven development-only by resolver data persisted in the report. It uses the same identifier validation as `--allow-licenses`; a supplied empty value is invalid. Any runtime or usage-unknown occurrence keeps the component under the primary allow-list. Inputs without reliable development reachability therefore fail closed. When supplied, the count admitted by this policy is always printed.

<a id="contract-development-usage-and-sbom"></a>

**An input that determines no usage does not withdraw a determination another input made.** [SBOM inputs determine no usage](packagemanager.md#contract-package-manager-usage) and their occurrences [abstain](packagemanager.md#contract-undetermined-usage-abstains), so scanning an SBOM beside the resolved tree it describes leaves each component classified as its resolver classified it. What stays fail-closed is the component no input classified: it is unknown, and this option never relaxes it.

This matters because the two recommendations otherwise point in opposite directions — scanning an SBOM together with the resolved tree, and relaxing policy for development-only dependencies. Measured across five ecosystems that determine usage, the same policy and the same dependency changed verdict when a second input merely mentioned it.

The residual risk is stated in [packagemanager.md](packagemanager.md#contract-undetermined-usage-abstains): an SBOM whose scope exceeds the resolved inputs can fold a runtime installation onto a development row. The [supply tally](#contract-component-supply) is what shows whether an SBOM reached beyond the resolved inputs.

The rule generalizes past usage. Which input determined a fact decides who owns it in the combined row; which input kind is generally finer does not. The [dependency relationship](#contract-folded-relationship) follows the same rule, and both are separate from license evidence, where [no candidate is selected over another by its supplying input](../Architecture.md#decision-evidence-preservation) and every claim is preserved and reconciled. Combining inputs therefore states three different things about the same component, and none of them is a ranking of SBOMs against package-manager inputs.

`--exclude-packages` removes matching purls from policy evaluation, baseline generation, violation output, SARIF, and the passing count, but never changes the scan report. The excluded count is always printed when the option is supplied. This is a policy-scope decision; it is distinct from `scan --skip-evidence-packages`.

<a id="contract-policy-baseline"></a>

`--baseline <file>` supplies a baseline of acknowledged unresolved components: the components a reviewer has already seen and accepted because their evidence cannot be resolved. Acknowledging them removes their violations, so that the unresolved set cannot grow silently rather than so that it becomes empty.

The option is repeatable and the supplied baselines compose: a component is acknowledged when any of them states it. The composition is a union, so the result does not depend on the order the files were named, and nothing has to be reconciled — an entry already identifies itself by identity and evidence fingerprint, so two files stating different evidence for one component contribute two entries and whichever matches the report applies. Composition exists because one population is commonly shared while another is not: the legacy `System.*` and `runtime.*` NuGet corpus is the same set in every repository targeting `netstandard2.0`, and copying it into each repository's own file would make the shared review a per-repository transcription.

`--update-baseline` requires `--baseline`, replaces the **last** supplied file, and then evaluates against the whole composition; it does not merely append or suppress evaluation. The written file holds a deterministic snapshot of what the earlier files do not already acknowledge. Writing the complete snapshot into it would copy the shared population back into the file that composes with it, which is what composing them removes; with one baseline there is nothing earlier to subtract and the snapshot is complete, as it was before the option became repeatable. Every file before the last is read and never written, so the general population stays under the review that produced it.

Only `unknown`, `ambiguous`, `conflict`, and `invalid` may be acknowledged into a baseline, and only when no recognizable candidate is rejected by the active allow-list. `matched` belongs in the allow-list and `error` represents an operation to repair. Entries identify the component and fingerprint its status and evidence, so changed evidence or identity expires that acknowledgement. Applying a baseline rechecks the active allow-list. Missing, malformed, or incompatible baselines exit `1`; acknowledged counts, including zero, are reported.

<a id="contract-policy-sarif"></a>

`--sarif <file>` writes the same violations as SARIF 2.1.0 without changing stdout, declaring `$schema` as `https://raw.githubusercontent.com/oasis-tcs/sarif-spec/main/sarif-2.1/schema/sarif-schema-2.1.0.json`. Stable rule IDs are `OL0001` not allowed, `OL0002` conflict, `OL0003` unresolved, `OL0004` ambiguous, `OL0005` invalid expression, and `OL0006` evidence error. Results use logical component locations and include the deterministic shortest dependency path when available; Ol does not invent source positions. Development-policy allowances are recorded as run properties rather than findings, a report the producing scan narrowed with `--dependency` records its [evaluated view](#contract-policy-filtered-report), and a scan that excluded input paths records its `inputScope` (`excludedPathCount` and `excludedPaths`) beside them, in the one run property bag they share. A result set that is complete for a narrowed population reads exactly like a complete one, and a CI job consuming only the SARIF file has nothing else to learn the difference from.

A report [whose input declared no resolved dependencies](#contract-empty-inventory) records an `inconclusive` reason in that same bag, naming the report's own warning identifier so one vocabulary states the condition in the scan warning, the check result, and the SARIF document. It is the same confusion as the narrowed view rather than a new one: zero results over zero components is what a clean run also produces, and the exit code that distinguishes them is not in the file. It is a run property rather than a finding for the reason the other two are — it says what the run covered instead of naming something a reviewer changes, and there is no component to attach a finding to.

Policy files, deny-lists, per-package exceptions, and concluded licenses are outside this command's contract. Dependency-scope policy is limited to `--allow-dev-licenses` above; no other scope distinction is evaluated.

### `ol diff`

<a id="contract-diff"></a>

```text
ol diff --previous <scan.json> --current <scan.json> [--format text|json]
```

`diff` reports `added`, `removed`, `version-changed`, `status-changed`, `license-changed`, and `evidence-changed`. Each material dimension is an independent change record even when human text groups changes for one component. Output is deterministic; JSON uses schema version 1 and reports both affected-component and independent-change counts. `diff` exits `0` when comparison succeeds even when changes exist, and `1` when either report or output is unusable.

<a id="contract-diff-boundaries"></a>

`diff` states the boundary each report was produced under before the changes: the excluded input paths as the audit boundary, and the [`--dependency` filter](#contract-dependency-filtering) as the evaluated view. Each names the previous and current value and whether it changed. Both are compared as sets rather than as text, because both options take unordered lists and the same configuration can be spelled several ways; reporting a boundary change on a respelling would be a change nobody made. JSON carries both unconditionally so a consumer never has to determine the document's shape before reading them; human text states a boundary only when at least one side has one. Adding a boundary object does not change the diff schema version, because a consumer reading `changes` is unaffected by a key it does not read; changing what an existing key means or holds would.

This exists because a filtered report holds fewer components than its scan resolved, so a diff over two of them compares populations rather than resolutions, and two different filters make every difference between them an artifact of the filters instead of a change in the dependencies. Ol states the two views and draws no conclusion from them: which components a comparison covers is a scope decision its reader makes, the same way the audit boundary beside it is.

### `ol cache clear`

```text
ol cache clear [package-metadata|source-repository|github-file|all]
```

The positional category defaults to `all`. Clearing a category removes only the corresponding Ol-managed child under the selected cache root. Clearing `all` preserves the isolation root and unrelated sibling files. An existing file cannot be used as a cache root.

### `ol skill`

```text
ol skill install [--target codex|claude] [--output <directory>] [--force]
ol skill export-plugin --output <directory> [--with-claude] [--force]
```

`install` writes the bundled `license-scan` Agent Skill. The default target is `codex`, which resolves to `.agents/skills/license-scan` under the current directory; `claude` resolves to `.claude/skills/license-scan`. `--output` overrides the target-specific destination but does not permit an unknown target value.

`export-plugin` writes an Agent Plugins v1.0.0 package with root `plugin.json` and the shared skill under `skills/license-scan`. `--with-claude` additionally writes `.claude-plugin/plugin.json`; it does not duplicate the skill. The export command requires `--output` and performs no network access.

Both commands stage a complete package beside the destination before moving it into place. An existing file is always an error. An existing directory is preserved unless `--force` is supplied; forced replacement removes stale files from the previous package. Embedded relative paths must remain inside the staged package. I/O, invalid target, and incomplete-command failures exit `1`. Once the staged package has been committed to the destination, staging and backup cleanup is best-effort and does not change the successful exit; a backup is preserved if replacement rollback itself cannot complete.

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
- A validator that only rejects the shapes it cannot parse still accepts the ones it must disbelieve. Requiring `metadata.view` to state its filter closed the case where a narrowed report reads as complete, and left three documents open that say the same thing in numbers instead of in structure: no filter beside a non-zero exclusion count, an unknown-relationship count above the total it is a subset of, and a negative count. Ol writes none of them, which is exactly why they were easy to miss — the reader was being checked against its own producer rather than against the contract it publishes. When a rule is about what a document may claim, enumerate the claims, not the syntax.
- Writing a decision into the field it was decided from destroys what the next decision needs. Folding an SBOM relationship onto a resolved row reads the resolver's answer to decide whether the SBOM may fill it in, and the first version read that answer back out of the row it had just written to. One SBOM can place the same purl at two positions in its own graph, so the second fold saw a row already holding the first fill-in and could not tell it from a resolver's determination: the same document with its components listed in the other order produced a different report, and neither matched what that SBOM alone reported. The rule was right and the state it read was wrong, which is the harder half to see, because every test over a document that mentions a package once passes. Where a computed value overwrites its own input, the input has to be captured before the first write.
- A run-level property bag has one owner or it has none. Development allowances opened `properties` themselves and closed it on the way out, which was correct while they were the only run-level fact; adding the evaluated view beside them would have emitted a second `properties` key on any run that had both, and a JSON reader resolves a duplicate key by keeping one of the two — the SARIF would have been well-formed, parseable, and quietly missing half of what it stated. A writer that opens a shared container is a container the next fact cannot be added to. The equivalence class that catches it is the one where both facts are present at once, which is neither of the two the existing tests covered.
- A fact the producer was fixed to state can be dropped again by the next consumer. `scan` was taught to record `--dependency` in `metadata.view` precisely because a filtered report looked like a complete one; `check` then read that report, gated the smaller population, and printed a pass without mentioning the filter — restoring the original failure one command downstream. Writing a fact into the artifact is half the fix. The other half is every reader that acts on the artifact repeating it, and the check for that is to follow the fact to the end of the pipeline rather than to the end of the writer.
- A merge rule is only as safe as the rung that has consequences. `DependencyType` was combined across inputs by taking whichever value sat closest to a root, which reads as harmless while the comparison is `direct` against `transitive`: that pair moves a printed column and a view filter and nothing else. But `root` is outside policy and only an SBOM ever states it, so the one rung no package-manager input can contest was the one that withdrew a resolved dependency from the gate — an artifact SBOM naming `pkg:npm/alpha@1.0.0` as its own root, scanned beside a lockfile that depends on it, turned a failing `check` into a passing one with the offending row still printed in the table. Long design notes about the harmless rungs never reached it, because the question they asked was which value is more accurate rather than which value is read by something that returns an exit code. Ask the second question first. The harmless rungs then turned out not to need the accuracy question either: whether an SBOM's `direct` is better evidence than a resolver's `transitive` is unanswerable in general, because the two describe different graphs, and it did not have to be answered — the row belongs to one graph, both relationships survive in the report's occurrences and edges, and the only asymmetry left was that the closest-to-a-root rule could drop a component out of a `--dependency`-filtered gate while the alternative could not. A comparison with no defensible winner is often a sign that the two values were never comparable.
- A derived per-component value should ride on the array that already gets sorted. Development usage was expected to need a display-to-inventory index mapping, because top-level `components` are sorted by `ecosystem,name,version` while occurrences are in input order. Persisting usage on the display components instead lets it pass through the same sort, so `check` re-resolves nothing and no index mapping exists to go stale. Inputs that determine no usage store nothing.
