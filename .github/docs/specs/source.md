# Source Repository Hint Specification

This document defines the v3 behavior for using source repository license evidence.

Source repository evidence is a hint source, not a legal authority. It is used because SBOM and package registry metadata can be absent, stale, inferred, or inconsistent with a repository's license file.

## Design Basis

This v3 specification derives from the [Ol architecture](../Architecture.md), especially the decisions to [preserve evidence instead of selecting a single authoritative source](../Architecture.md#decision-evidence-preservation), [add evidence sources through one reconciliation model](../Architecture.md#decision-shared-reconciliation), [make component/source failures best-effort](../Architecture.md#decision-failure-scope), [make evidence freshness explicit](../Architecture.md#decision-cache-freshness), [version the persistent evidence format](../Architecture.md#decision-cache-compatibility), [bound external I/O and avoid unnecessary requests](../Architecture.md#decision-bounded-io), [persist evidence with explicit provenance and privacy boundaries](../Architecture.md#decision-provenance-privacy), and [confine credentials to their intended authority](../Architecture.md#decision-credential-confinement).

Source repository results are therefore additional attributable evidence, not a replacement for SBOM or package metadata. The GitHub API boundary, explicit authentication variable, opaque cache names, and refusal to infer a license from unidentified content follow from the need for explainable results without exposing credentials or converting uncertainty into a guessed conclusion.

Using the GitHub License API is an intentional product boundary, not a temporary substitute for a generic repository crawler. Ol does not enumerate arbitrary repository trees or search repository contents for license files. A repository whose independently licensed subtrees require multi-license analysis is outside this source-evidence model; that component-level evidence should be supplied by the SBOM or other dependency input rather than guessed from repository layout.

## Version Scope

v1 uses SBOM evidence only.

v2 adds package metadata hints described in [packagemanager.md](packagemanager.md).

v3 adds source repository hints.

## GitHub Source License Hint

For GitHub repositories, v3 uses the GitHub License API as the supported source repository hint:

```text
GET /repos/{owner}/{repo}/license?ref=<ref>
```

> API Ref: https://docs.github.com/ja/rest/licenses/licenses?apiVersion=2026-03-10#get-the-license-for-a-repository

This endpoint is used instead of manually probing `LICENSE`, `COPYING`, and `NOTICE` paths because it returns GitHub's license detection result and SPDX candidate in one bounded request.

Ol does not perform recursive repository search and does not use the Contents API as a fallback for license file discovery.

A component selected by [`--skip-evidence-packages`](cli.md#contract-skip-evidence-packages) is not planned as a source target and receives no source candidate, not even an unavailable one. The package-side `external_evidence_not_collected` candidate already records that collection was disabled for that component, and repeating it per evidence source would describe one decision as several outcomes.

<a id="contract-source-evidence"></a>
## Evidence Semantics

GitHub License API results are interpreted as source repository evidence:

- valid `license.spdx_id` becomes a source-repository license candidate.
- `NOASSERTION` or `null` becomes unknown source-repository evidence.
- HTTP 404 becomes `license_not_detected` evidence.
- a completed lookup that named a license file but no SPDX identifier becomes `license_not_recognized` evidence.
- HTTP 403, 429, and 5xx become error evidence. A 403 or 429 reporting `X-RateLimit-Remaining: 0` is a primary rate limit; one carrying `Retry-After`, a bounded GitHub secondary-rate-limit error body, or a bare 429 is a secondary rate limit. That body only classifies a 403; when it cannot be read, the status alone decides and the failure stays a plain non-transient 403 rather than becoming a transport error. A plain non-rate-limit 403 remains non-transient.

`license_not_detected` and `license_not_recognized` are both unknown outcomes, but they are not the same fact and a reviewer acts on them differently: the second names a license document that exists and can be read, the first says there is nothing at that repository and ref to read. Both are retained as warnings on the source-repository candidate so a report states which one occurred. They are derived from the recorded HTTP status and license fields rather than from a stored warning string, so an entry cached before these outcomes were named explains itself without being collected again.

<a id="contract-source-ref-fallback"></a>

A lookup at a named ref that answers `404` is repeated once at the repository default ref, and only then becomes `license_not_detected`. The ref comes from package metadata — a commit, a tag, or the branch inside a legacy license URL — and a branch moves or is deleted while the repository keeps its license, so the same `404` covers both "there is no license file" and "there is no such ref". `dotnet/standard@master` and `Microsoft/dotnet@master` are the ordinary case: both branches are gone, both repositories are MIT, and one request cannot tell which fact it observed.

The second answer is reported as the default ref's, not as the named ref's. The record carries `Ref` = `default` and retains `source_repository_ref_not_found`, so a report never implies that the version's own ref was read. A default-ref lookup is never repeated, and a named ref that answers `404` twice keeps the original ref in its record so the evidence still names what was asked for. Any other failure — a rate limit, a 5xx, a transport error — is not a fallback case and is raised unchanged.

Entries a previous resolver wrote for a named ref that answered `404` are stale rather than valid, because that resolver stopped at the first answer. They are refetched instead of being kept as a stale unresolved result for the life of the cache.

<a id="contract-source-rate-limit"></a>

External collection honors one wait rule: Ol waits only for a delay the server itself named and that fits the run's wait budget of ten seconds. Anything longer is not shortened into an earlier retry, because retrying sooner than the server asked ignores the instruction that came with the failure, spends the remaining allowance, and can extend a secondary limit. The budget is not a per-request timeout; it bounds only the delay a failure asks the run to absorb.

Applied to GitHub, this rule stops collection in practice every time. A secondary limit means the request pace was too high, so a named delay within the budget is retried once the delay elapses. A primary limit means the allowance is spent, which no delay this run can absorb will change, so it stops collection whatever reset it names. GitHub's own guidance puts a secondary limit at a minute or more and a primary reset up to an hour later, so both normally exceed the budget. `Retry-After` and `X-RateLimit-Reset` are then read to explain the outcome rather than to schedule a retry.

A rate limit that stops collection stops it for the rest of the scan: requests still pending for other components are not sent.

Rate-limit failures are never persisted as source-cache entries, so the affected targets are collected normally by a later run. Ol reports the limit on stderr even when the summary is suppressed, because the remedy differs by kind and the run can act on it: a primary limit reached without authentication names `OL_GITHUB_TOKEN`, an authenticated primary limit names the reset instant, and a secondary limit names `--concurrency`. A token raises the primary allowance and does nothing for a secondary limit, so the two must not be reported interchangeably.
- missing repository URLs become `source_repository_unavailable` evidence, and non-GitHub or invalid repository URLs become `unsupported_source_repository` evidence.

<a id="contract-source-subdirectory"></a>

A component whose package metadata states that the publisher placed it in one directory of a shared repository is not planned as a source target. It receives `source_repository_subdirectory` evidence naming the repository that was set aside, and no request is made. This is the repository case the model already excludes: the repository-level API answers for the repository root, so in a monorepo it answers for a different package, and reading it as this component's license turned a correctly declared license into a conflict with a sibling's. The repository is still reported because a reviewer needs to see which one was set aside and why. The evidence contributes no license either way, so a package that declared none stays unresolved rather than inheriting one from its neighbours. npm supplies this fact through [`repository.directory`](packagemanager.md#contract-npm-repository-directory).

The API response body content is not parsed for custom license detection. If GitHub does not identify a license, `ol` does not try to outguess it.

Evidence may include:

- license `spdx_id`
- license name/key
- license file path
- license file SHA
- HTML URL or logical repository URL
- fetch status
- warnings or errors

JSON report schema version 1 exposes this provenance once, as the typed `evidence` object nested in the source candidate. It contains only source-specific audit details and does not duplicate the candidate's raw/normalized claim, source, status, or warnings. Provenance fields do not inflate warning counts.

When package metadata supplies a repository commit or ref for the package version, that ref is part of the source target and cache identity. Otherwise GitHub resolves the repository default branch. Package metadata repository URLs take precedence over SBOM repository references.

For NuGet packages, the package-metadata boundary may derive this repository/ref pair from a narrowly validated legacy GitHub `licenseUrl`. Source enrichment still performs only the repository-level GitHub License API request described above; it neither downloads that URL nor treats its file content or path as a license claim. Because that request describes the repository root rather than the named file, the package-metadata boundary derives a pair only from a root license file GitHub itself would report, and otherwise leaves the package unresolved. Equivalent repository/ref pairs are deduplicated with ordinary package repository hints, so this compatibility path does not introduce a second request scheduler or bypass concurrency, retry, rate-limit, and cache controls.

Report examples must not include token values or absolute local paths.

<a id="contract-source-authentication"></a>
## Authentication

`ol` uses only `OL_GITHUB_TOKEN` for GitHub authentication.

```text
OL_GITHUB_TOKEN set   -> authenticated GitHub requests
OL_GITHUB_TOKEN unset -> unauthenticated GitHub requests
```

`GITHUB_TOKEN` is not read implicitly. In GitHub Actions, users must explicitly map a token if they want the CLI to use it:

```yaml
env:
  OL_GITHUB_TOKEN: ${{ secrets.GITHUB_TOKEN }}
```

The token must only be sent to GitHub API requests for `github.com` or `api.github.com`. It must not be sent to package registries, arbitrary URLs, or non-GitHub hosts.

GitHub Enterprise Server is outside initial v3 scope. A future GHES design should require explicit host/API configuration.

Reports may include auth mode but never token values:

```json
{
  "network": {
    "githubAuth": "ol_github_token | none"
  }
}
```

<a id="contract-source-request-strategy"></a>
## Request Strategy

`ol` does not perform HEAD preflight requests for GitHub existence checks. Most GitHub REST `GET` and `HEAD` requests consume comparable rate-limit points, so preflight checks can waste requests.

`ol` should issue the needed GET request directly and record the resulting status as evidence.

Source repository fetches use the same bounded concurrency and retry controls as package metadata fetches. The default is one retry after the initial attempt. Timeout, HTTP 5xx, and transient network failures are retryable; HTTP 400, 401, plain 403, 404, every GitHub rate limit, invalid repository identity, and unsupported hosts are not. Completion order must not change report ordering.

Report metadata distinguishes work deduplication from component outcomes: `targetCount` is the number of unique repository/ref targets, while `unknownCount` is the number of components that received no usable source license. Components sharing one unknown target therefore each contribute to `unknownCount` without increasing `targetCount`.

<a id="contract-source-cache"></a>
## Cache

v3 introduces source repository evidence cache.

Cache identity is based on the logical repository and ref. Physical entry names are opaque so private repository names are not exposed in directory listings, while entries retain enough logical identity and provenance for auditability.

The exact persisted properties, casing, validation rules, and schema-version behavior are defined by [source repository cache schema version 1](cache_format.md#contract-source-cache-v1). Source integration must not define an independent cache shape.

Cache entries are persistent. There is no automatic TTL. `--refresh` ignores existing source repository cache and overwrites it with newly fetched evidence.

A corrupt entry is distinguished from a normal cache miss. Ol attempts recollection and retains `source_repository_cache_invalid` audit evidence even when recollection also fails. Except for rate-limit failures, retry-exhausted and non-retryable fetch failures are cacheable audit records so later reports can explain the collection outcome.

Source-cache entries carry a resolver capability version independently of the cache schema version. Pre-capability HTTP 429 and 403 error observations are collected once again because the old cache did not retain enough rate-limit context to distinguish a rate limit from an ordinary forbidden response. The refreshed result then follows normal cache behavior; rate-limit failures remain non-persistent.

A source-cache write failure records `source_repository_cache_write_failed` but does not discard successfully fetched license evidence or fail the whole scan.

`ol cache clear source-repository` removes source repository evidence cache.

<a id="contract-source-best-effort"></a>
## Best-Effort Execution

Source repository fetch errors are retained as a source candidate and aggregated component warning. They must not stop the whole scan.

If SBOM or package metadata evidence already yields a single valid license, a source repository fetch failure records a warning but does not change the component to `error`.

If no usable license evidence exists and source repository fetching fails, the component may be `error`.

If source repository evidence disagrees with SBOM or package metadata evidence, the component is `conflict`.

## Lessons Learned

- Preserve an exact package-version repository commit or ref from registry metadata when available. Otherwise use an explicit default-branch lookup rather than guessing a tag.
- A corrupt cache entry and a missing entry are different audit events. Recollection may be identical, but corrupt-cache evidence must survive even when the replacement fetch fails.
- Indexed target/result arrays keep bounded concurrent collection independent from deterministic component projection and avoid concurrent result-map overhead.
- Source provenance is typed report metadata, not a warning. Keeping it nested on the candidate prevents warning-count inflation and duplicated formatted strings for shared results.
