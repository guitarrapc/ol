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
- HTTP 403, 429, and 5xx become error evidence. A 403 or 429 reporting `X-RateLimit-Remaining: 0` is a primary rate limit; one carrying `Retry-After`, a bounded GitHub secondary-rate-limit error body, or a bare 429 is a secondary rate limit. That body only classifies a 403; when it cannot be read, the status alone decides and the failure stays a plain non-transient 403 rather than becoming a transport error. A plain non-rate-limit 403 remains non-transient.

<a id="contract-source-rate-limit"></a>

A reached rate limit stops source collection for the rest of the scan. Ol does not wait it out and does not retry it. GitHub decides when a limit lifts on a schedule a command-line run cannot absorb — a primary limit resets up to an hour later, and a secondary limit asks for at least a minute — so waiting would replace a fast, explainable failure with an unbounded silent one, and retrying would only spend the remaining allowance or extend a secondary limit. `Retry-After` and `X-RateLimit-Reset` are read to explain the outcome, not to schedule a retry. Requests still pending for other components are not sent once a limit is reached.

Rate-limit failures are never persisted as source-cache entries, so the affected targets are collected normally by a later run. Ol reports the limit on stderr even when the summary is suppressed, because the remedy differs by kind and the run can act on it: a primary limit reached without authentication names `OL_GITHUB_TOKEN`, an authenticated primary limit names the reset instant, and a secondary limit names `--concurrency`. A token raises the primary allowance and does nothing for a secondary limit, so the two must not be reported interchangeably.
- missing repository URLs become `source_repository_unavailable` evidence, and non-GitHub or invalid repository URLs become `unsupported_source_repository` evidence.

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
