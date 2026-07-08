# v3 Implementation Plan

This plan describes a language-agnostic implementation path for v3. v3 builds on v1 and v2 by adding source repository license hints, initially through the GitHub License API.

## Goals

- Preserve all v1 and v2 CLI behavior and report fields.
- Use source repository evidence as an additional non-authoritative license hint.
- Use GitHub License API as the initial primary source repository hint.
- Use only `OL_GITHUB_TOKEN` for GitHub authentication.
- Add source repository evidence cache with hash-based file names.
- Keep scan best-effort and component-scoped for source fetch failures.

## Non-Goals

- No implicit `GITHUB_TOKEN` usage.
- No GitHub Enterprise Server support in initial v3.
- No HEAD preflight requests.
- No recursive repository search.
- No Contents API fallback for `LICENSE`, `COPYING`, or `NOTICE` discovery.
- No custom license body parsing when GitHub returns `NOASSERTION` or `null`.
- No allow-list or policy enforcement.

## Preconditions

v3 depends on:

- v1 internal component and evidence model
- v1 SPDX validation
- v2 package metadata evidence model
- v2 cache store pattern
- v2 fetch scheduler, concurrency, and retry policy
- repository URL evidence from SBOM and/or package metadata when available

## New Architecture Slices

Add these boundaries or equivalents:

- Source repository target planner.
- Repository URL normalizer.
- GitHub repository identity parser.
- GitHub auth provider.
- GitHub License API client.
- Source repository cache store.
- Source evidence normalizer.
- Source evidence integration into reconciliation.

## CLI Changes

`ol scan --sbom bom.json` automatically gains source repository hints in v3 when repository identity can be discovered.

Existing v2 options apply:

- `--refresh`
- `--concurrency <n>`
- `--retry <n>`

`--refresh` ignores both package metadata and source repository cache and overwrites fetched entries.

`ol cache clear source-repository` removes source repository evidence cache.

## Source Target Planning

For each component:

1. Inspect existing evidence for repository URLs.
2. Prefer repository URLs from package metadata when available.
3. Fall back to SBOM external references when available.
4. Normalize GitHub URLs into owner, repo, and optional ref.
5. If the repository is not GitHub-hosted, record unsupported source evidence.
6. If no repository URL exists, record unavailable source evidence.

Supported GitHub URL shapes should include common forms:

- `https://github.com/owner/repo`
- `https://github.com/owner/repo.git`
- `git+https://github.com/owner/repo.git`
- `git://github.com/owner/repo.git`
- `ssh://git@github.com/owner/repo.git`
- `git@github.com:owner/repo.git`

Normalize repository names by removing `.git` suffixes and trailing path fragments that are not part of owner/repo identity.

If a version-to-repository ref mapping is available from package metadata, use it. Otherwise omit `ref` and allow GitHub to use the repository default branch.

## Authentication

Read only `OL_GITHUB_TOKEN`.

Do not read `GITHUB_TOKEN` automatically.

Authentication modes:

- `ol_github_token`
- `none`

Send the token only to GitHub API requests for `api.github.com` or documented GitHub hosts in future explicit GHES support. Do not send it to package registries or arbitrary repository URLs.

Never emit token values in logs, stderr, stdout, reports, cache entries, or test snapshots.

## GitHub License API Client

Use:

```text
GET /repos/{owner}/{repo}/license?ref=<ref>
```

Do not issue HEAD preflight requests.

Response handling:

- HTTP 200 with valid `license.spdx_id`: create source license candidate.
- HTTP 200 with `license.spdx_id` as `NOASSERTION` or `null`: create unknown source evidence.
- HTTP 404: create `license_not_detected` unknown evidence.
- HTTP 403 or 429: create rate-limit or auth-related error evidence.
- HTTP 5xx or timeout: retry according to v2 retry policy, then create error evidence if exhausted.

Do not parse the returned license content body to infer a custom license in initial v3.

Evidence should preserve useful GitHub metadata:

- owner/repo logical reference
- ref, when used
- license path
- license file SHA
- license key/name
- SPDX ID
- HTML URL or logical URL
- HTTP status
- fetch timestamp

## Source Cache Design

Source repository cache path:

```text
<user-data-dir>/ol/cache/source-repository/<sha256(cache-key)>.json
```

Cache key:

- `github:<owner>/<repo>@<ref>` when ref is known
- `github:<owner>/<repo>@default` when no ref is supplied

The file name is hash-based so private repository names are not visible in directory listings. The cache entry body keeps the plain logical key for debugging and auditability, along with its hash.

Cache entry includes:

