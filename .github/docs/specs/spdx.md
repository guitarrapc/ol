# SPDX Data and License Semantics Specification

This document defines how `ol` uses SPDX License List data and how it interprets SPDX license identifiers and expressions.

SPDX data is foundational for all versions because license matching must be explainable and versioned. The tool should not silently depend on whatever SPDX list is current on the network at scan time.

## Design Basis

This specification derives from the [Ol architecture](../Architecture.md), especially the decisions to [normalize only against versioned SPDX data](../Architecture.md#decision-versioned-spdx), [prefer explicit SPDX selection while retaining an offline fallback](../Architecture.md#decision-spdx-resolution), [preserve evidence instead of selecting a single authoritative source](../Architecture.md#decision-evidence-preservation), and [separate factual resolution from organizational policy](../Architecture.md#decision-policy-separation).

Those decisions require normalization to be reproducible and explainable rather than heuristic. Therefore Ol records the active SPDX source and hashes, restores official casing, retains raw claims, and distinguishes `unknown`, `ambiguous`, `invalid`, `conflict`, and deprecated-but-valid identifiers. A normalized `matched` result establishes what the evidence says; it does not establish that organizational policy permits the license.

## Development-Time Bundled Data Generation

`Ol.Update` is a development-time external generator, not an `ol` runtime dependency. Running `ol-update generate` refreshes the bundled SPDX snapshot used as the offline fallback. This keeps the published CLI independent from the generator and network while retaining versioned, reproducible data.

The snapshot carries license identifiers, license [names](#contract-spdx-license-name), exception identifiers, and the deprecated set. Names are emitted as an array sharing its index with the identifier array, and a license that states no name keeps an empty entry so the two stay aligned; sorting them apart would give every license its neighbour's name. User-managed data supplies the same fields from `licenses.json`.

`ol-update generate` reads the current upstream list and has no way to regenerate a snapshot at the version already bundled, so adding a field to the generated data necessarily advances the SPDX list version with it. Which licenses that upstream change adds, removes, or deprecates belongs in the same review as the field.

<a id="contract-spdx-data-resolution"></a>
## Data Sources

SPDX data is resolved in this order:

1. `--spdx-data <dir>`
2. User-managed data selected by `ol spdx use`
3. CLI-bundled data

The active data source must be recorded in JSON reports.

`--spdx-data <dir>` points to a directory containing:

```text
licenses.json
exceptions.json
```

These match the JSON files published by SPDX License List data. The `details/` and per-license JSON directories are not required for v1 validation.

## User-Managed Data

User-managed SPDX data is stored by version. Installation and selection are separate operations: `ol spdx update` installs data without changing the selection, while `ol spdx use` explicitly selects an installed version. The active selection persists across commands until it is changed, switched back to bundled data, or cleared.

The exact platform-specific user data root is not part of this spec. Reports must not emit absolute paths to it.

<a id="contract-spdx-commands"></a>
## Commands

### `ol spdx update`

Downloads the latest `licenses.json` and `exceptions.json` into the user-managed SPDX data store. Installation does not change the active selection; `ol spdx use <version>` activates the installed version explicitly.

```text
installed: 3.27.0
```

This is a user-facing command. It is distinct from `ol-update generate`, the development-time tool that refreshes generated bundled SPDX data.

### `ol spdx version`

Displays the effective active version and source, the selected user-managed version, and the bundled version. It does not display the platform-specific user data path.

Example:

```text
active: 3.27.0 (user)
user-selected: 3.27.0
bundled: 3.26.0
```

When no user-managed version is selected, `user-selected` is `none` and the bundled version is active.

### `ol spdx list`

Lists the bundled version and all valid installed user-managed versions. Every entry is identified by version and source, and the effective active entry is prefixed with `*`.

Example:

```text
* 3.26.0 (bundled)
  3.27.0 (user)
```

Bundled and user-managed data with the same version remain separate entries because their sources and selection state differ.

### `ol spdx use <version>`

Sets a valid installed user-managed SPDX version as current. The argument is a version identifier, not a directory or path. A valid installation is an immediate child of the user data root whose `licenses.json` and `exceptions.json` exist and whose declared License List version matches the directory name.

`ol spdx use bundled` clears the user-managed selection without deleting installed versions and makes the bundled version active.

Successful selection reports the effective version and source, for example `active: 3.27.0 (user)` or `active: 3.26.0 (bundled)`.

### `ol spdx clear`

Removes user-managed SPDX data. After clearing, scans fall back to bundled SPDX data unless `--spdx-data` is supplied.

## Report Metadata

Every JSON scan report includes active SPDX data metadata:

```json
{
  "spdx": {
    "source": "cli-argument | user | bundled",
    "licenseListVersion": "3.27.0",
    "dataRef": "ol/spdx/3.27.0",
    "licensesSha256": "...",
    "exceptionsSha256": "..."
  }
}
```

`dataRef` is a logical reference, not an absolute path. Examples:

- `ol/spdx/<version>` for user-managed data
- `bundled/spdx/<version>` for bundled data
- `cli-argument` for `--spdx-data`

<a id="contract-spdx-normalization"></a>
## License Identifiers and Expressions

`ol` validates SPDX License Identifiers, SPDX License Exception Identifiers, and SPDX License Expressions using the active SPDX data.

Valid examples:

```text
MIT
Apache-2.0
BSD-3-Clause
MIT OR Apache-2.0
GPL-2.0-only WITH Classpath-exception-2.0
```

SPDX identifier and exception matching is case-insensitive, but normalized output uses official SPDX casing.

Examples:

```text
mit -> MIT
apache-2.0 -> Apache-2.0
classpath-exception-2.0 -> Classpath-exception-2.0
```

<a id="contract-spdx-license-name"></a>

A declared value that is not an identifier is matched against the SPDX license list's `name` field next, exactly apart from case, and resolves to the identifier SPDX gives that name.

```text
MIT License                              -> MIT
Apache License 2.0                       -> Apache-2.0
BSD 3-Clause "New" or "Revised" License  -> BSD-3-Clause
```

This is not alias guessing. A name is published by the same SPDX record that defines the identifier, so resolving it reads the active SPDX data rather than interpreting a spelling. Loose aliases are still not normalized, and the distinction is what a value leaves unstated rather than how close it looks: `Apache 2.0` and `Modified BSD License` name no SPDX record, and a PyPI Trove classifier such as `License :: OSI Approved :: BSD License` names a family without a version, so all of them stay `ambiguous`.

Two rules follow from a name being one license rather than an expression. It is resolved for a whole declared value only, never for an operand inside an expression, because admitting names there would make a value's meaning depend on whether a name happens to contain an operator word. And it is attempted before the value is read as an expression, because SPDX names do contain those words: `BSD 3-Clause "New" or "Revised" License` was previously parsed as a disjunction and rejected as `invalid`.

Where SPDX gives one name to two identifiers, it is always a deprecated identifier and the replacement that supersedes it — `GPL-2.0` and `GPL-2.0-only` share a name because they are the same license — so the replacement is the answer and [deprecation](#contract-deprecated-identifiers) is not reported for the name. A name Ol cannot attribute to exactly one current identifier that way resolves nothing.

License exception names are not matched. An exception is only ever an operand of `WITH`, where the operand is an identifier.

<a id="contract-license-set"></a>

A source that enumerates the licenses it found without saying whether they are alternatives or cumulative states a license listing. deps.dev answers that way for Go modules and Maven artifacts. Each member is resolved against the active SPDX data where the listing is built, and the candidate records the result as kind `license-set`, written with `;` between members because `;` is not an SPDX operator and no expression will ever contain it.

```text
["MIT", "Apache-2.0"]  -> license-set  MIT; Apache-2.0        (ambiguous)
["non-standard", ...]  -> license                             (ambiguous, unresolved)
```

The status stays `ambiguous`. The members are known; the relation is not, and no member count makes it known. What resolution adds is that every member is a valid SPDX expression, that a deprecated member is reported as [deprecated](#contract-deprecated-identifiers), and that the value carries one kind whatever the members spell. That last point is the lesson: classifying the joined value instead read it as `ambiguous` or `invalid` depending on whether a member happened to contain an operator word, because the joined value parses as neither an identifier nor an expression and the heuristic that decides between them was written for publisher free text.

A member that resolves nothing leaves the whole value to ordinary classification. deps.dev answers `non-standard` for a license it could not identify, and a listing Ol cannot enumerate is not one a later reader may treat as enumerated.

The kind is what states that a value is a listing, not the separator. A publisher who writes a semicolon in a license field has not stated a listing, so their value is classified and evaluated whole like any other. Deciding this by punctuation instead would let a policy read free text as an enumeration Ol never built and never validated.

<a id="contract-spdx-license-see-also"></a>

A [declared license location](#contract-declared-license-reference) is matched against the SPDX license list's `seeAlso` URLs, and resolves to the identifier that publishes it.

```text
https://www.apache.org/licenses/LICENSE-2.0  -> Apache-2.0
https://opensource.org/license/MIT           -> MIT
```

This is the same reading as a name, applied to a value spelled as a URL: SPDX publishes the URL in the record that defines the identifier, so recognizing it is not reading the page and is not following a redirect. It is attempted only for a candidate whose own license value resolved nothing, so a declaration never overrides a license the publisher stated, and the resolved candidate keeps the URL in its evidence with kind `location` so a report shows what the value was read from.

Matching ignores only the spellings that cannot change which document a URL names: its scheme, its case, a leading `www.`, and a trailing slash. Nothing else is rewritten. A URL a site has since renamed is therefore not resolved — `http://opensource.org/licenses/MIT` is not `https://opensource.org/license/MIT` — because the equivalence lives in that site's redirects rather than in SPDX, and encoding it would make Ol assert third-party routing as a fact.

Where SPDX gives one URL to several identifiers it is not the deprecated-and-replacement pair the name rule resolves: one GNU page serves four LGPL identifiers, and one OSI page serves both `LGPL-2.1` and `LGPL-2.1-or-later`, which are different licenses. A shared URL therefore names no single license and resolves nothing.

Measured against the declared locations of 15 widely used libraries across five package managers, this resolves very little: 1 of 308 distinct URLs, on a component that already stated its license. Legacy NuGet `licenseUrl` values overwhelmingly name a redirector (`go.microsoft.com/fwlink`), a nuget.org rendering of the license page, a repository blob, or a vendor EULA — none of which SPDX publishes, and the first two of which are not licenses at all. The rule earns its place by resolving canonical URLs such as `apache.org/licenses/LICENSE-2.0` for packages that carry nothing else, not by rescuing the legacy NuGet corpus.

<a id="contract-license-family-classifier"></a>

A PyPI license classifier that names a license family resolves nothing and is [reported as that](cli.md#contract-unresolved-section) rather than only as `ambiguous`. The set is the one PEP 639's appendix enumerates as classifiers that "intend to specify a particular license, but do not specify the particular version or variant", and from which tools "MUST NOT attempt to automatically infer a `License-Expression`" — `License :: OSI Approved :: BSD License`, `... :: Apache Software License`, and twelve others. Recognizing them adds no mapping: the value stays unresolved, and `check` still fails closed on it. What changes is that a report can say the value can never resolve, so a reviewer asks the publisher or reads the artifact instead of waiting for Ol to gain a capability.

Matching is exact. A classifier comes from a closed vocabulary PyPI validates on upload, and PEP 639 states that new license classifiers must not be added to it, so a value differing at all is not one of these and is not described as one.

Ol maps no classifier to an identifier. PEP 639 says the remaining classifiers each correspond to one SPDX identifier and permits tools to infer them when analyzing packages, but publishes no machine-readable table for that direction: of PyPI's 89 license classifiers only 29 are derivable from the SPDX name they contain, and the rest embed an identifier or an abbreviation under no consistent rule. Reproducing the remainder would make Ol the author of a license mapping rather than a reader of published data, which is the same reason the free-text `License` field is left alone — PEP 639 forbids converting that field without affirmative user action, and no authority defines what `Apache 2.0`, `Modified BSD License`, or `PSFL` denote.

<a id="contract-strict-normalization"></a>
## Strict Normalization

v1 normalization is intentionally strict.

Valid SPDX identifiers or expressions become `matched` and are normalized to official casing.

Examples that remain ambiguous:

```text
Apache License
BSD
GPL
LGPL
MIT/Apache
Dual licensed
SEE LICENSE IN LICENSE
Custom
Commercial
Freeware
```

The tool must not guess that these mean a specific SPDX expression. Later evidence from package metadata or source repository hints may improve confidence, but the original ambiguous evidence remains recorded.

## Unknown Values

These values are treated as `unknown`:

- empty or missing license fields
- `NOASSERTION`
- `NONE`
- `UNKNOWN`

`NONE` is not treated as safe or matched. It is grouped with `unknown` because its policy meaning is difficult and should not be silently accepted.

The raw value should remain in JSON evidence.

<a id="contract-spdx-license-text-matcher"></a>
## SPDX License Text Matcher

`Ol.Core` exposes an immutable matcher for a caller-supplied, versioned collection of SPDX standard license templates. Construction parses the SPDX `beginOptional`, `endOptional`, and `var` rules once; matching accepts UTF-8 document bytes, treats literal template whitespace as insignificant, applies each variable's SPDX `match` expression, and succeeds only when exactly one distinct SPDX identifier matches. No match, invalid UTF-8, more than one matching identifier, a document above the configured byte limit, or a matcher timeout resolves nothing rather than guessing or failing the scan.

The template collection is trusted versioned data; the document is package-controlled input. The default document limit is 1 MiB and each template match has a bounded execution time. The matcher records no policy decision and performs no file or network I/O. A package-manager adapter added later supplies artifact bytes and chooses the template corpus; the current CLI scan pipeline does not yet inspect installed package artifacts.

The matcher corpus is passed explicitly rather than silently coupled to the identifier-only SPDX snapshot. Bundling or installing full template data changes the SPDX data and deployment contract and requires separate size, startup, and allocation measurements before the CLI adopts it.

<a id="contract-deprecated-identifiers"></a>
## Deprecated Identifiers

If an SPDX identifier exists in the active SPDX data but is deprecated, the component may still be `matched`, but the report records a warning.

Example candidate warning:

```json
{
  "raw": "GPL-2.0",
  "normalized": "GPL-2.0",
  "deprecated": true,
  "warnings": ["deprecated_spdx_identifier"]
}
```

stderr summary should include deprecated identifier warning counts.

<a id="contract-candidate-evidence"></a>
## Candidate and Evidence Records

Each component JSON record retains every license claim once in `licenseCandidates`. Each candidate includes:

- `source`
- `kind`, such as `declared`, `concluded`, `expression`, `id`, `name`, `location`, or `license-set`
- `raw` and normalized SPDX expression when valid
- classification `status`
- `deprecated` and candidate `warnings`
- one typed `evidence` object describing the provenance that is not already represented by the candidate

The former component-level `evidence` array duplicated `licenseCandidates` and is removed by JSON report schema version 1. Evidence is now subordinate to the claim it substantiates:

Audit evidence is a traceable observation, not an independent license conclusion. It identifies the input field or collected record from which the candidate was derived closely enough for a reviewer to locate and re-check it. Candidate fields hold the observed claim and Ol's interpretation; nested evidence holds only non-duplicated provenance needed to audit that claim.

- SBOM evidence records the exact source field. CycloneDX license `acknowledgement` is retained only when explicitly present; SPDX `licenseDeclared` and `licenseConcluded` are identified by field and are not relabeled as CycloneDX acknowledgements.
- Package registry evidence records the opaque cache-key hash and collection timestamp when known.
- Source repository evidence records the logical repository/ref, collection status, opaque cache-key hash, and detected license-file metadata when known.
- Package artifact evidence records the versioned logical artifact identity, logical path inside that artifact, SHA-256 of the exact document bytes, stable matcher identifier, and SPDX template corpus version. It never records an absolute local path or the document body.

<a id="contract-declared-license-reference"></a>

A publisher that cannot state an SPDX expression often states where its license is instead. Ol retains that as a declared license reference: a kind and the value exactly as the publisher wrote it. The same shapes occur across ecosystems, and one representation keeps them one concept rather than one vocabulary per ecosystem.

| Kind | JSON | Sources | Value |
|---|---|---|---|
| location | `location` | NuGet `licenseUrl`, CycloneDX `license.url`, npm's legacy license collection | The URL as written. |
| artifact path | `artifact-path` | NuGet `licenseFile`, Cargo `license_file`, PyPI `license_files`, CocoaPods `license.file` | The path as written. |
| inline text | `inline-text` | CocoaPods `license.text` | Always empty. Only that a document exists is recorded; a license document is never retained in a cache or a report. |

Because the kind decides what a reviewer does next, an unresolved component's [reason](cli.md#contract-unresolved-section) is derived from it rather than recorded per ecosystem. Inline text is a declaration with no place to name, so it must reach a report as its own outcome and never as an empty location.

A reference is not a license and is not license text. Ol has not read what it names, so a reference is resolved by reading the thing it names, or it stays an unresolved declaration. Measuring the declared locations in three .NET repositories found that most lead to a licensing overview page or a redirector rather than to a license document, which is why the retained fact is where the publisher pointed and not what is there.

The identifier-only exception reads no page: a `location` whose URL the SPDX license list itself publishes as one license's [`seeAlso`](#contract-spdx-license-see-also). That value is a URL spelling of an identifier rather than a pointer to unread text, so it resolves like a [name](#contract-spdx-license-name). Every other kind of reference, and every URL SPDX does not publish, contributes no license value by itself. Separately, the explicit [declared GitHub file collector](source.md) may read a narrowly validated exact file URL and match the returned bytes against a versioned SPDX template corpus; its conclusion is new package-artifact evidence, not trust in the URL declaration.

`acknowledgement: declared|concluded` records the producer's assertion semantics. It is not a verified attestation. CycloneDX `declarations.attestations`, BOM signatures, SPDX annotations, and package verification codes have broader document, conformance, or identity semantics and must not be projected onto a license candidate without an explicit relationship and a recorded verification result. Ol does not emit an inferred `attested` boolean.

<a id="contract-observed-licenses"></a>

CycloneDX `component.evidence.licenses` records what a producer detected rather than what it was told, which is a weaker claim than `component.licenses` and the reason the field exists separately. Ol reads it, because for some generators it is the only license fact in the document: a Go SBOM carries an identifier there for every module and nothing under `licenses` unless the producer was asked to assert its detections.

A detection never replaces a declared value. It supplies a license only where the component declares none. It can still contradict a declaration, and surfacing that is the reason to read the field at all: it is how a scan sees that an SBOM's stated license disagrees with what its own producer found. Agreement is decided by [expression agreement](#contract-expression-agreement), so a detected option of a declared disjunction agrees rather than conflicts.

That relation is read in one direction only here, unlike reconciliation across sources. The declaration must account for the detection; a detection that accounts for the declaration is not agreement, because it says something the publisher did not. Both shapes of "more" are a disagreement to report rather than a value to adopt: a detection offering an option the declaration withheld would widen a license nobody granted, and one requiring a term the declaration omitted would attach an obligation nobody stated. Where a declaration exists, the reconciled value is therefore always the declared expression or `conflict`, never the detected one.

A collection of several licenses states no relationship between its entries. It stays one unresolved observation: it never concludes an expression and never becomes a disagreement. This applies to `component.licenses` equally, and it constrains the entries and not just the collection — an entry of a collection that resolves to nothing does not itself resolve a license. Leaving each entry as a resolved claim would let a later evidence source find two of them disagreeing and report a conflict that no source ever stated. The individual claims stay readable in the report; only what they conclude changes.

The component `warnings` array aggregates candidate warnings. This preserves unknown-like, ambiguous, invalid, and deprecated values for later evidence sources to reconcile in v2 and v3.

## SBOM Field Reconciliation

SPDX SBOMs can contain both `licenseDeclared` and `licenseConcluded`. Both are evidence.

- If valid license candidates collapse to one expression, status is `matched`.
- If valid license candidates disagree, status is `conflict`.
- Unknown-like values do not create a conflict when a valid candidate exists.

<a id="contract-expression-agreement"></a>

Two valid expressions collapse to one when neither withdraws what the other states. Ol decides that with two rules, matching the two ways a source can say less than another without contradicting it. Otherwise they disagree and the status is `conflict`.

**A choice is covered by a wider choice.** Compare **top-level disjunct sets**: the exact normalized text between top-level `OR` operators. When one set contains the other, the two agree. A disjunction states a choice the publisher offers, not a claim that every option applies at once, and repository license detection answers with the one file it found at the repository root, so it names one option out of several by construction. Reading that as disagreement makes every dual-licensed package a conflict, which is the ordinary case in some ecosystems, and leaves a scan that collected more evidence worse off than one that collected none.

**A single license is covered by an expression that names it.** When one side is one license — with or without a `WITH` exception — and the other names that license among the licenses it requires, the two agree. This rule exists because a repository-level detector cannot express a conjunction at all: `(MIT OR Apache-2.0) AND Unicode-3.0` beside an observed `Apache-2.0` is one source stating the terms and another naming a license those terms already list, not two sources disagreeing. Without it, every Rust project reaching `unicode-ident` reported a conflict that no source ever stated.

It is restricted to a single license on one side, because two compound expressions can share a license and still require different terms: `MIT AND Unicode-3.0` and `MIT AND BSL-1.0` remain a disagreement. `WITH` binds to the license it modifies rather than acting as an operator, so `Apache-2.0 WITH LLVM-exception OR MIT` is not satisfied by a bare `Apache-2.0`, which is not among the options offered.

The reconciled value is the expression that accounts for the other, never the narrower observation. That is also the safe direction for policy in both rules: an allow-list permitting only an unobserved `OR` option still passes, and every `AND` term the publisher required is still evaluated, whereas narrowing to the observed license would drop options in the first case and obligations in the second.

The comparison stays deliberately shallow. Nothing is distributed, simplified, or interpreted beyond what the normalizer established, and relations Ol cannot decide this way remain `conflict` rather than becoming a guess. Equal sets written in a different order agree, and the reconciled value is the spelling of the first candidate in evidence order, which keeps the result deterministic.

For CycloneDX, a single `expression` or a single license `id` can be `matched`. Multiple license IDs without explicit `AND`/`OR` semantics are `ambiguous`, not automatically synthesized into a license expression, and each of those IDs is `ambiguous` too, as [observed licenses](#contract-observed-licenses) requires.

## Lessons Learned

- Comparing normalized expressions as text made collecting more evidence produce a worse result. Measured against a Rust project, adding registry and repository evidence to a lockfile scan turned 164 resolved components into 76 resolved and 94 conflicting, because `MIT OR Apache-2.0` and an observed `Apache-2.0` differ as strings while agreeing as offers. Reconciliation across sources needs a relation between expressions, not equality.
- The disjunct-set relation covered 93 of those 94 conflicts. The single remaining one had a top-level `AND`, and it was read as the shallow relation reaching its limit rather than as a case still to answer. That was the wrong conclusion: it was `unicode-ident`, which `proc-macro2` pulls into nearly every Rust dependency tree, so the one remaining conflict was not a tail case but a permanent finding for a whole ecosystem. A relation measured against one repository can undercount a case that is rare per component and universal per project; how far a single unresolved component reaches is worth checking before calling a relation sufficient.
- Answering it needed no deeper interpretation, only a second relation of the same shallowness. The detector's limitation is the same for `AND` as for `OR` — it names one license because that is all it can name — so the fix was to state that a single license is covered by an expression naming it, not to distribute conjunctions.
- A collection-level status is not enough on its own. A collection of several identifiers was already reported `ambiguous`, but its entries stayed individually resolved, so the first evidence source added afterwards saw two resolved claims disagreeing and produced a conflict the document never stated. Whatever a group concludes has to be pushed down to what its members conclude, or the group's meaning is lost as soon as another source arrives.
- Reading `component.evidence.licenses` needed no new reconciliation rule. Once expression agreement existed, a detection could be compared with a declaration safely, and the only added rule was that a detection does not replace a declared value.
- `Ol.Update` remains a development-time generator. The Native AOT CLI consumes generated SPDX lookup data through `Ol.Core` and must not acquire a runtime dependency on the generator.
- Installing and selecting user-managed SPDX data are separate operations. Explicit selection prevents a network refresh from silently changing the version used by later scans.
- A version argument must resolve through the validated installation inventory rather than through path combination. Treating an arbitrary directory name as a version can escape the managed data root and can make command output disagree with the data a scan actually resolves.
