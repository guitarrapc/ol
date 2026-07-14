# Source Repository Hint Specification

This document defines the v3 behavior for using source repository license evidence.

Source repository evidence is a hint source, not a legal authority. It is used because SBOM and package registry metadata can be absent, stale, inferred, or inconsistent with a repository's license file.

## Design Basis

This planned v3 specification derives from the [Ol design](../DESIGN.md), especially the decisions to [preserve evidence instead of selecting a single authoritative source](../DESIGN.md#decision-evidence-preservation), [add evidence sources through one reconciliation model](../DESIGN.md#decision-shared-reconciliation), [make component/source failures best-effort](../DESIGN.md#decision-failure-scope), [make evidence freshness explicit](../DESIGN.md#decision-cache-freshness), [version the persistent evidence format](../DESIGN.md#decision-cache-compatibility), [bound external I/O and avoid unnecessary requests](../DESIGN.md#decision-bounded-io), [persist evidence with explicit provenance and privacy boundaries](../DESIGN.md#decision-provenance-privacy), and [confine credentials to their intended authority](../DESIGN.md#decision-credential-confinement).

Source repository results are therefore additional attributable evidence, not a replacement for SBOM or package metadata. The GitHub API boundary, explicit authentication variable, opaque cache names, and refusal to infer a license from unidentified content follow from the need for explainable results without exposing credentials or converting uncertainty into a guessed conclusion.

## Version Scope

v1 uses SBOM evidence only.

v2 adds package metadata hints described in [packagemanager.md](packagemanager.md).

v3 adds source repository hints.

## GitHub Source License Hint

For GitHub repositories, v3 uses the GitHub License API as the primary source repository hint:

```text
GET /repos/{owner}/{repo}/license?ref=<ref>
```

> API Ref: https://docs.github.com/ja/rest/licenses/licenses?apiVersion=2026-03-10#get-the-license-for-a-repository

This endpoint is preferred over manually probing `LICENSE`, `COPYING`, and `NOTICE` paths because it returns GitHub's license detection result and SPDX candidate in one request.

Initial v3 does not perform recursive repository search and does not use the Contents API as a fallback for license file discovery.

<a id="contract-source-evidence"></a>
## Evidence Semantics

GitHub License API results are interpreted as source repository evidence:

- valid `license.spdx_id` becomes a source-repository license candidate.
- `NOASSERTION` or `null` becomes unknown source-repository evidence.
- HTTP 404 becomes `license_not_detected` evidence.
- HTTP 403, 429, and 5xx become error evidence.
- missing repository URLs become `source_repository_unavailable` evidence, and non-GitHub or invalid repository URLs become `unsupported_source_repository` evidence.

The API response body content is not parsed for custom license detection in initial v3. If GitHub does not identify a license, `ol` does not try to outguess it.

Evidence may include:

- license `spdx_id`
- license name/key
- license file path
- license file SHA
- HTML URL or logical repository URL
- fetch status
- warnings or errors

JSON reports expose this provenance as a structured `sourceRepository` object on the source candidate. Provenance fields do not inflate warning counts.

When package metadata supplies a repository commit or ref for the package version, that ref is part of the source target and cache identity. Otherwise GitHub resolves the repository default branch. Package metadata repository URLs take precedence over SBOM repository references.

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

Source repository fetches use the same bounded concurrency and retry controls as package metadata fetches. The default is one retry after the initial attempt. Timeout, HTTP 429, HTTP 5xx, and transient network failures are retryable; HTTP 400, 401, 403, 404, invalid repository identity, and unsupported hosts are not. Completion order must not change report ordering.

<a id="contract-source-cache"></a>
## Cache

v3 introduces source repository evidence cache.

Cache identity is based on the logical repository and ref. Physical entry names are opaque so private repository names are not exposed in directory listings, while entries retain enough logical identity and provenance for auditability.

The exact persisted properties, casing, validation rules, and schema-version behavior are defined by the planned [source repository cache schema version 1](cache_format.md#contract-source-cache-v1). Source integration must not define an independent cache shape.

Cache entries are persistent. There is no automatic TTL. `--refresh` ignores existing source repository cache and overwrites it with newly fetched evidence.

A corrupt entry is distinguished from a normal cache miss. Ol attempts recollection and retains `source_repository_cache_invalid` audit evidence even when recollection also fails. Retry-exhausted and non-retryable fetch failures are cacheable audit records so later reports can explain the collection outcome.

A source-cache write failure records `source_repository_cache_write_failed` but does not discard successfully fetched license evidence or fail the whole scan.

`ol cache clear source-repository` removes source repository evidence cache.

<a id="contract-source-best-effort"></a>
## Best-Effort Execution

Source repository fetch errors are component-level evidence. They must not stop the whole scan.

If SBOM or package metadata evidence already yields a single valid license, a source repository fetch failure records a warning but does not change the component to `error`.

If no usable license evidence exists and source repository fetching fails, the component may be `error`.

If source repository evidence disagrees with SBOM or package metadata evidence, the component is `conflict`.