- schema version
- cache key
- cache key SHA-256
- fetched timestamp
- source: `github-license-api`
- auth mode, not token value
- request logical target
- response status
- license candidate or unknown/error evidence
- warnings and errors

There is no TTL. `--refresh` overwrites source repository cache for requested components.

Corrupt source cache entries are component-level cache errors. The scan should attempt network fetch unless future options disable it.

## Evidence Integration

For each component:

1. Start with v1 SBOM evidence.
2. Add v2 package metadata evidence.
3. Add v3 source repository evidence.
4. Validate source license candidates through the active SPDX validator.
5. Re-run the shared reconciliation rules.

Outcomes:

- SBOM, package metadata, and source repository agree: `matched`.
- Source repository valid license fills SBOM/package unknown: `matched`.
- Source repository valid license disagrees with another valid source: `conflict`.
- Source repository unknown with no other valid source: `unknown` unless other source errors dominate.
- Source repository fetch failed but SBOM or package metadata has single valid license: keep `matched` and record warning evidence.
- Source repository fetch failed and no usable candidate exists: component may become `error`.

## Fetch Scheduling

Use the same scheduler and retry policy as v2.

Source repository fetches may share the global `--concurrency` budget with package metadata fetches or run in a separate phase with the same limit. Output must remain deterministic regardless of fetch completion order.

Retry policy:

- default 1 retry, 2 total attempts
- retry timeout, HTTP 429, HTTP 5xx, and transient network errors
- do not retry HTTP 400, 401, 403, 404, invalid URL, unsupported host, or missing repository identity

## JSON Report Additions

Add source repository evidence to component records.

Add network metadata:

```json
{
  "network": {
    "githubAuth": "ol_github_token | none"
  }
}
```

Do not include token values, absolute local paths, or hidden cache file paths.

Represent cache and repository evidence with logical references and hashes.

## Text and Markdown Output

The default and verbose columns remain the same as v1/v2:

```text
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS
NAME VERSION LICENSE ECOSYSTEM DEPENDENCY STATUS PURL
```

Source evidence affects `LICENSE` and `STATUS` through the common reconciliation rules. It does not add default columns.

## Stderr Summary

Add v3 source repository summary fields:

- source repository target count
- GitHub License API request count
- source cache hit count
- source cache miss count
- source fetch error count
- source unknown count
- GitHub auth mode, without token value

## Privacy and Security Checks

Verify:

- `GITHUB_TOKEN` is never read by default.
- `OL_GITHUB_TOKEN` is never printed.
- token is sent only to GitHub API requests.
- reports contain auth mode only.
- source cache file names are SHA-256 hashes.
- absolute paths are not emitted in reports.

## Test Plan

### Unit Tests

- GitHub URL normalization for HTTPS, git, git+https, SSH URL forms.
- Unsupported host evidence.
- Missing repository URL evidence.
- `OL_GITHUB_TOKEN` auth mode detection.
- Ensure `GITHUB_TOKEN` alone results in unauthenticated mode.
- Source cache key and hashed file path generation.
- Source evidence reconciliation with SBOM and package metadata candidates.

### GitHub API Client Fixture Tests

- 200 with valid SPDX ID.
- 200 with `NOASSERTION`.
- 200 with `license: null`.
- 404 license not detected.
- 403 auth/rate failure.
- 429 retry then success.
- 5xx retry exhausted.
- timeout retry exhausted.

### Integration Tests

- v3 scan fills unknown SBOM license from GitHub License API evidence.
- v3 scan reports conflict between package metadata and source repository license.
- v3 scan keeps matched status when source fetch fails but SBOM is valid.
- v3 scan with `OL_GITHUB_TOKEN` records `ol_github_token` auth mode without token value.
- v3 scan with only `GITHUB_TOKEN` records `none` auth mode.
- v3 scan with `--refresh` overwrites source cache.
- `ol cache clear source-repository` removes source cache.

### Golden Files

Extend v2 JSON golden files with source evidence. Keep text and markdown columns stable. Ensure snapshots contain no absolute local paths and no token values.

## Implementation Milestones

1. Add source cache store using the shared cache pattern.
2. Add repository URL extraction and normalization.
3. Add GitHub auth provider for `OL_GITHUB_TOKEN` only.
4. Add GitHub License API client with fixture tests.
5. Add source target planning and evidence creation.
6. Integrate source evidence into reconciliation.
7. Add network metadata and stderr summary fields.
8. Add cache clear support for source repository cache.
9. Add integration tests and golden outputs.
10. Add privacy/security regression tests.
