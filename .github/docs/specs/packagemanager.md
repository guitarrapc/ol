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

Successful fetches overwrite the relevant cache entry. A cache miss or refresh failure records component-scoped `package_metadata_fetch_failed` evidence; existing valid SBOM evidence remains authoritative for the component's final status.

<a id="contract-go-license"></a>

The Go module proxy states where a module version came from and at which ref, but carries no license field, so a Go lookup reads the license from deps.dev and keeps the proxy's origin. Both are needed, which is why the proxy response is retained rather than replaced: the ref pins repository evidence to the released version, and a project that relicenses after a tag would otherwise be reported with its default branch's license. A Go lookup that reaches the proxy but not deps.dev contributes what the proxy stated rather than failing the component, because the license source adds to a response that already stands on its own.

Reading the license from module contents rather than from a repository host is what makes an ecosystem-wide class of modules resolvable at all. Before it, a Go module could only be resolved by whatever API its host happened to expose, so every module outside GitHub stayed unresolved however plainly it was licensed: measuring three Go projects, all of `golang.org/x/*`, `google.golang.org/protobuf`, `gopkg.in/*`, and `rsc.io/*` came back unresolved, and those appear in nearly every Go build. Across those projects the change resolved 23 more components and left 4 unresolved, of which 3 are modules deps.dev lists several licenses for without stating how they relate.

HTTP 404 is a completed answer rather than a failed operation: the registry reported that the package is not published there. It therefore contributes unknown evidence with the warning `package_metadata_not_found`, not a collection error. This is what a package published only to a private feed produces, and it keeps such a component acknowledgeable by a baseline instead of permanently unresolvable. Timeouts, HTTP 429, and HTTP 5xx remain collection errors because the question was never answered. Ol does not infer the origin of a package from a lockfile download URL: a corporate proxy serves public packages from an internal host, so the host is not evidence about where a package is published.

<a id="contract-unqueryable-purl"></a>

A purl that no registry can be asked about is reported by which of the two reasons applies. `unsupported_package_metadata` means no provider owns that ecosystem, and only Ol gaining one changes it. `package_metadata_unversioned_purl` means the ecosystem is supported but the purl names no single package version, which the reader fixes in the input. Collapsing the two, as an earlier implementation did, told a reviewer that Ol does not support Maven when the truth was that the generator emitted a module without a version.

The distinction is not a corner case. A generator reading the child POMs of a multi-module build emits each module without the version its parent supplies, so one such project accounted for 180 components stating no version.

The scan summary keeps the same distinction: `unsupported ecosystems` and `unversioned purls` are counted and reported separately, in the stderr summary and as `unsupportedEcosystemCount` and `unversionedPurlCount` in the canonical report. A single count would restate at the aggregate exactly the claim the two reasons exist to avoid, and at the scale above it is the aggregate a reader looks at first.

