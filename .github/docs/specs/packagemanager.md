# Package Metadata Hint Specification

This document defines the v2 behavior for using package manager and package registry metadata as license evidence.

Package metadata is a hint source, not an authority. It complements SBOM license information because SBOM license fields can be missing, stale, inferred, or inconsistent with package registry metadata.

## Design Basis

This specification derives from the [Ol architecture](../Architecture.md), especially the decisions to [preserve evidence instead of selecting a single authoritative source](../Architecture.md#decision-evidence-preservation), [add evidence sources through one reconciliation model](../Architecture.md#decision-shared-reconciliation), [make component/source failures best-effort](../Architecture.md#decision-failure-scope), [make evidence freshness explicit](../Architecture.md#decision-cache-freshness), [version the persistent evidence format](../Architecture.md#decision-cache-compatibility), [bound external I/O and retry only transient failures](../Architecture.md#decision-bounded-io), and [persist evidence with explicit provenance and privacy boundaries](../Architecture.md#decision-provenance-privacy).

Package metadata is consequently additive evidence: it never silently replaces the SBOM claim, uses the shared SPDX and reconciliation semantics, and records disagreement as `conflict`. Cache, concurrency, and retry behavior exist to make enrichment practical and repeatable without turning a registry outage into the loss of the complete dependency report.

## Version Scope

v1 does not fetch package metadata and does not maintain package/source evidence cache.

v2 adds automatic package metadata hints.

## Current Implementation Status

The implemented v2 behavior plans supported versioned purls, consumes persistent package-metadata cache entries, and fetches registry metadata for cache misses and `--refresh`. It supports many package providers, cache metrics in JSON and stderr summaries, `--refresh`, `--concurrency`, `--retry`, and `ol cache clear` categories.

Successful fetches overwrite the relevant cache entry. A cache miss or refresh failure records component-scoped `package_metadata_fetch_failed` evidence; existing valid SBOM evidence remains authoritative for the component's final status. Go module proxy metadata provides source references but no license field, so a successful Go lookup without license text contributes unknown evidence rather than a fetch error.

HTTP 404 is a completed answer rather than a failed operation: the registry reported that the package is not published there. It therefore contributes unknown evidence with the warning `package_metadata_not_found`, not a collection error. This is what a package published only to a private feed produces, and it keeps such a component acknowledgeable by a baseline instead of permanently unresolvable. Timeouts, HTTP 429, and HTTP 5xx remain collection errors because the question was never answered. Ol does not infer the origin of a package from a lockfile download URL: a corporate proxy serves public packages from an internal host, so the host is not evidence about where a package is published.

A component selected by [`--skip-evidence-packages`](cli.md#contract-skip-evidence-packages) is not planned for a lookup at all. It receives one candidate with status `unknown` and the warning `external_evidence_not_collected`, which keeps "not asked" distinguishable from a registry that answered without a license, and keeps the component acknowledgeable rather than errored.

Registry responses that are valid JSON but do not embed the expected metadata object contribute unknown metadata rather than terminating the scan. In particular, a direct NuGet registration leaf normally exposes `catalogEntry` as a NuGet-hosted catalog URL. Ol follows that trusted URL to recover the package-version metadata; malformed or untrusted references remain unknown and must not erase usable SBOM evidence or fail the whole scan.

v3 keeps this behavior and adds source repository hints described in [source.md](source.md).

The resolved-input pipeline accepts NuGet `project.assets.json` version 3 or 4 through one or more `scan --input ...` file or directory options. The NuGet handler owns recursive discovery of the exact `project.assets.json` name and ASCII-case-insensitive package identity comparison. File content is still auto-detected as `nuget-assets`; `--input-format nuget-assets` remains available as an assertion over every discovered file. This is dependency inventory input, not package-registry license evidence. The adapter consumes restore results and does not reproduce NuGet resolution.

The same pipeline accepts npm `package-lock.json` lockfile version 2 or 3 as `npm-package-lock`. Its handler owns recursive discovery of the exact `package-lock.json` name. The adapter consumes the `packages` install tree and never calls a registry to resolve dependency ranges.

## .NET NuGet resolved input

Each `targets` object becomes a separate resolution context. A target key before `/` is retained as the target framework and the suffix is retained verbatim as the runtime identifier. Ol does not infer operating system or architecture fields from the RID.

Each context owns an implicit project root and package occurrences backed by `type: package` entries that also exist as package libraries. The root is represented by the edge endpoint sentinel `-1`, not by a license-report component allocation. Project, unresolved, and non-package entries do not receive NuGet purls and are not rendered as packages. Project nodes remain available while classifying reachability, so a package reached through a project reference is transitive, but an omitted project node is not misrepresented as a package edge.

Direct dependencies are package or project names declared by the matching `project.frameworks` entry. Reachable packages at depth zero are direct, packages reached below them are transitive, and packages whose relationship cannot be proven are unknown. Package-to-package edges and project-root-to-direct-package edges are retained per context. Identical package/version values in different projects or targets remain distinct occurrences but share one report component and one `pkg:nuget/{id}@{version}` enrichment identity. A shared component is direct if any occurrence is direct, otherwise transitive if any occurrence is transitive, otherwise unknown.

## JavaScript npm resolved input

The empty `packages` key is the root context. Non-`node_modules` package paths become additional workspace contexts. Link entries remain traversal nodes and are not emitted as npm registry packages. Registry package names are derived from installed `node_modules` paths, including scoped names, and versioned purls use canonical percent encoding such as `pkg:npm/%40scope/name@1.2.3`.

Dependency names from `dependencies`, `optionalDependencies`, `devDependencies`, and `peerDependencies` are resolved against installed package paths using Node-style ancestor lookup. Missing optional or peer entries do not create phantom components. Root-to-package and package-to-package edges are retained; a workspace or link traversal node is never relabeled as a registry package edge.

Different installed paths remain different components and occurrences even when their npm name and version match. The npm handler registers a `purl + sourceId` collection identity so repeated input combination also preserves nested duplicates. Their purls remain equal, allowing the existing enrichment planner to deduplicate registry work by versioned purl. Root and workspace reachability determine direct/transitive classification, and the strongest relationship is projected to the report component.

Package `dev`, `optional`, `devOptional`, and `peer` flags plus `os` and `cpu` arrays are retained as sparse occurrence variants. Ol records these values in deterministic lockfile order and does not evaluate them against the executing host. The `packages[].license` string is classified as `dependency-input` evidence with its npm format and field provenance; it is not presented as SBOM evidence.

## JavaScript pnpm resolved input

Each pnpm importer becomes a resolution context. Link and workspace nodes participate in traversal but do not become npm registry components. Version 9 snapshot identities remain source identifiers, including peer suffixes, while canonical versioned npm purls remain enrichment identities. Optional/dev reachability, peer snapshot suffixes, and package `os`/`cpu` restrictions are retained as sparse occurrence variants without host evaluation.

## JavaScript Yarn resolved input

Yarn Classic and Berry use separate detectors and parsers. Classic version 1 provides a descriptor-to-resolution graph but no workspace root manifest, so it produces one `yarn.lock` context and keeps relationship classification unknown; optional-only incoming resolutions retain an `optional` variant. Berry metadata version 8 workspace resolutions become contexts, while npm resolutions become components. Workspace/protocol nodes are traversal-only, and virtual resolution hashes are retained as `virtual` variants. A resolution that cannot be uniquely reached without Berry install state remains an unknown occurrence in the first workspace context rather than being discarded or guessed.

## Rust Cargo resolved input

The Cargo adapter consumes only JSON produced by `cargo metadata --format-version 1 --locked`. It does not resolve `Cargo.toml`, interpret `Cargo.lock`, invoke Cargo, or accept `resolve: null` output produced with `--no-deps`.

Each `workspace_members` package becomes a context identified by package name and its resolved feature set. Workspace nodes remain graph traversal nodes and do not become report components. Non-workspace registry, git, and path packages become components whose exact Cargo package id is the source identity. Only the two official crates.io source identifiers receive a canonical versioned `pkg:cargo/{name}@{version}` enrichment identity; alternate registries, git sources, and path packages do not masquerade as crates.io packages.

Resolve-node features plus incoming dependency `kind` and target expressions are retained in deterministic sparse occurrence variants. Workspace crossings participate in reachability classification, but omitting a workspace traversal node does not invent a package-to-package edge. The format records target expressions on dependencies but does not record the literal Cargo `--filter-platform` argument, so context target/platform/architecture fields remain unspecified rather than being inferred from the scanning host. `packages[].license` is classified as `dependency-input` evidence with Cargo metadata provenance.

## Go resolved input

The Go adapter consumes two standard tool outputs generated from the same module or workspace: `go list -m -json all` supplies the selected MVS build list, main modules, and replacement metadata; `go mod graph` supplies requirement edges. Raw graph nodes are never treated as the selected inventory. An edge is retained only when both identities occur in the selected-module list, which excludes superseded versions and the `go@...` and `toolchain@...` graph nodes without reimplementing MVS.

Each selected main module becomes a context root. Selected versioned modules become components with the original `path@version` as source identity. Unreplaced modules receive canonical `pkg:golang/{path}@{version}` enrichment identities. A local replacement has no version, receives no proxy identity, and retains only `replace=local`; local `Dir` and `GoMod` paths are ignored. A versioned module replacement retains the original source identity but uses the replacement path/version for its enrichment purl and records `replace={path}@{version}` as a sparse occurrence variant. `Indirect` and present `Retracted` fields become `indirect` and `retracted` variants.

Reachability from each main module determines direct/transitive classification and proven root/module edges. Selected modules not proven reachable remain unknown occurrences in the first context rather than being discarded. GOOS, GOARCH, build tags, and package-level import reachability are not present in these module outputs and are not inferred from the scanning host. Both companion files must be supplied explicitly or discovered in the same directory.

## Python pip resolved input

The Python adapter consumes only stable JSON format version 1 produced by `python -m pip inspect --local`. It does not resolve `requirements.txt`, `pyproject.toml`, Poetry, uv, or Pipenv inputs. The complete `installed` array is the resolved inventory. The report's `python_full_version` or `python_version`, `implementation_name`, `sys_platform`, and `platform_machine` fields form one resolution context, with `pip_version` retained as its resolver variant rather than inferred from the scanning host.

Distribution names are validated and compared using PyPA normalization: ASCII case is folded and each run of `.`, `_`, or `-` becomes one `-`. Normalized `name@version` values are source identities. Installed distributions without `direct_url` receive canonical `pkg:pypi/{normalized-name}@{version}` enrichment identities. A `direct_url` distribution receives no PyPI identity, retains only `source=direct`, and does not expose `metadata_location`, local paths, or URLs.

`requested=true` proves a root-to-distribution edge and direct classification. `requested=false` proves transitive classification only with `installer: pip`, whose supported versions generate REQUESTED metadata; false values from other installers and absence remain unknown. An unconditional `requires_dist` entry proves a package-to-package edge when its normalized target exists in the installed set. Marker- or extra-conditional requirements do not produce edges because the inspect report does not identify the extras that activated them. Missing optional targets do not create components. `license_expression` is preferred over legacy `license`; either is classified as dependency-input evidence, not SBOM or registry evidence.

## PHP Composer resolved input

The Composer adapter consumes a root `composer.json` and its resolved `composer.lock` from the same directory. The lock file is the selected package/version inventory; the manifest is read only for root identity and `require`/`require-dev` edges. Ol does not evaluate Composer version constraints, invoke Composer, consult repositories to resolve packages, or validate `content-hash` by reproducing Composer's normalization algorithm.

Entries in `packages` and `packages-dev` become one Composer resolution context. Package names are validated in lowercase `vendor/name` form and paired with the locked version for source identity and canonical `pkg:composer/{vendor}/{name}@{version}` enrichment identity. `packages-dev` occurrences retain a sparse `dev` variant. The optional lock `plugin-api-version` is retained as the context's resolver variant; PHP runtime, extensions, libraries, and the executing host are not inferred as resolved context.

Root and package `require` names resolve directly to a locked package name. A `provide` or `replace` name produces an edge only when exactly one locked package supplies it; multiple providers and missing targets remain unlinked rather than guessed. `php`, `hhvm`, `ext-*`, `lib-*`, and `composer-*` platform requirements never become package components. Proven root reachability determines direct/transitive classification, while locked packages without a proven path remain unknown.

Composer license arrays are interpreted as disjunctive claims in listed order and classified as `dependency-input` evidence with `composer-lock` provenance. A package's lock-file `source.url` is retained as its repository hint. Packagist enrichment for `pkg:composer` uses the public package JSON API, selects the exact requested entry from `package.versions`, and projects its license array as an ordered SPDX `OR` claim together with the package repository hint. It does not substitute metadata from another version when the requested entry is absent.

## Ruby Bundler resolved input

The Ruby adapter consumes only Bundler's resolved `Gemfile.lock`. It does not execute or parse the `Gemfile`, invoke Bundler or RubyGems, evaluate version constraints, or inspect the host's installed gems. The lockfile `DEPENDENCIES` section proves root/direct requirements, while each source's `specs` entries provide selected versions and package-to-package dependency names. Reachability from those roots determines direct/transitive classification; missing names do not create phantom components, and ambiguous same-platform targets are rejected rather than guessed.

Each `PLATFORMS` entry becomes a separate resolution context. A spec without a platform suffix is available in each context; a platform-suffixed spec is retained only in its matching context and receives a `platform=...` occurrence variant. The optional `RUBY VERSION` and `BUNDLED WITH` values are retained as runtime and resolver context data. Generic and platform-specific occurrences are not merged before graph projection.

Only `GEM` specs whose source is exactly `https://rubygems.org/` receive canonical `pkg:gem/{name}@{version}` identities. Platform-specific identities include the standard `platform` qualifier. Private `GEM`, `GIT`, and `PATH` specs remain graph components with `source=registry`, `source=git`, or `source=path` variants but receive no RubyGems.org enrichment identity; their remote or local paths are not emitted. RubyGems.org enrichment uses the version-specific API v2 endpoint, includes the platform query when present, and projects the listed licenses as an ordered SPDX `OR` claim plus the source-code or homepage repository hint.

## JVM Maven resolved input

The Maven adapter consumes JSON emitted by Maven Dependency Plugin 3.7.0 or later:

```bash
mvn org.apache.maven.plugins:maven-dependency-plugin:3.11.0:tree -DoutputType=json -DoutputFile=maven-dependency-tree.json
```

The root artifact becomes one resolution context and is not rendered as a dependency component. Root children are direct dependencies; deeper children are transitive. Every proven root/package and package/package relationship is retained as an edge. Repeated resolved artifact coordinates share one report component and enrichment identity, while each JSON tree node remains a distinct occurrence with its own incoming edge and resolver conditions.

Each dependency retains its effective `scope` and `optional` flag as a sparse occurrence variant. `groupId`, `artifactId`, `version`, `type`, and `classifier` form the resolver-native source identity. Canonical `pkg:maven/{groupId}/{artifactId}@{version}` identities include `classifier` and non-default `type` qualifiers when present. The JSON output does not record the requested `-Dscope`, Maven version, plugin version, repository origin, selected Gradle-style attributes, or license metadata; Ol leaves those values unspecified instead of inferring them from the host or file name.

Maven dependency tree input does not cause Ol to resolve a POM or dependency version. Canonical versioned Maven purls are enriched through deps.dev v3 with POM-derived SPDX license hints and a source-repository link when available. A single reported license is reconciled normally. Because deps.dev does not specify the relationship between multiple reported licenses, Ol preserves their listed values as one ambiguous raw claim and does not synthesize `AND` or `OR`. This metadata remains an additive hint rather than authority; CycloneDX is preferable when effective-POM metadata and build repository context must be captured in the input artifact itself.

Gradle's built-in `dependencies`, `dependencyInsight`, and project-report tasks produce human-oriented text or HTML rather than a stable portable graph schema. Ol does not parse those reports or embed the Gradle Tooling API; Gradle users should provide CycloneDX or SPDX JSON.

## SwiftPM resolved input

The SwiftPM adapter consumes `Package.resolved` schema version 2 or 3. Each selected pin becomes one component and occurrence in a `Package.resolved` context. The lock file has no project root, product selection, target conditions, or package-to-package edges, so dependency type and development usage remain unknown; Ol does not parse or execute `Package.swift` to invent that graph.

Remote source-control pins receive a standard `pkg:swift/{host}/{owner-path}/{repository}@{resolved}` identity when their HTTP(S) location contains a host, namespace, and repository. Semantic version is preferred as the resolved display version, followed by branch and revision. The exact revision is retained as a sparse occurrence variant and the repository location is passed to source-repository enrichment. Local source-control and registry pins remain inventory components but receive no remote-source purl or repository hint. Schema v3 `originHash` is retained as context provenance; its meaning is not used to infer resolver conditions.

## CocoaPods resolved input

The CocoaPods adapter consumes the YAML `Podfile.lock` generated by CocoaPods Core. `PODS` supplies selected root-pod versions and dependency names, while `DEPENDENCIES` proves project-root/direct edges. Subspec names are collapsed to their root pod because the podspec and license identity are root-scoped; duplicate subspec dependencies are merged into deterministic root-pod edges. Reachability from the root dependencies determines direct/transitive classification, and missing names do not create phantom components.

CocoaPods Core merges dependencies from different platform variants when generating `PODS`; the lock file does not retain the owning Podfile target or platform for each edge. Ol therefore creates one unspecified `Podfile.lock` context and does not infer platform, architecture, or target from the scanning host. Resolver version from `COCOAPODS` is retained as the input specification version and context variant.

Only pods mapped by `SPEC REPOS` to `trunk`, the public CocoaPods CDN, or the public CocoaPods Specs Git repository receive canonical `pkg:cocoapods/{name}@{version}` identities. Private spec repositories and `EXTERNAL SOURCES` receive no public registry identity and retain only a source-kind occurrence variant. Public pod purls are enriched from the exact version's podspec JSON on the official CocoaPods CDN; the podspec license type, source repository, and commit/tag/branch are projected as package metadata. Ol never asks the CDN to resolve a version constraint.

## Development usage classification

In addition to the audit-only occurrence variants above, adapters project a typed development-versus-runtime usage per occurrence, aggregated per component (any runtime or unknown occurrence wins). This is a resolver-scope classification only — never a claim about production artifact inclusion — and it is what `check --allow-dev-licenses` consumes; the per-adapter rules and policy semantics are specified in [cli.md](cli.md). Adapters that determine usage: npm (`dev` flag), pnpm (strict dev reachability), Composer (`packages-dev` confirmed by production-`require` reachability), Maven (`test` scope), and Cargo (dev-only reachability with build treated as production). Yarn, NuGet, Go, pip, Bundler, and SBOM inputs leave usage unknown because their standard input records no development scope, so `--allow-dev-licenses` never relaxes them (fail-closed). Usage information is added only for inputs that determine it; other inputs carry no additional per-occurrence storage.

## User Experience

Users should not have to specify package manager or ecosystem manually. The CLI derives the ecosystem from component purl and other SBOM metadata where possible.

There is no required `--package-manager`, `--assume-ecosystem`, or `--hint package-manager` flag in the normal flow.

```bash
ol scan --input bom.json
```

The same command gains richer evidence in v2.

## Ecosystem Support

Package metadata support targets:

- npm (JavaScript/Node.js)
- pnpm (JavaScript/Node.js)
- yarn (JavaScript/Node.js)
- NuGet (.NET)
- Cargo (Rust)
- Go modules (Go)
- pip (Python)
- Composer (PHP)
- Ruby Bundler (Ruby)
- Maven (Java)
- CocoaPods (Swift / Objective-C)

Each ecosystem is an independently registered metadata provider. A provider owns the versioned-purl acceptance rules, registry endpoint, and normalized response evidence for that ecosystem. This keeps ecosystem-specific changes local: adding or removing a provider does not change central request parsing, registry dispatch, or SBOM ecosystem detection. Provider registration is immutable for a scan so repeated component processing performs only data lookup, not runtime configuration work.

The NuGet provider discovers its registration base URL from `https://api.nuget.org/v3/index.json` and requires the `RegistrationsBaseUrl/3.6.0` resource so SemVer 2.0.0 packages remain visible. Discovery is single-flight and retained by the registry client for the scan: concurrent package lookups share one service-index request rather than requesting the index per component. Only an HTTPS `api.nuget.org` endpoint without credentials, a non-default port, query, or fragment is accepted from the public service index. Registry responses are decoded according to their supported HTTP `Content-Encoding` before streaming JSON parsing; this includes the gzip representation exposed by the NuGet SemVer 2 registration resource.

Every registered ecosystem must have exactly one repository fixture in the ecosystem CI manifest. CI derives its smoke-test matrix from that manifest rather than maintaining another ecosystem list. The test contract compares manifest count and names with the provider registry, so adding a provider without a runnable fixture fails before release. Each matrix entry names one published dependency and its expected metadata source. CI must generate a CycloneDX SBOM, recognize the dependency under its registered purl type, collect its package metadata without a fetch error, and render text, Markdown, and JSON reports. Registry failures for fixture-local root packages emitted by an SBOM generator do not replace this per-dependency success check.

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

In JSON report schema version 1, the package license claim appears once in `licenseCandidates`. Its nested `evidence` has type `package-registry` and carries an opaque cache-key SHA-256 plus the metadata collection timestamp when known. It does not repeat the raw or normalized license, source, status, or warnings already present on the candidate. A cache identity proves which persisted observation was used; it does not make the registry an authority or attest that the license is legally correct.

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

When a registry returns `Retry-After`, the retry scheduler waits for that duration before the next attempt. HTTP 429 without `Retry-After` uses a one-second fallback delay. A failed service-index discovery is not retained as the provider endpoint, so the bounded retry performs a fresh discovery request; a successful discovery remains shared by every later lookup in the scan.

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

## Lessons Learned

- Go module proxy metadata exposes repository identity as `Origin.URL` and does not provide a package license field. A successful lookup therefore contributes unknown license evidence plus a source reference, not a fetch error.
- Registry parsing and persisted report records necessarily allocate. Reconciliation must avoid extra per-component `List` and `HashSet` allocations by using pooled temporary storage where equivalent behavior is preserved.
- deps.dev exposes Maven license identifiers derived from package metadata, but multiple values have no declared relationship. Joining them with SPDX `OR` would create a legal conclusion that the source did not make, so they remain ambiguous evidence.
