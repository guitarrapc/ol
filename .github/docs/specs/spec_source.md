# Source Repository Hint Specification

This document defines the v3 behavior for using source repository license evidence.

Source repository evidence is a hint source, not a legal authority. It is used because SBOM and package registry metadata can be absent, stale, inferred, or inconsistent with a repository's license file.

## Version Scope

v1 uses SBOM evidence only.

v2 adds package metadata hints described in [spec_packagemanager.md](spec_packagemanager.md).

v3 adds source repository hints.

## GitHub Source License Hint

For GitHub repositories, v3 uses the GitHub License API as the primary source repository hint:

```text
GET /repos/{owner}/{repo}/license?ref=<ref>
```

> API Ref: https://docs.github.com/ja/rest/licenses/licenses?apiVersion=2026-03-10#get-the-license-for-a-repository

This endpoint is preferred over manually probing `LICENSE`, `COPYING`, and `NOTICE` paths because it returns GitHub's license detection result and SPDX candidate in one request.

Initial v3 does not perform recursive repository search and does not use the Contents API as a fallback for license file discovery.

## Evidence Semantics

GitHub License API results are interpreted as source repository evidence:

- valid `license.spdx_id` becomes a source-repository license candidate.
- `NOASSERTION` or `null` becomes unknown source-repository evidence.
- HTTP 404 becomes `license_not_detected` evidence.
- HTTP 403, 429, and 5xx become error evidence.

The API response body content is not parsed for custom license detection in initial v3. If GitHub does not identify a license, `ol` does not try to outguess it.

Evidence may include:

- license `spdx_id`
- license name/key
- license file path
- license file SHA
- HTML URL or logical repository URL
- fetch status
- warnings or errors

Report examples must not include token values or absolute local paths.

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

## Request Strategy

`ol` does not perform HEAD preflight requests for GitHub existence checks. Most GitHub REST `GET` and `HEAD` requests consume comparable rate-limit points, so preflight checks can waste requests.

`ol` should issue the needed GET request directly and record the resulting status as evidence.

## Cache

v3 introduces source repository evidence cache.

Cache files are stored under the user data area using hash-based file names:

```text
<user-data-dir>/ol/cache/source-repository/<sha256(cache-key)>.json
```

Source cache file names are hashed so private repository names are not visible in directory listings. The cache entry body stores the plain logical key for debugging and auditability, along with its hash.

Example shape:

```json
{
  "cacheKey": "github:owner/repo@ref",
  "cacheKeySha256": "...",
  "fetchedAt": "2026-07-08T00:00:00Z",
  "source": "github-license-api",
  "license": {
    "spdxId": "MIT"
  }
}
```

Cache entries are persistent. There is no automatic TTL. `--refresh` ignores existing source repository cache and overwrites it with newly fetched evidence.

`ol cache clear source-repository` removes source repository evidence cache.

## Best-Effort Execution

Source repository fetch errors are component-level evidence. They must not stop the whole scan.

If SBOM or package metadata evidence already yields a single valid license, a source repository fetch failure records a warning but does not change the component to `error`.

If no usable license evidence exists and source repository fetching fails, the component may be `error`.

If source repository evidence disagrees with SBOM or package metadata evidence, the component is `conflict`.