Both reasons outrank every repository reason when the [unresolved section](cli.md#contract-unresolved-section) picks one. A component no registry could be asked about also ends with no repository, because nothing ever produced one, so the repository outcome is the consequence and the skipped registry is the cause. Reporting the consequence sends the reader looking for a repository that was never sought: a scan of a Go project whose SBOM also catalogued its GitHub Actions reported six of them as `source_repository_unavailable` when the fact worth stating was that Ol has no provider for that ecosystem at all.

A component selected by [`--skip-evidence-packages`](cli.md#contract-skip-evidence-packages) is not planned for a lookup at all. It receives one candidate with status `unknown` and the warning `external_evidence_not_collected`, which keeps "not asked" distinguishable from a registry that answered without a license, and keeps the component acknowledgeable rather than errored.

Registry responses that are valid JSON but do not embed the expected metadata object contribute unknown metadata rather than terminating the scan. In particular, a NuGet registration entry exposes `@id` as a NuGet-hosted catalog URL. Ol follows that trusted URL to recover the package-version metadata the registration omits, as specified in [NuGet catalog entry resolution](#contract-nuget-catalog-entry); malformed or untrusted references remain unknown and must not erase usable SBOM evidence or fail the whole scan.

v3 keeps this behavior and adds source repository hints described in [source.md](source.md).

The resolved-input pipeline accepts NuGet `project.assets.json` version 3 or 4 through one or more `scan --input ...` file or directory options. The NuGet handler owns recursive discovery of the exact `project.assets.json` name and ASCII-case-insensitive package identity comparison. File content is still auto-detected as `nuget-assets`; `--input-format nuget-assets` remains available as an assertion over every discovered file. This is dependency inventory input, not package-registry license evidence. The adapter consumes restore results and does not reproduce NuGet resolution.

The same pipeline accepts npm `package-lock.json` lockfile version 2 or 3 as `npm-package-lock`. Its handler owns recursive discovery of the exact `package-lock.json` name. The adapter consumes the `packages` install tree and never calls a registry to resolve dependency ranges.

<a id="contract-sbom-component-name"></a>

A CycloneDX component states its name in `name` and its namespace in `group`, and whether the two form one package name is a fact about the ecosystem rather than about the document. Ol rejoins them as `group/name` for ecosystems whose provider declares that its package name includes its purl namespace, and keeps `name` alone everywhere else: npm installs `@scope/pkg`, Go requires `github.com/owner/repo`, and Composer requires `vendor/package`, while a Maven artifact stays `commons-lang3` with its group visible in the purl and source id, which is what the Maven adapter already produces. The rule belongs to the provider that already composes the same two parts into a registry endpoint, so a new ecosystem declares it once and no parser gains an ecosystem switch. A name that already begins with its group is left as it is, because generators disagree about whether `name` repeats it, and a component with no purl keeps its name because no convention is known. Acknowledgements recorded against a previously unqualified name expire when the name is corrected, which fails closed and asks for one review rather than carrying a stale approval.

## .NET NuGet resolved input

Each `targets` object becomes a separate resolution context. A target key before `/` is retained as the target framework and the suffix is retained verbatim as the runtime identifier. Ol does not infer operating system or architecture fields from the RID.

Each context owns an implicit project root and package occurrences backed by `type: package` entries that also exist as package libraries. The root is represented by the edge endpoint sentinel `-1`, not by a license-report component allocation. Project, unresolved, and non-package entries do not receive NuGet purls and are not rendered as packages. Project nodes remain available while classifying reachability, so a package reached through a project reference is transitive, but an omitted project node is not misrepresented as a package edge.

Direct dependencies are package or project names declared by the matching `project.frameworks` entry. Reachable packages at depth zero are direct, packages reached below them are transitive, and packages whose relationship cannot be proven are unknown. Package-to-package edges and project-root-to-direct-package edges are retained per context. Identical package/version values in different projects or targets remain distinct occurrences but share one report component and one `pkg:nuget/{id}@{version}` enrichment identity. A shared component is direct if any occurrence is direct, otherwise transitive if any occurrence is transitive, otherwise unknown.

Development usage stays unknown for this input, and `project.assets.json` carries enough fields that resemble a development scope to be worth naming. A `targets` entry records what a package provides for a framework rather than what a project consumes: a reference with `ExcludeAssets="compile;runtime"` keeps both sections, because that filter is recorded once per direct reference under `project.frameworks` and is never applied to the resolved target. The asset shape also cannot separate cases that must be classified differently, because `analyzers` is not a `targets` asset key at all, so a source generator whose output is compiled into the shipping assembly and a pure analyzer are both the bare entry `{"type": "package"}`. `suppressParent` is parent-flow control rather than usage and stays beside `compile` and `runtime` for a library the project uses at runtime, so reading it as development is fail-open, and restore does not persist the nuspec `developmentDependency` flag. Classifying `build`-only entries as development would also contradict Cargo, where build dependencies are production for the same code-generation reason. What .NET calls a development dependency is usually a package only a test or benchmark project references, which is a fact about solution topology rather than about any one resolved input, and is expressed by scoping `--input`.

<a id="contract-nuget-restore-artifact-evidence"></a>
### Restored package artifact evidence

The NuGet restore artifact collector consumes the same `project.assets.json` without reproducing restore. It joins package components to `libraries` case-insensitively, resolves each package through `packageFolders` plus the library's relative `path`, and refuses absolute paths or paths that escape a package root. A package directory that is absent is not an error: the assets document may have been copied from another machine or its cache may have been cleaned. The assets bytes, package-root workspace, component index, and license-document bytes are pooled within one synchronous owner scope. Library identities remain UTF-8 spans through a two-pass streaming parse; only filesystem paths and evidence retained in the result become owned strings.

For a restored package, the collector first honors a nuspec `<license type="file">` path that resolves to a file inside that package. Without a usable declaration, it examines top-level `COPYING`, `LICENCE`, `LICENSE`, and `UNLICENSE` files in deterministic case-insensitive order. It does not recurse or inspect arbitrary package content. Each document is capped by the active SPDX text matcher's byte limit and read into pooled storage. A match contributes ordinary `package-artifact` license evidence; an unrecognized document contributes unknown evidence so its logical path and exact content SHA-256 remain auditable. Missing, inaccessible, malformed, or oversized local documents are best-effort misses and do not fail dependency inventory ingestion.

The evidence artifact identity is the versioned NuGet purl. Its path is relative to the restored package directory and uses `/`; machine-local package-cache paths and license bodies are never retained. SPDX classification records the stable matcher name and corpus version. The collector itself is a local evidence boundary. It is not yet invoked automatically by `scan`, because the bundled SPDX data does not yet include the versioned text templates the matcher requires.

## JavaScript npm resolved input

The empty `packages` key is the root context. Non-`node_modules` package paths become additional workspace contexts. Link entries remain traversal nodes and are not emitted as npm registry packages. Registry package names are derived from installed `node_modules` paths, including scoped names, and versioned purls use canonical percent encoding such as `pkg:npm/%40scope/name@1.2.3`.

Dependency names from `dependencies`, `optionalDependencies`, `devDependencies`, and `peerDependencies` are resolved against installed package paths using Node-style ancestor lookup. Missing optional or peer entries do not create phantom components. Root-to-package and package-to-package edges are retained; a workspace or link traversal node is never relabeled as a registry package edge.

Different installed paths remain different components and occurrences even when their npm name and version match. The npm handler registers a `purl + sourceId` collection identity so repeated input combination also preserves nested duplicates. Their purls remain equal, allowing the existing enrichment planner to deduplicate registry work by versioned purl. Root and workspace reachability determine direct/transitive classification, and the strongest relationship is projected to the report component.

Package `dev`, `optional`, `devOptional`, and `peer` flags plus `os` and `cpu` arrays are retained as sparse occurrence variants. Ol records these values in deterministic lockfile order and does not evaluate them against the executing host. The `packages[].license` string is classified as `dependency-input` evidence with its npm format and field provenance; it is not presented as SBOM evidence.

## JavaScript pnpm resolved input

Each pnpm importer becomes a resolution context. Link and workspace nodes participate in traversal but do not become npm registry components. Version 9 snapshot identities remain source identifiers, including peer suffixes, while canonical versioned npm purls remain enrichment identities. Optional/dev reachability, peer snapshot suffixes, and package `os`/`cpu` restrictions are retained as sparse occurrence variants without host evaluation.

`os` and `cpu` are the only YAML sequences the adapter reads, in either block or inline form. A sequence under any other key is ignored, because pnpm writes `transitivePeerDependencies`, `libc`, `bundledDependencies`, and top-level build lists that carry no license evidence, and future versions will write more. A sequence is rejected only where it stands in for a mapping the adapter does read: an `importers`/`packages`/`snapshots` entry, or a resolved dependency under `dependencies`, `optionalDependencies`, or `devDependencies`.

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

Because root `require` and `require-dev` are seeded as distinct owners, production reachability is computed independently of the lock's own buckets and then used to validate them. A `packages-dev` entry that a production requirement can reach is a stale or hand-merged bundle, and the input is rejected as inconsistent rather than classified. This is what stops a runtime package from being relabeled development-only by editing the lock file alone.

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

Maven dependency tree input does not cause Ol to resolve a POM or dependency version. Canonical versioned Maven purls are enriched through deps.dev v3 with POM-derived SPDX license hints and a source-repository link when available. A single reported license is reconciled normally. Because deps.dev does not specify the relationship between multiple reported licenses, Ol preserves their listed values as one ambiguous raw claim and does not synthesize `AND` or `OR`. The members are resolved against SPDX and retained as a [license listing](spdx.md#contract-license-set), which is what lets a later reader treat them as an enumeration without recognizing the separator. This metadata remains an additive hint rather than authority; CycloneDX is preferable when effective-POM metadata and build repository context must be captured in the input artifact itself.

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

<a id="contract-provider-declared-reference"></a>

Every provider retains a [declared license reference](spdx.md#contract-declared-license-reference) when its registry states one, so the concept stays one representation rather than one vocabulary per ecosystem. NuGet supplies `licenseFile` as an artifact path and `licenseUrl` as a location, preferring the file when both are declared because a file the package carries is a more specific place to look than a URL that may lead anywhere. Cargo supplies `license_file` and PyPI supplies a single `license_files` entry as artifact paths; a collection of several entries names no single place, so it supplies none. CocoaPods supplies `license.file` as an artifact path, and records `license.text` as embedded text whose content is deliberately not retained.

A reference never contributes a license and never changes what a component resolves to. It is retained so a report can say where the publisher pointed, which for an unresolved component is the only actionable fact available.

The npm provider reads the license from whichever declaration shape the published metadata uses. npm declared a license as an object and as a collection of them before the current string field, and packages published under those shapes are still installed today: reading only the current field reported `wrench@1.5.9` unresolved while its registry metadata plainly states `MIT`. The current field wins when both are present, because a package carrying both was republished with the newer one. A collection of several entries states no relationship between them, so it resolves nothing rather than having one entry picked or an operator invented.

<a id="contract-npm-repository-directory"></a>

The npm provider also reads `repository.directory`, which exists precisely because the repository holds more than this package. The repository stays the package's repository, but its root license answers for whatever the repository as a whole is licensed under, which in a monorepo is a different package. The provider therefore records `source_repository_subdirectory`, and [source evidence](source.md#contract-source-subdirectory) plans no repository-level lookup for that component. Without it, `eslint-visitor-keys@5.0.1` declaring `Apache-2.0` was reported as conflicting with `eslint/js`'s root `BSD-2-Clause`, and `@conventional-changelog/git-client@3.1.0` declaring `MIT` with its monorepo's `ISC` — a correctly declared license turned into a finding no reviewer can act on.

The Cargo provider and the Cargo resolved-input adapter both read `MIT/Apache-2.0` as `MIT OR Apache-2.0`. Cargo accepted that spelling before its `license` field was defined as an SPDX expression, and documents `/` as the deprecated form of `OR`, so the expression is stated rather than inferred; crates such as `minimal-lexical@0.2.1` and the `unic-*` family still publish it. The rewrite is a classification input only: the candidate keeps the raw spelling the crate published, and the crates.io cache keeps the value crates.io returned, so a cached entry is still classified against the active SPDX data.

<a id="contract-go-module-path-repository"></a>

The Go provider derives the repository from the module path when the proxy states no `Origin`. `proxy.golang.org` omits that object for module versions it cached before it began recording one, which covers widely used modules such as `github.com/davecgh/go-spew@v1.1.1` and `github.com/json-iterator/go@v1.1.12`; without a repository they had no source evidence at all. This is Go module resolution rather than a guess about layout: the Go command itself treats the first two `github.com` path elements as the repository root, and a module path is by definition where the module is fetched from. It is limited to that host and derives no ref. A vanity import path such as `gopkg.in/yaml.v3` or `rsc.io/pdf` names a redirect only a `go-get` request resolves, so its module path is not a repository URL and the module stays unresolved rather than acquiring an invented one.

The NuGet provider discovers its registration base URL from `https://api.nuget.org/v3/index.json` and requires the `RegistrationsBaseUrl/3.6.0` resource so SemVer 2.0.0 packages remain visible. Discovery is single-flight and retained by the registry client for the scan: concurrent package lookups share one service-index request rather than requesting the index per component. Canceling one caller stops only that caller's wait and does not cancel discovery shared by other callers. Only an HTTPS `api.nuget.org` endpoint without credentials, a non-default port, query, or fragment is accepted from the public service index.

For each NuGet metadata lookup, the provider requests the documented `{registration-base}/{lower-id}/index.json` endpoint. It reads an inline registration leaf when present; otherwise it uses NuGet version semantics to select the page whose inclusive `lower` and `upper` bounds contain the requested normalized version, then follows that page's trusted `@id`. Page and leaf URLs are never predicted from a package version. Registry responses are decoded according to their supported HTTP `Content-Encoding` before streaming JSON parsing; this includes the gzip representation exposed by the NuGet SemVer 2 registration resource.

<a id="contract-nuget-catalog-entry"></a>

A registration document inlines only part of the catalog entry. It omits `licenseFile` and `repository`, and when a package declares an embedded license file the registration rewrites `licenseUrl` to the gallery license page, which erases the fact that a file was declared. The registration therefore cannot distinguish "the package declares nothing" from "the package declares a license location Ol has not read", and it hides a version-pinned repository the author supplied.

So when the matched registration entry declares no `licenseExpression`, Ol follows that entry's trusted `@id` to the catalog entry and projects the catalog entry instead. The catalog entry is immutable and version-addressed, so the same package version always yields the same observation. An entry that already declares an expression is projected as-is and costs no extra request, because an expression answers the question the catalog entry would be read for. A missing or untrusted `@id` leaves the registration entry as the projected document rather than failing the lookup. Following a document chain is bounded, which is what keeps a malformed or self-referential chain from requesting indefinitely; NuGet needs at most a page and then a catalog entry.

The NuGet catalog `repository.url` with its commit is the preferred source-repository hint. It must first pass the same credential, local-file, query, and fragment checks used when the normalized result is persisted. A repository that survives those checks is the package's declared repository even when Ol cannot collect from it, and is never replaced by another URL in the same catalog entry; only an absent one is filled. The `projectUrl` fills it after passing the same checks, so the value a warning is decided from is the value that is persisted. A legacy `licenseUrl` is consulted ahead of the unversioned `projectUrl`, and only when the catalog declares no `licenseExpression`, because its only purpose is to resolve a license Ol does not already have. It supplies a target only for an HTTPS GitHub license-file shape: `github.com/{owner}/{repository}/blob/{ref}/{file}`, `raw.githubusercontent.com/{owner}/{repository}/{ref}/{file}`, or the legacy `raw.github.com` equivalent. Ol extracts the explicit single-segment ref and passes the normalized repository/ref to source enrichment. Because source enrichment answers with the license GitHub detects at the repository root rather than with the file the URL names, `{file}` must be a repository-root file whose name GitHub reports as that license: `LICENSE`, `LICENCE`, `COPYING`, or `UNLICENSE`, compared case-insensitively, with no extension or with `.txt` or `.md`. A nested path is rejected whether the ref is a branch, a tag, or a full commit SHA, because the repository-level answer would describe a different file than the URL names. A qualified name such as `LICENSE.MIT` or `COPYING.LESSER` is rejected for the same reason: it selects one license among several in the same repository. Ol does not fetch the `licenseUrl`, follow redirects, scrape a page, interpret a URL path as an SPDX expression, or substitute the repository default branch when that URL supplies a ref. Credentials, non-default ports, query/fragment data, escapes, backslashes, literal dot segments, missing path segments, and refs exceeding the cache limit are rejected.

Package-metadata cache entries carry a resolver capability version independently of the cache schema version. After an upgrade adds new NuGet evidence resolution, an older NuGet observation with an empty license is collected once again, whichever warning or repository it already held: an empty license is the state that makes Ol read the catalog entry, and the entry can supply a repository, a license file, or a different legacy URL than the registration showed. A NuGet entry that already carries a declared expression is unaffected, because the newer resolver reads no further for it. The refreshed observation then remains a normal cache hit; an unresolved package is not fetched repeatedly merely because no license was found.

A provider records no warning for a declaration it could not resolve into a license. That a publisher named a place and Ol did not read it is one fact in every ecosystem, and the [declared license reference](spdx.md#contract-declared-license-reference) plus the component's status already state it, so reports [derive it](cli.md#contract-unresolved-section) instead. NuGet held three warning identifiers for this — one per reference kind — while Cargo, PyPI, and CocoaPods declared the same references and said nothing; deriving the fact removed the asymmetry and returned three of the sixteen available warning bits.

A NuGet registration that does not list the requested version contributes `package_metadata_not_found`. The registry completed the request and described no such version, which is the fact HTTP 404 states; calling it an absent license declaration would assert something about metadata the registry never returned.

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

An observation with an empty license that an older resolver capability version wrote is treated as a cache miss once, in every ecosystem rather than only the one whose resolver changed. Recollection writes either a usable repository/ref or an explicit NuGet warning, so subsequent scans return to normal cache hits; other ecosystems and NuGet entries with a declared expression are not invalidated.

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

When a registry returns `Retry-After`, the retry scheduler waits for that duration before the next attempt, subject to the same ten-second wait budget that governs source collection, specified in [source.md](source.md#contract-source-rate-limit). A longer delay is never shortened into an earlier retry: it stops that origin for the rest of the scan, and every later request to it fails without being sent. The delay reported with those failures is the longest one the registry asked for, so a later short cooldown cannot make a stopped origin look retryable. Registries answer a rate limit either with no delay, where the fallback below clears it, or with one measured in minutes, which an interactive run cannot absorb; shortening the second case would pay the wait without honoring what the registry asked for. A token is not a remedy here, because public registry metadata takes no authentication and authenticating raises no allowance, so a lower `--concurrency` is the only lever the next run has.

HTTP 429 without `Retry-After` uses a one-second fallback delay. The registry client also applies the cooldown to every later request for the same origin. At expiry, one request acts as a probe; other requests remain paused until that probe succeeds or establishes a new cooldown, preventing a retry burst from the bounded worker pool. Only the probe releases the probe slot, so a response that was already in flight when the cooldown began cannot admit a second concurrent probe. A failed service-index discovery is not retained as the provider endpoint, so the bounded retry performs a fresh discovery request; a successful discovery remains shared by every later lookup in the scan.

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

- Splitting a reason means splitting everything derived from it. The two unqueryable-purl reasons were separated at the component level while the summary kept one counter for both, so a Maven multi-module build still reported "180 unsupported ecosystems" — the sentence the split was written to prevent, surviving in the number a reader reaches for first. Per-component evidence was correct throughout, which is what let it go unnoticed: the report was right in detail and wrong in summary. When a distinction is worth making, find every place the old conflation is still spelled out.
- A sentinel space with meaning needs a value per meaning, and a fast path that skips it hides the collision. The metadata planner encodes "no request issued" as negative indexes: one for an unsupported ecosystem, one for a versionless purl. Excluded components and components without a purl were given the unsupported-ecosystem value as though it meant nothing, so a `pkg:nuget/` component excluded by `--skip-evidence-packages` was reported as an unsupported ecosystem and counted as one, on top of the `external_evidence_not_collected` the [option's contract](cli.md#contract-skip-evidence-packages) already promised. The spec was right and only the code disagreed. It survived because the single-component path returns before planning: every test of the option used one component, so the same input reported one thing at size one and another at size two. When a code path exists only above a size threshold, a test below it proves nothing about it.
- Go module proxy metadata exposes repository identity as `Origin.URL` and does not provide a package license field. A successful lookup therefore contributes unknown license evidence plus a source reference, not a fetch error. That object is absent for many older module versions, and treating its absence as "this module has no repository" silently disabled source evidence for roughly half the modules in an ordinary Go build graph. An ecosystem whose identifiers already state where a package comes from should derive the repository from the identifier rather than only from an optional response field.
- A registry field that says a package occupies part of a repository is license-relevant even though it is not a license. Reading npm's `repository.directory` was the difference between reporting a monorepo package's declared license and reporting it as conflicting with a sibling package's license. When an ecosystem tells Ol that repository-level evidence has a different subject, that is a stronger signal than the evidence itself.
- Registry parsing and persisted report records necessarily allocate. Reconciliation must avoid extra per-component `List` and `HashSet` allocations by using pooled temporary storage where equivalent behavior is preserved.
- deps.dev exposes license identifiers derived from package metadata, but multiple values have no declared relationship. Joining them with SPDX `OR` would create a legal conclusion that the source did not make, so they remain ambiguous evidence. This holds wherever the source is used: Maven adopted it first, and Go inherits the same restraint, which is why `gopkg.in/yaml.v3` reports `MIT; Apache-2.0` as ambiguous rather than resolving it.
- One evidence source answering an ecosystem says nothing about the next one. deps.dev stated a license for all 65 Go modules measured and agreed with Ol's existing answer in 44 of the 45 it already resolved; asked about the old `System.*` and `runtime.*` NuGet packages, it returned `non-standard` for every one. The difference is not source quality but where the license fact lives. A Go module carries it in its contents. Those NuGet packages carry `licenseUrl` pointing at a redirect that ends on a page opening with "This document is provided for informative purposes only and is not itself a license", so no SPDX identifier is reachable from the package's own declaration however far a tool follows it. `declared_license_location_not_collected` naming that URL is the complete answer, and `non-standard` is the same conclusion in another vocabulary. Measure a source per ecosystem before adopting it for one.
- An ecosystem's own infrastructure does not always state the license, and where the license lives decides what can be resolved. The Go module proxy is canonical for identity and origin and silent about licensing, so Go resolution was hostage to whatever repository host a module happened to use. Adding a source that reads the package contents removed a failure whose shape was "this host is not GitHub" rather than "this package has no discoverable license". Before reaching for a mapping from one host to another, look for a source that answers the actual question.
- Neither input path is generally better. Measuring five ecosystems against both a resolved package-manager input and a native-generator CycloneDX SBOM reversed the ranking three ways, decided by where the license fact lives: npm and Cargo carry it in the resolved input and resolve offline; NuGet and Go carry none and depend entirely on collection; Python's installed metadata beats its registry API, so the SBOM wins. A recommendation to prefer one path is a recommendation for one ecosystem.
- The spread between SBOM generators is wider than the spread between input paths. Both measured SBOMs were CycloneDX, but `cyclonedx-gomod` reads the license file in the module cache and resolved 39 of 40 components with no collection at all, while the `CycloneDX` .NET tool reads only nuspec metadata and resolved a strict subset of what the package-manager path already had. Accepting an SBOM delegates resolution to its generator; it does not obtain resolution.
- A general-purpose scanner and a resolved input rarely enumerate the same set, and the overlap can be the small part. Running syft and the package-manager input over the same eighteen real repositories produced 1,769 packages from the SBOMs and 2,842 from the resolved inputs for 3,589 distinct packages together; in the axios tree only 34 of 890 rows were seen by both, because the SBOM reached a documentation sub-project the root lockfile never mentions and the lockfile carried dev dependencies the scan did not. Any single-input report is a confident-looking answer about a population nobody chose.
- A generator can invent components that no registry can ever answer for. Pointed at .NET build output, syft catalogs assemblies rather than packages, emitting `pkg:nuget/Dia2Lib.dll@2.0.0.0` and `pkg:nuget/CommandLine@2.9.1.0` where the packages are `Dia2Lib` and `CommandLineParser@2.9.1`. That accounted for 651 of the 672 `package_metadata_not_found` results in the measurement, and 669 of those rows were supplied by the SBOM alone. This is what makes the supplying input worth reporting per component: it separates "this dependency has no discoverable license" from "this input made this component up".
- Where a generator splits a package identifier is a portability problem, not a cosmetic one. For Go, syft writes the trailing module-path segments as the purl subpath, so one module arrives as `github.com/ugorji/go@v1.3.1#codec` from the SBOM and `github.com/ugorji/go/codec@v1.3.1` from the module graph. Matching that dropped the subpath reported the module twice, once resolved and once not, and would have attached a submodule's license to its parent had the versions agreed.
- An input adapter's allocation floor is the owned inventory it returns, and every adopted adapter reaches it: measured parser-specific managed allocation is 0 B in all of them. Reaching that floor is a design consequence rather than a tuning exercise — source-backed `Utf8Slice` values, pooled node/dependency/edge/index buffers, and span-based open addressing instead of a DOM, a `Dictionary`, or a transient string per token. An adapter that cannot reach it is usually building an intermediate model the inventory does not need.
- An adapter tested only against its own fixture can be self-consistently wrong, because the fixture is written to whatever the adapter produces. What the SBOM path and the lockfile path do with the same dependency is checkable without either being a specification of the other, and comparing them is what surfaced that a CycloneDX SBOM named `@tailwindcss/cli` as `cli`, `@tailwindcss/node` as `node`, and `@isaacs/fs-minipass` as `fs-minipass` — three collapses into names that already belong to other packages — while the npm lockfile named all three correctly. An identity that differs by how the same fact was delivered is a defect even when every test passes, so an ecosystem reachable through more than one input path deserves one test that runs both and compares.
- A hand-written fixture encodes what the adapter author already models, so a passing suite says nothing about what an ecosystem actually emits. Every pnpm test passed while every real pnpm v9 lockfile failed on the first line of `transitivePeerDependencies`, a key that carries no license evidence and appears 589 times in one ordinary Docusaurus site. A line parser that reads a whitelist of keys must still decide what to do with the syntax it does not read, and both defaults are wrong: rejecting unknown shapes fails the whole input over values nobody wanted, and ignoring them silently accepts a corrupt lockfile as a short one. Scope the tolerance by position instead — reject only where a mapping the parser reads is required — and validate an adapter against a lockfile the ecosystem generated, not one the author wrote.
- A resolver record that resembles a usage record has to be tested against the cases it must keep apart, not the ones it happens to get right. NuGet `project.assets.json` was measured as a source of development usage because build-only packages visibly lack `compile` and `runtime`, and it fails on both halves of the question: `ExcludeAssets="compile;runtime"` leaving both sections in place proves the sections describe the package rather than the project, and the absence of an `analyzers` asset key collapses a source generator whose output ships inside the assembly and a pure analyzer into the same bare entry. The payoff decided it as much as the soundness — across 40 assets files and 298 NuGet components in one real solution, 93 of 94 unresolved components keep `compile` or `runtime` somewhere, and the single candidate was the `NETStandard.Library` metapackage, whose children carry the code the parent does not. A classification that cannot fire without being wrong buys nothing, and a compliance gate is the wrong place to spend a near-miss.
- Adding a resolved-input format must not require a decision anywhere else. Fourteen adapters were added without a format switch reaching enrichment, reconciliation, views, or output, because each handler owns its content signature, discovery names, parser, and identity rule as one registration. The property worth protecting is that the cost of the fifteenth adapter is the adapter.
